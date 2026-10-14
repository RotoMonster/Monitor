namespace Monitor.Core;

/// <summary>
/// Turns check results into alerts, but only when the state actually changes.
///
/// A check that fails every 10 minutes for two hours would otherwise send a
/// dozen identical notifications, which is how people end up turning the app
/// off. So: alert when something starts failing, stay quiet while it keeps
/// failing, and say so once when it recovers.
///
/// State is persisted, so a restart during an outage still produces the
/// recovery message rather than coming back silently.
/// </summary>
public class AlertNotifier
{
    private readonly AlertClient _client;
    private readonly AlertSettings _settings;
    private readonly MonitorStateStore? _store;
    private readonly MonitorState _state;
    private readonly Dictionary<string, CheckState> _lastState = new();
    private readonly Dictionary<string, int> _failuresInARow = new();

    public AlertNotifier(
        AlertClient client,
        AlertSettings settings,
        MonitorStateStore? store = null,
        MonitorState? state = null)
    {
        _client = client;
        _settings = settings;
        _store = store;
        _state = state ?? new MonitorState();

        foreach (var entry in _state.LastState)
        {
            if (Enum.TryParse<CheckState>(entry.Value, out var parsed))
                _lastState[entry.Key] = parsed;
        }
    }

    /// <summary>Raised when an alert is sent, or fails to send. For the activity log.</summary>
    public event EventHandler<string>? AlertLogged;

    private enum CheckState
    {
        Unknown,
        Ok,
        Attention,
        Failed
    }

    /// <summary>Hook this to CheckRunner.CheckCompleted.</summary>
    public async Task OnCheckCompletedAsync(MonitorCheck check, CheckResult result, CancellationToken ct = default)
    {
        if (!_client.IsConfigured) return;

        var current = StateOf(result);
        var previous = _lastState.TryGetValue(check.Name, out var known) ? known : CheckState.Unknown;

        if (current == CheckState.Failed)
        {
            var inARow = (_failuresInARow.TryGetValue(check.Name, out var n) ? n : 0) + 1;
            _failuresInARow[check.Name] = inARow;
            if (previous != CheckState.Failed && inARow < check.FailuresBeforeAlert) return;
        }
        else
        {
            _failuresInARow[check.Name] = 0;
        }

        if (current == previous) return;

        _lastState[check.Name] = current;
        _state.LastState[check.Name] = current.ToString();
        _store?.Save(_state);

        // Nothing to announce on the very first run of a healthy check.
        if (previous == CheckState.Unknown && current == CheckState.Ok) return;

        string title;
        bool notify;

        if (current == CheckState.Failed)
        {
            title = $"{check.Name} failed";
            notify = true;
        }
        else if (current == CheckState.Attention)
        {
            title = $"{check.Name} needs attention";
            notify = true;
        }
        else
        {
            // Recovered. Worth recording, but not worth waking anyone for.
            title = $"{check.Name} recovered";
            notify = _settings.NotifyOnRecovery;
        }

        var error = await _client.SendAsync(title, result.Message, notify, ct: ct);

        AlertLogged?.Invoke(this, error == null
            ? $"Alert sent: {title}"
            : $"Alert FAILED to send ({error}): {title}");
    }

    private static CheckState StateOf(CheckResult result)
    {
        if (!result.Success) return CheckState.Failed;
        return result.NeedsAttention ? CheckState.Attention : CheckState.Ok;
    }
}
