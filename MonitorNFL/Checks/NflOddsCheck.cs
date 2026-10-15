using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monitor.Core;

namespace MonitorNFL.Checks;

public class NflOddsCheck : MonitorCheck
{
    private const string Url = "https://api.jsonodds.com/odds/nfl";

    private readonly NflContext _nfl;

    public NflOddsCheck(NflContext nfl)
    {
        _nfl = nfl;
        Interval = TimeSpan.FromMinutes(Math.Max(10, nfl.Settings.OddsIntervalMinutes));
    }

    public override string Name => "NFL odds";

    public override string Category => "JSONOdds";

    private class Line
    {
        public string Home = "";
        public string Away = "";
        public DateTime StartUtc;
        public int? HomeMoneyLine;
        public int? AwayMoneyLine;
        public double? Total;
        public double? HomeSpread;
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        var key = _nfl.Settings.JsonOddsKey;
        if (string.IsNullOrWhiteSpace(key)) return Failed("Nfl:JsonOddsKey is not set.");

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("x-api-key", key);
        using var response = await _nfl.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return Failed($"JSONOdds returned HTTP {(int)response.StatusCode}.", body.Length > 500 ? body[..500] : body);

        var lines = new List<Line>();
        using (var doc = JsonDocument.Parse(body))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("Odds", out var odds) || odds.ValueKind != JsonValueKind.Array) continue;
                var game = odds.EnumerateArray().FirstOrDefault(o => string.Equals(Str(o, "OddType"), "Game", StringComparison.OrdinalIgnoreCase));
                if (game.ValueKind != JsonValueKind.Object) continue;
                if (!DateTime.TryParse(Str(e, "MatchTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)) continue;

                lines.Add(new Line
                {
                    Home = Str(e, "HomeTeam"),
                    Away = Str(e, "AwayTeam"),
                    StartUtc = start,
                    HomeMoneyLine = Int(Str(game, "MoneyLineHome")),
                    AwayMoneyLine = Int(Str(game, "MoneyLineAway")),
                    Total = Num(Str(game, "TotalNumber")),
                    HomeSpread = Num(Str(game, "PointSpreadHome"))
                });
            }
        }

        if (lines.Count == 0) return Ok("No NFL odds posted.");

        await _nfl.StatsWriteLock.WaitAsync(ct);
        try
        {
            using var db = _nfl.CreateDb();
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

            var teamByName = new Dictionary<string, int>();
            foreach (var r in await Query(conn, "SELECT Id, Name, Code FROM Teams"))
            {
                var id = Convert.ToInt32(r[0]);
                teamByName[Norm(Convert.ToString(r[1]))] = id;
                teamByName.TryAdd(Norm(Convert.ToString(r[2])), id);
            }
            try
            {
                foreach (var r in await Query(conn, "SELECT TeamId, Alias FROM TeamAliases"))
                    teamByName.TryAdd(Norm(Convert.ToString(r[1])), Convert.ToInt32(r[0]));
            }
            catch (DbException)
            {
            }

            var from = NflContext.NowEastern().Date.AddDays(-1);
            var games = (await Query(conn, @"
SELECT Id, GameDate, HomeTeamId, AwayTeamId, GameTime, HomeMoneyLine, AwayMoneyLine, OverUnder, HomeSpread
FROM Games WHERE SeasonId = @s AND GameDate >= @from", ("@s", _nfl.SeasonId), ("@from", from)))
                .Select(r => new
                {
                    Id = Convert.ToInt32(r[0]),
                    Date = Convert.ToDateTime(r[1]).Date,
                    Home = r[2] == DBNull.Value ? 0 : Convert.ToInt32(r[2]),
                    Away = r[3] == DBNull.Value ? 0 : Convert.ToInt32(r[3]),
                    Time = r[4] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r[4]),
                    HomeMl = r[5] == DBNull.Value ? (int?)null : Convert.ToInt32(r[5]),
                    AwayMl = r[6] == DBNull.Value ? (int?)null : Convert.ToInt32(r[6]),
                    Total = r[7] == DBNull.Value ? 0d : Convert.ToDouble(r[7]),
                    Spread = r[8] == DBNull.Value ? 0d : Convert.ToDouble(r[8])
                }).ToList();

            var nowUtc = DateTime.UtcNow;
            int updated = 0, unchanged = 0, started = 0;
            var notes = new List<string>();

            foreach (var line in lines)
            {
                if (!teamByName.TryGetValue(Norm(line.Home), out var homeId) || !teamByName.TryGetValue(Norm(line.Away), out var awayId))
                {
                    notes.Add($"Team not matched: {line.Away} @ {line.Home}");
                    continue;
                }

                var day = NflContext.ToEastern(line.StartUtc).Date;
                var game = games.FirstOrDefault(g => g.Home == homeId && g.Away == awayId && g.Date == day)
                           ?? games.FirstOrDefault(g => g.Home == homeId && g.Away == awayId && Math.Abs((g.Date - day).TotalDays) <= 1);
                if (game == null)
                {
                    notes.Add($"No game for {line.Away} @ {line.Home} on {day:M/d}");
                    continue;
                }

                if (line.StartUtc <= nowUtc || (game.Time.HasValue && game.Time.Value <= nowUtc)) { started++; continue; }

                var total = line.Total ?? game.Total;
                var spread = line.HomeSpread ?? game.Spread;
                var homeMl = line.HomeMoneyLine ?? game.HomeMl;
                var awayMl = line.AwayMoneyLine ?? game.AwayMl;

                if (game.HomeMl == homeMl && game.AwayMl == awayMl && Math.Abs(game.Total - total) < 0.001 && Math.Abs(game.Spread - spread) < 0.001)
                {
                    unchanged++;
                    continue;
                }

                await Exec(conn, "UPDATE Games SET HomeMoneyLine = @hm, AwayMoneyLine = @am, OverUnder = @ou, HomeSpread = @hs WHERE Id = @g",
                    ("@hm", (object?)homeMl ?? DBNull.Value), ("@am", (object?)awayMl ?? DBNull.Value),
                    ("@ou", total), ("@hs", spread), ("@g", game.Id));
                updated++;
            }

            var message = $"{lines.Count} game(s) with odds: {updated} updated, {unchanged} unchanged, {started} already started.";
            return notes.Count > 0 ? Attention(message, string.Join("\n", notes)) : Ok(message);
        }
        finally
        {
            _nfl.StatsWriteLock.Release();
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

    private static int? Int(string s)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
        var i = (int)Math.Round(d);
        return i == 0 ? null : i;
    }

    private static double? Num(string s)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
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
