using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace DT_Tools.Patches.System.HostSpectate
{
    /// <summary>
    /// 让**其他玩家**当房主不存在（纯服务端，对所有客户端生效，不依赖对方装插件）。
    ///
    /// ## 为什么只有 roster 这一条路
    ///
    /// 客户端"房间里有哪些人"的唯一数据源就是 roster：`PlayerManager.ApplyRoster`
    /// （ACS:31226）按 roster 维护 `_cache`，收尾时把"在 `_cache` 里、但不在本次 roster 里"
    /// 的人用 `Despawn(S_LEAVE_GAME)`（ACS:31292-31298）连 GameObject 一起销毁；
    /// 而选人界面的格子取的就是 `_cache`（`GetAllPlayers()` = `_cache.Values`，ACS:31049-31052），
    /// 房间人数标签取的也是它（`GetPlayerCacheCount`，ACS:31111-31129）。
    ///
    /// 所以**只有不在 roster 里，才既能不画格子、又不留实体、又不进人数**。
    /// 反过来，只发 `S_DESPAWN`（游戏自己的幽灵可见性做法，`EnterGhostVisibility`，ACS:169854-169894）
    /// 只会隐藏实体，`_cache` 里仍有他，选人格子照画 —— 两个机制叠在一起就是补丁叠补丁。
    ///
    /// 房主本人不会被误伤：`ApplyRoster` 收集差集时有
    /// `MyPlayer.PublicInfo.PlayerId != key` 的判断（ACS:31287），房主本机的 `_cache`
    /// 里**本来就装不下自己**（ACS:31239 同样跳过自己），所以整条机制天然绕开他。
    ///
    /// ## 为什么挂在 `BroadcastRoster` 而不是 `BuildRosterPacket`
    ///
    /// `BroadcastRoster`（ACS:169790-169796）是 `Broadcast(BuildRosterPacket())` ——
    /// **一个包发给所有人**。若在 `BuildRosterPacket` 上把房主删掉，房主本人也收不到自己那条 entry，
    /// 而客户端"我现在是观战者"这件事**只有** roster 里自己那条 entry 能告诉他：
    /// 原版只在进房那一刻由 `S_ENTER_GAME` 写过一次 `Managers.Game.IsSpectator`
    /// （ACS:42053），之后唯一的写入口就是上游 `SpectatorJoinFeature.RosterSpectatorPatch`
    /// 读 roster 里 "自己的 `IsSpectator`"。
    ///
    /// 那份标记是观战者一切客户端表现的总开关，丢了它就会出现一串看起来互不相关的毛病：
    ///   · 会议阶段的旁观按钮不出现（`UI_TrialEvent.Init` 的
    ///     `GetObject(50).SetActive(Managers.Game.IsSpectator)`，ACS:69823）；
    ///   · `S_DEAD` 之后被当成"死亡"而不是"观战"（`GameManagerEX.Dead` 里
    ///     `!IsSpectator` 才弹死亡提示、语音频道才走 Observe，ACS:28974-28979）；
    ///   · 上游 `PostfixDead` 不再放开操作（它判的就是 `IsSpectator`），幽灵因此不能自由移动；
    ///   · `UI_GameScene` 的观战面板不出现（`GetObject(40).SetActive(Managers.Game.IsSpectator)`，ACS:75680）。
    ///
    /// ⇒ 正确做法是**按收件人分包**：发给房主本人的是完整 roster（他靠它同步观战标记），
    /// 发给其他人的是摘掉房主那条 entry 的 roster。
    ///
    /// ## 隐藏的时机
    ///
    /// 选人阶段起**一律**隐藏（"隐藏房主格子"是硬要求，不做成开关）；
    /// 大厅阶段按 `HostVisibleInLobby`。
    /// 回到大厅时游戏自己的 `GameRoom.StartLobby`（ACS:170251-170255 复位 `IsSpectator`
    /// 并 `MarkRosterDirty`）会下发完整 roster，房主随即被重新加回别人的 `_cache`；
    /// 同一段里对每个玩家调的 `Move(LobbyPos, force:true)`（ACS:170284-170287）
    /// 会经 `SearchAndUpdatePlayer`（ACS:173427-173440）补发 `S_SPAWN`，
    /// 于是"大厅可见"这一档是自然恢复的，不需要额外补包。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.BroadcastRoster))]
    internal static class PatchRosterHideHost
    {
        /// <summary>上一次已告知的隐藏状态（null = 未知）。roster 每秒都会广播，只在状态翻转时记一行日志。</summary>
        private static bool? _reportedHide;

        /// <summary>
        /// 整替 `BroadcastRoster`。
        ///
        /// 原方法只有 `if (!IsMigrating) Broadcast(BuildRosterPacket())` 一句，
        /// 所以整替后必须自己复现 `IsMigrating` 守卫（迁移交接期间不能插包）；
        /// 不需要隐藏时直接 `return true`，仍然走原方法，避免重复实现广播。
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(GameRoom __instance)
        {
            if (!Engine.Enabled<HostSpectateFeature>())
                return true;
            if (__instance.IsMigrating)
                return false;                       // 原版守卫：迁移期间静默不发（等价于原方法的空操作）

            GamePlayer host = __instance.Host;
            if (host?.PublicInfo == null)
                return true;

            bool hide = !HostSpectateFeature.HostVisibleInLobby
                        || __instance.State != EGameState.Lobby;
            if (!hide)
            {
                if (_reportedHide == true)
                {
                    _reportedHide = false;
                    Log.Info<HostSpectateFeature>("房主观战：roster 已恢复完整名单（房主重新对其他人可见）");
                }
                return true;                        // 大厅且允许可见 ⇒ 走原版：一个包发所有人
            }

            int hostId = host.PublicInfo.PlayerId;
            // 两次构建而不是 Clone：Clone 只浅拷贝条目列表，语义上容易读错；roster 至多十几条，代价可忽略。
            S_PLAYER_ROSTER toHost = __instance.BuildRosterPacket();
            S_PLAYER_ROSTER toOthers = __instance.BuildRosterPacket();
            for (int i = toOthers.Entries.Count - 1; i >= 0; i--)
            {
                if (toOthers.Entries[i].PlayerId == hostId)
                    toOthers.Entries.RemoveAt(i);
            }

            int others = 0;
            foreach (GamePlayer p in __instance.Players)
            {
                if (p?.Session == null)
                    continue;
                if (p == host)
                    p.Session.Send(toHost);
                else
                {
                    p.Session.Send(toOthers);
                    others++;
                }
            }

            if (_reportedHide != true)
            {
                _reportedHide = true;
                Log.Info<HostSpectateFeature>(
                    $"房主观战：roster 已按收件人分包 —— 房主本人保留自己那条 entry（客户端据此同步观战标记），" +
                    $"其余 {others} 人拿到的名单里没有房主（阶段={__instance.State}）");
            }
            return false;
        }
    }
}
