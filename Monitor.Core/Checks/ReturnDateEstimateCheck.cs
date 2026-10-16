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
    private readonly AlertClient? _alerts;
    private readonly HttpClient _http;
    private readonly HashSet<int> _triedToday = new();
    private DateTime _triedDay = DateTime.MinValue;
    private DateTime _refreshedDay = DateTime.MinValue;

    public ReturnDateEstimateCheck(ReturnDateSettings settings, string connectionString, string sportName, AlertClient? alerts = null)
    {
        _settings = settings;
        _connectionString = connectionString;
        _sportName = sportName;
        _alerts = alerts;
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
        public DateTime? Current;
    }

    private class Estimate
    {
        public DateTime? Date;
        public int Confidence;
        public string Range = "";
        public string? Error;
        public string Raw = "";
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey)) return Failed("ReturnDates:ApiKey is not set.");

        var now = DateTime.Now;
        var today = now.Date;
        if (_triedDay != today)
        {
            _triedToday.Clear();
            _triedDay = today;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var details = new StringBuilder();
        string refreshMessage = "";
        var refreshProblems = false;

        var inWindow = now.Hour >= _settings.RefreshAtHour && now.Hour < _settings.RefreshAtHour + 3;
        if (_settings.NightlyRefresh && inWindow && _refreshedDay != today)
        {
            _refreshedDay = today;
            (refreshMessage, refreshProblems) = await RefreshAllAsync(conn, today, details, ct);
        }

        var pending = (await LoadAsync(conn, false, ct)).ToList();
        var todo = pending.Where(p => !_triedToday.Contains(p.StatusId) && p.PlayerName.Length > 0).Take(Math.Max(1, _settings.MaxPerRun)).ToList();

        if (todo.Count == 0)
        {
            var idle = pending.Count == 0 ? "Every out player has a return date." : $"{pending.Count} without a date, all already tried today.";
            var text = refreshMessage.Length > 0 ? refreshMessage + " " + idle : idle;
            return refreshProblems ? Attention(text, details.ToString()) : Ok(text, details.ToString());
        }

        int saved = 0, noDate = 0, lowConfidence = 0, failed = 0;

        foreach (var p in todo)
        {
            _triedToday.Add(p.StatusId);
            var est = await EstimateAsync(p, ct);

            if (est.Error != null)
            {
                failed++;
                details.AppendLine($"{p.PlayerName}: {est.Error}");
                continue;
            }
            if (est.Date == null)
            {
                noDate++;
                details.AppendLine($"{p.PlayerName} ({p.TeamName}, {p.Status}): no date in response. {Short(est.Raw)}");
                continue;
            }
            if (est.Confidence > 0 && est.Confidence < _settings.MinConfidence)
            {
                lowConfidence++;
                details.AppendLine($"{p.PlayerName} ({p.TeamName}): {est.Date:yyyy-MM-dd} skipped, confidence {est.Confidence}");
                continue;
            }

            var date = est.Date.Value < today ? today : est.Date.Value;
            await SaveAsync(conn, p.StatusId, date, true, ct);
            saved++;
            details.AppendLine($"{p.PlayerName} ({p.TeamName}, {p.Status}): {date:yyyy-MM-dd}{est.Range}, confidence {est.Confidence}");
        }

        var left = pending.Count - todo.Count;
        var message = (refreshMessage.Length > 0 ? refreshMessage + " " : "")
                      + $"{saved} return date(s) set" + (noDate > 0 ? $", {noDate} without a date" : "") + (lowConfidence > 0 ? $", {lowConfidence} low confidence" : "") + (failed > 0 ? $", {failed} failed" : "") + (left > 0 ? $", {left} waiting for the next run" : "") + ".";

        if (failed > 0 && saved == 0) return Failed(message, details.ToString());
        return failed > 0 || refreshProblems ? Attention(message, details.ToString()) : Ok(message, details.ToString());
    }

    private async Task<(string, bool)> RefreshAllAsync(SqlConnection conn, DateTime today, StringBuilder details, CancellationToken ct)
    {
        var rows = (await LoadAsync(conn, true, ct)).Where(p => p.PlayerName.Length > 0).Take(Math.Max(1, _settings.MaxNightly)).ToList();
        int changed = 0, pastDue = 0, same = 0, failed = 0;
        details.AppendLine("[Nightly refresh]");

        foreach (var p in rows)
        {
            var old = p.Current!.Value.Date;
            var est = await EstimateAsync(p, ct);
            var wasPast = old < today;

            if (est.Error != null || est.Date == null || (est.Confidence > 0 && est.Confidence < _settings.MinConfidence))
            {
                if (est.Error != null) failed++;
                if (wasPast)
                {
                    pastDue++;
                    await AlertAsync($"Past return date: {p.PlayerName}", $"{p.TeamName} · {p.Status} · was due {old:M/d}", ct);
                    details.AppendLine($"  {p.PlayerName}: past due {old:M/d}, no new estimate");
                }
                else if (est.Error != null)
                {
                    details.AppendLine($"  {p.PlayerName}: {est.Error}");
                }
                continue;
            }

            var date = est.Date.Value < today ? today : est.Date.Value.Date;
            if (date == old)
            {
                same++;
                if (wasPast)
                {
                    pastDue++;
                    await AlertAsync($"Past return date: {p.PlayerName}", $"{p.TeamName} · {p.Status} · was due {old:M/d}", ct);
                }
                continue;
            }

            await SaveAsync(conn, p.StatusId, date, false, ct);
            changed++;
            if (wasPast) pastDue++;
            var title = wasPast ? $"Past return date: {p.PlayerName}" : $"Return date changed: {p.PlayerName}";
            await AlertAsync(title, $"{p.TeamName} · {p.Status} · {old:M/d} → {date:M/d}", ct);
            details.AppendLine($"  {p.PlayerName}: {old:M/d} -> {date:M/d}{est.Range}, confidence {est.Confidence}");
        }

        var message = $"Nightly: {rows.Count} checked, {changed} changed, {pastDue} past due" + (failed > 0 ? $", {failed} failed" : "") + ".";
        return (message, failed > 0);
    }

    private async Task<List<Pending>> LoadAsync(SqlConnection conn, bool withDate, CancellationToken ct)
    {
        var sql = @"
DECLARE @SeasonId int = (SELECT TOP 1 Id FROM Seasons WHERE IsRegularSeason = 1 ORDER BY Year DESC);
SELECT ps.Id, LTRIM(RTRIM(ISNULL(p.FirstName, '') + ' ' + ISNULL(p.LastName, ''))), ISNULL(t.Name, ''), pst.Title, ps.EstimatedReturnDate
FROM PlayerStatuses ps
JOIN PlayerStatusTypes pst ON pst.Id = ps.PlayerStatusTypeId
JOIN Players p ON p.Id = ps.PlayerId
LEFT JOIN SeasonPlayers sp ON sp.PlayerId = ps.PlayerId AND sp.SeasonId = @SeasonId
LEFT JOIN Teams t ON t.Id = sp.TeamId
WHERE ps.IsActive = 1 AND ps.DateDeactivated IS NULL AND ps.DateDeleted IS NULL
  AND ps.EstimatedReturnDate IS " + (withDate ? "NOT NULL" : "NULL") + @"
  AND (pst.UsesDate = 1 OR pst.PlayType = 'O')
ORDER BY ps.DateAdded DESC";

        var list = new List<Pending>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Pending
            {
                StatusId = reader.GetInt32(0),
                PlayerName = reader.GetString(1),
                TeamName = reader.GetString(2),
                Status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Current = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
            });
        }
        return list;
    }

    private async Task<Estimate> EstimateAsync(Pending p, CancellationToken ct)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + "/api/AIResponse/GenerateAIResponseByFunctionName"
                  + "?functionName=" + Uri.EscapeDataString(_settings.FunctionName)
                  + "&apiSourceSetupId=" + _settings.ApiSourceSetupId.ToString(CultureInfo.InvariantCulture);
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
                return new Estimate { Error = $"HTTP {(int)response.StatusCode} {Short(body)}", Raw = body };

            var dates = DateField.Matches(body).Cast<Match>()
                .GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
            var est = new Estimate { Raw = body };
            est.Confidence = ConfidenceField.Match(body) is { Success: true } cm ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (dates.TryGetValue("estimated_date", out var text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                est.Date = d.Date;
            if (dates.TryGetValue("best_case_date", out var best) && dates.TryGetValue("worst_case_date", out var worst))
                est.Range = $" (best {best}, worst {worst})";
            return est;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Estimate { Error = ex.Message };
        }
    }

    private static async Task SaveAsync(SqlConnection conn, int statusId, DateTime date, bool onlyIfEmpty, CancellationToken ct)
    {
        var sql = "UPDATE PlayerStatuses SET EstimatedReturnDate = @d WHERE Id = @id" + (onlyIfEmpty ? " AND EstimatedReturnDate IS NULL" : "");
        await using var update = new SqlCommand(sql, conn);
        update.Parameters.AddWithValue("@d", date.Date);
        update.Parameters.AddWithValue("@id", statusId);
        await update.ExecuteNonQueryAsync(ct);
    }

    private async Task AlertAsync(string title, string body, CancellationToken ct)
    {
        if (_alerts == null || !_alerts.IsConfigured || !_settings.AlertChanges) return;
        await _alerts.SendAsync($"{_sportName} {title}", body, notify: false, ct: ct);
    }

    private static string Short(string s)
    {
        s = (s ?? "").Replace("\n", " ").Replace("\r", " ");
        return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
    }
}
