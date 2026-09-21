using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Monitor.Core;
using MonitorNFL.Checks;

namespace MonitorNFL.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly CheckRunner _runner;
    private readonly AlertNotifier? _notifier;
    private readonly MonitorStateStore _stateStore = MonitorStateStore.Default();
    private readonly MonitorState _state;
    private readonly CancellationTokenSource _cts = new();

    private bool _isRunning;
    private string _statusLine = "Idle.";

    public MainViewModel(MonitorSettings settings, NflSettings nfl)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        _state = _stateStore.Load();

        var context = new NflContext(nfl, http);

        var checks = new List<MonitorCheck>
        {
            new NflScheduleCheck(context),
            new NflRostersCheck(context),
            new NflBoxScoreCheck(context),
            new NflverseCheck(context)
        };

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

        MissingSettings = new List<string>();
        if (string.IsNullOrWhiteSpace(nfl.ConnectionString)) MissingSettings.Add("Nfl:ConnectionString");
        if (string.IsNullOrWhiteSpace(nfl.MySportsFeedsKey)) MissingSettings.Add("Nfl:MySportsFeedsKey");
    }

    private List<string> MissingSettings { get; }

    public ObservableCollection<CheckRowViewModel> Rows { get; }

    public ObservableCollection<LogEntry> Log { get; } = new();

    public ObservableCollection<LogEntry> ErrorLog { get; } = new();

    private int _errorCount;

    public string ErrorTabHeader => _errorCount == 0 ? "Errors" : $"Errors ({_errorCount})";

    public RelayCommand RunAllCommand { get; }

    public string Title => "MonitorNFL";

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

    public void Start()
    {
        Append("Monitor started.");

        if (MissingSettings.Count > 0)
        {
            Append($"X Missing settings in appsettings.local.json: {string.Join(", ", MissingSettings)}. Checks not started.");
            StatusLine = "Not configured.";
            return;
        }

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
        if (MissingSettings.Count > 0) return;

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

            var level = !e.Result.Success
                ? LogLevel.Error
                : (e.Result.NeedsAttention ? LogLevel.Attention : LogLevel.Info);

            var prefix = level == LogLevel.Error ? "X" : (level == LogLevel.Attention ? "!" : " ");

            var lines = new List<string> { $"{prefix} [{e.Result.RanAt:h:mm:ss tt}] {e.Check.Name}: {e.Result.Message}" };

            if (level != LogLevel.Info && !string.IsNullOrWhiteSpace(e.Result.Details))
            {
                foreach (var line in e.Result.Details.Split('\n').Take(15))
                    lines.Add("      " + line.TrimEnd());
            }

            for (var i = lines.Count - 1; i >= 0; i--)
                Append(lines[i], level);
        });

        if (_notifier != null)
        {
            _ = _notifier.OnCheckCompletedAsync(e.Check, e.Result, _cts.Token);
        }
    }

    private void Append(string line, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(line, level);
        Log.Insert(0, entry);

        while (Log.Count > 500)
        {
            Log.RemoveAt(Log.Count - 1);
        }

        if (level == LogLevel.Error)
        {
            if (!line.StartsWith("      ")) _errorCount++;
            ErrorLog.Insert(0, entry);

            while (ErrorLog.Count > 300)
            {
                ErrorLog.RemoveAt(ErrorLog.Count - 1);
            }

            Raise(nameof(ErrorTabHeader));
        }
    }
}
