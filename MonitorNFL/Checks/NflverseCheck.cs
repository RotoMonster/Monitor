using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;

namespace MonitorNFL.Checks;

public class NflverseCheck : MonitorCheck
{
    private readonly NflContext _nfl;

    public NflverseCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromHours(Math.Max(1, nfl.Settings.NflverseIntervalHours));
    }

    public override string Name => "nflverse stats";

    public override string Category => "nflverse";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var games = await _nfl.MySportsFeeds.GetGamesAsync(_nfl.Sport, _nfl.Settings.Season, null);
        if (!games.Success) return Failed("Games feed failed.", games.ErrorMessage);

        var weeks = games.Games
            .Where(g => g.IsFinished)
            .Select(g => g.Week)
            .Distinct()
            .OrderByDescending(w => w)
            .Take(Math.Max(1, _nfl.Settings.NflverseWeeksBack))
            .OrderBy(w => w)
            .ToList();

        if (weeks.Count == 0) return Ok("No finished games yet.");

        using var db = _nfl.CreateDb();
        var sync = new NFLDataSync(db);

        var result = new NFLSyncResult();
        var gameMap = await sync.SyncGamesAsync(_nfl.SeasonId, games.Games, result);

        var log = new List<string>();
        var written = 0;
        var unmatched = 0;

        foreach (var week in weeks)
        {
            var r = await _nfl.Nflverse.GetPlayerGamesAsync(_nfl.Sport, _nfl.Settings.Season, week, null);
            if (!r.Success) return Failed($"nflverse week {week} failed.", r.ErrorMessage);

            if (r.PlayerGames.Count == 0)
            {
                log.Add($"week {week}: not published yet");
                continue;
            }

            var weekGames = NFLDataSync.NflverseGameMap(_nfl.Settings.Year, games.Games.Where(g => g.Week == week), gameMap);
            var ids = new NFLSyncResult();
            var players = await sync.MapByNameAndTeamAsync(_nfl.SeasonId, NFLDataSync.NflverseProviderName, r.PlayerGames, ids);
            var stats = await sync.SyncPlayerGamesAsync(r.PlayerGames, weekGames, players);

            written += stats.Created + stats.Updated;
            unmatched += ids.Skipped;
            log.Add($"week {week}: {r.PlayerGames.Count} lines, {weekGames.Count} games");
            log.Add(NflContext.Summarize(ids, "  ids"));
            log.Add(NflContext.Summarize(stats, "  stats"));
        }

        var message = $"Weeks {string.Join(", ", weeks)}: {written} stat rows written, {unmatched} unmatched player(s).";
        return unmatched > 0 ? Attention(message, string.Join("\n", log)) : Ok(message, string.Join("\n", log));
    }
}
