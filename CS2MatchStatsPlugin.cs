using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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

    // 换边检测锚点：每回合结束时记录所有玩家的 TeamKey 快照
    // 用于在下一回合开始时比较"玩家的队是否变了"来检测换边
    // Key=玩家UniqueId, Value=该玩家在上回合结束时的队伍
    private readonly Dictionary<long, string> _previousRoundTeamSnapshot = new();

    // 本回合是否已记录过首杀 (每次 OnRoundStart 重置为 false, 第一次 kill 时置 true)
    private bool _roundFirstKillRecorded = false;

    // 最小助攻伤害阈值
    private const int MIN_ASSIST_DAMAGE = 10;
    // 交换击杀时间窗口（秒）
    private const float TRADE_TIME_WINDOW = 4.0f;

    // BOT难度记录
    private int _botDifficulty = 0;
    private string _difficultyLevel = "Unknown";

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

    // 稳定玩家唯一ID获取
    // 优先使用 UserId (所有在线玩家都有, 且 >= 0), +1 防止 UserId=0 被当成无效
    // 如果 UserId 不可用, 回退到 Slot + 1 (Slot 范围 0-63, 单服务器唯一)
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


    // 注册/更新玩家: 统一入口
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
            // TeamKey 跟着当前回合的队伍走, 换边时会变化
            if (!string.IsNullOrEmpty(teamKey)) existing.TeamKey = teamKey;
            // InitialTeam 保持不变 (玩家首登游戏时的队伍)
            if (!string.IsNullOrEmpty(steamId)) existing.SteamId = steamId;
            existing.Score = score;
            return existing;
        }

        var newPlayer = new PlayerData
        {
            UniqueId = uniqueId,
            Name = name,
            IsBot = false, // 统一 false (由外部显示决定)
            SteamId = steamId,
            // 首次注册时: 队伍初始值 = 当前队伍
            TeamKey = teamKey,
            InitialTeam = teamKey,
            Score = score
        };
        _allPlayers[uniqueId] = newPlayer;

        Server.PrintToConsole($"[CS2MatchStats] REGISTER player: {name} (ID={uniqueId}, InitialTeam={teamKey}, Steam={steamId})");
        return newPlayer;
    }


    // Map 事件: 初始化/保存
    private void OnMapStart(string mapName)
    {
        // 读取BOT难度
        ReadBotDifficulty();

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
        _currentMatch.BotDifficulty = _botDifficulty;
        _currentMatch.DifficultyLevel = _difficultyLevel;
        _currentRound = 0;
        _allPlayers.Clear();
        _damageDealers.Clear();
        _damageAmounts.Clear();
        _playerDeathInfo.Clear();
        _previousRoundTeamSnapshot.Clear();

        Server.PrintToConsole($"[CS2MatchStats] ========== NEW MATCH STARTED on {mapName} ==========");
        Server.PrintToConsole($"[CS2MatchStats] Bot Difficulty: {_botDifficulty} ({_difficultyLevel})");
    }

    private void ReadBotDifficulty()
    {
        // 方式1: 通过 botprofile.vpk 的 SHA256 哈希检测 CS2-Bot-Improver 的难度
        var (diffName, diffLevel) = DetectBotDifficultyByVpk();
        if (diffName != "Unknown" && diffName != "Custom / Unknown")
        {
            // BOT基础难度: Low=1, Medium=3, High=5
            int botBaseDiff = diffLevel switch
            {
                "1/3" => 10,
                "2/3" => 30,
                "3/3" => 50,
                _ => 10
            };

            _botDifficulty = botBaseDiff;
            _difficultyLevel = diffName;
        }
        else
        {
            // 方式2: 回退到原生 bot_difficulty CVar
            try
            {
                var botDiffConVar = ConVar.Find("bot_difficulty");
                if (botDiffConVar != null)
                {
                    var val = botDiffConVar.GetPrimitiveValue<object>();
                    if (val != null) _botDifficulty = Convert.ToInt32(val);
                }
            }
            catch { _botDifficulty = 0; }

            // 原生难度映射
            _botDifficulty = _botDifficulty switch
            {
                0 => 10,   // Easy
                1 => 15,   // Normal
                2 => 30,   // Hard
                3 => 50,   // Expert
                _ => 10
            };

            _difficultyLevel = "Standard";
        }
    }

    private (string Name, string Level) DetectBotDifficultyByVpk()
    {
        var overridesDir = FindOverridesDirectory();
        if (overridesDir == null)
        {
            return ("Unknown - overrides directory missing", "?/3");
        }

        var activePath = Path.Combine(overridesDir, "botprofile.vpk");
        if (!File.Exists(activePath))
        {
            return ("Unknown - active botprofile.vpk missing", "?/3");
        }

        byte[] activeHash = ComputeSha256(activePath);
        var knownProfiles = new[]
        {
            new { Name = "Low", Level = "1/3", Path = Path.Combine(overridesDir, "Low", "botprofile.vpk") },
            new { Name = "Medium", Level = "2/3", Path = Path.Combine(overridesDir, "Medium", "botprofile.vpk") },
            new { Name = "High", Level = "3/3", Path = Path.Combine(overridesDir, "High", "botprofile.vpk") }
        };

        foreach (var profile in knownProfiles)
        {
            if (!File.Exists(profile.Path)) continue;
            if (CryptographicOperations.FixedTimeEquals(activeHash, ComputeSha256(profile.Path)))
            {
                return (profile.Name, profile.Level);
            }
        }

        return ("Custom / Unknown", "?/3");
    }

    private static string? FindOverridesDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Server.GameDirectory, "overrides"),
            Path.Combine(Server.GameDirectory, "csgo", "overrides"),
            Path.Combine(Server.GameDirectory, "game", "csgo", "overrides")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "botprofile.vpk")))
            {
                return candidate;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "overrides");
            if (File.Exists(Path.Combine(candidate, "botprofile.vpk")))
            {
                return candidate;
            }
            current = current.Parent;
        }

        return null;
    }

    private static byte[] ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return sha256.ComputeHash(stream);
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

        //  打印所有已记录玩家
        Server.PrintToConsole("[CS2MatchStats] ---- ALL PLAYERS ----");
        foreach (var kvp in _allPlayers)
        {
            var p = kvp.Value;
            Server.PrintToConsole($"  [InitTeam={p.InitialTeam}, CurrentTeam={p.TeamKey}] {p.Name} (ID={kvp.Key}) K={p.Kills} D={p.Deaths} A={p.Assists} MVP={p.MVPs} Survived={p.RoundsSurvived}");
        }

        // 将玩家分配到队伍 (使用 InitialTeam)
        // 玩家在 12 局后会被换边, TeamKey 会变化
        // 前端按 InitialTeam
        foreach (var player in _allPlayers.Values)
        {
            string teamToUse = !string.IsNullOrEmpty(player.InitialTeam) ? player.InitialTeam : player.TeamKey;
            if (string.IsNullOrEmpty(teamToUse)) continue;
            if (_currentMatch.Teams.ContainsKey(teamToUse))
            {
                _currentMatch.Teams[teamToUse].Players[player.UniqueId] = player;
            }
        }

        // 计算 Rating
        CalculateRatings();

        SaveMatch();
    }


    // Round 事件: 开始/结束
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _currentRound++;
        _roundFirstKillRecorded = false; // 重置首杀标记

        // 收集本回合开始时所有玩家的 TeamKey 快照 (锚点数据)
        var currentSnapshot = new Dictionary<long, string>();
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            if (player.Team == CsTeam.None || player.Team == CsTeam.Spectator) continue;
            var uid = GetPlayerUniqueId(player);
            if (uid == 0) continue;
            currentSnapshot[uid] = GetTeamKey(player);
            RegisterPlayer(player); // 确保注册并设置 InitialTeam
        }


        // 换边检测: 锚点法 (玩家队伍变化) + 规则验证
        // 锚点: 如果上一回合的玩家在本回合的队伍变了 -> 换边
        // 规则: 已知换边回合 = 13, 28, 34, 40
        // 检测逻辑:
        // 1. 用锚点检测 (Primary): 比较 _previousRoundTeamSnapshot vs currentSnapshot
        //    若任一玩家 team 变化 -> sideSwapped = true
        // 2. 用规则验证 (Secondary): 若锚点说换边了但回合号不在已知换边点 -> 警告
        // 3. 若锚点没检测到但回合号是已知换边点 -> 说明锚点漏了 (用规则补充, 打印警告)
        bool sideSwappedByAnchor = false;
        bool sideSwappedByRule = IsSwapRound(_currentRound);

        // 比较上一回合快照 vs 本回合当前状态
        foreach (var (uid, currentTeam) in currentSnapshot)
        {
            if (_previousRoundTeamSnapshot.TryGetValue(uid, out var previousTeam))
            {
                // 该玩家在上一回合存在, 比较队伍
                if (!string.IsNullOrEmpty(previousTeam) && previousTeam != currentTeam)
                {
                    sideSwappedByAnchor = true;
                    break;
                }
            }
        }

        bool sideSwapped = sideSwappedByAnchor || sideSwappedByRule;

        // 规则一致性检查
        bool sideSwappedConfirmed = sideSwappedByRule;
        if (sideSwappedByAnchor && !sideSwappedByRule)
        {
            // 锚点检测到换边但回合号不在规则换边点 -> 异常
            Server.PrintToConsole($"[CS2MatchStats] *** WARNING: Side swap detected by anchor but round {_currentRound} is NOT a known swap round! Using anchor. ***");
            sideSwappedConfirmed = false;
        }
        else if (!sideSwappedByAnchor && sideSwappedByRule)
        {
            // 规则说应该换边但锚点没检测到 -> 锚点漏了 (用规则补充)
            Server.PrintToConsole($"[CS2MatchStats] *** WARNING: Rule-based swap at round {_currentRound} but anchor missed it. Using rule. ***");
            sideSwappedConfirmed = true;
        }
        else if (sideSwappedByAnchor && sideSwappedByRule)
        {
            sideSwappedConfirmed = true;
        }

        var newRound = new RoundData
        {
            RoundNumber = _currentRound,
            Events = new List<RoundEvent>(),
            PlayerStats = new Dictionary<long, PlayerRoundStats>(),
            PlayerTeamSnapshot = new Dictionary<long, string>(currentSnapshot),
            SideSwapped = sideSwapped,
            SideSwappedConfirmed = sideSwappedConfirmed
        };

        _currentMatch.Rounds.Add(newRound);

        _damageDealers.Clear();
        _damageAmounts.Clear();

        string swapNote = sideSwapped
            ? $"SWAP (anchor={sideSwappedByAnchor}, rule={sideSwappedByRule}, confirmed={sideSwappedConfirmed})"
            : "no-swap";
        Server.PrintToConsole($"[CS2MatchStats] Round {_currentRound} START - {currentSnapshot.Count} players, swap={swapNote}");

        return HookResult.Continue;
    }

    // 判断某回合号是否是换边回合 (即该回合是"换边后的第一回合")
    // 换边发生在两个半场之间的短暂过场, 所以"换边回合"= 下一个半场的首回合
    // 例如: 常规 12 局结束后换边 -> 第 13 局为换边标记回合
    private static bool IsSwapRound(int roundNumber)
    {
        // 已知的换边回合: 13 (常规下半场), 28 (OT1 下半场), 34 (OT2), 40 (OT3)
        // 46 理论上下一个, 但游戏 45-45 强制平局, 不会到 46
        int[] swapRounds = { 13, 28, 34, 40 };
        return swapRounds.Contains(roundNumber);
    }

    // 辅助: 某回合号属于哪个"半场"的阵营映射
    // 返回 true 表示当前局玩家的 TeamKey 与其 InitialTeam 相反 (即已经换过边)
    private static bool IsSwappedHalf(int roundNumber)
    {
        if (roundNumber <= 12) return false;
        if (roundNumber <= 24) return true;
        // OT: 每个 OT 6 局, 前 3 局不换 (与上一 OT 下半场相反), 后 3 局再换
        // 实际规则: OT1.25-27 initial-half mapping, OT1.28-30 swapped
        //           OT2.31-33 swapped-back, OT2.34-36 swapped...
        // 但 OT 的"initial" 跟常规赛的"初始队"相比: OT1上半场与常规下半场同, 即已经换过边
        // 所以统一判断: roundNumber 在 13-24(下半场), 28-30(OT1下半场), 34-36(OT2下半场), 40-42(OT3下半场)
        // 这些局数中的玩家 TeamKey 与其 InitialTeam 相反
        if (roundNumber >= 28 && roundNumber <= 30) return true;
        if (roundNumber >= 34 && roundNumber <= 36) return true;
        if (roundNumber >= 40 && roundNumber <= 42) return true;
        return false;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_currentMatch.Rounds.Count == 0) return HookResult.Continue;

        var round = _currentMatch.Rounds.Last();
        var winner = @event.Winner == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        round.Winner = winner;
        round.Reason = @event.Reason;

        // 比分计算: 使用游戏 Winner 记录到 round.Winner
        // 前端会根据 SideSwapped 和 InitialTeam 重新归类比分
        if (_currentMatch.Teams.ContainsKey(winner))
        {
            _currentMatch.Teams[winner].Score++;
        }

        // 计算存活回合, 多杀, 交换击杀
        CalculateRoundStats(round);

        // 计算 MVP
        CalculateRoundMVP(round);

        // 残局 (Clutch) 判定: 胜利队存活玩家 <= 2 时, 该队最高击杀者 获得 1 次 clutch
        int ctTotal = 0, tTotal = 0, ctAlive = 0, tAlive = 0;
        // 使用本回合开始时的队伍快照 (round.PlayerTeamSnapshot)
        var roundTeam = round.PlayerTeamSnapshot;
        foreach (var kvp in roundTeam)
        {
            if (kvp.Value == "CT") ctTotal++;
            else if (kvp.Value == "T") tTotal++;
        }
        foreach (var (uid, stats) in round.PlayerStats)
        {
            if (roundTeam.TryGetValue(uid, out var team))
            {
                bool isAlive = stats.Deaths == 0;
                if (team == "CT") { if (isAlive) ctAlive++; }
                else if (team == "T") { if (isAlive) tAlive++; }
            }
        }
        int winnerAlive = winner == "CT" ? ctAlive : tAlive;
        if (winnerAlive > 0 && winnerAlive <= 2 && ctTotal + tTotal >= 5)
        {
            long clutchPlayerId = 0;
            int maxKills = -1;
            foreach (var (uid, stats) in round.PlayerStats)
            {
                if (!roundTeam.TryGetValue(uid, out var team)) continue;
                if (team != winner) continue;
                if (stats.Kills > maxKills)
                {
                    maxKills = stats.Kills;
                    clutchPlayerId = uid;
                }
            }
            if (clutchPlayerId != 0 && _allPlayers.TryGetValue(clutchPlayerId, out var clutchPlayer))
            {
                clutchPlayer.Clutches++;
                Server.PrintToConsole($"[CS2MatchStats] CLUTCH! Round {_currentRound} winner={winner} had {winnerAlive} alive, {clutchPlayer.Name} gets clutch (total={clutchPlayer.Clutches})");
            }
        }

        // 同步分数 (从游戏)
        SyncPlayerScores();

        // 清空死亡信息
        _playerDeathInfo.Clear();

        // 更新换边检测锚点: 记录本回合结束时所有玩家的 TeamKey
        // 这样下一回合 OnRoundStart 时可以比较"玩家的队是否变了"
        _previousRoundTeamSnapshot.Clear();
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            if (player.Team == CsTeam.None || player.Team == CsTeam.Spectator) continue;
            var uid = GetPlayerUniqueId(player);
            if (uid == 0) continue;
            _previousRoundTeamSnapshot[uid] = GetTeamKey(player);
        }

        Server.PrintToConsole($"[CS2MatchStats] Round {_currentRound} END - Winner={winner}, Reason={@event.Reason}, TrackedPlayers={_allPlayers.Count}, SwapConfirmed={round.SideSwappedConfirmed}");

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

    // PlayerDeath统计
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

        // 记录死亡事件
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

        // 受害者死亡数 +1
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

        // 攻击者击杀数 +1 (排除自杀/世界伤害)
        bool isSuicide = (attacker == null || !attacker.IsValid || attacker == victim);
        if (!isSuicide)
        {
            var attackerData = RegisterPlayer(attacker);
            if (attackerData != null)
            {
                attackerData.Kills++;
                long attackerId = GetPlayerUniqueId(attacker);

                // 首杀: 如果本回合尚未记录过首杀, 则本次 kill 为首杀
                if (!_roundFirstKillRecorded)
                {
                    _roundFirstKillRecorded = true;
                    attackerData.FirstKills++;
                }

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

        // 助攻
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
            // 用伤害追踪计算助攻
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

        // 清除该受害者的伤害记录
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

    // PlayerHurt伤害统计 + 助攻追踪
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


    // 炸弹事件
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

    // 玩家断开: 如果所有玩家都离开, 触发一次保存
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
            // 不检查 IsBot, 只看有没有活跃玩家
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

    private void CalculateRatings()
    {
        int totalRounds = _currentMatch.Rounds.Count;
        if (totalRounds == 0) return;

        var players = _allPlayers.Values.Where(p => p.Kills + p.Deaths + p.Assists > 0).ToList();
        if (players.Count == 0) return;

        double avgKPR = players.Average(p => (double)p.Kills / totalRounds);
        double avgDPR = players.Average(p => (double)p.Deaths / totalRounds);
        double avgSurvivalRate = players.Average(p => (double)p.RoundsSurvived / totalRounds);
        double avgImpact = players.Average(p => ((double)p.Kills + p.Assists) / totalRounds);
        double avgADR = players.Average(p => (double)p.TotalDamageDealt / totalRounds);

        if (avgKPR <= 0) avgKPR = 0.5;
        if (avgDPR <= 0) avgDPR = 0.6;
        if (avgSurvivalRate <= 0) avgSurvivalRate = 0.4;
        if (avgImpact <= 0) avgImpact = 0.8;
        if (avgADR <= 0) avgADR = 50.0;

        var rawRatings = new Dictionary<long, double>();
        double sumRaw = 0;

        foreach (var player in players)
        {
            double kpr = (double)player.Kills / totalRounds;
            double dpr = (double)player.Deaths / totalRounds;
            double survivalRate = (double)player.RoundsSurvived / totalRounds;
            double impact = ((double)player.Kills + player.Assists) / totalRounds;
            double adr = (double)player.TotalDamageDealt / totalRounds;

            double killRating = kpr / avgKPR;
            double survivalRating = survivalRate / avgSurvivalRate;
            double impactRating = impact / avgImpact;

            double coreRating = killRating * 0.40 + survivalRating * 0.30 + impactRating * 0.30;

            double deathPenalty = (dpr / avgDPR - 1.0) * 0.30;

            double adrAdjust = (adr / avgADR - 1.0) * 0.08;

            double multiKillBonus = Math.Min(player.MultiKills * 0.004, 0.04);
            double tradeBonus = Math.Min(player.Trades * 0.003, 0.03);
            double mvpBonus = Math.Min(player.MVPs * 0.008, 0.04);
            double firstKillBonus = Math.Min(player.FirstKills * 0.005, 0.03);
            double clutchBonus = Math.Min(player.Clutches * 0.01, 0.04);
            double smallBonuses = multiKillBonus + tradeBonus + mvpBonus + firstKillBonus + clutchBonus;

            double raw = coreRating - deathPenalty + adrAdjust + smallBonuses;
            rawRatings[player.UniqueId] = raw;
            sumRaw += raw;
        }

        double avgRaw = sumRaw / players.Count;
        double scaleFactor = avgRaw > 0 ? 1.0 / avgRaw : 1.0;

        foreach (var player in players)
        {
            double rating = rawRatings[player.UniqueId] * scaleFactor;
            player.Rating = Math.Max(0.01, Math.Min(2.5, rating));
            player.Rating = Math.Round(player.Rating, 2);
        }
    }

    // 保存/命令
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

// 数据模型 (JSON 输出结构)
public class MatchData
{
    public string MapName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Duration { get; set; }
    public Dictionary<string, TeamData> Teams { get; set; } = new();
    public List<RoundData> Rounds { get; set; } = new();
    // BOT难度相关
    public int BotDifficulty { get; set; } = 0;      // 0-5+ 原始难度值
    public string DifficultyLevel { get; set; } = ""; // Easy/Normal/Hard/Expert/High/Pro
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
    public string TeamKey { get; set; } = "";     // 当前局所属队 (换边后会变)
    public string InitialTeam { get; set; } = "";  // 玩家首登游戏时的队伍, 用于前端展示和半场合并, 换边不变
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
    public int FirstKills { get; set; }   // 首杀次数 (完美世界的"首杀"列)
    public int Clutches { get; set; }    // 残局次数 (最后存活 1-2 人时所在队赢下回合)
}

public class RoundData
{
    public int RoundNumber { get; set; }
    public string Winner { get; set; } = "";
    public int Reason { get; set; }
    public bool SideSwapped { get; set; } // 该回合是否发生换边 (由玩家队伍变化锚点检测)
    public bool SideSwappedConfirmed { get; set; } // 换边是否通过规则验证 (规则: 13/28/34/40)
    public List<RoundEvent> Events { get; set; } = new();
    public Dictionary<long, PlayerRoundStats> PlayerStats { get; set; } = new();
    // 回合开始时每个玩家的 TeamKey 快照 (UniqueId -> TeamKey)
    // 用于锚点检测换边: 玩家队伍在上一回合 vs 本回合发生变化 = 换边
    public Dictionary<long, string> PlayerTeamSnapshot { get; set; } = new();
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
