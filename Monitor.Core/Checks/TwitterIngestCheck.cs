using System.Text;
using System.Text.Json;

namespace Monitor.Core.Checks;

/// <summary>
/// Triggers a tweet pull on the RotoMonsterTwitter API. The API does the work;
/// this just tells it when, and reports what came back.
/// </summary>
public class TwitterIngestCheck : MonitorCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly TwitterApiSettings _settings;

    public TwitterIngestCheck(HttpClient http, TwitterApiSettings settings)
    {
        _http = http;
        _settings = settings;
        Interval = settings.Interval;
        FailuresBeforeAlert = Math.Max(1, settings.FailuresBeforeAlert);
    }

    public override string Name => "Twitter ingest";

    public override string Category => "Twitter";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (_settings.ListId <= 0)
        {
            return Failed("No list id configured.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return Failed("No API key configured.");
        }

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/api/tweets/Ingest/{_settings.ListId}";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // IIS returns 411 on a POST with no body, so send an empty one.
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };

        request.Headers.Add("X-API-Key", _settings.ApiKey);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return Failed($"API returned {(int)response.StatusCode}.", body);
        }

        var result = JsonSerializer.Deserialize<IngestResponse>(body, JsonOptions);

        if (result == null)
        {
            return Failed("Could not read the API response.", body);
        }

        if (!result.Success)
        {
            return Failed(result.ErrorMessage ?? "Ingest failed.", body);
        }

        return result.NewTweets > 0
            ? Ok($"{result.NewTweets} new tweet(s), {result.NewUsers} new account(s).", body)
            : Ok("No new tweets.");
    }

    private class IngestResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int NewTweets { get; set; }
        public int NewUsers { get; set; }
        public int TweetsReturned { get; set; }
        public int PagesFetched { get; set; }
    }
}
