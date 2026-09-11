using System.Net.Http.Json;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class ProjectionTrackingCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly ProjectionTrackingSettings _settings;
    private DateOnly? _lastCompleted;

    public ProjectionTrackingCheck(HttpClient http, ProjectionTrackingSettings settings)
    {
        _http = http;
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

SELECT p.Id, p.FirstName, p.LastName, sp.TeamId, pt.Title AS PlayerType
FROM SeasonPlayers sp
JOIN Players p ON p.Id = sp.PlayerId
LEFT JOIN PlayerTypes pt ON pt.Id = sp.PlayerTypeId
WHERE sp.SeasonId = @SeasonId AND sp.TeamId <> @ExcludeTeamId;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ExcludeTeamId", _settings.ExcludeTeamId);
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
                    playerType = reader.IsDBNull(4) ? null : reader.GetString(4)
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
                sourcePlayerIdsToIgnore = pass.IgnorePlayerIds
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

    private async Task<(bool ok, string body)> PostAsync(string url, object payload, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            message.Headers.Add("X-API-Key", _settings.ApiKey);

        using var response = await _http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return (response.IsSuccessStatusCode, body);
    }
}
