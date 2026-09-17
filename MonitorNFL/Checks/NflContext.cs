using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Providers;
using RotoMonsterExternalAPIs.Client.Services.Providers;

namespace MonitorNFL.Checks;

public class NflContext
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly Dictionary<string, string?> _lastUpdated = new();

    public NflContext(NflSettings settings, HttpClient http)
    {
        Settings = settings;
        MySportsFeeds = new MySportsFeedsProvider(settings.MySportsFeedsKey, http);
        Nflverse = new NflverseProvider(http);
    }

    public NflSettings Settings { get; }
    public MySportsFeedsProvider MySportsFeeds { get; }
    public NflverseProvider Nflverse { get; }
    public SportsDataSport Sport => SportsDataSport.NFL;
    public int SeasonId => NFLDataSync.SeasonIdFor(Settings.Year);

    public RMDBContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RMDBContext>()
            .UseSqlServer(Settings.ConnectionString)
            .Options;
        return new RMDBContext(options);
    }

    private readonly Dictionary<DateTime, DateTime> _refusedDays = new();

    public bool IsRefused(DateTime day)
    {
        return _refusedDays.TryGetValue(day.Date, out var until) && DateTime.UtcNow < until;
    }

    public void MarkRefused(DateTime day)
    {
        _refusedDays[day.Date] = DateTime.UtcNow.AddHours(6);
    }

    public string? LastUpdated(string feed)
    {
        return _lastUpdated.TryGetValue(feed, out var value) ? value : null;
    }

    public void SetLastUpdated(string feed, string? value)
    {
        _lastUpdated[feed] = value;
    }

    public static DateTime NowEastern()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);
    }

    public static DateTime ToEastern(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
    }

    public static string Summarize(NFLSyncResult result, string prefix, int maxNotes = 20)
    {
        var lines = new List<string> { $"{prefix}: {result}" };
        lines.AddRange(result.Notes.Take(maxNotes).Select(n => "  " + n));
        if (result.Notes.Count > maxNotes) lines.Add($"  ...and {result.Notes.Count - maxNotes} more");
        return string.Join("\n", lines);
    }
}
