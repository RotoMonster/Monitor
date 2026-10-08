using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;

namespace MonitorNHL.Checks;

public class NhlRostersCheck : MonitorCheck
{
    private readonly NhlContext _nhl;

    public NhlRostersCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromMinutes(Math.Max(5, nhl.Settings.RostersIntervalMinutes));
    }

    public override string Name => "NHL rosters";

    public override string Category => "NHL API";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var players = await _nhl.Nhl.GetPlayersAsync(_nhl.Sport, _nhl.LastUpdated("players"));
        if (!players.Success) return Failed("Rosters feed failed.", players.ErrorMessage);
        if (players.NotModified) return Ok("Rosters unchanged.");

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var sync = new NHLDataSync(db);

            var result = new NHLSyncResult();
            var map = await sync.SyncPlayersAsync(_nhl.SeasonId, players.Players, result);

            _nhl.SetLastUpdated("players", players.LastUpdatedOn);

            var review = result.Notes.Where(n => n.StartsWith("Needs review")).ToList();
            var created = result.Notes.Count(n => n.StartsWith("Created"));
            var details = string.Join("\n", review.Concat(result.Notes.Where(n => !n.StartsWith("Needs review"))));
            var message = $"{players.Players.Count} on rosters, {map.Count} linked, {created} created, {review.Count} need review.";

            return created > 0 || review.Count > 0 ? Attention(message, details) : Ok(message, details);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }
}
