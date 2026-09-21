using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Providers;
using RotoMonsterExternalAPIs.Client.Models.Results;

namespace MonitorNFL.Checks;

public class NflPositionsCheck : MonitorCheck
{
    private readonly NflContext _nfl;

    public NflPositionsCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromHours(Math.Max(1, nfl.Settings.PositionsIntervalHours));
    }

    public override string Name => "NFL positions";

    public override string Category => "Positions";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var feeds = _nfl.PositionFeeds;
        var log = new List<string>();
        var problems = new List<string>();

        var fantrax = await feeds.GetFanTraxPlayersAsync();
        if (!fantrax.Success) return Failed("FanTrax players feed failed.", fantrax.ErrorMessage);
        log.Add($"FanTrax players: {fantrax.Players.Count}");

        var league = await Fetch("FanTrax league", feeds.GetFanTraxLeaguePositionsAsync(_nfl.Settings.FanTraxLeagueId), log, problems);
        var yahoo = await Fetch("Yahoo", feeds.GetYahooPlayersAsync(_nfl.Settings.YahooGameKey), log, problems);
        var espn = await Fetch("ESPN", feeds.GetEspnPlayersAsync(_nfl.Settings.Year, _nfl.Settings.EspnLeagueId), log, problems);
        var nflverse = await Fetch("nflverse", feeds.GetNflversePlayersAsync(), log, problems);

        using var db = _nfl.CreateDb();
        var sync = new NFLPositionSync(db);
        var seasonId = _nfl.SeasonId;
        var written = 0;

        var fxIds = await Resolve(sync, "FanTrax", fantrax, NFLPositionSync.FanTraxProvider, true, log);

        if (league != null)
            written += await SyncSource(sync, "FanTrax", seasonId, NFLPositionSync.FanTraxSource, league, fxIds, log);

        Dictionary<string, int> yahooIds = new();
        if (yahoo != null)
        {
            yahooIds = await Resolve(sync, "Yahoo", yahoo, NFLPositionSync.YahooProvider, false, log);
            written += await SyncSource(sync, "Yahoo", seasonId, NFLPositionSync.YahooSource, yahoo, yahooIds, log);
        }

        Dictionary<string, int> espnIds = new();
        if (espn != null)
        {
            espnIds = await Resolve(sync, "ESPN", espn, NFLPositionSync.EspnProvider, false, log);
            written += await SyncSource(sync, "ESPN", seasonId, NFLPositionSync.EspnSource, espn, espnIds, log);
        }

        Dictionary<string, int> nflverseIds = new();
        if (nflverse != null)
        {
            var nflverseProvider = await new NFLDataSync(db).GetProviderIdAsync(NFLDataSync.NflverseProviderName);
            nflverseIds = await Resolve(sync, "nflverse", nflverse, nflverseProvider, false, log);
        }

        var defaults = await sync.FillMissingDefaultsAsync(new[]
        {
            NFLPositionSync.PrimaryPositions(fantrax.Players, fxIds),
            nflverse == null ? new Dictionary<int, int>() : NFLPositionSync.PrimaryPositions(nflverse.Players, nflverseIds),
            yahoo == null ? new Dictionary<int, int>() : NFLPositionSync.PrimaryPositions(yahoo.Players, yahooIds),
            espn == null ? new Dictionary<int, int>() : NFLPositionSync.PrimaryPositions(espn.Players, espnIds)
        });
        log.Add($"Default positions: {defaults.Created} filled, {defaults.Skipped} players still without one");

        var message = $"{defaults.Created} default positions filled, {written} provider positions changed, {defaults.Skipped} players still without a default.";
        var details = string.Join("\n", problems.Concat(log));

        return problems.Count > 0 ? Attention(message + $" {problems.Count} feed(s) failed.", details) : Ok(message, details);
    }

    private static async Task<ProviderPositionsResult?> Fetch(string label, Task<ProviderPositionsResult> task, List<string> log, List<string> problems)
    {
        var r = await task;
        if (!r.Success)
        {
            problems.Add($"{label} failed: {r.ErrorMessage}");
            return null;
        }
        log.Add($"{label}: {r.Players.Count}");
        return r;
    }

    private static async Task<Dictionary<string, int>> Resolve(NFLPositionSync sync, string label, ProviderPositionsResult feed, int providerId, bool useSharedIds, List<string> log)
    {
        var result = new NFLSyncResult();
        var ids = await sync.ResolveAsync(feed.Players, providerId, useSharedIds, result);
        log.Add($"{label} ids: {ids.Count} matched, {result.Created} new mappings, {result.Skipped} unmatched");
        return ids;
    }

    private static async Task<int> SyncSource(NFLPositionSync sync, string label, int seasonId, int sourceId, ProviderPositionsResult feed, Dictionary<string, int> ids, List<string> log)
    {
        var result = await sync.SyncSourceAsync(seasonId, sourceId, feed.Players, ids);
        log.Add(NflContext.Summarize(result, $"{label} positions"));
        return result.Created + result.Updated;
    }
}
