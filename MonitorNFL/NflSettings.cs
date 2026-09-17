using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace MonitorNFL;

public class NflSettings
{
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
