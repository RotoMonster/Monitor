using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;

namespace MonitorNFL.Checks;

public class NflScheduleCheck : MonitorCheck
{
    private readonly NflContext _nfl;

    public NflScheduleCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromHours(Math.Max(1, nfl.Settings.ScheduleIntervalHours));
    }

    public override string Name => "NFL schedule";

    public override string Category => "MySportsFeeds";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var games = await _nfl.MySportsFeeds.GetGamesAsync(_nfl.Sport, _nfl.Settings.Season, _nfl.LastUpdated("games"));
        if (!games.Success) return Failed("Games feed failed.", games.ErrorMessage);
        if (games.NotModified) return Ok("Schedule unchanged.");

        var teams = await _nfl.MySportsFeeds.GetTeamsAsync(_nfl.Sport, _nfl.Settings.Season, null);
        if (!teams.Success) return Failed("Teams feed failed.", teams.ErrorMessage);

        if (games.Games.Count == 0) return Failed("Games feed returned no games.");

        using var db = _nfl.CreateDb();
        var sync = new NFLDataSync(db);

        var dates = games.Games.Select(g => NflContext.ToEastern(g.StartTimeUtc).Date).ToList();
        var season = await sync.EnsureSeasonAsync(_nfl.Settings.Year, dates.Min(), dates.Max(), teams.Teams.Select(t => t.Code));

        var result = new NFLSyncResult();
        var map = await sync.SyncGamesAsync(_nfl.SeasonId, games.Games, result);

        _nfl.SetLastUpdated("games", games.LastUpdatedOn);

        var details = NflContext.Summarize(season, "season") + "\n" + NflContext.Summarize(result, "games");
        var message = $"{map.Count} of {games.Games.Count} games mapped, {result.Created} created, {result.Updated} updated.";

        if (map.Count < games.Games.Count || result.Skipped > 0 || season.Skipped > 0)
            return Attention(message, details);

        return Ok(message, details);
    }
}
