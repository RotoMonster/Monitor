using System.Text;
using System.Text.Json;

namespace Monitor.Core.Checks;

public class TwitterIngestCheck : MonitorCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly TwitterApiSettings _settings;
    private DateTime _lastXCall = DateTime.MinValue;
    private DateTime? _primaryDownSince;

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

        var primary = await IngestAsync(null, ct);
        if (primary.Ok)
        {
            var recovered = _primaryDownSince.HasValue
                ? $" twitterapi.io is back (was down since {_primaryDownSince.Value:h:mm tt})."
                : "";
            _primaryDownSince = null;
            return Ok(primary.Summary + recovered, primary.Body);
        }

        _primaryDownSince ??= DateTime.Now;
        var primaryProblem = $"twitterapi.io failed ({primary.Problem})";

        if (!_settings.XFallback)
        {
            return Failed(primaryProblem + ".", primary.Body);
        }

        var xGap = TimeSpan.FromSeconds(Math.Max(1, _settings.XIntervalSeconds));
        var sinceX = DateTime.Now - _lastXCall;
        if (sinceX < xGap)
        {
            var wait = (int)Math.Ceiling((xGap - sinceX).TotalSeconds);
            return Attention($"{primaryProblem}; X API fallback waits {wait}s more (limit every {(int)xGap.TotalSeconds}s).", primary.Body);
        }

        _lastXCall = DateTime.Now;
        var fallback = await IngestAsync("x", ct);
        if (fallback.Ok)
        {
            return Attention($"{primaryProblem}; used X API instead: {fallback.Summary}", primary.Body + "\n\nX API:\n" + fallback.Body);
        }

        return Failed($"{primaryProblem}; X API also failed ({fallback.Problem}).", primary.Body + "\n\nX API:\n" + fallback.Body);
    }

    private async Task<IngestOutcome> IngestAsync(string? source, CancellationToken ct)
    {
        var url = $"{_settings.BaseUrl.TrimEnd('/')}/api/tweets/Ingest/{_settings.ListId}";
        if (!string.IsNullOrEmpty(source)) url += "?source=" + Uri.EscapeDataString(source);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", _settings.ApiKey);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new IngestOutcome { Problem = "could not reach the Twitter API: " + ex.Message, Body = "" };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new IngestOutcome { Problem = Describe((int)response.StatusCode, body), Body = body };
        }

        IngestResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<IngestResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result == null)
        {
            return new IngestOutcome { Problem = "unreadable response", Body = body };
        }

        if (!result.Success)
        {
            return new IngestOutcome { Problem = result.ErrorMessage ?? "ingest failed", Body = body };
        }

        return new IngestOutcome
        {
            Ok = true,
            Body = body,
            Summary = result.NewTweets > 0
                ? $"{result.NewTweets} new tweet(s), {result.NewUsers} new account(s)."
                : "No new tweets."
        };
    }

    private static string Describe(int status, string body)
    {
        var marker = "twitterapi.io returned ";
        var at = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var code = new string(body.Skip(at + marker.Length).TakeWhile(char.IsDigit).ToArray());
            if (code.Length > 0) return "provider error " + code;
        }
        return "API returned " + status;
    }

    private class IngestOutcome
    {
        public bool Ok { get; set; }
        public string Summary { get; set; } = "";
        public string Problem { get; set; } = "";
        public string Body { get; set; } = "";
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
