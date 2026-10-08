using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;
using RotoMonster.Core;
using RotoMonster.Data;

namespace MonitorNHL.Checks;

public class NhlReprocessCheck : MonitorCheck
{
    private readonly NhlContext _nhl;
    private static readonly string DoneTodayFile = System.IO.Path.Combine(AppContext.BaseDirectory, "nhl-reprocess-today.txt");

    public NhlReprocessCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromMinutes(Math.Max(15, nhl.Settings.ReprocessIntervalMinutes));
    }

    public override string Name => "NHL reprocess";

    public override string Category => "NHL API";

    private static HashSet<int> ReadDoneToday(DateTime today)
    {
        try
        {
            if (!System.IO.File.Exists(DoneTodayFile)) return new HashSet<int>();
            var lines = System.IO.File.ReadAllLines(DoneTodayFile);
            if (lines.Length == 0 || lines[0].Trim() != today.ToString("yyyy-MM-dd")) return new HashSet<int>();
            return new HashSet<int>(lines.Skip(1).Select(l => int.TryParse(l.Trim(), out var id) ? id : 0).Where(id => id > 0));
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    private static void SaveDoneToday(DateTime today, HashSet<int> ids)
    {
        try
        {
            System.IO.File.WriteAllLines(DoneTodayFile, new[] { today.ToString("yyyy-MM-dd") }.Concat(ids.Select(i => i.ToString())));
        }
        catch
        {
        }
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var settings = _nhl.Settings;
        var now = NhlContext.NowEastern();

        if (now.Hour < settings.ReprocessHour)
            return Ok($"Waiting for {settings.ReprocessHour}:00.");

        var today = now.Date;
        var oldest = today.AddDays(-Math.Max(1, settings.ReprocessMaxAgeDays));
        var passes = Math.Max(0, settings.ReprocessPasses);
        var seasonId = _nhl.SeasonId;

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();

            var candidates = await db.Set<Game>().AsNoTracking()
                .Where(g => g.SeasonId == seasonId && g.IsFinished && g.ReprocessCount < passes && g.GameDate >= oldest)
                .Select(g => new { g.Id, g.GameDate, g.ReprocessCount })
                .ToListAsync(ct);

            var doneToday = ReadDoneToday(today);
            var due = candidates.Where(g => g.GameDate.Date.AddDays(g.ReprocessCount + 1) <= today && !doneToday.Contains(g.Id)).ToList();
            if (due.Count == 0) return Ok("No games due for reprocessing.");

            var sync = new NHLDataSync(db);
            var players = await sync.GetProviderPlayerMapAsync();
            var dueIds = new HashSet<int>(due.Select(g => g.Id));
            var log = new List<string>();
            var doneGameIds = new List<int>();
            var gameResult = new NHLSyncResult();
            var stats = new NHLSyncResult();

            foreach (var day in due.Select(g => g.GameDate.Date).Distinct().OrderBy(d => d))
            {
                var dayGames = await _nhl.Nhl.GetGamesByDateAsync(_nhl.Sport, _nhl.SeasonKey, day, null);
                if (!dayGames.Success)
                {
                    log.Add($"{day:yyyy-MM-dd}: {dayGames.ErrorMessage}");
                    continue;
                }

                var map = await sync.SyncGamesAsync(seasonId, dayGames.Games, gameResult);
                var games = dayGames.Games
                    .Where(g => g.IsFinished && map.ContainsKey(g.GameId) && dueIds.Contains(map[g.GameId].Id))
                    .ToDictionary(g => g.GameId, g => map[g.GameId]);
                if (games.Count == 0) continue;

                var lines = await _nhl.Nhl.GetPlayerGamesByDateAsync(_nhl.Sport, _nhl.SeasonKey, day, null);
                if (!lines.Success)
                {
                    log.Add($"{day:yyyy-MM-dd}: {lines.ErrorMessage}");
                    continue;
                }

                var dayStats = await sync.SyncPlayerGamesAsync(lines.PlayerGames, games, players);
                stats.Created += dayStats.Created;
                stats.Updated += dayStats.Updated;
                stats.Skipped += dayStats.Skipped;
                doneGameIds.AddRange(games.Values.Select(g => g.Id));
                log.Add($"{day:yyyy-MM-dd}: {games.Count} game(s), {dayStats.Created} created, {dayStats.Updated} updated");
            }

            if (doneGameIds.Count > 0)
            {
                var tracked = await db.Set<Game>().Where(g => doneGameIds.Contains(g.Id)).ToListAsync(ct);
                foreach (var g in tracked) g.ReprocessCount++;
                await db.SaveChangesAsync(ct);
                doneToday.UnionWith(doneGameIds);
                SaveDoneToday(today, doneToday);
            }

            var message = $"{doneGameIds.Count} game(s) reprocessed, {stats.Created + stats.Updated} stat rows written.";
            var details = string.Join("\n", log) + "\n" + NhlContext.Summarize(gameResult, "games") + "\n" + NhlContext.Summarize(stats, "stats");
            return Ok(message, details);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }
}
