using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Text.Json;
#pragma warning disable CS8604 // Suppress nullable reference warnings from event nullability (we handle null checks inside methods)

namespace CS2MatchStats;

[MinimumApiVersion(304)]
public class CS2MatchStatsPlugin : BasePlugin
{
    public override string ModuleName => "CS2 Match Stats";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Records match statistics for web viewing (no bot/human distinction)";

    private MatchData _currentMatch = new();
    private int _currentRound = 0;
    private readonly string _storagePath;

    // 所有在线玩家统一容器：long = 稳定唯一ID (UserId >= 0 时使用 UserId+1)
    // 不再区分 bot/人类，所有玩家一视同仁
    private readonly ConcurrentDictionary<long, PlayerData> _allPlayers = new();

    // 伤害追踪：Key=受害者ID, Value=(攻击者ID Set, 累计伤害)
    private readonly ConcurrentDictionary<long, HashSet<long>> _damageDealers = new();
    private readonly ConcurrentDictionary<long, int> _damageAmounts = new();

    // 交换击杀：记录每回合死亡信息 (受害者ID -> (凶手ID, 死亡时间))
    private readonly ConcurrentDictionary<long, (long KillerId, float DeathTime)> _playerDeathInfo = new();

    // 最小助攻伤害阈值
    private const int MIN_ASSIST_DAMAGE = 10;
    // 交换击杀时间窗口（秒）
    private const float TRADE_TIME_WINDOW = 4.0f;

