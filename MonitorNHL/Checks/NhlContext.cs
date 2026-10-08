using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Providers;
using RotoMonsterExternalAPIs.Client.Services.Providers;

namespace MonitorNHL.Checks;

public class NhlContext
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly Dictionary<string, string?> _lastUpdated = new();

    public readonly SemaphoreSlim StatsWriteLock = new(1, 1);

    public NhlContext(NhlSettings settings, HttpClient http)
    {
        Settings = settings;
        Http = http;
        Nhl = new NhlApiProvider(http);
    }

    public NhlSettings Settings { get; }
    public HttpClient Http { get; }
    public HashSet<int> InjuredPlayerIds { get; set; } = new();
    public NhlApiProvider Nhl { get; }
    public SportsDataSport Sport => SportsDataSport.NHL;
    public int SeasonId => NHLDataSync.SeasonIdFor(Settings.Year);
    public string SeasonKey => NHLDataSync.SeasonKey(Settings.Year);

    public RMDBContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RMDBContext>()
            .UseSqlServer(Settings.ConnectionString)
            .Options;
        return new RMDBContext(options);
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

    public static string Summarize(NHLSyncResult result, string prefix, int maxNotes = 20)
    {
        var lines = new List<string> { $"{prefix}: {result}" };
        lines.AddRange(result.Notes.Take(maxNotes).Select(n => "  " + n));
        if (result.Notes.Count > maxNotes) lines.Add($"  ...and {result.Notes.Count - maxNotes} more");
        return string.Join("\n", lines);
    }
}
