using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Monitor.Core;

public class NbaInjuryEntry
{
    public DateTime GameDate { get; set; }
    public string GameTime { get; set; } = "";
    public DateTime GameStartEastern { get; set; }
    public string Matchup { get; set; } = "";
    public string Team { get; set; } = "";
    public string TeamCode { get; set; } = "";
    public string ListedName { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool NotYetSubmitted { get; set; }

    public override string ToString() => NotYetSubmitted
        ? $"{GameDate:M/d} {GameTime} {Matchup} {TeamCode}: NOT YET SUBMITTED"
        : $"{GameDate:M/d} {GameTime} {Matchup} {TeamCode}: {PlayerName} - {Status} - {Reason}";
}

public class NbaInjuryReport
{
    private const string NotYetSubmittedText = "NOT YET SUBMITTED";
    private const double SpaceGap = 1.0;
    private const double SameLine = 2.0;
    private const double WrapDistance = 14.0;

    private static readonly string[] HeaderWords = { "Game", "Game", "Matchup", "Team", "Player", "Current", "Reason" };
    private const int ColDate = 0, ColTime = 1, ColMatchup = 2, ColTeam = 3, ColPlayer = 4, ColStatus = 5, ColReason = 6;

    private static readonly Dictionary<string, string> TeamCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Atlanta Hawks"] = "ATL", ["Boston Celtics"] = "BOS", ["Brooklyn Nets"] = "BKN", ["Charlotte Hornets"] = "CHA",
        ["Chicago Bulls"] = "CHI", ["Cleveland Cavaliers"] = "CLE", ["Dallas Mavericks"] = "DAL", ["Denver Nuggets"] = "DEN",
        ["Detroit Pistons"] = "DET", ["Golden State Warriors"] = "GSW", ["Houston Rockets"] = "HOU", ["Indiana Pacers"] = "IND",
        ["LA Clippers"] = "LAC", ["Los Angeles Clippers"] = "LAC", ["Los Angeles Lakers"] = "LAL", ["Memphis Grizzlies"] = "MEM",
        ["Miami Heat"] = "MIA", ["Milwaukee Bucks"] = "MIL", ["Minnesota Timberwolves"] = "MIN", ["New Orleans Pelicans"] = "NOP",
        ["New York Knicks"] = "NYK", ["Oklahoma City Thunder"] = "OKC", ["Orlando Magic"] = "ORL", ["Philadelphia 76ers"] = "PHI",
        ["Phoenix Suns"] = "PHX", ["Portland Trail Blazers"] = "POR", ["Sacramento Kings"] = "SAC", ["San Antonio Spurs"] = "SAS",
        ["Toronto Raptors"] = "TOR", ["Utah Jazz"] = "UTA", ["Washington Wizards"] = "WAS"
    };

    public DateTime? ReportTimeEastern { get; set; }
    public List<NbaInjuryEntry> Entries { get; set; } = new();

    public static string ReportUrl(string format, DateTime eastern) =>
        string.Format(CultureInfo.InvariantCulture, format, eastern);

