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

namespace MonitorNHL.Checks;

public class NhlLinesCheck : MonitorCheck
{
    private const int ProbableTypeId = 1;
    private const int ConfirmedTypeId = 2;
    private const int MsfProviderId = 6;
    private const string BaseUrl = "https://api.mysportsfeeds.com/v2.1/pull/nhl/";

    private static readonly Regex ForwardSlot = new(@"^ForwardLine(\d)-(LW|C|RW)$", RegexOptions.IgnoreCase);
    private static readonly Regex DefenseSlot = new(@"^DefensePair(\d)-(L|R)$", RegexOptions.IgnoreCase);
    private static readonly Regex StarterSlot = new(@"^Goalie(-Starter)?$", RegexOptions.IgnoreCase);
    private const string MsfExpected = "MySportsFeeds expected";
    private const string MsfConfirmed = "MySportsFeeds confirmed";

    private readonly NhlContext _nhl;

    public NhlLinesCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromMinutes(Math.Max(5, nhl.Settings.LinesIntervalMinutes));
    }

    public override string Name => "NHL lines";

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
        public string TeamId = "";
        public bool IsActual;
        public List<MsfSlot> Slots = new();
        public MsfSlot? Goalie;
        public bool GoalieIsActual;
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
        var key = _nhl.Settings.MySportsFeedsKey;
        if (string.IsNullOrWhiteSpace(key)) return Failed("Nhl:MySportsFeedsKey is not set.");

        var today = NhlContext.NowEastern().Date;
        var days = Enumerable.Range(0, Math.Max(0, _nhl.Settings.LinesDaysAhead) + 1).Select(i => today.AddDays(i)).ToList();

        var msfGames = new List<MsfGame>();
        foreach (var day in days)
        {
            var fetched = await FetchAsync(key, $"current/date/{day:yyyyMMdd}/games.json", ct);
            if (fetched.Doc == null) return Failed($"Games feed failed for {day:M/d}.", fetched.Error);
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
                        HomeTeam = Str(s.GetProperty("homeTeam"), "id"),
                        AwayTeam = Str(s.GetProperty("awayTeam"), "id")
                    });
                }
            }
        }

        if (msfGames.Count == 0) return Ok("No games today.");

        var failures = new List<string>();
        foreach (var game in msfGames)
        {
            var fetched = await FetchAsync(key, $"current/games/{game.Id}/lineup.json", ct);
            if (fetched.Doc == null)
            {
                failures.Add($"Game {game.Id}: {fetched.Error}");
                if (fetched.Error != null && fetched.Error.StartsWith("HTTP 4", StringComparison.Ordinal)) break;
                continue;
            }
            using (fetched.Doc)
            {
                if (!fetched.Doc.RootElement.TryGetProperty("teamLineups", out var teams)) continue;
                foreach (var t in teams.EnumerateArray())
                {
                    var actual = ReadSlots(t, "actual");
                    var expected = ReadSlots(t, "expected");
                    var use = actual.Count > 0 ? actual : expected;
                    var actualGoalie = ReadStarter(t, "actual");
                    var goalie = actualGoalie ?? ReadStarter(t, "expected");
                    if (use.Count == 0 && goalie == null) continue;
                    game.Lineups.Add(new MsfTeamLineup
                    {
                        TeamId = Str(t.GetProperty("team"), "id"),
                        IsActual = actual.Count > 0,
                        Slots = use,
                        Goalie = goalie,
                        GoalieIsActual = actualGoalie != null
                    });
                }
            }
            await Task.Delay(300, ct);
        }

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            var seasonId = _nhl.SeasonId;

            var positionIds = (await Query(conn, "SELECT Id, Abbreviation FROM Positions"))
                .GroupBy(r => Convert.ToString(r[1])!.Trim().ToUpperInvariant())
                .ToDictionary(gr => gr.Key, gr => Convert.ToInt32(gr.First()[0]));
            if (!positionIds.TryGetValue("G", out var goaliePositionId)) return Failed("No G position in Positions.");

            var teamIds = new Dictionary<string, int>();
            foreach (var r in await Query(conn, "SELECT Id, MySportsFeedId FROM Teams WHERE MySportsFeedId IS NOT NULL AND MySportsFeedId <> ''"))
                teamIds[Convert.ToString(r[1])!.Trim()] = Convert.ToInt32(r[0]);

            var from = days.First();
            var to = days.Last().AddDays(1);
            var ourGames = (await Query(conn, @"
SELECT Id, GameDate, HomeTeamId, AwayTeamId, MySportFeedId
FROM Games WHERE SeasonId = @s AND GameDate >= @from AND GameDate < @to",
                ("@s", seasonId), ("@from", from), ("@to", to)))
                .Select(r => new
                {
                    Id = Convert.ToInt32(r[0]),
                    Date = Convert.ToDateTime(r[1]).Date,
                    Home = r[2] == DBNull.Value ? 0 : Convert.ToInt32(r[2]),
                    Away = r[3] == DBNull.Value ? 0 : Convert.ToInt32(r[3]),
                    Msf = r[4] == DBNull.Value ? "" : Convert.ToString(r[4])!.Trim()
                }).ToList();

            var playerByMsf = new Dictionary<string, int>();
            var playersWithMsf = new HashSet<int>();
            foreach (var r in await Query(conn, "SELECT ProviderId, PlayerId FROM FantasyProviderPlayers WHERE FantasyProviderId = @f", ("@f", MsfProviderId)))
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
            var teamOf = names.GroupBy(n => n.PlayerId).ToDictionary(gr => gr.Key, gr => gr.First().TeamId);
            foreach (var r in await Query(conn, @"
SELECT DISTINCT a.PlayerId, a.Alias
FROM PlayerAliases a JOIN SeasonPlayers sp ON sp.PlayerId = a.PlayerId AND sp.SeasonId = @s", ("@s", seasonId)))
            {
                var pid = Convert.ToInt32(r[0]);
                names.Add((pid, teamOf.TryGetValue(pid, out var t) ? t : 0, Norm(Convert.ToString(r[1]))));
            }

            var existing = new Dictionary<(int Game, int Team), List<(int Player, int Type, string Details)>>();
            foreach (var r in await Query(conn, @"
SELECT s.GameId, s.TeamId, s.PlayerId, s.PlayerGameStateTypeId, s.Details
FROM PlayerGameStates s JOIN Games g ON g.Id = s.GameId
WHERE g.SeasonId = @s AND g.GameDate >= @from AND g.GameDate < @to
  AND (s.PositionId IS NULL OR s.PositionId <> @gpos)",
                ("@s", seasonId), ("@from", from), ("@to", to), ("@gpos", goaliePositionId)))
            {
                var k = (Convert.ToInt32(r[0]), Convert.ToInt32(r[1]));
                if (!existing.TryGetValue(k, out var list)) existing[k] = list = new();
                list.Add((Convert.ToInt32(r[2]), Convert.ToInt32(r[3]), r[4] == DBNull.Value ? "" : Convert.ToString(r[4])!));
            }

            var goalieRows = new Dictionary<(int Game, int Team), List<(int Player, int Type, string Details)>>();
            foreach (var r in await Query(conn, @"
SELECT s.GameId, s.TeamId, s.PlayerId, s.PlayerGameStateTypeId, s.Details
FROM PlayerGameStates s JOIN Games g ON g.Id = s.GameId
WHERE g.SeasonId = @s AND g.GameDate >= @from AND g.GameDate < @to AND s.PositionId = @gpos",
                ("@s", seasonId), ("@from", from), ("@to", to), ("@gpos", goaliePositionId)))
            {
                var k = (Convert.ToInt32(r[0]), Convert.ToInt32(r[1]));
                if (!goalieRows.TryGetValue(k, out var list)) goalieRows[k] = list = new();
                list.Add((Convert.ToInt32(r[2]), Convert.ToInt32(r[3]), r[4] == DBNull.Value ? "" : Convert.ToString(r[4])!));
            }

            var nowUtc = DateTime.UtcNow;
            int saved = 0, confirmed = 0, unchanged = 0, waiting = 0, linked = 0, goaliesSet = 0;
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
                        ("@f", MsfProviderId), ("@p", match.Value), ("@m", slot.PlayerId));
                    linked++;
                }
                return match.Value;
            }

            foreach (var game in msfGames)
            {
                if (!teamIds.TryGetValue(game.HomeTeam, out var homeId) || !teamIds.TryGetValue(game.AwayTeam, out var awayId))
                {
                    notes.Add($"Game {game.Id}: team not mapped ({game.AwayTeam} @ {game.HomeTeam})");
                    continue;
                }

                var ours = ourGames.FirstOrDefault(g => g.Msf == game.Id)
                           ?? ourGames.FirstOrDefault(g => g.Date == game.Day && g.Home == homeId && g.Away == awayId);
                if (ours == null)
                {
                    notes.Add($"Game {game.Id}: no matching game on {game.Day:M/d}");
                    continue;
                }
                if (ours.Msf != game.Id)
                    await Exec(conn, "UPDATE Games SET MySportFeedId = @m WHERE Id = @g", ("@m", game.Id), ("@g", ours.Id));

                if (game.Lineups.Count == 0) { waiting++; continue; }

                foreach (var lineup in game.Lineups)
                {
                    if (!teamIds.TryGetValue(lineup.TeamId, out var teamId)) continue;

                    if (lineup.Goalie != null)
                    {
                        var goalieId = await Resolve(lineup.Goalie, teamId);
                        if (goalieId.HasValue)
                        {
                            var gType = lineup.GoalieIsActual ? ConfirmedTypeId : ProbableTypeId;
                            var gDetails = lineup.GoalieIsActual ? MsfConfirmed : MsfExpected;
                            goalieRows.TryGetValue((ours.Id, teamId), out var grows);
                            grows ??= new();
                            var keep = grows.Any(x => x.Type == ConfirmedTypeId && (gType == ProbableTypeId || !x.Details.StartsWith("MySportsFeeds", StringComparison.Ordinal)));
                            var same = grows.Count == 1 && grows[0] == (goalieId.Value, gType, gDetails);
                            if (!keep && !same)
                            {
                                await Exec(conn, "DELETE FROM PlayerGameStates WHERE GameId = @g AND TeamId = @t AND PositionId = @gpos",
                                    ("@g", ours.Id), ("@t", teamId), ("@gpos", goaliePositionId));
                                await Exec(conn, @"INSERT INTO PlayerGameStates (GameId, PlayerId, TeamId, PositionId, PlayerGameStateTypeId, DateAdded, Details, IsProbableStarter)
VALUES (@g, @p, @t, @pos, @type, @now, @d, 1)",
                                    ("@g", ours.Id), ("@p", goalieId.Value), ("@t", teamId), ("@pos", goaliePositionId),
                                    ("@type", gType), ("@now", nowUtc), ("@d", gDetails));
                                goalieRows[(ours.Id, teamId)] = new() { (goalieId.Value, gType, gDetails) };
                                goaliesSet++;
                            }
                        }
                    }

                    var desired = new List<(int Player, int Position, string Details)>();
                    foreach (var slot in lineup.Slots)
                    {
                        var resolved = await Resolve(slot, teamId);
                        if (!resolved.HasValue) continue;
                        var playerId = resolved.Value;
                        if (!positionIds.TryGetValue(slot.Position, out var positionId)) continue;
                        if (desired.Any(d => d.Player == playerId)) continue;
                        desired.Add((playerId, positionId, slot.Details));
                    }
                    if (desired.Count == 0) continue;

                    var type = lineup.IsActual ? ConfirmedTypeId : ProbableTypeId;
                    existing.TryGetValue((ours.Id, teamId), out var rows);
                    rows ??= new();

                    if (type == ProbableTypeId && rows.Any(x => x.Type == ConfirmedTypeId)) { unchanged++; continue; }

                    var want = desired.Select(d => (d.Player, type, d.Details)).OrderBy(x => x.Player).ToList();
                    var have = rows.OrderBy(x => x.Player).ToList();
                    if (want.SequenceEqual(have)) { unchanged++; continue; }

                    await Exec(conn, "DELETE FROM PlayerGameStates WHERE GameId = @g AND TeamId = @t AND (PositionId IS NULL OR PositionId <> @gpos)",
                        ("@g", ours.Id), ("@t", teamId), ("@gpos", goaliePositionId));
                    foreach (var d in desired)
                    {
                        await Exec(conn, @"INSERT INTO PlayerGameStates (GameId, PlayerId, TeamId, PositionId, PlayerGameStateTypeId, DateAdded, Details, IsProbableStarter)
VALUES (@g, @p, @t, @pos, @type, @now, @d, NULL)",
                            ("@g", ours.Id), ("@p", d.Player), ("@t", teamId), ("@pos", d.Position),
                            ("@type", type), ("@now", nowUtc), ("@d", d.Details));
                    }
                    existing[(ours.Id, teamId)] = want;
                    saved++;
                    if (type == ConfirmedTypeId) confirmed++;
                }
            }

            var details = new List<string>();
            details.AddRange(failures);
            details.AddRange(notes);
            if (unmatched.Count > 0)
            {
                details.Add($"{unmatched.Count} player(s) not matched:");
                details.AddRange(unmatched.Distinct().Take(40).Select(u => "  " + u));
            }

            var message = $"{msfGames.Count} game(s): {saved} team lineup(s) saved ({confirmed} confirmed), {goaliesSet} starting goalie(s) set, {unchanged} unchanged, {waiting} game(s) without lines yet, {linked} player id(s) linked, {unmatched.Distinct().Count()} not matched.";
            if (failures.Count > 0 && saved == 0 && unchanged == 0) return Failed(message, string.Join("\n", details));
            return details.Count > 0 ? Attention(message, string.Join("\n", details)) : Ok(message);
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
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

    private static MsfSlot? ReadStarter(JsonElement team, string which)
    {
        if (!team.TryGetProperty(which, out var block) || block.ValueKind != JsonValueKind.Object) return null;
        if (!block.TryGetProperty("lineupPositions", out var slots) || slots.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in slots.EnumerateArray())
        {
            if (!StarterSlot.IsMatch(Str(s, "position"))) continue;
            if (!s.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            return new MsfSlot
            {
                PlayerId = Str(p, "id"),
                FirstName = Str(p, "firstName"),
                LastName = Str(p, "lastName"),
                Position = "G",
                Details = "G"
            };
        }
        return null;
    }

    private static List<MsfSlot> ReadSlots(JsonElement team, string which)
    {
        var result = new List<MsfSlot>();
        if (!team.TryGetProperty(which, out var block) || block.ValueKind != JsonValueKind.Object) return result;
        if (!block.TryGetProperty("lineupPositions", out var slots) || slots.ValueKind != JsonValueKind.Array) return result;

        foreach (var s in slots.EnumerateArray())
        {
            if (!s.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            var position = Str(s, "position");

            string details, pos;
            var f = ForwardSlot.Match(position);
            var d = DefenseSlot.Match(position);
            if (f.Success)
            {
                pos = f.Groups[2].Value.ToUpperInvariant();
                details = $"L{f.Groups[1].Value}-{pos}";
            }
            else if (d.Success)
            {
                pos = "D";
                details = $"D{d.Groups[1].Value}-{d.Groups[2].Value.ToUpperInvariant()}";
            }
            else continue;

            result.Add(new MsfSlot
            {
                PlayerId = Str(p, "id"),
                FirstName = Str(p, "firstName"),
                LastName = Str(p, "lastName"),
                Position = pos,
                Details = details
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
            using var response = await _nhl.Http.SendAsync(request, ct);
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
