using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Monitor.Core;
using Monitor.Core.Checks;

namespace MonitorNBA.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly CheckRunner _runner;
    private readonly AlertNotifier? _notifier;
    private readonly MonitorStateStore _stateStore = MonitorStateStore.Default();
    private readonly MonitorState _state;
    private readonly CancellationTokenSource _cts = new();

    private bool _isRunning;
    private string _statusLine = "Idle.";

    public MainViewModel(MonitorSettings settings)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        _state = _stateStore.Load();

        var checks = new List<MonitorCheck>
        {
            new TwitterIngestCheck(http, settings.TwitterApi),
            new ApiHealthCheck(http, "Twitter API",
                $"{settings.TwitterApi.BaseUrl.TrimEnd('/')}/health")
        };

        if (settings.ProjectionTracking.Enabled)
        {
            checks.Add(new ProjectionTrackingCheck(http, settings.ProjectionTracking));
        }

        // Restore anything that was paused when we last shut down.
        foreach (var check in checks)
        {
            if (_state.Paused.TryGetValue(check.Name, out var paused) && paused)
                check.IsEnabled = false;
        }

        _runner = new CheckRunner(checks);
        _runner.CheckCompleted += OnCheckCompleted;

        var alertClient = new AlertClient(http, settings.Alerts);

        if (alertClient.IsConfigured)
        {
            _notifier = new AlertNotifier(alertClient, settings.Alerts, _stateStore, _state);
            _notifier.AlertLogged += (_, line) => Dispatcher.UIThread.Post(() => Append(line));
        }

        Rows = new ObservableCollection<CheckRowViewModel>(
            checks.Select(c => new CheckRowViewModel(c)));

        foreach (var row in Rows)
        {
            row.PauseChanged += OnPauseChanged;
        }

        RunAllCommand = new RelayCommand(async void () => await RunAllAsync(),
            () => !IsRunning);
    }

    public ObservableCollection<CheckRowViewModel> Rows { get; }

    public ObservableCollection<string> Log { get; } = new();

    public RelayCommand RunAllCommand { get; }

    public string Title => "MonitorNBA";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value)) RunAllCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }

    /// <summary>Starts the scheduled loop. Called once at startup.</summary>
    public void Start()
    {
        Append("Monitor started.");

        if (_notifier == null)
            Append("Alerts are off - no alert settings configured.");

        var pausedCount = Rows.Count(r => !r.IsEnabled);
        if (pausedCount > 0)
            Append($"  {pausedCount} check(s) restored as paused.");

        _ = _runner.RunLoopAsync(_cts.Token);
    }

    public void Stop() => _cts.Cancel();

    public async Task RunAllAsync()
    {
        IsRunning = true;
        StatusLine = "Running all checks...";

        try
        {
            await _runner.RunAllAsync(_cts.Token);
            StatusLine = $"Last full run at {DateTime.Now:h:mm:ss tt}.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnPauseChanged(object? sender, bool isEnabled)
    {
        if (sender is not CheckRowViewModel row) return;

        _state.Paused[row.Name] = !isEnabled;
        _stateStore.Save(_state);

        Append($"  {row.Name} {(isEnabled ? "resumed" : "paused")}.");
    }

    private void OnCheckCompleted(object? sender, CheckCompletedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Check == e.Check);
            row?.Refresh();

            var prefix = e.Result.Success
                ? (e.Result.NeedsAttention ? "!" : " ")
                : "X";

            Append($"{prefix} [{e.Result.RanAt:h:mm:ss tt}] {e.Check.Name}: {e.Result.Message}");

            if (!string.IsNullOrWhiteSpace(e.Result.Details) && (!e.Result.Success || e.Result.NeedsAttention))
            {
                foreach (var line in e.Result.Details.Split('\n').Take(15))
                    Append("      " + line);
            }
        });

        // Fire and forget - a slow alerts API must never hold up the next check.
        if (_notifier != null)
        {
            _ = _notifier.OnCheckCompletedAsync(e.Check, e.Result, _cts.Token);
        }
    }

    private void Append(string line)
    {
        Log.Insert(0, line);

        while (Log.Count > 300)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }
}
