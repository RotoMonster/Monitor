using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace MonitorNFL;

public class NflSettings
{
    public int InjuriesIntervalMinutes { get; set; } = 60;

    public int ReprocessHour { get; set; } = 4;
    public int ReprocessPasses { get; set; } = 2;
    public int ReprocessMaxAgeDays { get; set; } = 7;
    public int ReprocessIntervalMinutes { get; set; } = 60;

    public System.Collections.Generic.List<string> IgnoredMySportsFeedsIds { get; set; } = new() { "7826", "7706" };

    public string BoxScoreCacheFolder { get; set; } = "";
    public bool BoxScoreForceReload { get; set; }

    public int PositionsIntervalHours { get; set; } = 24;
    public string FanTraxLeagueId { get; set; } = "5uueprb5mqk851en";
    public string EspnLeagueId { get; set; } = "762165865";
    public string YahooGameKey { get; set; } = "470";

    public string ConnectionString { get; set; } = "";
    public string MySportsFeedsKey { get; set; } = "";
    public int Year { get; set; } = 2026;
    public string Season { get; set; } = "2026-regular";
    public int ScheduleIntervalHours { get; set; } = 24;
    public int RostersIntervalMinutes { get; set; } = 60;
    public int BoxScoreIntervalSeconds { get; set; } = 60;
    public int NflverseIntervalHours { get; set; } = 24;
    public int NflverseWeeksBack { get; set; } = 2;
}
