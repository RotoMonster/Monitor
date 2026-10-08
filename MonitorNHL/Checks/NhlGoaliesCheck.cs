using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;
using RotoMonster.Data;
using RotoMonsterExternalAPIs.Client.Services.Providers;

namespace MonitorNHL.Checks;

public class NhlGoaliesCheck : MonitorCheck
{
    private const int ProbableTypeId = 1;
    private const int ConfirmedTypeId = 2;
    private const string StarterTitle = "Starting Goalie";
    private const string TandemTitle = "Goalie Tandem";
    private const string BackupTitle = "Backup Goalie";

    private readonly NhlContext _nhl;

    public NhlGoaliesCheck(NhlContext nhl)
    {
        _nhl = nhl;
        Interval = TimeSpan.FromMinutes(Math.Max(15, nhl.Settings.GoaliesIntervalMinutes));
    }

    public override string Name => "NHL goalies";

    public override string Category => "NHL API";

    private class TeamPlan
    {
        public int TeamId;
        public string Code = "";
        public List<int> Goalies = new();
        public Dictionary<int, double> Score = new();
        public Dictionary<int, int> StartsThisSeason = new();
        public int GamesThisSeason;
        public bool Tandem;
        public int? LastStarter;
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var rosters = await _nhl.Nhl.GetPlayersAsync(_nhl.Sport, null);
        if (!rosters.Success) return Failed("Rosters feed failed.", rosters.ErrorMessage);

        await _nhl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nhl.CreateDb();
            var sync = new NHLDataSync(db);
            var seasonId = _nhl.SeasonId;
            var lastSeasonId = seasonId - 10;
            var players = await sync.GetProviderPlayerMapAsync();
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

            var goalieIdRows = await Query(conn, "SELECT Id FROM Positions WHERE Abbreviation = 'G'");
            int? goaliePositionId = goalieIdRows.Count > 0 ? Convert.ToInt32(goalieIdRows[0][0]) : null;

            var plans = new Dictionary<int, TeamPlan>();
            foreach (var p in rosters.Players.Where(p => string.Equals(p.Position, "G", StringComparison.OrdinalIgnoreCase)))
            {
                if (!players.TryGetValue(p.PlayerId, out var playerId)) continue;
                var teamId = await sync.GetTeamIdAsync(p.TeamCode);
                if (!teamId.HasValue) continue;
                if (!plans.TryGetValue(teamId.Value, out var plan))
                    plans[teamId.Value] = plan = new TeamPlan { TeamId = teamId.Value, Code = p.TeamCode };
                plan.Goalies.Add(playerId);
            }

