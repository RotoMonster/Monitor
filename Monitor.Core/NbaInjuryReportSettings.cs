namespace Monitor.Core;

public class NbaInjuryReportSettings
{
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 15;

    public int LookBackHours { get; set; } = 6;

    public string UrlFormat { get; set; } = "https://ak-static.cms.nba.com/referee/injury/Injury-Report_{0:yyyy-MM-dd}_{0:hh_mmtt}.pdf";
}
