using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class DailyCleanupCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly TwitterApiSettings _api;
    private readonly DailyCleanupSettings _settings;
    private readonly string _connectionString;
    private readonly int _sportId;
    private DateOnly? _lastAttempted;
    private readonly string _attemptFile;

    public DailyCleanupCheck(TwitterApiSettings api, DailyCleanupSettings settings, string connectionString, int sportId)
    {
        _api = api;
        _settings = settings;
        _connectionString = connectionString;
        _sportId = sportId;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _attemptFile = Path.Combine(AppContext.BaseDirectory, $"daily-cleanup-{sportId}-last.txt");
        Interval = TimeSpan.FromMinutes(15);
    }

    public override string Name => "Daily cleanup";

    public override string Category => "Maintenance";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (now.Hour < _settings.RunAtHour)
            return Ok($"Waiting for hour {_settings.RunAtHour}.");

        _lastAttempted ??= ReadLastAttempt();
        if (_lastAttempted == today && !_settings.DryRun)
            return Ok($"Already ran today. Next run after hour {_settings.RunAtHour} tomorrow.");

        if (!_settings.DryRun)
        {
            _lastAttempted = today;
            SaveLastAttempt(today);
        }

        var lines = new List<string>();
        var failed = false;

        try
        {
            lines.Add(await CleanTweetsAsync(ct));
        }
        catch (Exception ex)
        {
            failed = true;
            lines.Add("Tweets: " + ex.Message);
        }

        try
        {
            lines.Add(await CleanTasksAsync(ct));
        }
        catch (Exception ex)
        {
            failed = true;
            lines.Add("Tasks: " + ex.Message);
        }

        var summary = string.Join(" ", lines);
        return failed ? Failed(summary) : Ok((_settings.DryRun ? "Dry run. " : "") + summary);
    }

    private async Task<string> CleanTweetsAsync(CancellationToken ct)
    {
        if (_settings.TweetDays <= 0)
            return "Tweets: off.";
        if (_sportId <= 0)
            return "Tweets: no SportId, skipped.";
        if (string.IsNullOrWhiteSpace(_api.ApiKey))
            return "Tweets: no Twitter API key, skipped.";

        var cutoff = DateTime.UtcNow.AddDays(-_settings.TweetDays);
        if (_settings.DryRun)
            return $"Tweets: would delete tweets before {cutoff:yyyy-MM-dd HH:mm} UTC.";

        var request = new HttpRequestMessage(HttpMethod.Post, _api.BaseUrl.TrimEnd('/') + "/api/tweets/DeleteTweets")
        {
            Content = JsonContent.Create(new { createdBefore = cutoff, sportId = _sportId })
        };
        request.Headers.Add("X-API-Key", _api.ApiKey);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"API returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new Exception(root.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "failed." : "failed.");

        var deleted = root.TryGetProperty("deletedCount", out var dc) ? dc.GetInt32() : 0;
        return $"Tweets: deleted {deleted} older than {_settings.TweetDays} days.";
    }

    private async Task<string> CleanTasksAsync(CancellationToken ct)
    {
        if (_settings.TaskDays <= 0)
            return "Tasks: off.";
        if (string.IsNullOrWhiteSpace(_connectionString))
            return "Tasks: no connection string, skipped.";

        var cutoff = DateTime.UtcNow.AddDays(-_settings.TaskDays);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = _settings.DryRun
            ? "IF OBJECT_ID('dbo.AdminTasks') IS NULL SELECT -1 ELSE SELECT COUNT(*) FROM AdminTasks WHERE CreatedAt < @Cutoff"
            : "IF OBJECT_ID('dbo.AdminTasks') IS NULL SELECT -1 ELSE BEGIN DELETE FROM AdminTasks WHERE CreatedAt < @Cutoff; SELECT @@ROWCOUNT; END";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Cutoff", cutoff);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        if (count < 0)
            return "Tasks: no AdminTasks table, skipped.";
        return _settings.DryRun
            ? $"Tasks: would delete {count} older than {_settings.TaskDays} days."
            : $"Tasks: deleted {count} older than {_settings.TaskDays} days.";
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
}
