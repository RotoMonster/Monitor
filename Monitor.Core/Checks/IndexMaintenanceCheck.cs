using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class IndexMaintenanceCheck : MonitorCheck
{
    private readonly IndexMaintenanceSettings _settings;
    private readonly List<string> _connectionStrings;
    private DateTime? _completedDay;
    private string _lastSummary = "";

    public IndexMaintenanceCheck(IndexMaintenanceSettings settings, IEnumerable<string> connectionStrings)
    {
        _settings = settings;
        _connectionStrings = connectionStrings.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        Interval = TimeSpan.FromMinutes(30);
    }

    public override string Name => "Index maintenance";

    public override string Category => "Database";

    private class IndexRow
    {
        public string Schema = "";
        public string Table = "";
        public string Index = "";
        public double Fragmentation;
        public long Pages;
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (_connectionStrings.Count == 0) return Failed("No connection string for index maintenance.");

        var now = DateTime.Now;
        if (_completedDay == now.Date)
            return Ok("Done for today. " + _lastSummary);

        var inWindow = _settings.RunUntilHour > _settings.RunAtHour
            ? now.Hour >= _settings.RunAtHour && now.Hour < _settings.RunUntilHour
            : now.Hour >= _settings.RunAtHour || now.Hour < _settings.RunUntilHour;
        if (!inWindow)
            return Ok($"Waiting for {_settings.RunAtHour}:00.");

        var deadline = Stopwatch.StartNew();
        var limit = TimeSpan.FromMinutes(Math.Max(5, _settings.MaxMinutes));
        int rebuilt = 0, reorganized = 0, failed = 0, skipped = 0;
        var details = new StringBuilder();
        var timedOut = false;

        foreach (var cs in _connectionStrings)
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            details.AppendLine($"[{conn.Database}]");

            var online = await SupportsOnlineAsync(conn, ct);
            var rows = await FragmentedAsync(conn, ct);
            if (rows.Count == 0)
            {
                details.AppendLine("  Nothing over " + _settings.ReorganizeAbove.ToString(CultureInfo.InvariantCulture) + "%.");
                continue;
            }

            foreach (var r in rows)
            {
                if (deadline.Elapsed > limit)
                {
                    timedOut = true;
                    skipped++;
                    continue;
                }

                var target = $"[{r.Index.Replace("]", "]]")}] ON [{r.Schema.Replace("]", "]]")}].[{r.Table.Replace("]", "]]")}]";
                var rebuild = r.Fragmentation > _settings.RebuildAbove;
                var label = $"  {r.Schema}.{r.Table}.{r.Index} {r.Fragmentation:0.0}% ({r.Pages * 8 / 1024} MB)";
                var watch = Stopwatch.StartNew();

                try
                {
                    if (rebuild)
                    {
                        try
                        {
                            await ExecAsync(conn, $"ALTER INDEX {target} REBUILD" + (online ? " WITH (ONLINE = ON)" : ""), ct);
                        }
                        catch (SqlException) when (online)
                        {
                            await ExecAsync(conn, $"ALTER INDEX {target} REBUILD", ct);
                        }
                        rebuilt++;
                        details.AppendLine($"{label} rebuilt in {watch.Elapsed.TotalSeconds:0}s");
                    }
                    else
                    {
                        await ExecAsync(conn, $"ALTER INDEX {target} REORGANIZE", ct);
                        await ExecAsync(conn, $"UPDATE STATISTICS [{r.Schema.Replace("]", "]]")}].[{r.Table.Replace("]", "]]")}] [{r.Index.Replace("]", "]]")}]", ct);
                        reorganized++;
                        details.AppendLine($"{label} reorganized in {watch.Elapsed.TotalSeconds:0}s");
                    }
                }
                catch (SqlException ex)
                {
                    failed++;
                    details.AppendLine($"{label} FAILED: {ex.Message}");
                }
            }
        }

        _lastSummary = $"{rebuilt} rebuilt, {reorganized} reorganized" + (failed > 0 ? $", {failed} failed" : "") + (skipped > 0 ? $", {skipped} left for tomorrow" : "") + $" in {deadline.Elapsed.TotalMinutes:0} min.";
        _completedDay = now.Date;

        if (failed > 0) return Attention(_lastSummary, details.ToString());
        if (timedOut) return Attention(_lastSummary + " Hit the time limit.", details.ToString());
        return Ok(_lastSummary, details.ToString());
    }

    private async Task<List<IndexRow>> FragmentedAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT OBJECT_SCHEMA_NAME(i.object_id), OBJECT_NAME(i.object_id), i.name, ips.avg_fragmentation_in_percent, ips.page_count
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
JOIN sys.objects o ON o.object_id = i.object_id
WHERE ips.index_id > 0
  AND ips.alloc_unit_type_desc = 'IN_ROW_DATA'
  AND o.is_ms_shipped = 0
  AND i.name IS NOT NULL
  AND ips.page_count > @pages
  AND ips.avg_fragmentation_in_percent > @min
ORDER BY ips.avg_fragmentation_in_percent DESC";

        var rows = new List<IndexRow>();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@pages", _settings.MinPageCount);
        cmd.Parameters.AddWithValue("@min", _settings.ReorganizeAbove);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new IndexRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Index = reader.GetString(2),
                Fragmentation = reader.GetDouble(3),
                Pages = reader.GetInt64(4)
            });
        }
        return rows;
    }

    private static async Task<bool> SupportsOnlineAsync(SqlConnection conn, CancellationToken ct)
    {
        using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", conn);
        var edition = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return edition == 3 || edition == 5 || edition == 8;
    }

    private async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = Math.Max(60, _settings.MaxMinutes * 60) };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
