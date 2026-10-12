using System.Net.Http.Json;

namespace Monitor.Core;

/// <summary>
/// Posts a message to the RotoMonster alerts API, which stores it and pushes
/// it to the phones registered there.
/// </summary>
public class AlertClient
{
    private readonly HttpClient _http;
    private readonly AlertSettings _settings;

    public AlertClient(HttpClient http, AlertSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public bool IsConfigured =>
        _settings.Enabled
        && !string.IsNullOrWhiteSpace(_settings.BaseUrl)
        && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    /// <summary>
    /// Returns the failure reason, or null when it worked. Never throws - a
    /// broken alerts service must not take the monitor down with it.
    /// </summary>
    public async Task<string?> SendAsync(
        string title,
        string? body = null,
        bool notify = true,
        string? url = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return "Alerts not configured";

        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_settings.BaseUrl.TrimEnd('/')}/api/message");

            request.Headers.Add("X-API-Key", _settings.ApiKey);

            request.Content = JsonContent.Create(new
            {
                sportId = _settings.SportId,
                title,
                body,
                url,
                notify,
                staffOnly = _settings.StaffOnly
            });

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return $"Alerts API returned {(int)response.StatusCode}";

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
