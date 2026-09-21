using System.Net.Http.Json;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class ProjectionTrackingCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly ProjectionTrackingSettings _settings;
    private DateOnly? _lastCompleted;
    private DateOnly? _lastAttempted;
    private static readonly string AttemptFile = Path.Combine(AppContext.BaseDirectory, "projection-tracking-last.txt");

    public ProjectionTrackingCheck(HttpClient http, ProjectionTrackingSettings settings)
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(Math.Max(1, settings.TimeoutMinutes))
        };
        _settings = settings;
        Interval = TimeSpan.FromMinutes(15);
    }

    public override string Name => "AI projection tracking";

    public override string Category => "Projections";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (_lastCompleted == today)
            return Ok($"Already ran today at hour {_settings.RunAtHour}.");

        if (now.Hour < _settings.RunAtHour)
            return Ok($"Waiting for hour {_settings.RunAtHour}.");

        _lastAttempted ??= ReadLastAttempt();
        if (_lastAttempted == today)
            return Ok($"Already attempted today. Next run after hour {_settings.RunAtHour} tomorrow.");

        _lastAttempted = today;
        SaveLastAttempt(today);

        var teams = new List<object>();
        var players = new List<object>();

        await using (var connection = new SqlConnection(_settings.ConnectionString))
        {
            await connection.OpenAsync(ct);

            var sql = @"
DECLARE @SeasonId int = (SELECT TOP 1 Id FROM Seasons WHERE IsRegularSeason = 1 ORDER BY Year DESC);

SELECT DISTINCT t.Id, t.Code, t.Name
FROM SeasonPlayers sp
JOIN Teams t ON t.Id = sp.TeamId
WHERE sp.SeasonId = @SeasonId AND sp.TeamId <> @ExcludeTeamId;

SELECT p.Id, p.FirstName, p.LastName, sp.TeamId, pt.Title AS PlayerType,
       (SELECT TOP 1 pst.Title
               + CASE WHEN ps.Comment IS NULL THEN '' ELSE ': ' + ps.Comment END
               + ', reported ' + CONVERT(varchar(10), ps.DateAdded, 23)
               + CASE WHEN ps.EstimatedReturnDate IS NULL THEN ''
                      ELSE ', current estimate ' + CONVERT(varchar(10), ps.EstimatedReturnDate, 23) END
        FROM PlayerStatuses ps
        JOIN PlayerStatusTypes pst ON pst.Id = ps.PlayerStatusTypeId
        WHERE ps.PlayerId = p.Id AND ps.IsActive = 1
          AND ps.DateDeactivated IS NULL AND ps.DateDeleted IS NULL
          AND pst.Title IN (SELECT value FROM STRING_SPLIT(@InjuryTitles, '|'))
        ORDER BY ps.DateAdded DESC) AS InjuryNote
FROM SeasonPlayers sp
JOIN Players p ON p.Id = sp.PlayerId
LEFT JOIN PlayerTypes pt ON pt.Id = sp.PlayerTypeId
WHERE sp.SeasonId = @SeasonId AND sp.TeamId <> @ExcludeTeamId;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ExcludeTeamId", _settings.ExcludeTeamId);
            command.Parameters.AddWithValue("@InjuryTitles", string.Join("|", _settings.InjuryStatusTitles));
            command.CommandTimeout = 60;

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                teams.Add(new
                {
                    sourceTeamId = reader.GetInt32(0),
                    code = reader.GetString(1),
                    name = reader.GetString(2)
                });
            }

            await reader.NextResultAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                players.Add(new
                {
                    sourcePlayerId = reader.GetInt32(0),
                    firstName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    lastName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    sourceTeamId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                    playerType = reader.IsDBNull(4) ? null : reader.GetString(4),
                    injuryNote = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        if (teams.Count == 0 || players.Count == 0)
            return Failed($"Read {teams.Count} teams and {players.Count} players from the database.");

        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        var log = new List<string> { $"Read {teams.Count} teams and {players.Count} players." };

        var teamSync = await PostAsync($"{baseUrl}/api/sync/teams",
            new { sportId = _settings.SportId, teams }, ct);
        if (!teamSync.ok) return Failed("Team sync failed.", teamSync.body);
        log.Add($"Teams synced. {teamSync.body}");

        var playerSync = await PostAsync($"{baseUrl}/api/sync/players",
            new { sportId = _settings.SportId, players }, ct);
        if (!playerSync.ok) return Failed("Player sync failed.", playerSync.body);
        log.Add($"Players synced. {playerSync.body}");

        List<int> injured = new();

        if (_settings.Passes.Any(x => x.IgnoreInjured))
        {
            injured = await LoadInjuredAsync(ct);
            log.Add($"{injured.Count} players have an active injury status.");
        }

        var projectionDate = today.ToString("yyyy-MM-dd");
        var totalChanged = 0;
        var failures = new List<string>();

        foreach (var pass in _settings.Passes)
        {
            var run = await PostAsync($"{baseUrl}/api/ProjectionTracking/run", new
            {
                sportId = _settings.SportId,
                functionName = pass.FunctionName,
                reviewFunctionName = pass.ReviewFunctionName,
                apiSourceSetupId = _settings.ApiSourceSetupId,
                projectionDate,
                sourcePlayerIdsToIgnore = pass.IgnoreInjured
                    ? pass.IgnorePlayerIds.Concat(injured).Distinct().ToList()
                    : pass.IgnorePlayerIds,
                injuredOnly = pass.InjuredOnly
            }, ct);

            if (!run.ok)
            {
                failures.Add(pass.FunctionName);
                log.Add($"{pass.FunctionName} failed. {run.body}");
                continue;
            }

            log.Add($"{pass.FunctionName}: {run.body}");

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(run.body);
                if (doc.RootElement.TryGetProperty("totalChanged", out var changed))
                    totalChanged += changed.GetInt32();
            }
            catch
            {
            }
        }

        var details = string.Join("\n\n", log);

        if (failures.Count > 0)
            return Failed($"{failures.Count} of {_settings.Passes.Count} passes failed.", details);

        _lastCompleted = today;

        return totalChanged > 0
            ? Attention($"{totalChanged} projection(s) changed for {projectionDate}.", details)
            : Ok($"No changes for {projectionDate}.", details);
    }

    private static DateOnly? ReadLastAttempt()
    {
        try
        {
            if (!File.Exists(AttemptFile)) return null;
            return DateOnly.TryParse(File.ReadAllText(AttemptFile).Trim(), out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLastAttempt(DateOnly day)
    {
        try
        {
            File.WriteAllText(AttemptFile, day.ToString("yyyy-MM-dd"));
        }
        catch
        {
        }
    }

    private async Task<List<int>> LoadInjuredAsync(CancellationToken ct)
    {
        var injured = new List<int>();

        if (_settings.InjuryStatusTitles.Count == 0) return injured;

        var names = string.Join(",", _settings.InjuryStatusTitles
            .Select((_, i) => "@t" + i));

        var sql = $@"
SELECT DISTINCT ps.PlayerId
FROM PlayerStatuses ps
JOIN PlayerStatusTypes pst ON pst.Id = ps.PlayerStatusTypeId
WHERE ps.IsActive = 1
  AND ps.DateDeactivated IS NULL
  AND ps.DateDeleted IS NULL
  AND pst.Title IN ({names});";

        await using var connection = new SqlConnection(_settings.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < _settings.InjuryStatusTitles.Count; i++)
            command.Parameters.AddWithValue("@t" + i, _settings.InjuryStatusTitles[i]);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            injured.Add(reader.GetInt32(0));

        return injured;
    }

    private async Task<(bool ok, string body)> PostAsync(string url, object payload, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            message.Headers.Add("X-API-Key", _settings.ApiKey);

        try
        {
            using var response = await _http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
