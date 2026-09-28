using System.Net.Http.Json;
using System.Text.Json;

namespace Monitor.Core.Checks;

public class AdvancedOwnershipCheck : MonitorCheck
{
    private readonly AdvancedOwnershipSettings _settings;
    private readonly HttpClient _http;

    public AdvancedOwnershipCheck(AdvancedOwnershipSettings settings)
    {
        _settings = settings;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(Math.Max(1, settings.TimeoutMinutes))
        };
        Interval = TimeSpan.FromMinutes(Math.Max(15, settings.IntervalMinutes));
    }

    public override string Name => "Advanced ownership";

    public override string Category => "Ownership";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + "/api/Ownership/fill";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                categoriesCode = _settings.CategoriesCode,
                leagueCount = _settings.LeagueCount,
                pauseSeconds = _settings.PauseSeconds
            })
        };
        request.Headers.Add("X-API-Key", _settings.ApiKey);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return Failed($"Returned {(int)response.StatusCode}.", body);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var running = root.GetProperty("running").GetBoolean();
            var startedRun = root.GetProperty("startedRun").GetBoolean();

            if (!root.TryGetProperty("lastRun", out var last) || last.ValueKind == JsonValueKind.Null)
                return Ok(startedRun ? "Run started, no results yet." : "Waiting on the first run.");

            var found = last.GetProperty("leaguesFound").GetInt32();
            var refreshed = last.GetProperty("leaguesRefreshed").GetInt32();
            var skipped = last.GetProperty("leaguesSkipped").GetInt32();
            var failed = last.GetProperty("leaguesFailed").GetInt32();
            var filled = last.GetProperty("ownershipFilled").GetBoolean();
            var seconds = last.GetProperty("durationSeconds").GetDouble();
            var finishedAt = last.GetProperty("finishedAt").GetDateTime();

            var fillError = last.TryGetProperty("fillError", out var fe) && fe.ValueKind == JsonValueKind.String
                ? fe.GetString()
                : null;

            var details = Details(last, found, refreshed, skipped, failed, seconds, finishedAt);
            var suffix = running ? " Another run is going now." : "";

            if (!string.IsNullOrEmpty(fillError))
                return Failed("Last run failed to fill ownership." + suffix, details);

            if (refreshed == 0)
                return Attention((found == 0 ? "No leagues found for that categories code." : "No leagues refreshed.") + suffix, details);

            if (!filled)
                return Attention($"{refreshed} league(s) refreshed but ownership was not filled.{suffix}", details);

            if (refreshed < _settings.LeagueCount)
                return Attention($"{refreshed} of {_settings.LeagueCount} league(s) refreshed, ownership filled.{suffix}", details);

            return Ok($"{refreshed} league(s) refreshed, ownership filled.{suffix}", details);
        }
        catch (JsonException)
        {
            return Failed("Could not read the response.", body);
        }
    }

    private static string Details(JsonElement last, int found, int refreshed, int skipped, int failed, double seconds, DateTime finishedAt)
    {
        var lines = new List<string>
        {
            $"last run finished {finishedAt.ToLocalTime():g}",
            $"found {found}, refreshed {refreshed}, skipped {skipped}, failed {failed}, {seconds:0.#}s"
        };

        if (last.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failures.EnumerateArray())
            {
                var league = failure.TryGetProperty("providerLeagueId", out var id) ? id.GetString() : "?";
                var error = failure.TryGetProperty("error", out var err) ? err.GetString() : "";
                lines.Add($"  {league}: {error}");
            }
        }

        return string.Join("\n", lines);
    }
}
