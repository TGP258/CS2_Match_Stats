using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;

namespace CS2MatchStats;

[MinimumApiVersion(304)]
public class CS2MatchStatsPlugin : BasePlugin
{
    public override string ModuleName => "CS2 Match Stats";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Records match statistics for web viewing";

    private MatchData _currentMatch = new();
    private int _currentRound = 0;
    private readonly string _storagePath;

    // 玩家唯一标识: 人类玩家用 UserId, 机器人用 -(UserId + 1)
    private readonly Dictionary<long, PlayerData> _allPlayers = new();

    public CS2MatchStatsPlugin()
    {
        _storagePath = Path.Combine(Server.GameDirectory, "csgo", "match_history");
        Directory.CreateDirectory(_storagePath);
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        AddCommand("match_save", "Save current match data", (_, _) => SaveMatch());
        AddCommand("match_list", "List saved matches", (caller, _) => ListMatches(caller));
        AddCommand("match_info", "Show current match info", (caller, _) => ShowCurrentMatch(caller));
    }

    private void OnMapStart(string mapName)
    {
        _currentMatch = new MatchData
        {
            MapName = mapName,
            StartTime = DateTime.Now,
            Teams = new Dictionary<string, TeamData>
            {
                { "CT", new TeamData { Name = "Counter-Terrorists" } },
                { "T", new TeamData { Name = "Terrorists" } }
            },
            Rounds = new List<RoundData>()
        };
        _currentRound = 0;
        _allPlayers.Clear();

        Server.PrintToConsole($"[CS2MatchStats] Match started on {mapName}");
    }

