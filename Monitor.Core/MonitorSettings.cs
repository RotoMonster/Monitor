namespace Monitor.Core;

public class MonitorSettings
{
    public TwitterApiSettings TwitterApi { get; set; } = new();

    public AlertSettings Alerts { get; set; } = new();

    public ProjectionTrackingSettings ProjectionTracking { get; set; } = new();
}

public class TwitterApiSettings
{
    public string BaseUrl { get; set; } = "https://twitter.rotomonster.com";
    public string ApiKey { get; set; } = "";

    /// <summary>List to pull for this app's sport.</summary>
    public long ListId { get; set; }

    public int IntervalMinutes { get; set; } = 10;

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

    /// <summary>Local hour the nightly run is allowed to start.</summary>
    public int RunAtHour { get; set; } = 3;

    public List<ProjectionPass> Passes { get; set; } = new();
}

public class ProjectionPass
{
    public string FunctionName { get; set; } = "";

    public string ReviewFunctionName { get; set; } = "";

    public List<int> IgnorePlayerIds { get; set; } = new();
}