            var startsThis = await Query(conn, @"
SELECT gg.TeamId, gg.PlayerId, COUNT(*)
FROM NHLGoalieGames gg JOIN Games g ON g.Id = gg.GameId
WHERE g.SeasonId = @s AND gg.Started = 1
GROUP BY gg.TeamId, gg.PlayerId", ("@s", seasonId));
            var startsLast = await Query(conn, @"
SELECT gg.PlayerId, COUNT(*)
FROM NHLGoalieGames gg JOIN Games g ON g.Id = gg.GameId
WHERE g.SeasonId = @s AND gg.Started = 1
GROUP BY gg.PlayerId", ("@s", lastSeasonId));
            var lastByPlayer = startsLast.ToDictionary(r => Convert.ToInt32(r[0]), r => Convert.ToInt32(r[1]));

            var thisByTeamPlayer = new Dictionary<(int, int), int>();
            foreach (var r in startsThis) thisByTeamPlayer[(Convert.ToInt32(r[0]), Convert.ToInt32(r[1]))] = Convert.ToInt32(r[2]);

            var teamGames = await Query(conn, @"
SELECT g.Id, g.GameDate, g.GameTime, g.IsFinished, g.HomeTeamId, g.AwayTeamId
FROM Games g WHERE g.SeasonId = @s ORDER BY g.GameTime", ("@s", seasonId));
            var games = teamGames.Select(r => new
            {
                Id = Convert.ToInt32(r[0]),
                Date = Convert.ToDateTime(r[1]).Date,
                Time = Convert.ToDateTime(r[2]),
                Finished = Convert.ToBoolean(r[3]),
                Home = r[4] == DBNull.Value ? 0 : Convert.ToInt32(r[4]),
                Away = r[5] == DBNull.Value ? 0 : Convert.ToInt32(r[5])
            }).ToList();

            var lastStarterRows = await Query(conn, @"
SELECT x.TeamId, x.PlayerId FROM (
  SELECT gg.TeamId, gg.PlayerId, ROW_NUMBER() OVER (PARTITION BY gg.TeamId ORDER BY g.GameTime DESC) AS rn
  FROM NHLGoalieGames gg JOIN Games g ON g.Id = gg.GameId
  WHERE g.SeasonId = @s AND gg.Started = 1) x
WHERE x.rn = 1", ("@s", seasonId));
            var lastStarter = lastStarterRows.ToDictionary(r => Convert.ToInt32(r[0]), r => Convert.ToInt32(r[1]));

            var injured = _nhl.InjuredPlayerIds;
            var shareCutoff = _nhl.Settings.GoalieStarterShare;

            foreach (var plan in plans.Values)
            {
                plan.GamesThisSeason = games.Count(g => g.Finished && (g.Home == plan.TeamId || g.Away == plan.TeamId));
                var lastWeight = Math.Max(0, 10 - plan.GamesThisSeason) / 82.0 * 0.6;
                foreach (var gid in plan.Goalies)
                {
                    thisByTeamPlayer.TryGetValue((plan.TeamId, gid), out var s);
                    lastByPlayer.TryGetValue(gid, out var l);
                    plan.StartsThisSeason[gid] = s;
                    plan.Score[gid] = s + l * lastWeight * 10;
                }
                plan.Goalies = plan.Goalies.OrderByDescending(g => plan.Score[g]).ToList();
                var healthy = plan.Goalies.Where(g => !injured.Contains(g)).ToList();
                if (healthy.Count >= 2)
                {
                    var a = plan.Score[healthy[0]];
                    var b = plan.Score[healthy[1]];
                    plan.Tandem = a + b > 0 && a / (a + b) < shareCutoff;
                }
                if (lastStarter.TryGetValue(plan.TeamId, out var ls)) plan.LastStarter = ls;
            }

            var todayEastern = NhlContext.NowEastern().Date;
            var horizon = todayEastern.AddDays(Math.Max(1, _nhl.Settings.GoalieDaysAhead));
            var nowUtc = DateTime.UtcNow;

            var existingRows = await Query(conn, @"
SELECT s.GameId, s.TeamId, s.PlayerId, s.PlayerGameStateTypeId
FROM PlayerGameStates s JOIN Games g ON g.Id = s.GameId
WHERE g.SeasonId = @s", ("@s", seasonId));
            var existing = new Dictionary<(int Game, int Team), List<(int Player, int Type)>>();
            foreach (var r in existingRows)
            {
                var k = (Convert.ToInt32(r[0]), Convert.ToInt32(r[1]));
                if (!existing.TryGetValue(k, out var list)) existing[k] = list = new();
                list.Add((Convert.ToInt32(r[2]), Convert.ToInt32(r[3])));
            }

            int probableWritten = 0, confirmedWritten = 0, unchanged = 0;
            var notes = new List<string>();

            var startedRows = await Query(conn, @"
SELECT gg.GameId, gg.TeamId, gg.PlayerId
FROM NHLGoalieGames gg JOIN Games g ON g.Id = gg.GameId
WHERE g.SeasonId = @s AND gg.Started = 1", ("@s", seasonId));
            foreach (var r in startedRows)
            {
                int gameId = Convert.ToInt32(r[0]), teamId = Convert.ToInt32(r[1]), playerId = Convert.ToInt32(r[2]);
                existing.TryGetValue((gameId, teamId), out var rows);
                if (rows != null && rows.Count == 1 && rows[0] == (playerId, ConfirmedTypeId)) { unchanged++; continue; }
                await Exec(conn, "DELETE FROM PlayerGameStates WHERE GameId = @g AND TeamId = @t",
                    ("@g", gameId), ("@t", teamId));
                await Exec(conn, @"INSERT INTO PlayerGameStates (GameId, PlayerId, TeamId, PositionId, PlayerGameStateTypeId, DateAdded, Details, IsProbableStarter)
VALUES (@g, @p, @t, @pos, @type, @now, @d, NULL)",
                    ("@g", gameId), ("@p", playerId), ("@t", teamId), ("@pos", (object?)goaliePositionId ?? DBNull.Value),
                    ("@type", ConfirmedTypeId), ("@now", nowUtc), ("@d", "Started"));
                existing[(gameId, teamId)] = new() { (playerId, ConfirmedTypeId) };
                confirmedWritten++;
            }

            foreach (var plan in plans.Values)
            {
                var healthy = plan.Goalies.Where(g => !injured.Contains(g)).ToList();
                if (healthy.Count == 0) { notes.Add($"{plan.Code}: no healthy goalie on the roster"); continue; }

                var teamSchedule = games.Where(g => g.Home == plan.TeamId || g.Away == plan.TeamId).OrderBy(g => g.Time).ToList();
                var upcoming = teamSchedule.Where(g => !g.Finished && g.Time > nowUtc && g.Date >= todayEastern && g.Date <= horizon).ToList();
                var previousStarter = plan.LastStarter;

                foreach (var game in upcoming)
                {
                    var index = teamSchedule.IndexOf(game);
                    var backToBack = index > 0 && (game.Date - teamSchedule[index - 1].Date).TotalDays == 1;

                    int pick;
                    string why;
                    if (healthy.Count == 1)
                    {
                        pick = healthy[0];
                        why = "Only healthy goalie";
                    }
                    else if (plan.Tandem)
                    {
                        pick = previousStarter.HasValue && previousStarter.Value == healthy[0] ? healthy[1] : healthy[0];
                        why = "Tandem, alternating starts";
                    }
                    else if (backToBack)
                    {
                        pick = healthy[1];
                        why = "Back-to-back, second night goes to the backup";
                    }
                    else
                    {
                        pick = healthy[0];
                        var s = plan.StartsThisSeason.TryGetValue(pick, out var n) ? n : 0;
                        why = plan.GamesThisSeason > 0 ? $"Starter, {s} of {plan.GamesThisSeason} starts" : "Starter";
                    }
                    previousStarter = pick;

                    existing.TryGetValue((game.Id, plan.TeamId), out var rows);
                    if (rows != null && rows.Any(x => x.Type == ConfirmedTypeId)) { unchanged++; continue; }
                    if (rows != null && rows.Count == 1 && rows[0] == (pick, ProbableTypeId)) { unchanged++; continue; }

                    await Exec(conn, "DELETE FROM PlayerGameStates WHERE GameId = @g AND TeamId = @t AND PlayerGameStateTypeId = @type",
                        ("@g", game.Id), ("@t", plan.TeamId), ("@type", ProbableTypeId));
                    await Exec(conn, @"INSERT INTO PlayerGameStates (GameId, PlayerId, TeamId, PositionId, PlayerGameStateTypeId, DateAdded, Details, IsProbableStarter)
VALUES (@g, @p, @t, @pos, @type, @now, @d, 1)",
                        ("@g", game.Id), ("@p", pick), ("@t", plan.TeamId), ("@pos", (object?)goaliePositionId ?? DBNull.Value),
                        ("@type", ProbableTypeId), ("@now", nowUtc), ("@d", why));
                    probableWritten++;
                }
            }

            var roleChanges = await UpdateRolesAsync(conn, plans, injured, nowUtc, ct);

            var tandems = plans.Values.Where(p => p.Tandem).Select(p => p.Code).OrderBy(c => c).ToList();
            var details = new List<string>
            {
                $"{plans.Count} teams, {plans.Count - tandems.Count} with a main starter, {tandems.Count} tandems" + (tandems.Count > 0 ? ": " + string.Join(", ", tandems) : "")
            };
            details.AddRange(roleChanges);
            details.AddRange(notes);

            var message = $"{probableWritten} probable starter(s) set, {confirmedWritten} confirmed, {roleChanges.Count} role change(s), {unchanged} unchanged.";
            return notes.Count > 0 ? Attention(message, string.Join("\n", details)) : Ok(message, string.Join("\n", details));
        }
        finally
        {
            _nhl.StatsWriteLock.Release();
        }
    }

    private async Task<List<string>> UpdateRolesAsync(DbConnection conn, Dictionary<int, TeamPlan> plans, HashSet<int> injured, DateTime nowUtc, CancellationToken ct)
    {
        var typeIds = new Dictionary<string, int>();
        foreach (var (title, format, bg) in new[] { (StarterTitle, "G1", "1d4ed8"), (TandemTitle, "1A/1B", "7c3aed"), (BackupTitle, "G2", "64748b") })
        {
            var found = await Query(conn, "SELECT TOP 1 Id FROM PlayerStatusTypes WHERE Title = @t", ("@t", title));
            if (found.Count == 0)
            {
                await Exec(conn, @"INSERT INTO PlayerStatusTypes (Title, BackgroundColor, TextColor, TextFormat, AutoClear, UsesDate, ShowInDaily, AllowFilter, AppliesToNextGame, IsInGame, IsUndetermined, ShowOnPlayerProfile)
VALUES (@t, @bg, 'FFFFFF', @f, 0, 0, 0, 1, 0, 0, 0, 1)", ("@t", title), ("@bg", bg), ("@f", format));
                found = await Query(conn, "SELECT TOP 1 Id FROM PlayerStatusTypes WHERE Title = @t", ("@t", title));
            }
            typeIds[title] = Convert.ToInt32(found[0][0]);
        }

        var roleTypeIds = typeIds.Values.ToList();
        var active = await Query(conn, $"SELECT Id, PlayerId, PlayerStatusTypeId FROM PlayerStatuses WHERE IsActive = 1 AND PlayerStatusTypeId IN ({string.Join(",", roleTypeIds)})");
        var current = new Dictionary<int, (int StatusId, int TypeId)>();
        foreach (var r in active) current[Convert.ToInt32(r[1])] = (Convert.ToInt32(r[0]), Convert.ToInt32(r[2]));

        var changes = new List<string>();
        foreach (var plan in plans.Values)
        {
            var healthy = plan.Goalies.Where(g => !injured.Contains(g)).ToList();
            foreach (var goalie in plan.Goalies)
            {
                if (injured.Contains(goalie)) continue;

                string role;
                if (plan.Tandem && healthy.Count >= 2 && (goalie == healthy[0] || goalie == healthy[1])) role = TandemTitle;
                else if (healthy.Count > 0 && goalie == healthy[0]) role = StarterTitle;
                else role = BackupTitle;

                var typeId = typeIds[role];
                var starts = plan.StartsThisSeason.TryGetValue(goalie, out var n) ? n : 0;
                var comment = plan.GamesThisSeason > 0 ? $"Started {starts} of {plan.GamesThisSeason} games" : "Based on last season";

                if (current.TryGetValue(goalie, out var cur))
                {
                    if (cur.TypeId == typeId)
                    {
                        await Exec(conn, "UPDATE PlayerStatuses SET Comment = @c WHERE Id = @id", ("@c", comment), ("@id", cur.StatusId));
                        continue;
                    }
                    await Exec(conn, "UPDATE PlayerStatuses SET IsActive = 0, DateDeactivated = @now WHERE Id = @id", ("@now", nowUtc), ("@id", cur.StatusId));
                }

                await Exec(conn, @"INSERT INTO PlayerStatuses (PlayerId, GameId, IsActive, PlayerStatusTypeId, DateAdded, Comment, Source)
VALUES (@p, NULL, 1, @type, @now, @c, 'RotoMonster')", ("@p", goalie), ("@type", typeId), ("@now", nowUtc), ("@c", comment));
                changes.Add($"{plan.Code}: player {goalie} is now {role}");
            }
        }
        return changes;
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
