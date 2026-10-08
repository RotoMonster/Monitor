using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Monitor.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Services.Providers;

namespace MonitorNHL.Checks;

public class NhlScheduleCheck : MonitorCheck
{
    private readonly NhlContext _nhl;

    public NhlScheduleCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromHours(Math.Max(1, nhl.Settings.ScheduleIntervalHours));
    }

    public override string Name => "NHL schedule";

    public override string Category => "NHL API";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var games = await _nhl.Nhl.GetGamesAsync(_nhl.Sport, _nhl.SeasonKey, _nhl.LastUpdated("games"));
        if (!games.Success) return Failed("Schedule feed failed.", games.ErrorMessage);
        if (games.NotModified) return Ok("Schedule unchanged.");
        if (games.Games.Count == 0) return Failed("Schedule feed returned no games.");

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var sync = new NHLDataSync(db);

            var dates = games.Games.Select(g => NhlContext.ToEastern(g.StartTimeUtc).Date).ToList();
            var season = await sync.EnsureSeasonAsync(_nhl.Settings.Year, dates.Min(), dates.Max(), NhlApiProvider.TeamCodes);

            var result = new NHLSyncResult();
            var map = await sync.SyncGamesAsync(_nhl.SeasonId, games.Games, result);

            _nhl.SetLastUpdated("games", games.LastUpdatedOn);

            var details = NhlContext.Summarize(season, "season") + "\n" + NhlContext.Summarize(result, "games");
            var message = $"{map.Count} of {games.Games.Count} games mapped, {result.Created} created, {result.Updated} updated.";

            if (map.Count < games.Games.Count || result.Skipped > 0 || season.Skipped > 0)
                return Attention(message, details);

            return Ok(message, details);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }
}
