using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;
using RotoMonster.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Models.Providers;

namespace MonitorNHL.Checks;

public class NhlBoxScoreCheck : MonitorCheck
{
    private readonly NhlContext _nhl;

    public NhlBoxScoreCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromSeconds(Math.Max(30, nhl.Settings.BoxScoreIntervalSeconds));
    }

    public override string Name => "NHL box scores";

    public override string Category => "NHL API";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var seasonId = _nhl.SeasonId;

            var pendingGames = await db.Set<Game>().AsNoTracking()
                .Where(g => g.SeasonId == seasonId && g.GameTime <= DateTime.UtcNow
                            && (!g.IsFinished || !db.Set<NHLSkaterGame>().Any(s => s.GameId == g.Id)))
                .Select(g => new { g.Id, g.GameDate, g.IsFinished })
                .ToListAsync(ct);

            if (pendingGames.Count == 0) return Ok("No games in progress or missing stats.");

            var days = pendingGames
                .Select(p => p.GameDate.Date)
                .Distinct()
                .OrderByDescending(d => d)
                .Take(Math.Max(1, _nhl.Settings.BoxScoreMaxDaysPerRun))
                .OrderBy(d => d)
                .ToList();

            var sync = new NHLDataSync(db);
            var players = await sync.GetProviderPlayerMapAsync();
            var gameResult = new NHLSyncResult();
            var stats = new NHLSyncResult();
            var log = new List<string>();
            var unchanged = 0;
            var processed = 0;
            var finished = 0;

            foreach (var day in days)
            {
                var feed = "box-" + day.ToString("yyyyMMdd");
                var dayGames = await _nhl.Nhl.GetGamesByDateAsync(_nhl.Sport, _nhl.SeasonKey, day, null);
                if (!dayGames.Success)
                {
                    log.Add($"{day:yyyy-MM-dd}: {dayGames.ErrorMessage}");
                    continue;
                }

                var map = await sync.SyncGamesAsync(seasonId, dayGames.Games, gameResult);

                var lines = await _nhl.Nhl.GetPlayerGamesByDateAsync(_nhl.Sport, _nhl.SeasonKey, day, _nhl.LastUpdated(feed));
                if (!lines.Success)
                {
                    log.Add($"{day:yyyy-MM-dd}: {lines.ErrorMessage}");
                    continue;
                }

                var started = dayGames.Games.Where(g => (g.IsInProgress || g.IsFinished) && map.ContainsKey(g.GameId)).ToList();
                processed += started.Count;
                finished += started.Count(g => g.IsFinished);

                if (lines.NotModified)
                {
                    unchanged++;
                    continue;
                }

                var games = started.ToDictionary(g => g.GameId, g => map[g.GameId]);
                var dayStats = await sync.SyncPlayerGamesAsync(lines.PlayerGames, games, players);
                stats.Created += dayStats.Created;
                stats.Updated += dayStats.Updated;
                stats.Skipped += dayStats.Skipped;
                stats.Notes.AddRange(dayStats.Notes);

                _nhl.SetLastUpdated(feed, lines.LastUpdatedOn);
                log.Add($"{day:yyyy-MM-dd}: {started.Count} game(s), {lines.PlayerGames.Count} lines");
            }

            var details = string.Join("\n", log) + "\n" + NhlContext.Summarize(gameResult, "games") + "\n" + NhlContext.Summarize(stats, "stats");
            var message = $"{processed} game(s) processed, {finished} final, {stats.Created + stats.Updated} stat rows written"
                          + (unchanged > 0 ? $", {unchanged} day(s) unchanged" : "") + ".";

            return stats.Skipped > 0 ? Attention(message + $" {stats.Skipped} line(s) had no player mapping.", details) : Ok(message, details);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }
}
