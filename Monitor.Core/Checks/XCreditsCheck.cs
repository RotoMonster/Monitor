using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Monitor.Core.Checks;

public class XCreditsCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly TwitterApiSettings _settings;

    public XCreditsCheck(HttpClient http, TwitterApiSettings settings)
    {
        _http = http;
        _settings = settings;
        Interval = TimeSpan.FromMinutes(Math.Max(5, settings.CreditsIntervalMinutes));
    }

    public override string Name => "X API credits";

    public override string Category => "Twitter";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.XBearerToken))
            return Failed("No X bearer token configured.");

        var url = string.IsNullOrWhiteSpace(_settings.XCreditsUrl) ? "https://api.x.com/2/usage/credits" : _settings.XCreditsUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.XBearerToken);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return Failed($"X API returned {(int)response.StatusCode}.", body);

        double total;
        double? free = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("total_balance", out var value)
                || !value.TryGetDouble(out total))
                return Failed("No total_balance in the response.", body);
            if (data.TryGetProperty("free_balance", out var freeValue) && freeValue.TryGetDouble(out var f) && f > 0)
                free = f;
        }
        catch (JsonException)
        {
            return Failed("Could not read the X API response.", body);
        }

        var us = CultureInfo.GetCultureInfo("en-US");
        var text = total.ToString("C2", us) + (free.HasValue ? $" ({free.Value.ToString("C2", us)} of it free credit)" : "");

        return total < _settings.XCreditsAlertBelowDollars
            ? Attention($"X API credits low: {text} left.", body)
            : Ok($"{text} left.");
    }
}
