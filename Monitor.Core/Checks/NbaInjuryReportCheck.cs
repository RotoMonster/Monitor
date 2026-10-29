using System.Text;

namespace Monitor.Core.Checks;

public class NbaInjuryReportCheck : MonitorCheck
{
    private readonly HttpClient _http;
    private readonly NbaInjuryReportSettings _settings;
    private string? _lastUrl;

    public NbaInjuryReportCheck(HttpClient http, NbaInjuryReportSettings settings)
    {
        _http = http;
        _settings = settings;
        Interval = TimeSpan.FromMinutes(Math.Max(5, settings.IntervalMinutes));
    }

    public override string Name => "NBA injury report";

    public override string Category => "Injuries";

    public NbaInjuryReport? LastReport { get; private set; }

    public List<NbaInjuryEntry> LastEntries { get; private set; } = new();

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var now = NbaInjuryReport.EasternNow();
        var slot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0);
        var oldest = slot.AddHours(-Math.Max(1, _settings.LookBackHours));

        byte[]? pdf = null;
        string? url = null;
        for (var t = slot; t >= oldest && pdf == null; t = t.AddMinutes(-15))
        {
            url = NbaInjuryReport.ReportUrl(_settings.UrlFormat, t);
            using var response = await _http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                pdf = await response.Content.ReadAsByteArrayAsync(ct);
        }

        if (pdf == null || url == null)
            return Ok($"No report posted in the last {_settings.LookBackHours} hours.");

        NbaInjuryReport report;
        try
        {
            report = NbaInjuryReport.Parse(pdf);
        }
        catch (Exception ex)
        {
            return Failed("Could not read the injury report PDF: " + ex.Message, url);
        }

        var entries = report.Upcoming(now);
        LastReport = report;
        LastEntries = entries;
        _lastUrl = url;

        var players = entries.Where(e => !e.NotYetSubmitted).ToList();
        var notSubmitted = entries.Where(e => e.NotYetSubmitted).ToList();
        var when = report.ReportTimeEastern?.ToString("h:mm tt") ?? "?";

        var body = new StringBuilder();
        body.AppendLine(url);
        body.AppendLine($"{report.Entries.Count} rows in the report, {entries.Count} for games that haven't started.");
        foreach (var e in entries.OrderBy(e => e.GameStartEastern).ThenBy(e => e.Matchup).ThenBy(e => e.TeamCode).ThenBy(e => e.PlayerName))
            body.AppendLine(e.ToString());

        if (report.Entries.Count == 0)
            return Attention($"Report {when} has no rows; the layout may have changed.", body.ToString());

        var summary = $"Report {when}: {players.Count} players on {players.Select(p => p.Team).Distinct().Count()} teams";
        if (notSubmitted.Count > 0) summary += $", {notSubmitted.Count} teams not yet submitted";
        return Ok(summary + ".", body.ToString());
    }
}
