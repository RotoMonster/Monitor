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

namespace MonitorNFL.Checks;

public class NflReprocessCheck : MonitorCheck
{
    private readonly NflContext _nfl;
    private static readonly string DoneTodayFile = System.IO.Path.Combine(AppContext.BaseDirectory, "nfl-reprocess-today.txt");

    public NflReprocessCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromMinutes(Math.Max(15, nfl.Settings.ReprocessIntervalMinutes));
    }

    public override string Name => "NFL reprocess";

    public override string Category => "MySportsFeeds";

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
        var settings = _nfl.Settings;
        var now = NflContext.NowEastern();

        if (now.Hour < settings.ReprocessHour)
            return Ok($"Waiting for {settings.ReprocessHour}:00.");

        var today = now.Date;
        var oldest = today.AddDays(-Math.Max(1, settings.ReprocessMaxAgeDays));
        var passes = Math.Max(0, settings.ReprocessPasses);
        var seasonId = _nfl.SeasonId;

        using var db = _nfl.CreateDb();

        var candidates = await db.Set<Game>().AsNoTracking()
            .Where(g => g.SeasonId == seasonId && g.IsFinished && g.ReprocessCount < passes && g.GameDate >= oldest)
            .Select(g => new { g.Id, g.GameDate, g.ReprocessCount })
            .ToListAsync(ct);

        var doneToday = ReadDoneToday(today);
        var due = candidates.Where(g => g.GameDate.Date.AddDays(g.ReprocessCount + 1) <= today && !doneToday.Contains(g.Id)).ToList();
        if (due.Count == 0) return Ok("No games due for reprocessing.");

        var games = await _nfl.MySportsFeeds.GetGamesAsync(_nfl.Sport, settings.Season, null);
        if (!games.Success) return Failed("Games feed failed.", games.ErrorMessage);

        var sync = new NFLDataSync(db);
        var gameResult = new NFLSyncResult();
        var gameMap = await sync.SyncGamesAsync(seasonId, games.Games, gameResult);

        var dueIds = new HashSet<int>(due.Select(g => g.Id));
        var todo = games.Games
            .Where(g => gameMap.ContainsKey(g.GameId) && dueIds.Contains(gameMap[g.GameId].Id))
            .ToList();

        if (todo.Count == 0)
            return Attention($"{due.Count} game(s) due but not found in the feed.");

        var players = await sync.GetProviderPlayerMapAsync(NFLDataSync.ProviderName);
        var log = new List<string>();
        var failedDays = new List<string>();
        var doneGameIds = new List<int>();
        var stats = new NFLSyncResult();

        foreach (var day in todo.Select(g => NflContext.ToEastern(g.StartTimeUtc).Date).Distinct().OrderBy(d => d))
        {
            if (_nfl.IsRefused(day))
            {
                failedDays.Add(day.ToString("M/d"));
                continue;
            }

            var r = await _nfl.MySportsFeeds.GetPlayerGamesByDateAsync(_nfl.Sport, settings.Season, day, null, false);
            if (!r.Success)
            {
                _nfl.MarkRefused(day);
                failedDays.Add(day.ToString("M/d"));
                log.Add($"{day:yyyy-MM-dd}: {r.ErrorMessage}");
                continue;
            }

            var dayGames = todo.Where(g => NflContext.ToEastern(g.StartTimeUtc).Date == day)
                .ToDictionary(g => g.GameId, g => gameMap[g.GameId]);

            var dayStats = await sync.SyncPlayerGamesAsync(r.PlayerGames, dayGames, players);
            stats.Created += dayStats.Created;
            stats.Updated += dayStats.Updated;
            stats.Skipped += dayStats.Skipped;
            doneGameIds.AddRange(dayGames.Values.Select(g => g.Id));
            log.Add($"{day:yyyy-MM-dd}: {dayGames.Count} game(s), {r.PlayerGames.Count} lines, {dayStats.Created} created, {dayStats.Updated} updated");
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
        var details = string.Join("\n", log) + "\n" + NflContext.Summarize(gameResult, "games") + "\n" + NflContext.Summarize(stats, "stats");

        if (failedDays.Count > 0)
            return Attention(message + $" Feed refused {string.Join(", ", failedDays)}, will retry.", details);

        return Ok(message, details);
    }
}
