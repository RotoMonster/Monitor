using System;
using Avalonia.Media;
using Monitor.Core;

namespace MonitorNFL.ViewModels;

public class CheckRowViewModel : ViewModelBase
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#c0392b"));
    private static readonly IBrush AttentionBrush = new SolidColorBrush(Color.Parse("#d68910"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#1e8449"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#7f8c8d"));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.Parse("#95a5a6"));

    public CheckRowViewModel(MonitorCheck check)
    {
        Check = check;
        TogglePauseCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
    }

    public MonitorCheck Check { get; }

    public RelayCommand TogglePauseCommand { get; }

    /// <summary>Raised when a check is paused or resumed, so it can be logged.</summary>
    public event EventHandler<bool>? PauseChanged;

    public string Name => Check.Name;

    public string Category => Check.Category;

    public bool IsEnabled
    {
        get => Check.IsEnabled;
        set
        {
            if (Check.IsEnabled == value) return;

            Check.IsEnabled = value;

            Raise(nameof(IsEnabled));
            Raise(nameof(PauseButtonText));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(RowOpacity));

            PauseChanged?.Invoke(this, value);
        }
    }

    public string PauseButtonText => IsEnabled ? "Pause" : "Resume";

    /// <summary>Paused rows are dimmed so the running ones stand out.</summary>
    public double RowOpacity => IsEnabled ? 1.0 : 0.5;

    public string IntervalText => Check.Interval.TotalMinutes >= 1
        ? $"every {Check.Interval.TotalMinutes:0} min"
        : $"every {Check.Interval.TotalSeconds:0} sec";

    public string LastRunText => Check.LastRun == null
        ? "never run"
        : Check.LastRun.Value.ToString("h:mm:ss tt");

    public string StatusText
    {
        get
        {
            if (!Check.IsEnabled) return "paused";
            if (Check.LastResult == null) return "waiting";
            if (!Check.LastResult.Success) return "error";
            return Check.LastResult.NeedsAttention ? "attention" : "ok";
        }
    }

    public IBrush StatusBrush => StatusText switch
    {
        "paused" => PausedBrush,
        "error" => ErrorBrush,
        "attention" => AttentionBrush,
        "ok" => OkBrush,
        _ => IdleBrush
    };

    public string MessageText => Check.LastResult?.Message ?? "";

    public void Refresh()
    {
        Raise(nameof(LastRunText));
        Raise(nameof(StatusText));
        Raise(nameof(StatusBrush));
        Raise(nameof(MessageText));
    }
}
