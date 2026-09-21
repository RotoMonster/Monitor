using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;

namespace MonitorNFL.Checks;

public class NflRostersCheck : MonitorCheck
{
    private readonly NflContext _nfl;

    public NflRostersCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromMinutes(Math.Max(5, nfl.Settings.RostersIntervalMinutes));
    }

    public override string Name => "NFL rosters";

    public override string Category => "MySportsFeeds";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var players = await _nfl.MySportsFeeds.GetPlayersAsync(_nfl.Sport, _nfl.LastUpdated("players"));
        if (!players.Success) return Failed("Players feed failed.", players.ErrorMessage);
        if (players.NotModified) return Ok("Rosters unchanged.");

        using var db = _nfl.CreateDb();
        var sync = new NFLDataSync(db);
        foreach (var id in _nfl.Settings.IgnoredMySportsFeedsIds) sync.IgnoredProviderIds.Add(id);

        var result = new NFLSyncResult();
        var map = await sync.SyncPlayersAsync(_nfl.SeasonId, players.Players, result);

        _nfl.SetLastUpdated("players", players.LastUpdatedOn);

        var review = result.Notes.Where(n => n.StartsWith("Needs review")).ToList();
        var created = result.Notes.Count(n => n.StartsWith("Created"));
        var details = string.Join("\n", review.Concat(result.Notes.Where(n => !n.StartsWith("Needs review"))));
        var message = $"{map.Count} players mapped, {created} created, {review.Count} need review.";

        return created > 0 ? Attention(message, details) : Ok(message, details);
    }
}
