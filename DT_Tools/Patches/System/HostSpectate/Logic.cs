using System.Reflection;
using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace DT_Tools.Patches.System.HostSpectate
{
    /// <summary>
    /// 房主观战的判定与动作 —— 只有两件事：**何时标记房主**、**自动开始时怎么开局**。
    ///
    /// **分段设计**（这是本功能的骨架，改动前务必先读懂）：
    ///
    ///   · **大厅阶段：房主是普通玩家**，不标记 `IsSpectator`。
    ///     游戏自己也不允许"大厅里存在观战者"：`GameRoom.StartLobby`
    ///     （ACS:170251-170255）会把所有观战者复位并置脏 roster。
    ///     所以这一阶段只做大厅该做的事：对外人数口径不把房主算进去（见 `Patch.Lobby.cs`）。
    ///   · **进入选人之前**：这才把房主标记为观战者（<see cref="MarkHostBeforePick"/>）。
    ///     时点必须**早于** `StartPick`：上游那套 StartPick 整替是"按 `!IsSpectator` 挑真实玩家"，
    ///     晚了房主就会进存活池、参与角色/黑方分配。
    ///   · **局内**：观战者身份由游戏原版语义 + 上游 `SpectatorJoin` 接管
    ///     （不进存活池、不分配角色、`GameStart` 时由 `EnterSpectator` 转幽灵观战）。
    ///   · **回到大厅**：游戏自己的 `StartLobby` 会清掉标记，本功能**放手不管**。
    ///
    /// **硬约束**：绝不在局内把房主还原成活人 —— 那等于让已出局的人复活。
    /// </summary>
    internal static class HostSpectateLogic
    {
        /// <summary>本功能是否开启（段开关即总开关）。</summary>
        internal static bool Enabled => Engine.Enabled<HostSpectateFeature>();

        /// <summary>
        /// 进入选人阶段**之前**：把房主标记为观战者。
        ///
        /// 这是本功能的**唯一**标记点，时机由 `Patch.Spectator.cs` 挂在状态切换之前：
        ///   · 大厅阶段不能标 —— 游戏会复位，而且客户端 UI 会因 `IsSpectator` 走观战者分支；
        ///   · 更晚也不能标 —— 那时房主已被算进存活池、分了角色，就得再撤销。
        ///
        /// `MarkRosterDirty`（ACS:169752）是**必需**的，它有两个作用：
        ///   1. 让游戏的 roster 泵（`HostManager.Update`，ACS:159731-159736）把带
        ///      `IsSpectator=true` 的 roster 广播出去 —— **房主自己的客户端**靠它同步
        ///      `Managers.Game.IsSpectator`（上游 `RosterSpectatorPatch` 读 roster 里自己那条 entry）；
        ///   2. 触发别人客户端上的 roster 差集处理，把房主从 `_cache` 里摘掉（见 `Patch.Roster.cs`）。
        /// </summary>
        internal static void MarkHostBeforePick(GameRoom room)
        {
            if (!Enabled)
                return;

            GamePlayer host = room?.Host;
            if (host == null || host.IsSpectator)
                return;
            // 只在大厅状态下动作：进入选人的那一刻 State 还是 Lobby（ACS:169991-170001 先判后写）
            if (room.State != EGameState.Lobby)
                return;

            // 原版给"进房即观战"的人也是这么赋值的（ACS:170549）
            host.IsSpectator = true;
            room.MarkRosterDirty();
            Log.Info<HostSpectateFeature>(
                $"房主观战：房主 {host.Name}(#{host.PublicInfo.PlayerId}) 进入观战 —— " +
                "不进存活池、不分配角色，开局由 EnterSpectator 转幽灵围观");
        }

        /// <summary>
        /// 取 `CheckAllPlayerReady`（私有方法用字符串定位）。
        /// 0.1.16b GameRoom.cs:CheckAllPlayerReady（ACS:170999）。
        /// </summary>
        internal static bool InvokeCheckAllPlayerReady(GameRoom room)
        {
            MethodInfo m = AccessTools.Method(typeof(GameRoom), "CheckAllPlayerReady");
            if (m == null)
            {
                Log.Warn<HostSpectateFeature>("CheckAllPlayerReady 不可用，自动开始取消");
                return false;
            }
            return (bool)m.Invoke(room, null);
        }
    }
}
