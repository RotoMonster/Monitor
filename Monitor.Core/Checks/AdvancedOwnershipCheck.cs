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
            Timeout = TimeSpan.FromMinutes(Math.Max(5, settings.TimeoutMinutes))
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

            var found = root.GetProperty("leaguesFound").GetInt32();
            var refreshed = root.GetProperty("leaguesRefreshed").GetInt32();
            var failed = root.GetProperty("leaguesFailed").GetInt32();
            var filled = root.GetProperty("ownershipFilled").GetBoolean();
            var seconds = root.GetProperty("durationSeconds").GetDouble();

            var fillError = root.TryGetProperty("fillError", out var fe) && fe.ValueKind == JsonValueKind.String
                ? fe.GetString()
                : null;

            var details = Details(root, found, refreshed, failed, seconds);

            if (!string.IsNullOrEmpty(fillError))
                return Failed("Ownership fill failed.", details);

            if (refreshed == 0)
                return Attention(found == 0 ? "No leagues found for that categories code." : "No leagues refreshed.", details);

            if (!filled)
                return Attention($"{refreshed} league(s) refreshed but ownership was not filled.", details);

            if (refreshed < _settings.LeagueCount)
                return Attention($"{refreshed} of {_settings.LeagueCount} league(s) refreshed, ownership filled.", details);

            return Ok($"{refreshed} league(s) refreshed, ownership filled.", details);
        }
        catch (JsonException)
        {
            return Failed("Could not read the response.", body);
        }
    }

    private static string Details(JsonElement root, int found, int refreshed, int failed, double seconds)
    {
        var lines = new List<string>
        {
            $"found {found}, refreshed {refreshed}, failed {failed}, {seconds:0.#}s"
        };

        if (root.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array)
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
