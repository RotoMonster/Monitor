namespace Monitor.Core;

public class MonitorSettings
{
    public TwitterApiSettings TwitterApi { get; set; } = new();

    public AlertSettings Alerts { get; set; } = new();

    public ProjectionTrackingSettings ProjectionTracking { get; set; } = new();

    public AdvancedOwnershipSettings AdvancedOwnership { get; set; } = new();

    public IndexMaintenanceSettings IndexMaintenance { get; set; } = new();

    public ReturnDateSettings ReturnDates { get; set; } = new();

    public SeqSettings Seq { get; set; } = new();
}

public class SeqSettings
{
    public bool Enabled { get; set; }

    public string Url { get; set; } = "http://localhost:5341";

    public string ApiKey { get; set; } = "";

    public string App { get; set; } = "";

    public string MinimumLevel { get; set; } = "Information";

    public int FlushSeconds { get; set; } = 5;
}

public class TwitterApiSettings
{
    public string BaseUrl { get; set; } = "https://twitter.rotomonster.com";
    public string ApiKey { get; set; } = "";

    /// <summary>List to pull for this app's sport.</summary>
    public long ListId { get; set; }

    public int IntervalMinutes { get; set; } = 10;

    public int FailuresBeforeAlert { get; set; } = 3;

    public string ProviderApiKey { get; set; } = "";

    public string ProviderInfoUrl { get; set; } = "https://api.twitterapi.io/oapi/my/info";

    public double CreditsAlertBelowDollars { get; set; } = 5;

    public int CreditsIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// When set above zero, this wins over IntervalMinutes. For in-season
    /// pulls that need to run faster than once a minute.
    /// </summary>
    public int IntervalSeconds { get; set; }

    /// <summary>The effective interval, whichever was set.</summary>
    public TimeSpan Interval => IntervalSeconds > 0
        ? TimeSpan.FromSeconds(IntervalSeconds)
        : TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
}

public class AlertSettings
{
    /// <summary>Off by default, so a monitor without alert settings stays silent.</summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://alerts.rotomonster.com";

    public string ApiKey { get; set; } = "";

    /// <summary>1 basketball, 2 baseball. Matches the Twitter API's convention.</summary>
    public int SportId { get; set; } = 1;

    /// <summary>
    /// Failures always notify. Recoveries are quieter by default - they still
    /// appear in the app, they just don't buzz the phone.
    /// </summary>
    public bool NotifyOnRecovery { get; set; }

    public bool StaffOnly { get; set; } = true;
}

public class ProjectionTrackingSettings
{
    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = "";

    public string BaseUrl { get; set; } = "https://server.rotomonster.com";

    public string ApiKey { get; set; } = "";

    public int SportId { get; set; } = 1;

    public int ApiSourceSetupId { get; set; } = 3;

    /// <summary>The pseudo-team holding free agents, skipped on sync.</summary>
    public int ExcludeTeamId { get; set; } = 34;

    /// <summary>How long a single pass is allowed to take.</summary>
    public int TimeoutMinutes { get; set; } = 45;

    /// <summary>Local hour the nightly run is allowed to start.</summary>
    public int RunAtHour { get; set; } = 3;

    /// <summary>Status titles treated as injured when IgnoreInjured is set.</summary>
    public List<string> InjuryStatusTitles { get; set; } = new()
        { "Injured", "Out", "Out for season", "Doubtful" };

    public List<ProjectionPass> Passes { get; set; } = new();
}

public class ProjectionPass
{
    public string FunctionName { get; set; } = "";

    public string ReviewFunctionName { get; set; } = "";

    public List<int> IgnorePlayerIds { get; set; } = new();

    /// <summary>
    /// Adds every player with an active injury status to the ignore list, so the
    /// pass only sees players we believe are healthy.
    /// </summary>
    public bool IgnoreInjured { get; set; }

    /// <summary>Only sends players who have an injury note.</summary>
    public bool InjuredOnly { get; set; }
}

public class AdvancedOwnershipSettings
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://basketballmonster.com";

    public string ApiKey { get; set; } = "";

    public string CategoriesCode { get; set; } = "";

    public int LeagueCount { get; set; } = 100;

    public int PauseSeconds { get; set; } = 3;

    public int IntervalMinutes { get; set; } = 60;

    public int TimeoutMinutes { get; set; } = 30;
}

public class IndexMaintenanceSettings
{
    public bool Enabled { get; set; }

    public List<string> ConnectionStrings { get; set; } = new();

    public int RunAtHour { get; set; } = 4;

    public int RunUntilHour { get; set; } = 6;

    public double ReorganizeAbove { get; set; } = 5;

    public double RebuildAbove { get; set; } = 30;

    public int MinPageCount { get; set; } = 1000;

    public int MaxMinutes { get; set; } = 60;
}

public class ReturnDateSettings
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://server.rotomonster.com";

    public string ApiKey { get; set; } = "";

    public int ApiSourceSetupId { get; set; } = 1;

    public string FunctionName { get; set; } = "GetEstimatedReturnDate";

    public int IntervalMinutes { get; set; } = 60;

    public int MaxPerRun { get; set; } = 20;

    public int MinConfidence { get; set; } = 1;

    public bool NightlyRefresh { get; set; } = true;

    public int RefreshAtHour { get; set; } = 3;

    public int MaxNightly { get; set; } = 300;

    public bool AlertChanges { get; set; } = true;
}
