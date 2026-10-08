using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Monitor.Core;
using RotoMonster.Core;
using RotoMonster.Data;

namespace MonitorNHL.Checks;

public class NhlInjuriesCheck : MonitorCheck
{
    private const string Url = "https://site.api.espn.com/apis/site/v2/sports/hockey/nhl/injuries";

    private static readonly Dictionary<string, string> StatusTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Day-To-Day", "Day-to-Day" },
        { "Out", "Out" },
        { "Injured Reserve", "Injured Reserve" },
        { "Suspension", "Suspended" }
    };

    private static readonly Dictionary<string, string> TeamCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "UTAH", "UTA" },
        { "LAK", "LA" },
        { "NJD", "NJ" },
        { "SJS", "SJ" },
        { "TBL", "TB" }
    };

    private static readonly HashSet<string> Suffixes = new() { "jr", "sr", "ii", "iii", "iv" };

    private readonly NhlContext _nhl;

    public NhlInjuriesCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromMinutes(Math.Max(15, nhl.Settings.InjuriesIntervalMinutes));
    }

    public override string Name => "NHL injuries";

    public override string Category => "ESPN";

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        string body;
        try
        {
            body = await _nhl.Http.GetStringAsync(Url, ct);
        }
        catch (Exception ex)
        {
            return Failed("ESPN injuries feed failed.", ex.Message);
        }

        var fingerprint = body.Length + ":" + body.GetHashCode();
        if (_nhl.LastUpdated("injuries") == fingerprint) return Ok("Injuries unchanged.");

        var entries = new List<(string First, string Last, string Team, string Position, string Status, string Description)>();
        using (var doc = JsonDocument.Parse(body))
        {
            if (!doc.RootElement.TryGetProperty("injuries", out var teams))
                return Failed("ESPN injuries feed had no injuries list.");

            foreach (var team in teams.EnumerateArray())
            {
                if (!team.TryGetProperty("injuries", out var list)) continue;
                foreach (var i in list.EnumerateArray())
                {
                    var athlete = i.TryGetProperty("athlete", out var a) ? a : default;
                    var first = Text(athlete, "firstName");
                    var last = Text(athlete, "lastName");
                    var teamCode = athlete.ValueKind == JsonValueKind.Object && athlete.TryGetProperty("team", out var t) ? Text(t, "abbreviation") : "";
                    var position = athlete.ValueKind == JsonValueKind.Object && athlete.TryGetProperty("position", out var pos) ? Text(pos, "abbreviation") : "";
                    entries.Add((first, last, teamCode, position, Text(i, "status"), Describe(i)));
                }
            }
        }

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var seasonId = _nhl.SeasonId;

            var sync = new NHLDataSync(db);
            var linked = new HashSet<int>((await sync.GetProviderPlayerMapAsync()).Values);

            var people = await db.Set<Player>().AsNoTracking()
                .Where(p => linked.Contains(p.Id))
                .Select(p => new { p.Id, p.FirstName, p.LastName })
                .ToListAsync(ct);

            var teamCodes = await db.Set<Team>().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Code.Trim(), ct);
            var teamIds = teamCodes.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

            var seasonRows = await db.Set<SeasonPlayer>().AsNoTracking()
                .Where(sp => linked.Contains(sp.PlayerId))
                .Select(sp => new { sp.PlayerId, sp.SeasonId, sp.TeamId })
                .ToListAsync(ct);

            var latestTeam = seasonRows.GroupBy(r => r.PlayerId)
                .ToDictionary(g => g.Key, g => teamCodes.TryGetValue(g.OrderByDescending(r => r.SeasonId).First().TeamId, out var c) ? c : "");
            var thisSeason = new HashSet<int>(seasonRows.Where(r => r.SeasonId == seasonId).Select(r => r.PlayerId));

            var byName = people.GroupBy(r => Norm(r.FirstName + " " + r.LastName))
                .ToDictionary(g => g.Key, g => g.Select(x => new { x.Id, Code = latestTeam.TryGetValue(x.Id, out var c) ? c : "" }).ToList());

            var addedSeasonRows = new List<string>();

            var injuries = new List<PlayerInjury>();
            var unmatched = new List<string>();
            var unknown = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var e in entries)
            {
                if (!StatusTitles.TryGetValue(e.Status ?? "", out var title))
                {
                    unknown.Add(e.Status + " (" + e.First + " " + e.Last + ")");
                    continue;
                }

                var code = TeamCodes.TryGetValue(e.Team ?? "", out var mapped) ? mapped : e.Team;
                byName.TryGetValue(Norm(e.First + " " + e.Last), out var candidates);
                candidates ??= new();
                var hits = candidates.Where(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count != 1 && candidates.Count == 1) hits = candidates;

                if (hits.Count != 1)
                {
                    unmatched.Add($"{e.First} {e.Last} {e.Team} ({e.Status}), {candidates.Count} name match(es)");
                    continue;
                }

                if (!thisSeason.Contains(hits[0].Id) && teamIds.TryGetValue(code ?? "", out var injuredTeamId))
                {
                    db.Set<SeasonPlayer>().Add(new SeasonPlayer
                    {
                        SeasonId = seasonId,
                        PlayerId = hits[0].Id,
                        TeamId = injuredTeamId,
                        PlayerTypeId = string.Equals(e.Position, "G", StringComparison.OrdinalIgnoreCase) ? NHLDataSync.GoaliePlayerTypeId : NHLDataSync.SkaterPlayerTypeId
                    });
                    thisSeason.Add(hits[0].Id);
                    addedSeasonRows.Add($"{e.First} {e.Last} {code}");
                }

                injuries.Add(new PlayerInjury
                {
                    PlayerId = hits[0].Id,
                    PlayerStatus = title,
                    InjuryStatus = title,
                    Description = e.Description,
                    Comment = string.IsNullOrEmpty(e.Description) ? title : title + " - " + e.Description,
                    DownloadDate = now,
                    StartDate = now,
                    UpdateDate = now
                });
            }

            if (addedSeasonRows.Count > 0) await db.SaveChangesAsync(ct);

            _nhl.InjuredPlayerIds = new HashSet<int>(injuries.Where(i => i.PlayerStatus != "Day-to-Day").Select(i => i.PlayerId));

            var config = new ConfigurationBuilder().Build();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var data = new RMSqlData(db, config, cache);
            var added = data.UpdatePlayerInjuries(injuries);

            _nhl.SetLastUpdated("injuries", fingerprint);

            var details = new List<string>();
            if (addedSeasonRows.Count > 0) details.Add($"Added {addedSeasonRows.Count} injured player(s) to this season: " + string.Join(", ", addedSeasonRows.Take(20)));
            if (unmatched.Count > 0) details.Add($"{unmatched.Count} not matched to our players:");
            details.AddRange(unmatched.Take(15).Select(u => "  " + u));
            if (unknown.Count > 0) details.Add("Unknown status: " + string.Join(", ", unknown.Take(10)));

            var message = $"{injuries.Count} of {entries.Count} injuries matched, {added.Count} new or changed"
                          + (unmatched.Count > 0 ? $", {unmatched.Count} unmatched" : "")
                          + (addedSeasonRows.Count > 0 ? $", {addedSeasonRows.Count} added to this season" : "") + ".";

            if (unknown.Count > 0) return Attention(message + $" {unknown.Count} unknown status.", string.Join("\n", details));
            return added.Count > 0 || addedSeasonRows.Count > 0 ? Attention(message, string.Join("\n", details)) : Ok(message, string.Join("\n", details));
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }

    private static string Describe(JsonElement injury)
    {
        var parts = new List<string>();
        if (injury.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            var type = Text(d, "type");
            var detail = Text(d, "detail");
            var side = Text(d, "side");
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "Not Specified", StringComparison.OrdinalIgnoreCase)) parts.Add(type);
            if (!string.IsNullOrWhiteSpace(detail) && !string.Equals(detail, type, StringComparison.OrdinalIgnoreCase) && !string.Equals(detail, "Not Specified", StringComparison.OrdinalIgnoreCase)) parts.Add(detail);
            if (!string.IsNullOrWhiteSpace(side) && !string.Equals(side, "Not Specified", StringComparison.OrdinalIgnoreCase)) parts.Insert(0, side);
        }
        if (parts.Count > 0) return string.Join(" ", parts);

        var comment = Text(injury, "longComment");
        if (string.IsNullOrWhiteSpace(comment) || comment.Length <= 3) comment = Text(injury, "shortComment");
        return comment != null && comment.Length > 3 ? comment : "";
    }

    private static string Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetter(ch) || ch == ' ') sb.Append(char.ToLowerInvariant(ch));
        var parts = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && Suffixes.Contains(parts[^1])) parts.RemoveAt(parts.Count - 1);
        return string.Join("", parts);
    }
}
