using System.Globalization;
using System.Text.Json;

namespace Monitor.Core.Checks;

public class TwitterCreditsCheck : MonitorCheck
{
    private const double CreditsPerDollar = 100000;

    private readonly HttpClient _http;
    private readonly TwitterApiSettings _settings;

    public TwitterCreditsCheck(HttpClient http, TwitterApiSettings settings)
    {
        _http = http;
        _settings = settings;
        Interval = TimeSpan.FromMinutes(Math.Max(5, settings.CreditsIntervalMinutes));
    }

    public override string Name => "Twitter credits";

    public override string Category => "Twitter";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ProviderApiKey))
            return Failed("No twitterapi.io key configured.");

        var url = string.IsNullOrWhiteSpace(_settings.ProviderInfoUrl) ? "https://api.twitterapi.io/oapi/my/info" : _settings.ProviderInfoUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-API-Key", _settings.ProviderApiKey);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return Failed($"twitterapi.io returned {(int)response.StatusCode}.", body);

        double credits;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("recharge_credits", out var value) || !value.TryGetDouble(out credits))
                return Failed("No recharge_credits in the response.", body);
        }
        catch (JsonException)
        {
            return Failed("Could not read the twitterapi.io response.", body);
        }

        var dollars = credits / CreditsPerDollar;
        var text = dollars.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

        return dollars < _settings.CreditsAlertBelowDollars
            ? Attention($"Twitter credits low: {text} left.", body)
            : Ok($"{text} left.");
    }
}
