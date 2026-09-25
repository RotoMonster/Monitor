using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Monitor.Core;
using RotoMonster.Core;
using RotoMonster.Data;

namespace MonitorNFL.Checks;

public class NflInjuriesCheck : MonitorCheck
{
    private static readonly Dictionary<string, string> StatusTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        { "OUT", "Out" },
        { "DOUBTFUL", "Doubtful" },
        { "QUESTIONABLE", "Questionable" },
        { "PROBABLE", "Probable" }
    };

    private readonly NflContext _nfl;

    public NflInjuriesCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromMinutes(Math.Max(15, nfl.Settings.InjuriesIntervalMinutes));
    }

    public override string Name => "NFL injuries";

    public override string Category => "MySportsFeeds";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var players = await _nfl.MySportsFeeds.GetPlayersAsync(_nfl.Sport, _nfl.LastUpdated("injuries"));
        if (!players.Success) return Failed("Players feed failed.", players.ErrorMessage);
        if (players.NotModified) return Ok("Injuries unchanged.");

        using var db = _nfl.CreateDb();
        var sync = new NFLDataSync(db);
        var byProviderId = await sync.GetProviderPlayerMapAsync(NFLDataSync.ProviderName);

        var injuries = new List<PlayerInjury>();
        var unknown = new List<string>();
        var unmapped = 0;
        var now = DateTime.UtcNow;

        foreach (var p in players.Players)
        {
            if (p.Injury == null || string.IsNullOrEmpty(p.Injury.PlayingProbability)) continue;

            int playerId;
            if (string.IsNullOrEmpty(p.PlayerId) || !byProviderId.TryGetValue(p.PlayerId, out playerId))
            {
                unmapped++;
                continue;
            }

            string title;
            if (!StatusTitles.TryGetValue(p.Injury.PlayingProbability, out title))
            {
                unknown.Add(p.Injury.PlayingProbability + " (" + p.FirstName + " " + p.LastName + ")");
                continue;
            }

            var description = p.Injury.Description ?? "";

            injuries.Add(new PlayerInjury
            {
                PlayerId = playerId,
                PlayerStatus = title,
                InjuryStatus = title,
                Description = description,
                Comment = title + " - " + description,
                DownloadDate = now,
                StartDate = now,
                UpdateDate = now
            });
        }

        var config = new ConfigurationBuilder().Build();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var data = new RMSqlData(db, config, cache);

        var added = data.UpdatePlayerInjuries(injuries);

        _nfl.SetLastUpdated("injuries", players.LastUpdatedOn);

        var message = $"{injuries.Count} current injuries, {added.Count} new or changed.";
        var details = new List<string>();
        if (unmapped > 0) details.Add($"{unmapped} injured player(s) not mapped to our players");
        if (unknown.Count > 0) details.Add("Unknown status: " + string.Join(", ", unknown.Take(10)));

        if (unknown.Count > 0)
            return Attention(message + $" {unknown.Count} unknown status.", string.Join("\n", details));

        return added.Count > 0
            ? Attention(message, string.Join("\n", details))
            : Ok(message, string.Join("\n", details));
    }
}
