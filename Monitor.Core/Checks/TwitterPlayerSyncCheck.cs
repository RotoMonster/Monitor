using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Monitor.Core.Checks;

public class TwitterPlayerSyncCheck : MonitorCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> CommonWordSurnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Love", "Green", "White", "Young", "Brown", "Black", "Gray", "Grey", "Price", "Rice", "Hill", "King",
        "Bell", "Long", "Strong", "Wright", "Hunt", "Moore", "Little", "Small", "Short", "Head", "Hand", "Wood",
        "Woods", "Rivers", "Field", "Fields", "Cash", "Banks", "Miles", "Hope", "Hart", "Day", "Knight", "Bishop",
        "Cook", "Major", "Free", "Best", "Story", "Swift", "Rush", "Waters", "Winter", "Summer", "Rose", "Stone",
        "Booker", "Hunter", "Mason", "Carter", "Walker", "Golden", "Gay", "Parks", "Ford", "Page", "Lane", "Chase"
    };

    private readonly HttpClient _http;
    private readonly TwitterApiSettings _settings;
    private readonly string _connectionString;
    private readonly Func<int> _seasonId;
    private readonly int _sportId;
    private readonly int _idOffset;
    private readonly string _sportName;

    public TwitterPlayerSyncCheck(HttpClient http, TwitterApiSettings settings, string connectionString, Func<int> seasonId, int sportId, int idOffset, string sportName)
    {
        _http = http;
        _settings = settings;
        _connectionString = connectionString;
        _seasonId = seasonId;
        _sportId = sportId;
        _idOffset = idOffset;
        _sportName = sportName;
        Interval = TimeSpan.FromHours(Math.Max(1, settings.PlayerSyncIntervalHours));
    }

    public override string Name => _sportName + " Twitter players";

    public override string Category => "Twitter";

    private class ImportResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int PlayersImported { get; set; }
        public int TeamsImported { get; set; }
        public int Skipped { get; set; }
    }

    protected override async Task<CheckResult> ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey)) return Failed("No Twitter API key configured.");
        if (_sportId <= 0) return Failed("No Twitter sport id configured.");

        var seasonId = _seasonId();
        var teams = new List<object>();
        var players = new List<object>();

        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct);

            var teamAliases = new Dictionary<int, List<string>>();
            await using (var cmd = new SqlCommand("SELECT TeamId, Alias FROM TeamAliases WHERE Alias IS NOT NULL AND Alias <> ''", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    if (!teamAliases.TryGetValue(id, out var list)) teamAliases[id] = list = new List<string>();
                    list.Add(r.GetString(1).Trim());
                }
            }

            await using (var cmd = new SqlCommand(@"
SELECT t.Id, t.Code, t.Name
FROM Teams t
WHERE t.Id IN (SELECT st.TeamId FROM SeasonTeams st WHERE st.SeasonId = @s)", conn))
            {
                cmd.Parameters.AddWithValue("@s", seasonId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    var code = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var name = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                    if (name.Length == 0) continue;

                    var aliases = teamAliases.TryGetValue(id, out var a) ? new List<string>(a) : new List<string>();
                    var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 1) aliases.Add(words[^1]);

                    teams.Add(new
                    {
                        TeamId = _idOffset + id,
                        City = "",
                        Name = name,
                        Abbreviation = code,
                        Aliases = aliases.Where(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    });
                }
            }

            var playerAliases = new Dictionary<int, List<string>>();
            await using (var cmd = new SqlCommand("SELECT PlayerId, Alias FROM PlayerAliases WHERE Alias IS NOT NULL AND Alias <> ''", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    if (!playerAliases.TryGetValue(id, out var list)) playerAliases[id] = list = new List<string>();
                    list.Add(r.GetString(1).Trim());
                }
            }

            await using (var cmd = new SqlCommand(@"
SELECT sp.PlayerId, p.FirstName, p.LastName, MAX(sp.TeamId)
FROM SeasonPlayers sp JOIN Players p ON p.Id = sp.PlayerId
WHERE sp.SeasonId = @s
GROUP BY sp.PlayerId, p.FirstName, p.LastName", conn))
            {
                cmd.Parameters.AddWithValue("@s", seasonId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    var first = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var last = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                    if (last.Length == 0) continue;
                    int? teamId = r.IsDBNull(3) ? null : _idOffset + r.GetInt32(3);

                    players.Add(new
                    {
                        PlayerId = _idOffset + id,
                        FirstName = first,
                        LastName = last,
                        TeamId = teamId,
                        FullNameOnly = CommonWordSurnames.Contains(last),
                        Aliases = playerAliases.TryGetValue(id, out var a) ? a.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>()
                    });
                }
            }
        }

        if (players.Count == 0) return Failed($"No players found for season {seasonId}, nothing sent.");

        var payload = JsonSerializer.Serialize(new { SportId = _sportId, Players = players, Teams = teams }, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.BaseUrl.TrimEnd('/')}/api/import/players")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", _settings.ApiKey);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return Failed($"Import returned {(int)response.StatusCode}.", body);

        var result = JsonSerializer.Deserialize<ImportResponse>(body, JsonOptions);
        if (result == null) return Failed("Could not read the import response.", body);
        if (!result.Success) return Failed(result.ErrorMessage ?? "Import failed.", body);

        return Ok($"{result.PlayersImported} players and {result.TeamsImported} teams sent as sport {_sportId}" + (result.Skipped > 0 ? $", {result.Skipped} skipped." : "."));
    }
}
