using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Monitor.Core;

public class SeqLogger : IDisposable
{
    private const int MaxQueue = 10000;
    private const int MaxBatch = 500;

    private static readonly string[] Levels = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    private readonly HttpClient _http;
    private readonly SeqSettings _settings;
    private readonly string _app;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly List<string> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    public SeqLogger(HttpClient http, SeqSettings settings, string app)
    {
        _http = http;
        _settings = settings;
        _app = app;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool IsConfigured => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.Url);

    public void Write(string level, string message, IDictionary<string, object?>? properties = null)
    {
        if (!IsConfigured || Rank(level) < Rank(_settings.MinimumLevel)) return;

        var evt = new Dictionary<string, object?>
        {
            ["@t"] = DateTime.UtcNow.ToString("o"),
            ["@l"] = level,
            ["@m"] = message,
            ["App"] = _app,
            ["Host"] = Environment.MachineName
        };

        if (properties != null)
        {
            foreach (var kv in properties)
            {
                if (kv.Value != null) evt[kv.Key] = kv.Value;
            }
        }

        _queue.Enqueue(JsonSerializer.Serialize(evt));

        while (_queue.Count > MaxQueue && _queue.TryDequeue(out _)) { }
    }

    public void CheckCompleted(string check, CheckResult result)
    {
        if (result.Message != null && result.Message.StartsWith("Still running from an earlier start")) return;

        var level = !result.Success ? "Error" : (result.NeedsAttention ? "Warning" : "Information");

        Write(level, $"{check}: {result.Message}", new Dictionary<string, object?>
        {
            ["Check"] = check,
            ["Details"] = string.IsNullOrWhiteSpace(result.Details) ? null : result.Details,
            ["DurationMs"] = (long)result.Duration.TotalMilliseconds
        });
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _settings.FlushSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(ct);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        while (_pending.Count < MaxBatch && _queue.TryDequeue(out var line))
        {
            _pending.Add(line);
        }

        if (_pending.Count == 0) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.Url.TrimEnd('/')}/ingest/clef")
            {
                Content = new StringContent(string.Join("\n", _pending), Encoding.UTF8, "application/vnd.serilog.clef")
            };

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Add("X-Seq-ApiKey", _settings.ApiKey);
            }

            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode || (int)response.StatusCode == 400)
            {
                _pending.Clear();
            }
        }
        catch
        {
        }

        if (_pending.Count > MaxQueue)
        {
            _pending.RemoveRange(0, _pending.Count - MaxQueue);
        }
    }

    private static int Rank(string? level)
    {
        var i = Array.FindIndex(Levels, l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 2 : i;
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
