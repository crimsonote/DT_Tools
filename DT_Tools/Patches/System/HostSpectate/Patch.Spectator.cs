using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Server.Game;

namespace DT_Tools.Patches.System.HostSpectate
{
    /// <summary>
    /// **唯一的补丁点**：进入选人阶段之前把房主标记为观战者。
    ///
    /// 整个功能只在这一个时点改变游戏状态，之后"观战者该怎么被对待"完全由游戏原版语义
    /// 与上游 `SpectatorJoin` 已有的规则承担。所以本功能既不复制开局流程
    /// （那会让开局跑两遍），也不改造客户端 UI（那会和原版旁观者逻辑打架）。
    ///
    /// 观战者这条路上**其余需要发生的事**各自落在别处，逐条列清避免以后有人再补丁叠补丁：
    ///   · 不计人数 / 全房人数口径 → `Patch.Lobby.cs` 的 `PatchLobbyMemberCount`
    ///   · 别人看不到房主（含选人格子）→ `Patch.Roster.cs` 的 `PatchRosterHideHost`
    ///   · 房主客户端知道自己是观战者 → 上游 `SpectatorJoinFeature.RosterSpectatorPatch`（roster 驱动）
    ///   · 房主不进存活池 / 不分配角色 / 不参与选角 → 上游 `SpectatorJoin` 的 StartPick / PickCharacter 补丁
    ///   · 开局转幽灵观战（`EnterSpectator` + 区域包 + `S_DEAD`）→ 上游 `SpectatorJoin/Feature.GameStart.cs`
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
    internal static class PatchEnterPick
    {
        /// <summary>
        /// 用 Prefix：状态切换之前动手。
        ///
        /// 时点保证：`ChangeGameState` 的第一句只是 `State = state`（ACS:169991-170001），
        /// 真正跑选人流程的 `StartPick` 要等客户端全部应答后的回调（ACS:170004-170017 → ACS:170035）。
        /// 所以在这里标记，一定早于 `StartPick` 里"按 `!IsSpectator` 挑真实玩家"的判断。
        /// </summary>
        [HarmonyPrefix]
        private static void Prefix(GameRoom __instance, EGameState state)
        {
            if (!Engine.Enabled<HostSpectateFeature>())
                return;
            if (state != EGameState.PickCharacter)
                return;

            HostSpectateLogic.MarkHostBeforePick(__instance);
        }
    }
}
