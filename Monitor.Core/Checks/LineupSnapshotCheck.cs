using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class LineupSnapshotCheck : MonitorCheck
{
    private readonly string _connectionString;
    private readonly LineupSnapshotSettings _settings;
    private readonly string _sportName;
    private bool _setupDone;
    private DateOnly? _lastCompleted;

    public LineupSnapshotCheck(string connectionString, LineupSnapshotSettings settings, string sportName)
    {
        _connectionString = connectionString;
        _settings = settings;
        _sportName = sportName;
        Interval = TimeSpan.FromMinutes(30);
    }

    public override string Name => _sportName + " lineup snapshot";

    public override string Category => "Lineups";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (_lastCompleted == today)
            return Ok("Snapshot already taken today.");

        if (now.DayOfWeek != _settings.Day || now.Hour < _settings.RunAtHour)
            return Ok($"Waiting for {_settings.Day} at hour {_settings.RunAtHour}.");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (!_setupDone)
        {
            foreach (var batch in Regex.Split(SetupSql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var setup = new SqlCommand(batch, conn) { CommandTimeout = 120 };
                await setup.ExecuteNonQueryAsync(ct);
            }
            _setupDone = true;
        }

        var todayDate = today.ToDateTime(TimeOnly.MinValue);

        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM dbo.LineupProjectionSnapshots WHERE SnapshotDate = @d", conn))
        {
            exists.Parameters.AddWithValue("@d", todayDate);
            if ((int)(await exists.ExecuteScalarAsync(ct) ?? 0) > 0)
            {
                _lastCompleted = today;
                return Ok("Snapshot already taken today.");
            }
        }

        DateTime? previous = null;
        await using (var prev = new SqlCommand("SELECT MAX(SnapshotDate) FROM dbo.LineupProjectionSnapshots WHERE SnapshotDate < @d", conn))
        {
            prev.Parameters.AddWithValue("@d", todayDate);
            var value = await prev.ExecuteScalarAsync(ct);
            if (value is DateTime dt) previous = dt;
        }

        var report = new StringBuilder();
        string summary = "no earlier snapshot to grade";
        if (previous.HasValue)
        {
            await using var grade = new SqlCommand("EXEC dbo.ProjectedLineupAccuracy @SnapshotDate", conn) { CommandTimeout = 120 };
            grade.Parameters.AddWithValue("@SnapshotDate", previous.Value);
            await using var reader = await grade.ExecuteReaderAsync(ct);
            report.AppendLine($"Accuracy for the {previous.Value:yyyy-MM-dd} snapshot:");
            while (await reader.ReadAsync(ct))
            {
                var pos = reader["Position"]?.ToString();
                var src = reader["Source"]?.ToString();
                var pct = reader["HitPct"] is DBNull ? "-" : reader["HitPct"] + "%";
                var line = $"{pos} / {src}: {reader["Hits"]} hit, {reader["Misses"]} miss, {reader["Ungraded"]} ungraded, {pct}";
                report.AppendLine("  " + line);
                if (pos == "All" && src == "All") summary = $"last week {pct} ({reader["Hits"]}/{(int)reader["Hits"] + (int)reader["Misses"]})";
            }
            if (await reader.NextResultAsync(ct))
            {
                report.AppendLine("Misses:");
                while (await reader.ReadAsync(ct))
                    report.AppendLine($"  {reader["GameDate"]:M/d} {reader["Team"]} {reader["Position"]}: projected {reader["Projected"]} ({reader["Details"]}), started {reader["ActualStarter"]}");
            }
        }

        int saved;
        await using (var snap = new SqlCommand("EXEC dbo.SnapshotProjectedLineups @Days", conn) { CommandTimeout = 120 })
        {
            snap.Parameters.AddWithValue("@Days", Math.Max(1, _settings.Days));
            saved = Convert.ToInt32(await snap.ExecuteScalarAsync(ct) ?? 0);
        }

        if (saved > 0) _lastCompleted = today;

        var message = saved > 0
            ? $"Saved {saved} projected starters for the next {_settings.Days} days; {summary}."
            : "No projected starters yet; will try again.";
        return Ok(message, report.Length > 0 ? report.ToString().TrimEnd() : null);
    }

    private const string SetupSql = """
        IF OBJECT_ID('dbo.LineupProjectionSnapshots', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.LineupProjectionSnapshots (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                SnapshotDate DATE NOT NULL,
                TakenAt DATETIME2 NOT NULL,
                GameId INT NOT NULL,
                TeamId INT NOT NULL,
                PlayerId INT NOT NULL,
                PositionId INT NULL,
                PlayerGameStateTypeId INT NOT NULL,
                Details NVARCHAR(200) NULL
            );
            CREATE UNIQUE INDEX IX_LineupProjectionSnapshots_Snap ON dbo.LineupProjectionSnapshots (SnapshotDate, GameId, PlayerId);
        END
        GO

        IF OBJECT_ID('dbo.SnapshotProjectedLineups', 'P') IS NOT NULL DROP PROCEDURE dbo.SnapshotProjectedLineups;
        GO

        CREATE PROCEDURE dbo.SnapshotProjectedLineups
            @Days INT = 7
        AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @today DATE = CAST(GETDATE() AS DATE);

            INSERT INTO dbo.LineupProjectionSnapshots
                (SnapshotDate, TakenAt, GameId, TeamId, PlayerId, PositionId, PlayerGameStateTypeId, Details)
            SELECT @today, SYSUTCDATETIME(), s.GameId, s.TeamId, s.PlayerId, s.PositionId, s.PlayerGameStateTypeId, s.Details
            FROM PlayerGameStates s
            JOIN PlayerGameStateTypes t ON t.Id = s.PlayerGameStateTypeId
            JOIN Games g ON g.Id = s.GameId
            WHERE t.IsProbableStarter = 1
              AND g.GameDate >= @today
              AND g.GameDate < DATEADD(DAY, @Days, @today)
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.LineupProjectionSnapshots x
                  WHERE x.SnapshotDate = @today AND x.GameId = s.GameId AND x.PlayerId = s.PlayerId);

            SELECT @@ROWCOUNT AS RowsSnapshotted;
        END
        GO

        IF OBJECT_ID('dbo.ProjectedLineupAccuracy', 'P') IS NOT NULL DROP PROCEDURE dbo.ProjectedLineupAccuracy;
        GO

        CREATE PROCEDURE dbo.ProjectedLineupAccuracy
            @SnapshotDate DATE = NULL
        AS
        BEGIN
            SET NOCOUNT ON;
            IF @SnapshotDate IS NULL
                SELECT @SnapshotDate = MAX(SnapshotDate) FROM dbo.LineupProjectionSnapshots;

            ;WITH graded AS (
                SELECT
                    p.SnapshotDate,
                    pos.Abbreviation AS Pos,
                    CASE WHEN CHARINDEX(',', ISNULL(p.Details, '')) > 0
                         THEN LEFT(p.Details, CHARINDEX(',', p.Details) - 1)
                         ELSE ISNULL(NULLIF(p.Details, ''), '(none)') END AS Src,
                    CASE
                        WHEN g.IsFinished = 0 THEN 'Not played'
                        WHEN EXISTS (
                            SELECT 1 FROM PlayerGameStates a
                            JOIN PlayerGameStateTypes ast ON ast.Id = a.PlayerGameStateTypeId
                            WHERE a.GameId = p.GameId AND a.PlayerId = p.PlayerId AND ast.IsStarter = 1) THEN 'Hit'
                        WHEN EXISTS (
                            SELECT 1 FROM PlayerGameStates a
                            JOIN PlayerGameStateTypes ast ON ast.Id = a.PlayerGameStateTypeId
                            WHERE a.GameId = p.GameId AND a.TeamId = p.TeamId AND ast.IsStarter = 1
                              AND (p.PositionId IS NULL OR a.PositionId = p.PositionId)) THEN 'Miss'
                        ELSE 'No final lineup'
                    END AS Result
                FROM dbo.LineupProjectionSnapshots p
                JOIN Games g ON g.Id = p.GameId
                LEFT JOIN Positions pos ON pos.Id = p.PositionId
                WHERE p.SnapshotDate = @SnapshotDate
            )
            SELECT
                CASE WHEN GROUPING(Pos) = 1 THEN 'All' ELSE ISNULL(Pos, '-') END AS Position,
                CASE WHEN GROUPING(Src) = 1 THEN 'All' ELSE Src END AS Source,
                SUM(CASE WHEN Result = 'Hit' THEN 1 ELSE 0 END) AS Hits,
                SUM(CASE WHEN Result = 'Miss' THEN 1 ELSE 0 END) AS Misses,
                SUM(CASE WHEN Result IN ('Not played', 'No final lineup') THEN 1 ELSE 0 END) AS Ungraded,
                CAST(100.0 * SUM(CASE WHEN Result = 'Hit' THEN 1 ELSE 0 END)
                     / NULLIF(SUM(CASE WHEN Result IN ('Hit', 'Miss') THEN 1 ELSE 0 END), 0) AS DECIMAL(5,1)) AS HitPct
            FROM graded
            GROUP BY GROUPING SETS ((Pos, Src), (Pos), ())
            ORDER BY GROUPING(Pos), Pos, GROUPING(Src), Src;

            SELECT
                p.SnapshotDate, g.GameDate, tm.Code AS Team, pos.Abbreviation AS Position,
                pl.FirstName + ' ' + pl.LastName AS Projected, p.Details,
                STUFF((
                    SELECT ', ' + apl.FirstName + ' ' + apl.LastName
                    FROM PlayerGameStates a
                    JOIN PlayerGameStateTypes ast ON ast.Id = a.PlayerGameStateTypeId
                    JOIN Players apl ON apl.Id = a.PlayerId
                    WHERE a.GameId = p.GameId AND a.TeamId = p.TeamId AND ast.IsStarter = 1
                      AND (p.PositionId IS NULL OR a.PositionId = p.PositionId)
                    FOR XML PATH('')), 1, 2, '') AS ActualStarter
            FROM dbo.LineupProjectionSnapshots p
            JOIN Games g ON g.Id = p.GameId
            JOIN Teams tm ON tm.Id = p.TeamId
            JOIN Players pl ON pl.Id = p.PlayerId
            LEFT JOIN Positions pos ON pos.Id = p.PositionId
            WHERE p.SnapshotDate = @SnapshotDate
              AND g.IsFinished = 1
              AND NOT EXISTS (
                  SELECT 1 FROM PlayerGameStates a
                  JOIN PlayerGameStateTypes ast ON ast.Id = a.PlayerGameStateTypeId
                  WHERE a.GameId = p.GameId AND a.PlayerId = p.PlayerId AND ast.IsStarter = 1)
            ORDER BY g.GameDate, tm.Code;
        END
        GO
        """;
}
