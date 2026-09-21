using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;
using RotoMonster.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Providers;

namespace MonitorNFL.Checks;

public class NflBoxScoreCheck : MonitorCheck
{
    private readonly NflContext _nfl;

    public NflBoxScoreCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromSeconds(Math.Max(30, nfl.Settings.BoxScoreIntervalSeconds));
    }

    public override string Name => "NFL box scores";

    public override string Category => "MySportsFeeds";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        using var db = _nfl.CreateDb();
        var seasonId = _nfl.SeasonId;
        var now = NflContext.NowEastern();

        var pending = await db.Set<Game>().AsNoTracking()
            .Where(g => g.SeasonId == seasonId && g.GameTime <= now
                        && (!g.IsFinished || !db.Set<NFLOffensiveGame>().Any(o => o.GameId == g.Id)))
            .Select(g => g.Id)
            .ToListAsync(ct);

        if (pending.Count == 0) return Ok("No games in progress or missing stats.");

        var games = await _nfl.MySportsFeeds.GetGamesAsync(_nfl.Sport, _nfl.Settings.Season, null);
        if (!games.Success) return Failed("Games feed failed.", games.ErrorMessage);

        var sync = new NFLDataSync(db);
        var gameResult = new NFLSyncResult();
        var gameMap = await sync.SyncGamesAsync(seasonId, games.Games, gameResult);

        var pendingSet = new HashSet<int>(pending);
        var todo = games.Games
            .Where(g => gameMap.ContainsKey(g.GameId) && pendingSet.Contains(gameMap[g.GameId].Id))
            .ToList();

        if (todo.Count == 0)
            return Attention($"{pending.Count} pending game(s) not found in the feed.");

        var skipped = todo.Where(g => _nfl.IsRefused(NflContext.ToEastern(g.StartTimeUtc))).ToList();
        todo = todo.Except(skipped).ToList();

        if (todo.Count == 0)
            return Ok($"{skipped.Count} game(s) waiting on nflverse, feed refused their date.");

        var players = await sync.GetProviderPlayerMapAsync(NFLDataSync.ProviderName);

        var lines = new List<SportsDataPlayerGame>();
        var log = new List<string>();
        var failedDays = new List<string>();
        var unchanged = 0;

        foreach (var day in todo.Select(g => NflContext.ToEastern(g.StartTimeUtc).Date).Distinct().OrderBy(d => d))
        {
            var feed = "gamelogs-" + day.ToString("yyyyMMdd");
            var allFinal = todo.Where(g => NflContext.ToEastern(g.StartTimeUtc).Date == day).All(g => g.IsFinished);
            var useStored = allFinal && !_nfl.Settings.BoxScoreForceReload && _nfl.MySportsFeeds.HasStored(_nfl.Sport, _nfl.Settings.Season, day);
            var r = await _nfl.MySportsFeeds.GetPlayerGamesByDateAsync(_nfl.Sport, _nfl.Settings.Season, day, useStored ? null : _nfl.LastUpdated(feed), useStored);

            if (!r.Success)
            {
                failedDays.Add(day.ToString("M/d"));
                _nfl.MarkRefused(day);
                log.Add($"{day:yyyy-MM-dd}: {r.ErrorMessage}");
                continue;
            }

            if (r.NotModified)
            {
                unchanged++;
                continue;
            }

            lines.AddRange(r.PlayerGames);
            _nfl.SetLastUpdated(feed, r.LastUpdatedOn);
            log.Add($"{day:yyyy-MM-dd}: {r.PlayerGames.Count} lines" + (useStored ? " (stored file)" : " (fetched)"));
        }

        var stats = new NFLSyncResult();
        if (lines.Count > 0)
        {
            var todoGames = todo.ToDictionary(g => g.GameId, g => gameMap[g.GameId]);
            stats = await sync.SyncPlayerGamesAsync(lines, todoGames, players);
        }

        var finished = todo.Count(g => g.IsFinished);
        if (skipped.Count > 0) log.Add($"{skipped.Count} game(s) skipped, date refused earlier");
        var details = string.Join("\n", log) + "\n" + NflContext.Summarize(gameResult, "games") + "\n" + NflContext.Summarize(stats, "stats");
        var message = $"{todo.Count} game(s) processed, {finished} now final, {stats.Created + stats.Updated} stat rows written"
                      + (unchanged > 0 ? $", {unchanged} day(s) unchanged" : "") + ".";

        if (failedDays.Count > 0)
            return Attention(message + $" Feed refused {string.Join(", ", failedDays)}, nflverse will fill it.", details);

        return Ok(message, details);
    }
}
