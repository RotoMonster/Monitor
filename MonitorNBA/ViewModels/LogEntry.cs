namespace MonitorNBA.ViewModels;

public enum LogLevel
{
    Info,
    Attention,
    Error
}

public class LogEntry
{
    public LogEntry(string text, LogLevel level)
    {
        Text = text;
        Level = level;
    }

    public string Text { get; }

    public LogLevel Level { get; }

    public bool IsError => Level == LogLevel.Error;

    public bool IsAttention => Level == LogLevel.Attention;
}
