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
    /// ## 隐藏的时机与恢复
    ///
    /// **只在"屏幕上真有玩家名单"的两个阶段隐藏**：
    ///   · 选人界面（`PickCharacter`）——"隐藏房主格子"是硬要求，不做成开关；
    ///   · 大厅（`Lobby`）——按 `HostVisibleInLobby` 配置。
    ///
    /// **局内（`Survive` 起）一律不摘**。两条理由都是踩出来的：
    ///   1. 局内名单的用途只剩"幽灵可见性"。摘掉 ⇒ 别人的 `_cache` 里没有他 ⇒
    ///      `Spawn(PublicPlayerInfo)` 因缺条目成为空操作（ACS:31301-31310）⇒
    ///      **连其他幽灵都看不到房主**（而需要的是能看到）。
    ///   2. "服务端 `Players` 里有、其他客户端 `_cache` 里没有"这个状态**原版从不产生**
    ///      （原版 `SpectatorJoin` 的观战者在所有人的 roster 里），任何依赖 `_cache` 的
    ///      第三方逻辑都会踩坑。实测：捉迷藏的 `SilhouetteResolver` 把"房主本机缓存里有他"
    ///      当成"所有机器都有他"，于是把房主选成回放首帧剪影 ⇒ 其余机器 `ChangeMyPlayer`
    ///      直接索引 `_cache` 抛 `KeyNotFoundException` ⇒ 客户端回放永久卡住。
    ///      （机制与证据见 `docs/观战者机制.md` §5.2、§7。）
    ///
    /// 恢复时**只把名单加回去还不够**：客户端的 Player 实体由 `S_SPAWN` 激活，
    /// 而 `ApplyRoster` 只会在 `_cache` 里建一条**未激活**的记录（ACS:31241-31257）。
    /// 原版"中途加入的观战者"不需要这一步，是因为 `HandleEnterPlayer` 在同一帧里
    /// 先发 `S_ADD_PLAYER`（建条目，ACS:170607）再 `EnterSpectator` → `EnterGhostVisibility`
    /// （发 `S_SPAWN`，ACS:169884-169893）；而房主是"已在房间的玩家转观战"，
    /// 他这条 entry 在选人阶段被我们摘掉过，等名单恢复时那个 `S_SPAWN` 早已发完。
    /// 所以这里补一次 `EnterGhostVisibility(房主)`（每轮一次）——它只把房主的实体发给
    /// `DeadPlayers`（其他幽灵），活人看不到，与幽灵可见性规则一致。
    /// 顺序安全：名单与 `S_SPAWN` 走同一条会话连接，可靠有序。
    ///
    /// 回到大厅时游戏自己的 `GameRoom.StartLobby`（ACS:170251-170255 复位 `IsSpectator`
    /// 并置脏 roster）会下发完整名单，房主随即被重新加回别人的 `_cache`；
    /// 同一段里对每个玩家调的 `Move(LobbyPos, force:true)`（ACS:170284-170287）
    /// 会经 `SearchAndUpdatePlayer`（ACS:173427-173440）补发 `S_SPAWN`，
    /// 于是"大厅可见"这一档是自然恢复的，不需要额外补包。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.BroadcastRoster))]
    internal static class PatchRosterHideHost
    {
        /// <summary>上一次已告知的隐藏状态（null = 未知）。roster 每秒都会广播，只在状态翻转时记一行日志。</summary>
        private static bool? _reportedHide;

        /// <summary>本轮是否已补发过"把房主介绍给其他幽灵"（进入隐藏阶段时复位，见 <see cref="RevealToGhostsOnce"/>）。</summary>
        private static bool _revealedToGhosts;

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

            // 只在"屏幕上真有玩家名单"的两个阶段隐藏（理由见类注释）：
            // 选人界面（硬要求）+ 大厅（按配置）。局内一律不摘。
            bool hide = (__instance.State == EGameState.Lobby && !HostSpectateFeature.HostVisibleInLobby)
                        || __instance.State == EGameState.PickCharacter;

            if (!hide)
            {
                if (_reportedHide == true)
                {
                    _reportedHide = false;
                    Log.Info<HostSpectateFeature>("房主观战：roster 已恢复完整名单（房主重新对其他人可见）");
                }
                RevealToGhostsOnce(__instance, host);
                return true;                        // 走原版：一个包发所有人
            }

            _revealedToGhosts = false;              // 新一轮隐藏开始 ⇒ 允许本轮结束后再补发一次幽灵可见性

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

        /// <summary>
        /// 名单恢复之后，把房主**重新介绍给其他幽灵**（每轮一次）。
        ///
        /// 为什么需要：名单恢复只让别人 `_cache` 里多出一条**未激活**的记录，
        /// 实体要 `S_SPAWN` 才激活（`Spawn(PublicPlayerInfo)` 只 `SetActive(true)`，
        /// ACS:31301-31310）。房主这条 entry 在选人阶段被摘过，原版那句
        /// `EnterGhostVisibility`（在 `EnterSpectator` 里）早就跑完了 ⇒ 没人再发 `S_SPAWN`。
        ///
        /// 用 `EnterGhostVisibility` 而不是自己发 `S_SPAWN`：它就是原版"幽灵可见性"的
        /// 唯一权威（ACS:169854-169894），四段包（给其他人 S_DESPAWN / 给幽灵 S_SPAWN）
        /// 一次做齐，且只发给 `DeadPlayers` ⇒ 活人看不到房主，与规则一致。
        ///
        /// 这里不比 `_revealedToGhosts` 更早地标记：进入局内的第一次广播可能发生在
        /// `EnterSpectator` 之前（`State` 在"选人→存活"切换时就已同步置为 `Survive`，
        /// 而 `GameStart` 要等客户端应答回调，见 `docs/观战者机制.md` §2.1），
        /// 那时房主还不是观战者 ⇒ 直接返回、留待下一次广播再补，避免漏发。
        /// </summary>
        private static void RevealToGhostsOnce(GameRoom room, GamePlayer host)
        {
            if (_revealedToGhosts || host == null || !host.IsSpectator)
                return;
            if (room.State == EGameState.Lobby || room.State == EGameState.NoneState)
                return;

            _revealedToGhosts = true;
            try
            {
                room.EnterGhostVisibility(host);
                Log.Info<HostSpectateFeature>("房主观战：已把房主重新介绍给其他幽灵（EnterGhostVisibility → DeadPlayers）");
            }
            catch (global::System.Exception ex)
            {
                Log.Warn<HostSpectateFeature>("房主观战：重新介绍房主给幽灵失败（已忽略）：" + ex.Message);
            }
        }
    }
}
