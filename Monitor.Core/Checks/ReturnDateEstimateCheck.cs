using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class ReturnDateEstimateCheck : MonitorCheck
{
    private static readonly Regex DateField = new(@"\\?""(estimated_date|best_case_date|worst_case_date)\\?""\s*:\s*\\?""([^""\\]+)\\?""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConfidenceField = new(@"\\?""result_confidence\\?""\s*:\s*\\?""?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ReturnDateSettings _settings;
    private readonly string _connectionString;
    private readonly string _sportName;
    private readonly HttpClient _http;
    private readonly HashSet<int> _triedToday = new();
    private DateTime _triedDay = DateTime.MinValue;

    public ReturnDateEstimateCheck(ReturnDateSettings settings, string connectionString, string sportName)
    {
        _settings = settings;
        _connectionString = connectionString;
        _sportName = sportName;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        Interval = TimeSpan.FromMinutes(Math.Max(15, settings.IntervalMinutes));
    }

    public override string Name => $"{_sportName} return dates";

    public override string Category => "AI";

    private class Pending
    {
        public int StatusId;
        public string PlayerName = "";
        public string TeamName = "";
        public string Status = "";
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey)) return Failed("ReturnDates:ApiKey is not set.");

        var today = DateTime.Now.Date;
        if (_triedDay != today)
        {
            _triedToday.Clear();
            _triedDay = today;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var pending = new List<Pending>();
        const string sql = @"
DECLARE @SeasonId int = (SELECT TOP 1 Id FROM Seasons WHERE IsRegularSeason = 1 ORDER BY Year DESC);
SELECT ps.Id, LTRIM(RTRIM(ISNULL(p.FirstName, '') + ' ' + ISNULL(p.LastName, ''))), ISNULL(t.Name, ''), pst.Title
FROM PlayerStatuses ps
JOIN PlayerStatusTypes pst ON pst.Id = ps.PlayerStatusTypeId
JOIN Players p ON p.Id = ps.PlayerId
LEFT JOIN SeasonPlayers sp ON sp.PlayerId = ps.PlayerId AND sp.SeasonId = @SeasonId
LEFT JOIN Teams t ON t.Id = sp.TeamId
WHERE ps.IsActive = 1 AND ps.DateDeactivated IS NULL AND ps.DateDeleted IS NULL
  AND ps.EstimatedReturnDate IS NULL
  AND (pst.UsesDate = 1 OR pst.PlayType = 'O')
ORDER BY ps.DateAdded DESC";

        await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                pending.Add(new Pending
                {
                    StatusId = reader.GetInt32(0),
                    PlayerName = reader.GetString(1),
                    TeamName = reader.GetString(2),
                    Status = reader.IsDBNull(3) ? "" : reader.GetString(3)
                });
            }
        }

        var todo = pending.Where(p => !_triedToday.Contains(p.StatusId) && p.PlayerName.Length > 0).Take(Math.Max(1, _settings.MaxPerRun)).ToList();
        if (todo.Count == 0)
            return Ok(pending.Count == 0 ? "Every out player has a return date." : $"{pending.Count} without a date, all already tried today.");

        int saved = 0, noDate = 0, lowConfidence = 0, failed = 0;
        var details = new StringBuilder();
        var url = _settings.BaseUrl.TrimEnd('/') + "/api/AIResponse/GenerateAIResponseByFunctionName"
                  + "?functionName=" + Uri.EscapeDataString(_settings.FunctionName)
                  + "&apiSourceSetupId=" + _settings.ApiSourceSetupId.ToString(CultureInfo.InvariantCulture);

        foreach (var p in todo)
        {
            _triedToday.Add(p.StatusId);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new Dictionary<string, string>
                    {
                        ["playerName"] = p.PlayerName,
                        ["teamName"] = p.TeamName
                    })
                };
                request.Headers.Add("X-API-Key", _settings.ApiKey);

                using var response = await _http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    failed++;
                    details.AppendLine($"{p.PlayerName}: HTTP {(int)response.StatusCode} {Short(body)}");
                    continue;
                }

                var dates = DateField.Matches(body).Cast<Match>()
                    .GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
                var confidence = ConfidenceField.Match(body) is { Success: true } cm ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

                if (!dates.TryGetValue("estimated_date", out var text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var estimate))
                {
                    noDate++;
                    details.AppendLine($"{p.PlayerName} ({p.TeamName}, {p.Status}): no date in response. {Short(body)}");
                    continue;
                }

                if (confidence > 0 && confidence < _settings.MinConfidence)
                {
                    lowConfidence++;
                    details.AppendLine($"{p.PlayerName} ({p.TeamName}): {estimate:yyyy-MM-dd} skipped, confidence {confidence}");
                    continue;
                }

                if (estimate.Date < today)
                    estimate = today;

                await using var update = new SqlCommand("UPDATE PlayerStatuses SET EstimatedReturnDate = @d WHERE Id = @id AND EstimatedReturnDate IS NULL", conn);
                update.Parameters.AddWithValue("@d", estimate.Date);
                update.Parameters.AddWithValue("@id", p.StatusId);
                await update.ExecuteNonQueryAsync(ct);

                saved++;
                var range = dates.TryGetValue("best_case_date", out var best) && dates.TryGetValue("worst_case_date", out var worst) ? $" (best {best}, worst {worst})" : "";
                details.AppendLine($"{p.PlayerName} ({p.TeamName}, {p.Status}): {estimate:yyyy-MM-dd}{range}, confidence {confidence}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                details.AppendLine($"{p.PlayerName}: {ex.Message}");
            }
        }

        var left = pending.Count - todo.Count;
        var message = $"{saved} return date(s) set" + (noDate > 0 ? $", {noDate} without a date" : "") + (lowConfidence > 0 ? $", {lowConfidence} low confidence" : "") + (failed > 0 ? $", {failed} failed" : "") + (left > 0 ? $", {left} waiting for the next run" : "") + ".";

        if (failed > 0 && saved == 0) return Failed(message, details.ToString());
        return failed > 0 || noDate > 0 ? Attention(message, details.ToString()) : Ok(message, details.ToString());
    }

    private static string Short(string s)
    {
        s = (s ?? "").Replace("\n", " ").Replace("\r", " ");
        return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
    }
}
