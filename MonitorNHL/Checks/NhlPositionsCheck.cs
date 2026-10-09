using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Results;
using RotoMonsterExternalAPIs.Client.Services.Providers;

namespace MonitorNHL.Checks;

public class NhlPositionsCheck : MonitorCheck
{
    private readonly NhlContext _nhl;
    private readonly NhlPositionFeeds _feeds = new();

    public NhlPositionsCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromHours(Math.Max(1, nhl.Settings.PositionsIntervalHours));
    }

    public override string Name => "NHL positions";

    public override string Category => "Positions";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var log = new List<string>();
        var problems = new List<string>();
        var settings = _nhl.Settings;

        var fantrax = await _feeds.GetFanTraxPlayersAsync();
        if (!fantrax.Success) return Failed("FanTrax players feed failed.", fantrax.ErrorMessage);
        log.Add($"FanTrax players: {fantrax.Players.Count}");

        ProviderPositionsResult? league = null;
        if (!string.IsNullOrWhiteSpace(settings.FanTraxLeagueId))
            league = await Fetch("FanTrax league", _feeds.GetFanTraxLeaguePositionsAsync(settings.FanTraxLeagueId), log, problems);
        var yahoo = await Fetch("Yahoo", _feeds.GetYahooPlayersAsync(settings.YahooGameKey), log, problems);
        var espn = await Fetch("ESPN", _feeds.GetEspnPlayersAsync(settings.Year + 1), log, problems);

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var sync = new NHLPositionSync(db);
            var data = new NHLDataSync(db);
            Func<string, Task<int?>> teamIdFor = code => data.GetTeamIdAsync(code);
            var seasonId = _nhl.SeasonId;
            var written = 0;

            var fxIds = await Resolve(sync, seasonId, "FanTrax", fantrax, NHLPositionSync.FanTraxProvider, true, teamIdFor, log);
            var fxSource = await SourceId(sync, "FanTrax", NHLPositionSync.FanTraxProvider, problems);
            if (fxSource.HasValue)
            {
                var fxPositions = league ?? fantrax;
                if (league == null) log.Add("FanTrax positions taken from the players feed (no FanTrax league id set)");
                written += await SyncSource(sync, "FanTrax", seasonId, fxSource.Value, fxPositions, fxIds, log);
            }

            var yahooIds = new Dictionary<string, int>();
            if (yahoo != null)
            {
                yahooIds = await Resolve(sync, seasonId, "Yahoo", yahoo, NHLPositionSync.YahooProvider, false, teamIdFor, log);
                var source = await SourceId(sync, "Yahoo", NHLPositionSync.YahooProvider, problems);
                if (source.HasValue) written += await SyncSource(sync, "Yahoo", seasonId, source.Value, yahoo, yahooIds, log);
            }

            var espnIds = new Dictionary<string, int>();
            if (espn != null)
            {
                espnIds = await Resolve(sync, seasonId, "ESPN", espn, NHLPositionSync.EspnProvider, false, teamIdFor, log);
                var source = await SourceId(sync, "ESPN", NHLPositionSync.EspnProvider, problems);
                if (source.HasValue) written += await SyncSource(sync, "ESPN", seasonId, source.Value, espn, espnIds, log);
            }

            var defaults = await sync.FillMissingDefaultsAsync(new[]
            {
                await sync.PrimaryPositionsAsync(fantrax.Players, fxIds),
                yahoo == null ? new Dictionary<int, int>() : await sync.PrimaryPositionsAsync(yahoo.Players, yahooIds),
                espn == null ? new Dictionary<int, int>() : await sync.PrimaryPositionsAsync(espn.Players, espnIds)
            });
            log.Add($"Default positions: {defaults.Created} filled, {defaults.Skipped} players still without one");

            var message = $"{written} provider positions changed, {defaults.Created} default positions filled.";
            var details = string.Join("\n", problems.Concat(log));
            return problems.Count > 0 ? Attention(message + $" {problems.Count} problem(s).", details) : Ok(message, details);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
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

    private static async Task<int?> SourceId(NHLPositionSync sync, string label, int providerId, List<string> problems)
    {
        var id = await sync.GetPositionSourceIdAsync(providerId);
        if (id == null) problems.Add($"No PositionSources row for {label} (FantasyProviderId {providerId})");
        return id;
    }

    private static async Task<Dictionary<string, int>> Resolve(NHLPositionSync sync, int seasonId, string label, ProviderPositionsResult feed,
        int providerId, bool useSharedIds, Func<string, Task<int?>> teamIdFor, List<string> log)
    {
        var result = new NHLSyncResult();
        var ids = await sync.ResolveAsync(seasonId, feed.Players, providerId, useSharedIds, true, teamIdFor, result);
        log.Add($"{label} ids: {ids.Count} matched, {result.Created} new mappings, {result.Skipped} unmatched" +
                (result.Notes.Count > 0 ? " (" + string.Join("; ", result.Notes) + ")" : ""));
        return ids;
    }

    private static async Task<int> SyncSource(NHLPositionSync sync, string label, int seasonId, int sourceId, ProviderPositionsResult feed,
        Dictionary<string, int> ids, List<string> log)
    {
        var result = await sync.SyncSourceAsync(seasonId, sourceId, feed.Players, ids);
        log.Add(NhlContext.Summarize(result, $"{label} positions"));
        return result.Created + result.Updated;
    }
}
