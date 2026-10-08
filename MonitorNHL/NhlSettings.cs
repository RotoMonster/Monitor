namespace MonitorNHL;

public class NhlSettings
{
    public string ConnectionString { get; set; } = "";
    public int Year { get; set; } = 2026;
    public int ScheduleIntervalHours { get; set; } = 24;
    public int RostersIntervalMinutes { get; set; } = 60;
    public int InjuriesIntervalMinutes { get; set; } = 60;
    public int GoaliesIntervalMinutes { get; set; } = 60;
    public int GoalieDaysAhead { get; set; } = 14;
    public double GoalieStarterShare { get; set; } = 0.62;
    public int BoxScoreIntervalSeconds { get; set; } = 60;
    public int BoxScoreMaxDaysPerRun { get; set; } = 5;
    public int ReprocessHour { get; set; } = 4;
    public int ReprocessPasses { get; set; } = 2;
    public int ReprocessMaxAgeDays { get; set; } = 7;
    public int ReprocessIntervalMinutes { get; set; } = 60;
}