    private void OnMapEnd()
    {
        if (_currentMatch.Rounds.Count > 0)
        {
            // 将所有玩家分配到队伍
            foreach (var player in _allPlayers.Values)
            {
                if (string.IsNullOrEmpty(player.TeamKey)) continue;
                if (_currentMatch.Teams.ContainsKey(player.TeamKey))
                {
                    _currentMatch.Teams[player.TeamKey].Players[player.UniqueId] = player;
                }
            }

            SaveMatch();
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _currentRound++;
        _currentMatch.Rounds.Add(new RoundData
        {
            RoundNumber = _currentRound,
            Events = new List<RoundEvent>(),
            PlayerStats = new Dictionary<long, PlayerRoundStats>()
        });

        // 确保当前所有玩家都被记录
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            RegisterPlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var winner = @event.Winner == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        round.Winner = winner;
        round.Reason = @event.Reason;

        // 更新获胜队伍的比分
        _currentMatch.Teams[winner].Score++;

        // 计算MVP
        CalculateRoundMVP(round);

        // 同步玩家分数
        SyncPlayerScores();

        return HookResult.Continue;
    }

    private void CalculateRoundMVP(RoundData round)
    {
        if (round.PlayerStats.Count == 0) return;

        // 找出本回合击杀最多的玩家
        var topKiller = round.PlayerStats
            .OrderByDescending(kv => kv.Value.Kills)
            .FirstOrDefault();

        if (topKiller.Value != null && topKiller.Value.Kills > 0)
        {
            var uniqueId = topKiller.Key;
            if (_allPlayers.TryGetValue(uniqueId, out var player))
            {
                player.MVPs++;
            }
        }
    }

    private void SyncPlayerScores()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            var uniqueId = GetPlayerUniqueId(player);
            if (uniqueId == 0) continue;

            if (_allPlayers.TryGetValue(uniqueId, out var playerData))
            {
                playerData.Score = player.Score;
            }
        }
    }

    private long GetPlayerUniqueId(CCSPlayerController player)
    {
        if (player == null) return 0;
        var userId = player.UserId ?? -1;
        if (userId < 0) return 0;

        if (player.IsBot)
        {
            // 机器人用负数，确保不与人类玩家冲突
            return -(userId + 1);
        }
        else
        {
            // 人类玩家用正数
            return userId;
        }
    }

    private void RegisterPlayer(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return;

        var uniqueId = GetPlayerUniqueId(player);
        if (uniqueId == 0) return;

        if (!_allPlayers.ContainsKey(uniqueId))
        {
            var teamKey = player.Team == CsTeam.CounterTerrorist ? "CT" : "T";
            _allPlayers[uniqueId] = new PlayerData
            {
                UniqueId = uniqueId,
                Name = player.PlayerName ?? "Unknown",
                IsBot = player.IsBot,
                TeamKey = teamKey,
                Score = player.Score
            };
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        // 添加死亡事件
        round.Events.Add(new RoundEvent
        {
            Type = "death",
            Time = Server.CurrentTime,
            Victim = victim?.PlayerName ?? "Unknown",
            Attacker = attacker?.PlayerName ?? "World",
            Weapon = @event.Weapon ?? "",
            Headshot = @event.Headshot
        });

        // 记录受害者死亡
        if (victim != null && victim.IsValid)
        {
            var victimUniqueId = GetPlayerUniqueId(victim);
            if (victimUniqueId == 0) return HookResult.Continue;

            RegisterPlayer(victim);

            if (!round.PlayerStats.TryGetValue(victimUniqueId, out var victimStats))
            {
                victimStats = new PlayerRoundStats();
                round.PlayerStats[victimUniqueId] = victimStats;
            }
            victimStats.Deaths++;
            victimStats.DamageTaken += @event.DmgHealth;

            // 更新总数据
            if (_allPlayers.TryGetValue(victimUniqueId, out var playerData))
            {
                playerData.Deaths++;
            }
        }

        // 记录攻击者击杀
        if (attacker != null && attacker.IsValid && attacker != victim)
        {
            var attackerUniqueId = GetPlayerUniqueId(attacker);
            if (attackerUniqueId == 0) return HookResult.Continue;

            RegisterPlayer(attacker);

            if (!round.PlayerStats.TryGetValue(attackerUniqueId, out var attackerStats))
            {
                attackerStats = new PlayerRoundStats();
                round.PlayerStats[attackerUniqueId] = attackerStats;
            }
            attackerStats.Kills++;
            if (@event.Headshot) attackerStats.Headshots++;
            attackerStats.DamageDealt += @event.DmgHealth;

            // 更新总数据
            if (_allPlayers.TryGetValue(attackerUniqueId, out var playerData))
            {
                playerData.Kills++;
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var attacker = @event.Attacker;

        if (attacker != null && attacker.IsValid && attacker != @event.Userid)
        {
            var attackerUniqueId = GetPlayerUniqueId(attacker);
            if (attackerUniqueId == 0) return HookResult.Continue;

            RegisterPlayer(attacker);

            if (!round.PlayerStats.TryGetValue(attackerUniqueId, out var stats))
            {
                stats = new PlayerRoundStats();
                round.PlayerStats[attackerUniqueId] = stats;
            }
            stats.DamageDealt += @event.DmgHealth;
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var player = @event.Userid;

        round.Events.Add(new RoundEvent
        {
            Type = "bomb_planted",
            Time = Server.CurrentTime,
            Player = player?.PlayerName ?? "Unknown"
        });

        if (player != null && player.IsValid)
        {
            RegisterPlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var player = @event.Userid;

        round.Events.Add(new RoundEvent
        {
            Type = "bomb_defused",
            Time = Server.CurrentTime,
            Player = player?.PlayerName ?? "Unknown"
        });

        if (player != null && player.IsValid)
        {
            RegisterPlayer(player);
        }

        return HookResult.Continue;
    }

    private void SaveMatch()
    {
        if (_currentMatch.Rounds.Count == 0) return;

        _currentMatch.EndTime = DateTime.Now;
        _currentMatch.Duration = (int)(_currentMatch.EndTime - _currentMatch.StartTime).TotalSeconds;

        var fileName = $"match_{_currentMatch.StartTime:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(_storagePath, fileName);

        var json = JsonSerializer.Serialize(_currentMatch, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        Server.PrintToConsole($"[CS2MatchStats] Match saved to {filePath}");
    }

    private void ListMatches(CCSPlayerController? caller)
    {
        var files = Directory.GetFiles(_storagePath, "match_*.json")
            .OrderByDescending(f => f)
            .Take(10);

        var msg = "Saved matches:";
        foreach (var file in files)
        {
            msg += $"\n  - {Path.GetFileName(file)}";
        }

        if (caller != null && caller.IsValid)
        {
            caller.PrintToChat(msg);
        }
        Server.PrintToConsole(msg);
    }

    private void ShowCurrentMatch(CCSPlayerController? caller)
    {
        var msg = $"Map: {_currentMatch.MapName} | Rounds: {_currentMatch.Rounds.Count} | Players: {_allPlayers.Count}";

        if (caller != null && caller.IsValid)
        {
            caller.PrintToChat(msg);
        }
        Server.PrintToConsole(msg);
    }
}

public class MatchData
{
    public string MapName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Duration { get; set; }
    public Dictionary<string, TeamData> Teams { get; set; } = new();
    public List<RoundData> Rounds { get; set; } = new();
}

public class TeamData
{
    public string Name { get; set; } = "";
    public Dictionary<long, PlayerData> Players { get; set; } = new();
    public int Score { get; set; }
}

public class PlayerData
{
    public long UniqueId { get; set; }
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }
    public string TeamKey { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Score { get; set; }
    public int MVPs { get; set; }
}

public class RoundData
{
    public int RoundNumber { get; set; }
    public string Winner { get; set; } = "";
    public int Reason { get; set; }
    public List<RoundEvent> Events { get; set; } = new();
    public Dictionary<long, PlayerRoundStats> PlayerStats { get; set; } = new();
}

public class RoundEvent
{
    public string Type { get; set; } = "";
    public float Time { get; set; }
    public string Player { get; set; } = "";
    public string Victim { get; set; } = "";
    public string Attacker { get; set; } = "";
    public string Weapon { get; set; } = "";
    public bool Headshot { get; set; }
}

public class PlayerRoundStats
{
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Headshots { get; set; }
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
}
