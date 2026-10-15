using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;
using RotoMonster.Data;

namespace MonitorNFL.Checks;

public class NflLinesCheck : MonitorCheck
{
    private const int ProbableTypeId = 1;
    private const int ConfirmedTypeId = 2;
    private const string BaseUrl = "https://api.mysportsfeeds.com/v2.1/pull/nfl/";

    private static readonly Regex OffenseSlot = new(@"^Offense-(QB|RB|WR|TE)-(\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex KickerSlot = new(@"^SpecialTeams-(K|PK)-(\d+)$", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> MaxDepth = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QB"] = 1,
        ["RB"] = 2,
        ["WR"] = 3,
        ["TE"] = 1,
        ["K"] = 1
    };

    private readonly NflContext _nfl;

    public NflLinesCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromMinutes(Math.Max(10, nfl.Settings.LinesIntervalMinutes));
    }

    public override string Name => "NFL lineups";

    public override string Category => "MySportsFeeds";

    private class MsfGame
    {
        public string Id = "";
        public DateTime Day;
        public string HomeTeam = "";
        public string AwayTeam = "";
        public List<MsfTeamLineup> Lineups = new();
    }

    private class MsfTeamLineup
    {
        public string TeamCode = "";
        public bool IsActual;
        public List<MsfSlot> Slots = new();
    }

    private class MsfSlot
    {
        public string PlayerId = "";
        public string FirstName = "";
        public string LastName = "";
        public string Position = "";
        public string Details = "";
    }

    private class FetchResult
    {
        public JsonDocument? Doc;
        public string? Error;
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var key = _nfl.Settings.MySportsFeedsKey;
        if (string.IsNullOrWhiteSpace(key)) return Failed("Nfl:MySportsFeedsKey is not set.");

        var today = NflContext.NowEastern().Date;
        var days = Enumerable.Range(0, Math.Max(0, _nfl.Settings.LinesDaysAhead) + 1).Select(i => today.AddDays(i)).ToList();

        var msfGames = new List<MsfGame>();
        foreach (var day in days)
        {
            var fetched = await FetchAsync(key, $"current/date/{day:yyyyMMdd}/games.json", ct);
            if (fetched.Doc == null)
            {
                if (fetched.Error == "No content") continue;
                return Failed($"Games feed failed for {day:M/d}.", fetched.Error);
            }
            using (fetched.Doc)
            {
                if (!fetched.Doc.RootElement.TryGetProperty("games", out var games)) continue;
                foreach (var g in games.EnumerateArray())
                {
                    var s = g.GetProperty("schedule");
                    msfGames.Add(new MsfGame
                    {
                        Id = Str(s, "id"),
                        Day = day,
                        HomeTeam = Str(s.GetProperty("homeTeam"), "abbreviation"),
                        AwayTeam = Str(s.GetProperty("awayTeam"), "abbreviation")
                    });
                }
            }
            await Task.Delay(300, ct);
        }

        if (msfGames.Count == 0) return Ok("No games in the next few days.");

        var failures = new List<string>();
        var unknownSlots = new HashSet<string>();
        foreach (var game in msfGames)
        {
            var fetched = await FetchAsync(key, $"current/games/{game.Id}/lineup.json", ct);
            if (fetched.Doc == null)
            {
                if (fetched.Error == "No content") continue;
                failures.Add($"Game {game.Id}: {fetched.Error}");
                if (fetched.Error != null && fetched.Error.StartsWith("HTTP 4", StringComparison.Ordinal)) break;
                continue;
            }
            using (fetched.Doc)
            {
                if (!fetched.Doc.RootElement.TryGetProperty("teamLineups", out var teams)) continue;
                foreach (var t in teams.EnumerateArray())
                {
                    var actual = ReadSlots(t, "actual", unknownSlots);
                    var expected = ReadSlots(t, "expected", unknownSlots);
                    var use = actual.Count > 0 ? actual : expected;
                    if (use.Count == 0) continue;
                    game.Lineups.Add(new MsfTeamLineup
                    {
                        TeamCode = Str(t.GetProperty("team"), "abbreviation"),
                        IsActual = actual.Count > 0,
                        Slots = use
                    });
                }
            }
            await Task.Delay(300, ct);
        }

        await _nfl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nfl.CreateDb();
            var sync = new NFLDataSync(db);
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            var seasonId = _nfl.SeasonId;
            await EnsureStateTypes(conn);

            var positionIds = (await Query(conn, "SELECT Id, Abbreviation FROM Positions"))
                .Where(r => r[1] != DBNull.Value)
                .GroupBy(r => Convert.ToString(r[1])!.Trim().ToUpperInvariant())
                .ToDictionary(gr => gr.Key, gr => Convert.ToInt32(gr.First()[0]));

            var providerRows = await Query(conn, "SELECT Id FROM FantasyProviders WHERE Name = @n", ("@n", NFLDataSync.ProviderName));
            if (providerRows.Count == 0) return Failed("FantasyProviders has no MySportsFeeds row.");
            var msfProviderId = Convert.ToInt32(providerRows[0][0]);

            var from = days.First();
            var to = days.Last().AddDays(1);
            var ourGames = (await Query(conn, @"
SELECT Id, GameDate, HomeTeamId, AwayTeamId
FROM Games WHERE SeasonId = @s AND GameDate >= @from AND GameDate < @to",
                ("@s", seasonId), ("@from", from), ("@to", to)))
                .Select(r => new
                {
                    Id = Convert.ToInt32(r[0]),
                    Date = Convert.ToDateTime(r[1]).Date,
                    Home = r[2] == DBNull.Value ? 0 : Convert.ToInt32(r[2]),
                    Away = r[3] == DBNull.Value ? 0 : Convert.ToInt32(r[3])
                }).ToList();

            var playerByMsf = new Dictionary<string, int>();
            var playersWithMsf = new HashSet<int>();
            foreach (var r in await Query(conn, "SELECT ProviderId, PlayerId FROM FantasyProviderPlayers WHERE FantasyProviderId = @f AND ProviderId IS NOT NULL", ("@f", msfProviderId)))
            {
                playerByMsf[Convert.ToString(r[0])!.Trim()] = Convert.ToInt32(r[1]);
                playersWithMsf.Add(Convert.ToInt32(r[1]));
            }

            var names = new List<(int PlayerId, int TeamId, string Key)>();
            var lastNames = new List<(int PlayerId, int TeamId, string Key)>();
            foreach (var r in await Query(conn, @"
SELECT sp.PlayerId, sp.TeamId, p.FirstName, p.LastName
FROM SeasonPlayers sp JOIN Players p ON p.Id = sp.PlayerId
WHERE sp.SeasonId = @s", ("@s", seasonId)))
            {
                var teamId = r[1] == DBNull.Value ? 0 : Convert.ToInt32(r[1]);
                names.Add((Convert.ToInt32(r[0]), teamId, Norm(Convert.ToString(r[2]) + Convert.ToString(r[3]))));
                lastNames.Add((Convert.ToInt32(r[0]), teamId, Norm(Convert.ToString(r[3]))));
            }

            var existing = new Dictionary<(int Game, int Team), List<(int Player, int Type, string Details)>>();
            foreach (var r in await Query(conn, @"
SELECT s.GameId, s.TeamId, s.PlayerId, s.PlayerGameStateTypeId, s.Details
FROM PlayerGameStates s JOIN Games g ON g.Id = s.GameId
WHERE g.SeasonId = @s AND g.GameDate >= @from AND g.GameDate < @to",
                ("@s", seasonId), ("@from", from), ("@to", to)))
            {
                var k = (Convert.ToInt32(r[0]), Convert.ToInt32(r[1]));
                if (!existing.TryGetValue(k, out var list)) existing[k] = list = new();
                list.Add((Convert.ToInt32(r[2]), Convert.ToInt32(r[3]), r[4] == DBNull.Value ? "" : Convert.ToString(r[4])!));
            }

            var nowUtc = DateTime.UtcNow;
            int saved = 0, confirmed = 0, unchanged = 0, waiting = 0, linked = 0;
            var notes = new List<string>();
            var unmatched = new List<string>();

            async Task<int?> Resolve(MsfSlot slot, int teamId)
            {
                if (playerByMsf.TryGetValue(slot.PlayerId, out var known)) return known;
                var match = MatchByName(names, Norm(slot.FirstName + slot.LastName), teamId)
                            ?? MatchOnTeam(lastNames, Norm(slot.LastName), teamId);
                if (match == null)
                {
                    unmatched.Add($"{slot.FirstName} {slot.LastName} ({slot.Details}, MSF {slot.PlayerId})");
                    return null;
                }
                playerByMsf[slot.PlayerId] = match.Value;
                if (playersWithMsf.Add(match.Value))
                {
                    await Exec(conn, "INSERT INTO FantasyProviderPlayers (FantasyProviderId, PlayerId, ProviderId) VALUES (@f, @p, @m)",
                        ("@f", msfProviderId), ("@p", match.Value), ("@m", slot.PlayerId));
                    linked++;
                }
                return match.Value;
            }

            int? PositionFor(string code)
            {
                if (positionIds.TryGetValue(code, out var id)) return id;
                if (code == "K" && positionIds.TryGetValue("PK", out var pk)) return pk;
                return null;
            }

            foreach (var game in msfGames)
            {
                var homeId = await sync.GetTeamIdAsync(game.HomeTeam);
                var awayId = await sync.GetTeamIdAsync(game.AwayTeam);
                if (!homeId.HasValue || !awayId.HasValue)
                {
                    notes.Add($"Game {game.Id}: team not mapped ({game.AwayTeam} @ {game.HomeTeam})");
                    continue;
                }

                var ours = ourGames.FirstOrDefault(g => g.Date == game.Day && g.Home == homeId.Value && g.Away == awayId.Value)
                           ?? ourGames.FirstOrDefault(g => g.Home == homeId.Value && g.Away == awayId.Value && Math.Abs((g.Date - game.Day).TotalDays) <= 1);
                if (ours == null)
                {
                    notes.Add($"Game {game.Id}: no matching game on {game.Day:M/d}");
                    continue;
                }

                if (game.Lineups.Count == 0) { waiting++; continue; }

                foreach (var lineup in game.Lineups)
                {
                    var teamId = await sync.GetTeamIdAsync(lineup.TeamCode);
                    if (!teamId.HasValue) continue;

                    var desired = new List<(int Player, int Position, string Details)>();
                    foreach (var slot in lineup.Slots)
                    {
                        var resolved = await Resolve(slot, teamId.Value);
                        if (!resolved.HasValue) continue;
                        var positionId = PositionFor(slot.Position);
                        if (!positionId.HasValue) continue;
                        if (desired.Any(d => d.Player == resolved.Value)) continue;
                        desired.Add((resolved.Value, positionId.Value, slot.Details));
                    }
                    if (desired.Count == 0) continue;

                    var type = lineup.IsActual ? ConfirmedTypeId : ProbableTypeId;
                    existing.TryGetValue((ours.Id, teamId.Value), out var rows);
                    rows ??= new();

                    if (type == ProbableTypeId && rows.Any(x => x.Type == ConfirmedTypeId)) { unchanged++; continue; }

                    var want = desired.Select(d => (d.Player, type, d.Details)).OrderBy(x => x.Player).ToList();
                    var have = rows.OrderBy(x => x.Player).ToList();
                    if (want.SequenceEqual(have)) { unchanged++; continue; }

                    await Exec(conn, "DELETE FROM PlayerGameStates WHERE GameId = @g AND TeamId = @t",
                        ("@g", ours.Id), ("@t", teamId.Value));
                    foreach (var d in desired)
                    {
                        await Exec(conn, @"INSERT INTO PlayerGameStates (GameId, PlayerId, TeamId, PositionId, PlayerGameStateTypeId, DateAdded, Details)
VALUES (@g, @p, @t, @pos, @type, @now, @d)",
                            ("@g", ours.Id), ("@p", d.Player), ("@t", teamId.Value), ("@pos", d.Position),
                            ("@type", type), ("@now", nowUtc), ("@d", d.Details));
                    }
                    existing[(ours.Id, teamId.Value)] = want;
                    saved++;
                    if (type == ConfirmedTypeId) confirmed++;
                }
            }

            var details = new List<string>();
            details.AddRange(failures);
            details.AddRange(notes);
            if (unmatched.Count > 0)
            {
                details.Add($"{unmatched.Distinct().Count()} player(s) not matched:");
                details.AddRange(unmatched.Distinct().Take(40).Select(u => "  " + u));
            }
            if (saved == 0 && unchanged == 0 && unknownSlots.Count > 0)
                details.Add("Lineup slots not used: " + string.Join(", ", unknownSlots.Take(30)));

            var message = $"{msfGames.Count} game(s): {saved} team lineup(s) saved ({confirmed} confirmed), {unchanged} unchanged, {waiting} game(s) without lineups yet, {linked} player id(s) linked, {unmatched.Distinct().Count()} not matched.";
            if (failures.Count > 0 && saved == 0 && unchanged == 0) return Failed(message, string.Join("\n", details));
            return details.Count > 0 ? Attention(message, string.Join("\n", details)) : Ok(message);
        }
        finally
        {
            _nfl.StatsWriteLock.Release();
        }
    }

    private static async Task EnsureStateTypes(DbConnection conn)
    {
        await Exec(conn, @"
IF NOT EXISTS (SELECT 1 FROM PlayerGameStateTypes WHERE Id IN (1, 2, 3))
BEGIN
    SET IDENTITY_INSERT PlayerGameStateTypes ON;
    INSERT INTO PlayerGameStateTypes (Id, Title, IsStarter, IsBench, ShowLockAfterStart, IsProbableStarter) VALUES (1, 'Probable', 1, 0, 0, 1);
    INSERT INTO PlayerGameStateTypes (Id, Title, IsStarter, IsBench, ShowLockAfterStart, IsProbableStarter) VALUES (2, 'Confirmed', 1, 0, 1, 0);
    INSERT INTO PlayerGameStateTypes (Id, Title, IsStarter, IsBench, ShowLockAfterStart, IsProbableStarter) VALUES (3, 'Bench', 0, 1, 0, 0);
    SET IDENTITY_INSERT PlayerGameStateTypes OFF;
END");
    }

    private static int? MatchByName(List<(int PlayerId, int TeamId, string Key)> names, string key, int teamId)
    {
        if (key.Length == 0) return null;
        var onTeam = names.Where(n => n.Key == key && n.TeamId == teamId).Select(n => n.PlayerId).Distinct().ToList();
        if (onTeam.Count == 1) return onTeam[0];
        var anywhere = names.Where(n => n.Key == key).Select(n => n.PlayerId).Distinct().ToList();
        return anywhere.Count == 1 ? anywhere[0] : null;
    }

    private static int? MatchOnTeam(List<(int PlayerId, int TeamId, string Key)> names, string key, int teamId)
    {
        if (key.Length == 0) return null;
        var onTeam = names.Where(n => n.Key == key && n.TeamId == teamId).Select(n => n.PlayerId).Distinct().ToList();
        return onTeam.Count == 1 ? onTeam[0] : null;
    }

    private static List<MsfSlot> ReadSlots(JsonElement team, string which, HashSet<string> unknownSlots)
    {
        var result = new List<MsfSlot>();
        if (!team.TryGetProperty(which, out var block) || block.ValueKind != JsonValueKind.Object) return result;
        if (!block.TryGetProperty("lineupPositions", out var slots) || slots.ValueKind != JsonValueKind.Array) return result;
        foreach (var s in slots.EnumerateArray())
        {
            if (!s.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            var position = Str(s, "position");

            string pos;
            int depth;
            var o = OffenseSlot.Match(position);
            var k = KickerSlot.Match(position);
            if (o.Success)
            {
                pos = o.Groups[1].Value.ToUpperInvariant();
                depth = int.Parse(o.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (k.Success)
            {
                pos = "K";
                depth = int.Parse(k.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                if (unknownSlots.Count < 60) unknownSlots.Add(position);
                continue;
            }

            if (!MaxDepth.TryGetValue(pos, out var max) || depth > max) continue;

            result.Add(new MsfSlot
            {
                PlayerId = Str(p, "id"),
                FirstName = Str(p, "firstName"),
                LastName = Str(p, "lastName"),
                Position = pos,
                Details = pos + depth.ToString(CultureInfo.InvariantCulture)
            });
        }
        return result;
    }

    private async Task<FetchResult> FetchAsync(string key, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":MYSPORTSFEEDS")));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        try
        {
            using var response = await _nfl.Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NoContent) return new FetchResult { Error = "No content" };
            if (!response.IsSuccessStatusCode) return new FetchResult { Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" };

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 1 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var gz = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new System.IO.MemoryStream();
                await gz.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            return new FetchResult { Doc = JsonDocument.Parse(bytes) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FetchResult { Error = ex.Message };
        }
    }

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        };
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static async Task<List<object[]>> Query(DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        var rows = new List<object[]>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return rows;
    }

    private static async Task Exec(DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync();
    }
}
