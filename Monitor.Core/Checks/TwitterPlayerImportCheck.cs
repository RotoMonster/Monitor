using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class TwitterPlayerImportCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly TwitterApiSettings _api;
    private readonly TwitterPlayerImportSettings _settings;
    private readonly string _connectionString;
    private readonly int _sportId;
    private DateOnly? _lastAttempted;
    private readonly string _attemptFile;

    public TwitterPlayerImportCheck(TwitterApiSettings api, TwitterPlayerImportSettings settings, string fallbackConnectionString, int defaultSportId)
    {
        _api = api;
        _settings = settings;
        _sportId = settings.SportId > 0 ? settings.SportId : defaultSportId;
        _connectionString = string.IsNullOrWhiteSpace(settings.ConnectionString) ? fallbackConnectionString : settings.ConnectionString;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _attemptFile = Path.Combine(AppContext.BaseDirectory, $"twitter-player-import-{_sportId}-last.txt");
        Interval = TimeSpan.FromMinutes(15);
    }

    public override string Name => "Twitter player import";

    public override string Category => "Twitter";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (now.Hour < _settings.RunAtHour)
            return Ok($"Waiting for hour {_settings.RunAtHour}.");

        _lastAttempted ??= ReadLastAttempt();
        if (_lastAttempted == today && !_settings.DryRun)
            return Ok($"Already ran today. Next run after hour {_settings.RunAtHour} tomorrow.");

        if (_sportId <= 0)
            return Failed("No SportId set for the player import.");

        if (string.IsNullOrWhiteSpace(_connectionString))
            return Failed("No connection string set for the player import.");

        if (!_settings.DryRun)
        {
            _lastAttempted = today;
            SaveLastAttempt(today);
        }

        var rmPlayers = new Dictionary<int, (string First, string Last, int? TeamId)>();
        var rmAliases = new Dictionary<int, List<string>>();

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            var sql = @"
DECLARE @SeasonId int = (SELECT TOP 1 Id FROM Seasons WHERE IsRegularSeason = 1 ORDER BY Year DESC);

SELECT p.Id, p.FirstName, p.LastName, sp.TeamId
FROM SeasonPlayers sp
JOIN Players p ON p.Id = sp.PlayerId
WHERE sp.SeasonId = @SeasonId;

SELECT pa.PlayerId, pa.Alias
FROM PlayerAliases pa
WHERE pa.PlayerId IN (SELECT PlayerId FROM SeasonPlayers WHERE SeasonId = @SeasonId);";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var first = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                var last = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                int? teamId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                rmPlayers[id] = (first, last, teamId);
            }

            await reader.NextResultAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1)) continue;
                var id = reader.GetInt32(0);
                var alias = reader.GetString(1).Trim();
                if (alias.Length == 0) continue;
                if (!rmAliases.TryGetValue(id, out var list)) rmAliases[id] = list = new List<string>();
                list.Add(alias);
            }
        }

        if (rmPlayers.Count < _settings.MinPlayers)
            return Failed($"Only {rmPlayers.Count} players found in the database, expected at least {_settings.MinPlayers}. Import skipped.");

        var existing = await GetExistingAsync(ct);

        var payload = new List<object>();
        foreach (var kv in rmPlayers)
        {
            existing.TryGetValue(kv.Key, out var current);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rmAliases.TryGetValue(kv.Key, out var fromRm)) aliases.UnionWith(fromRm);
            if (current != null) aliases.UnionWith(current.Aliases);

            payload.Add(new
            {
                playerId = kv.Key,
                firstName = kv.Value.First,
                lastName = kv.Value.Last,
                teamId = kv.Value.TeamId,
                fullNameOnly = current?.FullNameOnly ?? false,
                aliases = aliases.ToList()
            });
        }

        var added = rmPlayers.Keys.Count(id => !existing.ContainsKey(id));
        var dropped = existing.Keys.Count(id => !rmPlayers.ContainsKey(id));

        if (existing.Count > 0 && dropped > existing.Count * _settings.MaxDropPercent / 100.0)
            return Failed($"Import would drop {dropped} of {existing.Count} players, more than {_settings.MaxDropPercent}%. Import skipped.");

        if (_settings.DryRun)
        {
            var newNames = rmPlayers.Where(p => !existing.ContainsKey(p.Key)).Take(20).Select(p => $"+ {p.Value.First} {p.Value.Last} ({p.Key})");
            var droppedIds = existing.Keys.Where(id => !rmPlayers.ContainsKey(id)).Take(20).Select(id => $"- {id}");
            return Ok($"Dry run: would import {payload.Count} players ({added} new, {dropped} dropped, {existing.Count} there now).", string.Join("\n", newNames.Concat(droppedIds)));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _api.BaseUrl.TrimEnd('/') + "/api/import/players")
        {
            Content = JsonContent.Create(new { sportId = _sportId, players = payload, teams = Array.Empty<object>() })
        };
        request.Headers.Add("X-API-Key", _api.ApiKey);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return Failed($"Import returned {(int)response.StatusCode}.", body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var error = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : "";
            return Failed("Import failed. " + error, body);
        }

        var imported = root.TryGetProperty("playersImported", out var pi) ? pi.GetInt32() : payload.Count;
        return Ok($"Imported {imported} players ({added} new, {dropped} dropped).");
    }

    private async Task<Dictionary<int, ExistingPlayer>> GetExistingAsync(CancellationToken ct)
    {
        var result = new Dictionary<int, ExistingPlayer>();
        var skip = 0;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _api.BaseUrl.TrimEnd('/') + "/api/players/GetPlayers")
            {
                Content = JsonContent.Create(new { sportId = _sportId, skip, maxResults = 1000 })
            };
            request.Headers.Add("X-API-Key", _api.ApiKey);

            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var total = root.TryGetProperty("totalCount", out var tc) ? tc.GetInt32() : 0;

            var count = 0;
            if (root.TryGetProperty("players", out var players))
            {
                foreach (var p in players.EnumerateArray())
                {
                    count++;
                    var aliases = new List<string>();
                    if (p.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array)
                        aliases.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));

                    result[p.GetProperty("playerId").GetInt32()] = new ExistingPlayer
                    {
                        FullNameOnly = p.TryGetProperty("fullNameOnly", out var f) && f.ValueKind == JsonValueKind.True,
                        Aliases = aliases
                    };
                }
            }

            skip += count;
            if (count == 0 || skip >= total) break;
        }

        return result;
    }

    private DateOnly? ReadLastAttempt()
    {
        try
        {
            if (!File.Exists(_attemptFile)) return null;
            return DateOnly.TryParse(File.ReadAllText(_attemptFile).Trim(), out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveLastAttempt(DateOnly day)
    {
        try
        {
            File.WriteAllText(_attemptFile, day.ToString("yyyy-MM-dd"));
        }
        catch
        {
        }
    }

    private class ExistingPlayer
    {
        public bool FullNameOnly { get; set; }
        public List<string> Aliases { get; set; } = new();
    }
}