    public CS2MatchStatsPlugin()
    {
        _storagePath = Path.Combine(Server.GameDirectory, "csgo", "match_history");
        Directory.CreateDirectory(_storagePath);
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Post);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted, HookMode.Post);
        RegisterEventHandler<EventBombDefused>(OnBombDefused, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        AddCommand("match_save", "Save current match data", (_, _) => SaveMatch());
        AddCommand("match_list", "List saved matches", (caller, _) => ListMatches(caller));
        AddCommand("match_info", "Show current match info", (caller, _) => ShowCurrentMatch(caller));

        Server.PrintToConsole($"[CS2MatchStats] Plugin loaded v{ModuleVersion}");
    }

    // ============================================================
    // 核心：稳定玩家唯一ID获取
    // 策略：优先使用 UserId (所有在线玩家都有, 对 bot 和人类都稳定)
    // 如果 UserId 不可用, 回退到 Slot + 1 (Slot 范围 0-63, 单服务器唯一)
    // 不使用 IsBot 字段 (伪装插件会篡改它)
    // ============================================================
    private long GetPlayerUniqueId(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return 0;

        // 优先使用 UserId
        // 注意: uint? 在某些情况下可能为 null, 需要安全转换
        try
        {
            var userIdRaw = player.UserId;
            if (userIdRaw.HasValue)
            {
                long userId = (long)userIdRaw.Value;
                if (userId >= 0)
                {
                    // +1 防止 UserId=0 的情况被当成"无效"
                    return userId + 1;
                }
            }
        }
        catch { /* ignore */ }

        // 回退: 使用 EntityIndex (EntitySystem.Instance.IndexFor(v) 不稳定, 改用 Index)
        try
        {
            long slot = (long)(player.Index);
            if (slot >= 0)
            {
                // 使用 100000 + slot 作为回退ID, 防止与 UserId 冲突
                return 100000L + slot;
            }
        }
        catch { /* ignore */ }

        return 0;
    }

    // 获取玩家名字的安全方法
    private string GetPlayerName(CCSPlayerController player)
    {
        try
        {
            return string.IsNullOrEmpty(player?.PlayerName) ? "Unknown" : player.PlayerName;
        }
        catch
        {
            return "Unknown";
        }
    }

    // 获取队伍
    private string GetTeamKey(CCSPlayerController player)
    {
        try
        {
            return player.Team switch
            {
                CsTeam.CounterTerrorist => "CT",
                CsTeam.Terrorist => "T",
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    // 获取 SteamID (人类玩家才会有, bot 没有)
    private string GetPlayerSteamId(CCSPlayerController player)
    {
        try
        {
            if (player == null || !player.IsValid) return "";
            // 尝试通过 SteamID 获取
            var sid = player.SteamID;
            return sid.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ============================================================
    // 注册/更新玩家: 统一入口, 所有玩家一视同仁
    // ============================================================
    private PlayerData? RegisterPlayer(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return null;

        long uniqueId = GetPlayerUniqueId(player);
        if (uniqueId == 0) return null;

        string name = GetPlayerName(player);
        string teamKey = GetTeamKey(player);
        string steamId = GetPlayerSteamId(player);
        int score = 0;
        try { score = player.Score; } catch { }

        if (_allPlayers.TryGetValue(uniqueId, out var existing))
        {
            // 更新可能变化的字段
            if (!string.IsNullOrEmpty(name)) existing.Name = name;
            if (!string.IsNullOrEmpty(teamKey)) existing.TeamKey = teamKey;
            if (!string.IsNullOrEmpty(steamId)) existing.SteamId = steamId;
            existing.Score = score;
            return existing;
        }

        var newPlayer = new PlayerData
        {
            UniqueId = uniqueId,
            Name = name,
            IsBot = false, // 不再依赖 IsBot 字段, 统一 false (由外部显示决定)
            SteamId = steamId,
            TeamKey = teamKey,
            Score = score
        };
        _allPlayers[uniqueId] = newPlayer;

        Server.PrintToConsole($"[CS2MatchStats] REGISTER player: {name} (ID={uniqueId}, Team={teamKey}, Steam={steamId})");
        return newPlayer;
    }

    // ============================================================
    // Map 事件: 初始化/保存
    // ============================================================
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
        _damageDealers.Clear();
        _damageAmounts.Clear();
        _playerDeathInfo.Clear();

        Server.PrintToConsole($"[CS2MatchStats] ========== NEW MATCH STARTED on {mapName} ==========");
    }

    private void OnMapEnd()
    {
        Server.PrintToConsole($"[CS2MatchStats] ========== MAP END ==========");
        Server.PrintToConsole($"[CS2MatchStats] Total rounds: {_currentMatch?.Rounds?.Count ?? -1}, Total players tracked: {_allPlayers.Count}");

        if (_currentMatch == null || _currentMatch.Rounds.Count == 0)
        {
            Server.PrintToConsole("[CS2MatchStats] No rounds recorded, skipping save");
            return;
        }

        // 调试: 打印所有已记录玩家
        Server.PrintToConsole("[CS2MatchStats] ---- ALL PLAYERS ----");
        foreach (var kvp in _allPlayers)
        {
            var p = kvp.Value;
            Server.PrintToConsole($"  [{p.TeamKey}] {p.Name} (ID={kvp.Key}) K={p.Kills} D={p.Deaths} A={p.Assists} MVP={p.MVPs} Survived={p.RoundsSurvived}");
        }

        // 将玩家分配到队伍 (用于输出 JSON)
        foreach (var player in _allPlayers.Values)
        {
            if (string.IsNullOrEmpty(player.TeamKey)) continue;
            if (_currentMatch.Teams.ContainsKey(player.TeamKey))
            {
                _currentMatch.Teams[player.TeamKey].Players[player.UniqueId] = player;
            }
        }

        // 计算 Rating
        CalculateRatings();

        SaveMatch();
    }

    // ============================================================
    // Round 事件: 开始/结束
    // ============================================================
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _currentRound++;
        _currentMatch.Rounds.Add(new RoundData
        {
            RoundNumber = _currentRound,
            Events = new List<RoundEvent>(),
            PlayerStats = new Dictionary<long, PlayerRoundStats>()
        });

        _damageDealers.Clear();
        _damageAmounts.Clear();

        // 确保所有当前在线玩家已注册
        int registered = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            if (player.Team == CsTeam.None || player.Team == CsTeam.Spectator) continue;
            if (RegisterPlayer(player) != null) registered++;
        }
        Server.PrintToConsole($"[CS2MatchStats] Round {_currentRound} START - {registered} players registered, total {_allPlayers.Count} tracked");

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var winner = @event.Winner == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        round.Winner = winner;
        round.Reason = @event.Reason;

        if (_currentMatch.Teams.ContainsKey(winner))
        {
            _currentMatch.Teams[winner].Score++;
        }

        // 计算存活回合, 多杀, 交换击杀
        CalculateRoundStats(round);

        // 计算 MVP
        CalculateRoundMVP(round);

        // 同步分数 (从游戏)
        SyncPlayerScores();

        // 清空死亡信息
        _playerDeathInfo.Clear();

        // 打印回合总结
        Server.PrintToConsole($"[CS2MatchStats] Round {_currentRound} END - Winner={winner}, Reason={@event.Reason}, TrackedPlayers={_allPlayers.Count}");

        return HookResult.Continue;
    }

    private void CalculateRoundStats(RoundData round)
    {
        foreach (var (uniqueId, stats) in round.PlayerStats)
        {
            if (!_allPlayers.TryGetValue(uniqueId, out var playerData)) continue;

            // 存活: 本回合没有死亡
            if (stats.Deaths == 0)
            {
                playerData.RoundsSurvived++;
            }

            // 多杀奖励
            if (stats.Kills >= 2)
            {
                playerData.MultiKills += stats.Kills - 1;
            }

            // 总伤害累加
            playerData.TotalDamageDealt += stats.DamageDealt;
            playerData.TotalDamageTaken += stats.DamageTaken;

            // 爆头统计
            playerData.Headshots += stats.Headshots;
        }

        // 计算交换击杀: 遍历本回合死亡记录, 检查死亡后队友是否在窗口期内击杀该凶手
        var deathList = _playerDeathInfo.ToList();
        foreach (var (victimId, info) in deathList)
        {
            var (killerId, deathTime) = info;
            // 查找: 该受害者队友, 在 deathTime + TRADE_TIME_WINDOW 内击杀了 killerId 的记录
            // 简化实现: 如果 killerId 在死亡后较短时间内被同一队伍(受害者队伍)的其他人击杀, 则算交换
            // 这里我们用 round.Events 中的死亡事件回溯
            foreach (var ev in round.Events.Where(e => e.Type == "death" && e.Time > deathTime && e.Time <= deathTime + TRADE_TIME_WINDOW))
            {
                // 找到该事件的凶手
                long evKillerId = FindPlayerIdByName(ev.Attacker);
                long evVictimId = FindPlayerIdByName(ev.Victim);

                // 如果这次击杀的受害者是之前的凶手, 且这次的凶手与原受害者同队, 就是交换
                if (evVictimId == killerId && evKillerId != 0 && evKillerId != victimId)
                {
                    if (_allPlayers.TryGetValue(victimId, out var victimData) &&
                        _allPlayers.TryGetValue(evKillerId, out var traderData) &&
                        victimData.TeamKey == traderData.TeamKey && !string.IsNullOrEmpty(victimData.TeamKey))
                    {
                        traderData.Trades++;
                    }
                }
            }
        }
    }

    // 通过名字查找玩家ID (用于在回合事件回溯时匹配)
    private long FindPlayerIdByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        foreach (var kvp in _allPlayers)
        {
            if (kvp.Value.Name == name) return kvp.Key;
        }
        return 0;
    }

    private void CalculateRoundMVP(RoundData round)
    {
        // 本回合最高击杀者为 MVP
        long topPlayerId = 0;
        int topKills = 0;
        int topDamage = 0;

        foreach (var (uniqueId, stats) in round.PlayerStats)
        {
            if (stats.Kills > topKills || (stats.Kills == topKills && stats.DamageDealt > topDamage))
            {
                topPlayerId = uniqueId;
                topKills = stats.Kills;
                topDamage = stats.DamageDealt;
            }
        }

        if (topPlayerId != 0 && topKills > 0)
        {
            if (_allPlayers.TryGetValue(topPlayerId, out var player))
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
            long uniqueId = GetPlayerUniqueId(player);
            if (uniqueId == 0) continue;

            if (_allPlayers.TryGetValue(uniqueId, out var playerData))
            {
                try { playerData.Score = player.Score; } catch { }
            }
        }
    }

    // ============================================================
    // PlayerDeath: 核心统计
    // ============================================================
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        string victimName = GetPlayerName(victim);
        string attackerName = attacker != null && attacker.IsValid ? GetPlayerName(attacker) : "World";
        string assisterName = assister != null && assister.IsValid ? GetPlayerName(assister) : "";

        // 1. 记录死亡事件
        round.Events.Add(new RoundEvent
        {
            Type = "death",
            Time = Server.CurrentTime,
            Victim = victimName,
            Attacker = attackerName,
            Assister = assisterName,
            Weapon = @event.Weapon ?? "",
            Headshot = @event.Headshot
        });

        // 2. 受害者: 死亡数 +1 (所有玩家都计入, 没有排除)
        if (victim != null && victim.IsValid)
        {
            var victimData = RegisterPlayer(victim);
            if (victimData != null)
            {
                victimData.Deaths++;
                long victimId = GetPlayerUniqueId(victim);

                // 回合统计
                if (!round.PlayerStats.TryGetValue(victimId, out var victimStats))
                {
                    victimStats = new PlayerRoundStats();
                    round.PlayerStats[victimId] = victimStats;
                }
                victimStats.Deaths++;
                try { victimStats.DamageTaken += @event.DmgHealth; } catch { }

                Server.PrintToConsole($"[CS2MatchStats] DEATH: {victimName} (ID={victimId}) -> Total Deaths={victimData.Deaths}");
            }
        }

        // 3. 攻击者: 击杀数 +1 (排除自杀/世界伤害)
        bool isSuicide = (attacker == null || !attacker.IsValid || attacker == victim);
        if (!isSuicide)
        {
            var attackerData = RegisterPlayer(attacker);
            if (attackerData != null)
            {
                attackerData.Kills++;
                long attackerId = GetPlayerUniqueId(attacker);

                // 回合统计
                if (!round.PlayerStats.TryGetValue(attackerId, out var attackerStats))
                {
                    attackerStats = new PlayerRoundStats();
                    round.PlayerStats[attackerId] = attackerStats;
                }
                attackerStats.Kills++;
                if (@event.Headshot) attackerStats.Headshots++;
                try { attackerStats.DamageDealt += @event.DmgHealth; } catch { }

                Server.PrintToConsole($"[CS2MatchStats] KILL: {attackerName} killed {victimName} ({(@event.Weapon ?? "")}{(@event.Headshot ? " HS" : "")}) -> K={attackerData.Kills} D={attackerData.Deaths} A={attackerData.Assists}");

                // 记录死亡信息用于交换计算
                if (victim != null && victim.IsValid)
                {
                    long victimId = GetPlayerUniqueId(victim);
                    if (victimId != 0)
                    {
                        _playerDeathInfo[victimId] = (attackerId, Server.CurrentTime);
                    }
                }
            }
        }

        // 4. 助攻: 优先游戏提供的 assister
        if (assister != null && assister.IsValid && assister != attacker && assister != victim)
        {
            var assisterData = RegisterPlayer(assister);
            if (assisterData != null)
            {
                assisterData.Assists++;
                long assisterId = GetPlayerUniqueId(assister);

                if (!round.PlayerStats.TryGetValue(assisterId, out var assisterStats))
                {
                    assisterStats = new PlayerRoundStats();
                    round.PlayerStats[assisterId] = assisterStats;
                }
                assisterStats.Assists++;

                Server.PrintToConsole($"[CS2MatchStats] ASSIST: {assisterName} -> Total Assists={assisterData.Assists}");
            }
        }
        else if (!isSuicide && victim != null && victim.IsValid)
        {
            // 回退: 用伤害追踪计算助攻
            long victimId = GetPlayerUniqueId(victim);
            if (victimId != 0 && _damageDealers.TryGetValue(victimId, out var dealers))
            {
                long killerId = attacker != null && attacker.IsValid ? GetPlayerUniqueId(attacker) : 0;

                foreach (var dealerId in dealers)
                {
                    if (dealerId == killerId || dealerId == victimId) continue;
                    if (!_allPlayers.TryGetValue(dealerId, out var dealerData)) continue;

                    dealerData.Assists++;

                    if (!round.PlayerStats.TryGetValue(dealerId, out var dealerStats))
                    {
                        dealerStats = new PlayerRoundStats();
                        round.PlayerStats[dealerId] = dealerStats;
                    }
                    dealerStats.Assists++;
                }
            }
        }

        // 5. 清除该受害者的伤害记录
        if (victim != null && victim.IsValid)
        {
            long victimId = GetPlayerUniqueId(victim);
            if (victimId != 0)
            {
                _damageDealers.Remove(victimId, out _);
                _damageAmounts.Remove(victimId, out _);
            }
        }

        return HookResult.Continue;
    }

    // ============================================================
    // PlayerHurt: 伤害统计 + 助攻追踪
    // ============================================================
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        // 确保双方已注册
        if (attacker != null && attacker.IsValid) RegisterPlayer(attacker);
        if (victim != null && victim.IsValid) RegisterPlayer(victim);

        if (attacker != null && attacker.IsValid && victim != null && attacker != victim)
        {
            long attackerId = GetPlayerUniqueId(attacker);
            long victimId = GetPlayerUniqueId(victim);
            if (attackerId == 0 || victimId == 0) return HookResult.Continue;

            if (!round.PlayerStats.TryGetValue(attackerId, out var stats))
            {
                stats = new PlayerRoundStats();
                round.PlayerStats[attackerId] = stats;
            }
            try { stats.DamageDealt += @event.DmgHealth; } catch { }

            // 追踪伤害者 (跨队伍且达阈值)
            try
            {
                if (@event.DmgHealth >= MIN_ASSIST_DAMAGE
                    && attacker.Team != victim.Team
                    && attacker.Team != CsTeam.None
                    && attacker.Team != CsTeam.Spectator)
                {
                    if (!_damageDealers.TryGetValue(victimId, out var dealers))
                    {
                        dealers = new HashSet<long>();
                        _damageDealers[victimId] = dealers;
                    }
                    dealers.Add(attackerId);

                    _damageAmounts.AddOrUpdate(victimId, _ => @event.DmgHealth, (_, old) => old + @event.DmgHealth);
                }
            }
            catch { /* ignore */ }
        }

        return HookResult.Continue;
    }

    // ============================================================
    // 炸弹事件
    // ============================================================
    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var player = @event.Userid;
        round.Events.Add(new RoundEvent
        {
            Type = "bomb_planted",
            Time = Server.CurrentTime,
            Player = GetPlayerName(player)
        });
        if (player != null && player.IsValid) RegisterPlayer(player);

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
            Player = GetPlayerName(player)
        });
        if (player != null && player.IsValid) RegisterPlayer(player);

        return HookResult.Continue;
    }

    // ============================================================
    // 玩家断开: 如果所有玩家都离开, 触发一次保存
    // ============================================================
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;

        string name = GetPlayerName(player);
        long id = GetPlayerUniqueId(player);
        Server.PrintToConsole($"[CS2MatchStats] Player DISCONNECTED: {name} (ID={id})");

        // 尝试触发保存
        if (_currentMatch.Rounds.Count > 0)
        {
            int remaining = 0;
            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.Team != CsTeam.None && p.Team != CsTeam.Spectator)
                {
                    remaining++;
                }
            }
            // 不再检查 IsBot, 只看有没有活跃玩家
            if (remaining == 0)
            {
                Server.PrintToConsole("[CS2MatchStats] No active players remain, saving match...");
                OnMapEnd();
            }
            else
            {
                Server.PrintToConsole($"[CS2MatchStats] {remaining} players still in game, not saving yet");
            }
        }

        return HookResult.Continue;
    }

    // ============================================================
    // Rating 计算
    // ============================================================
    private void CalculateRatings()
    {
        int totalRounds = _currentMatch.Rounds.Count;
        if (totalRounds == 0) return;

        var players = _allPlayers.Values.Where(p => p.Kills + p.Deaths + p.Assists > 0).ToList();
        if (players.Count == 0) return;

        double avgKPR = players.Average(p => (double)p.Kills / totalRounds);
        double avgDPR = players.Average(p => (double)p.Deaths / totalRounds);

        if (avgKPR <= 0) avgKPR = 0.5;
        if (avgDPR <= 0) avgDPR = 0.3;

        foreach (var player in players)
        {
            double kpr = (double)player.Kills / totalRounds;
            double dpr = (double)player.Deaths / totalRounds;

            double killRating = kpr / avgKPR * 0.45;
            double survivalRate = (double)player.RoundsSurvived / totalRounds;
            double survivalRating = survivalRate * 0.25;
            double multiKillRating = player.MultiKills * 0.04;
            double tradeRating = player.Trades * 0.03;
            double impactFactor = ((double)player.Kills + player.Assists) / (totalRounds * 2);
            double impactRating = impactFactor * 0.25;

            player.Rating = 0.7 + killRating + survivalRating + multiKillRating + tradeRating + impactRating;
            player.Rating = Math.Max(0.5, Math.Min(3.0, player.Rating));
            player.Rating = Math.Round(player.Rating, 2);
        }
    }

    // ============================================================
    // 保存/命令
    // ============================================================
    private void SaveMatch()
    {
        if (_currentMatch.Rounds.Count == 0) return;

        _currentMatch.EndTime = DateTime.Now;
        _currentMatch.Duration = (int)(_currentMatch.EndTime - _currentMatch.StartTime).TotalSeconds;

        var fileName = $"match_{_currentMatch.StartTime:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(_storagePath, fileName);

        var json = JsonSerializer.Serialize(_currentMatch, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        Server.PrintToConsole($"[CS2MatchStats] Match SAVED -> {filePath} ({_allPlayers.Count} players, {_currentMatch.Rounds.Count} rounds)");
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

        // 详细打印
        Server.PrintToConsole("[CS2MatchStats] Current tracked players:");
        foreach (var kvp in _allPlayers)
        {
            var p = kvp.Value;
            Server.PrintToConsole($"  [{p.TeamKey}] {p.Name} K={p.Kills} D={p.Deaths} A={p.Assists} Rating={p.Rating}");
        }
    }
}

// ============================================================
// 数据模型 (JSON 输出结构)
// ============================================================
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
    public bool IsBot { get; set; }          // 不再用来做逻辑判断, 仅作为元数据字段保留
    public string SteamId { get; set; } = ""; // 人类玩家的 SteamID, bot 通常为空
    public string TeamKey { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Score { get; set; }
    public int MVPs { get; set; }
    public double Rating { get; set; }
    public int RoundsSurvived { get; set; }
    public int MultiKills { get; set; }
    public int Trades { get; set; }
    public int Headshots { get; set; }
    public int TotalDamageDealt { get; set; }
    public int TotalDamageTaken { get; set; }
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
    public string Assister { get; set; } = "";
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