    public static DateTime EasternNow()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
    }

    public List<NbaInjuryEntry> Upcoming(DateTime nowEastern)
    {
        var notStarted = Entries.Where(e => e.GameStartEastern > nowEastern).ToList();
        var nextGame = notStarted
            .GroupBy(e => e.Team)
            .ToDictionary(g => g.Key, g => g.Min(e => e.GameStartEastern));
        return notStarted.Where(e => e.GameStartEastern == nextGame[e.Team]).ToList();
    }

    public static string FullName(string listed)
    {
        var parts = listed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return listed.Trim();
        var first = parts[^1];
        var last = parts[0];
        var suffix = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
        return string.Join(" ", new[] { first, last, suffix }.Where(s => s.Length > 0));
    }

    public static NbaInjuryReport Parse(byte[] pdf)
    {
        var report = new NbaInjuryReport();
        var records = new List<Row>();
        var carryToNext = new List<Fragment>();
        double[]? starts = null;

        using var doc = PdfDocument.Open(pdf);
        var pageIndex = 0;
        foreach (var page in doc.GetPages())
        {
            var lines = Lines(page.Letters);
            starts ??= FindColumns(lines);
            if (starts == null)
                throw new InvalidDataException("Could not find the column headers on the first page.");

            var pageRows = new List<Row>();
            var fragments = new List<Fragment>();

            foreach (var line in lines)
            {
                var plain = string.Concat(line.Letters.Select(l => l.Value)).Replace(" ", "");
                if (plain.StartsWith("InjuryReport:", StringComparison.Ordinal))
                {
                    report.ReportTimeEastern ??= ParseReportTime(Text(line.Letters));
                    continue;
                }
                if (Regex.IsMatch(plain, @"^Page\d+of\d+$") || plain.StartsWith("GameDateGameTime", StringComparison.Ordinal))
                    continue;

                var cells = Cells(line.Letters, starts);
                if (cells[ColPlayer].Length > 0 || cells[ColReason] == NotYetSubmittedText)
                {
                    pageRows.Add(new Row { Cells = cells, Page = pageIndex, Y = line.Y });
                }
                else if (cells[ColReason].Length > 0)
                {
                    fragments.Add(new Fragment { Page = pageIndex, Y = line.Y, Text = cells[ColReason] });
                }
            }

            if (pageRows.Count > 0 && carryToNext.Count > 0)
            {
                pageRows[0].Extra.AddRange(carryToNext);
                carryToNext.Clear();
            }

            foreach (var f in fragments)
            {
                if (pageRows.Count == 0)
                {
                    if (records.Count > 0) records[^1].Extra.Add(f); else carryToNext.Add(f);
                    continue;
                }

                var nearest = pageRows.OrderBy(r => Math.Abs(r.Y - f.Y)).First();
                var distance = Math.Abs(nearest.Y - f.Y);
                if (distance > WrapDistance && nearest == pageRows[0] && f.Y > nearest.Y && records.Count > 0)
                    records[^1].Extra.Add(f);
                else if (distance > WrapDistance && nearest == pageRows[^1] && f.Y < nearest.Y)
                    carryToNext.Add(f);
                else
                    nearest.Extra.Add(f);
            }

            records.AddRange(pageRows);
            pageIndex++;
        }

        string date = "", time = "", matchup = "", team = "";
        foreach (var r in records)
        {
            if (r.Cells[ColDate].Length > 0) date = r.Cells[ColDate];
            if (r.Cells[ColTime].Length > 0) time = r.Cells[ColTime];
            if (r.Cells[ColMatchup].Length > 0) matchup = r.Cells[ColMatchup];
            if (r.Cells[ColTeam].Length > 0) team = r.Cells[ColTeam];

            var reasonParts = new List<Fragment>();
            if (r.Cells[ColReason].Length > 0) reasonParts.Add(new Fragment { Page = r.Page, Y = r.Y, Text = r.Cells[ColReason] });
            reasonParts.AddRange(r.Extra);
            var reason = string.Join(" ", reasonParts.OrderBy(p => p.Page).ThenByDescending(p => p.Y).Select(p => p.Text));

            var gameDate = DateTime.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
            var listed = r.Cells[ColPlayer];

            report.Entries.Add(new NbaInjuryEntry
            {
                GameDate = gameDate,
                GameTime = time,
                GameStartEastern = GameStart(gameDate, time),
                Matchup = matchup,
                Team = team,
                TeamCode = TeamCodes.TryGetValue(team, out var code) ? code : "",
                ListedName = listed,
                PlayerName = listed.Length > 0 ? FullName(listed) : "",
                Status = r.Cells[ColStatus],
                Reason = reason,
                NotYetSubmitted = listed.Length == 0 && reason == NotYetSubmittedText
            });
        }

        return report;
    }

    private static DateTime GameStart(DateTime gameDate, string time)
    {
        var m = Regex.Match(time, @"(\d{1,2}):(\d{2})");
        if (gameDate == DateTime.MinValue || !m.Success) return gameDate;
        var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour < 11) hour += 12;
        return gameDate.Date.AddHours(hour).AddMinutes(minute);
    }

    private static DateTime? ParseReportTime(string title)
    {
        var m = Regex.Match(title, @"(\d{1,2}/\d{1,2}/\d{2,4})\s+(\d{1,2}:\d{2})\s*(AM|PM)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var text = $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value.ToUpperInvariant()}";
        return DateTime.TryParseExact(text, new[] { "MM/dd/yy hh:mm tt", "M/d/yy h:mm tt", "MM/dd/yyyy hh:mm tt", "M/d/yyyy h:mm tt" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    private static double[]? FindColumns(List<Line> lines)
    {
        foreach (var line in lines)
        {
            var words = Words(line.Letters);
            var starts = new List<double>();
            var at = 0;
            foreach (var w in words)
            {
                if (at < HeaderWords.Length && w.Text == HeaderWords[at])
                {
                    starts.Add(w.X);
                    at++;
                }
            }
            if (at == HeaderWords.Length) return starts.ToArray();
        }
        return null;
    }

    private static List<Line> Lines(IReadOnlyList<Letter> letters)
    {
        var lines = new List<Line>();
        foreach (var l in letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
        {
            var y = l.StartBaseLine.Y;
            var line = lines.FirstOrDefault(x => Math.Abs(x.Y - y) < SameLine);
            if (line == null) lines.Add(line = new Line { Y = y });
            line.Letters.Add(l);
        }
        foreach (var line in lines) line.Letters.Sort((a, b) => a.StartBaseLine.X.CompareTo(b.StartBaseLine.X));
        lines.Sort((a, b) => b.Y.CompareTo(a.Y));
        return lines;
    }

    private static List<Word> Words(List<Letter> letters)
    {
        var words = new List<Word>();
        Word? current = null;
        Letter? previous = null;
        foreach (var l in letters)
        {
            if (current == null || previous == null || l.StartBaseLine.X - previous.EndBaseLine.X > SpaceGap)
            {
                current = new Word { X = l.StartBaseLine.X };
                words.Add(current);
            }
            current.Text += l.Value;
            previous = l;
        }
        return words;
    }

    private static string Text(List<Letter> letters) => string.Join(" ", Words(letters).Select(w => w.Text));

    private static string[] Cells(List<Letter> letters, double[] starts)
    {
        var cells = new StringBuilder[starts.Length];
        for (var i = 0; i < cells.Length; i++) cells[i] = new StringBuilder();
        foreach (var w in Words(letters))
        {
            var col = 0;
            for (var i = 0; i < starts.Length; i++)
                if (w.X >= starts[i] - 3) col = i;
            if (cells[col].Length > 0) cells[col].Append(' ');
            cells[col].Append(w.Text);
        }
        return cells.Select(c => c.ToString().Trim()).ToArray();
    }

    private class Line
    {
        public double Y { get; set; }
        public List<Letter> Letters { get; } = new();
    }

    private class Word
    {
        public double X { get; set; }
        public string Text { get; set; } = "";
    }

    private class Fragment
    {
        public int Page { get; set; }
        public double Y { get; set; }
        public string Text { get; set; } = "";
    }

    private class Row
    {
        public string[] Cells { get; set; } = Array.Empty<string>();
        public int Page { get; set; }
        public double Y { get; set; }
        public List<Fragment> Extra { get; } = new();
    }
}
