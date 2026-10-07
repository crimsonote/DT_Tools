using System.Linq;
using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace DT_Tools.Patches.System.HostSpectate
{
    /// <summary>
    /// **大厅不参与** —— "房主在大厅阶段不是观战者，只是不参与"这一段的落点。
    ///
    /// 两个点各管一件事，彼此没有依赖：对外人数口径、开局条件。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.RoomMemberCountForMetadata))]
    internal static class PatchLobbyMemberCount
    {
        /// <summary>
        /// 房主**不算占位**（对外人数口径）。
        ///
        /// 一处覆盖两条链路（均已核对）：
        ///   · **对外显示**：`EnterPlayer` / `LeavePlayer` 调
        ///     `UpdateMemberCountMetadata(RoomMemberCountForMetadata())`
        ///     写入 Steam lobby metadata "members"（ACS:172176-172177、172188-172189、
        ///     160443-160453）；查询方 `OnLobbyList` 读它填 `LobbyListEntry.Cur`
        ///     （ACS:160320、160340）→ 即 DT_Tools `/room_list` 显示的 {Cur}/{Max}。
        ///   · **满员判定**：`HandleEnterPlayer` 里 `RoomMemberCountForMetadata() >= 8`（ACS:170497）。
        ///
        /// ⚠️ 用 Postfix 而不是 Prefix 整替：原版方法自带"假人去重"逻辑（ACS:172193-172197），
        /// 那是它自己的口径，我们只该在结果上减掉房主。
        /// 上游 `SpectatorJoin` 在同一点有 Prefix（排除观战者、返回 false 整替）；
        /// Harmony 里 Prefix 一律先于 Postfix 执行，所以这里拿到的就是它的结果，
        /// **不需要**写 `[HarmonyPriority]`（优先级只在同类补丁之间排序）。
        ///
        /// ⚠️ 显示侧下限是 1：查询方在 `members <= 0` 时会回退去读 Steam 真实成员数
        /// （ACS:160320-160323），所以"只有房主一个人时显示 0/8"这一档游戏侧做不到；
        /// 满员判定侧减到 0 是正确的，房内人数标签侧靠 roster 分包（见 `Patch.Roster.cs`）也不含房主。
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(GameRoom __instance, ref int __result)
        {
            if (!Engine.Enabled<HostSpectateFeature>())
                return;
            if (__instance.State != EGameState.Lobby)
                return;
            if (__instance.Host == null)
                return;

            if (__result > 0)
                __result -= 1;
        }
    }

    /// <summary>
    /// **开局条件**：所有真实玩家准备好后自动开始（房主不必点开始）。
    ///
    /// 直接调 `GameRoom.HandleStart(房主)`，不自己复制"进选角"流程 —— 上游 `SpectatorJoin`
    /// 已对该方法做 Prefix（校验房主身份 + 校验真实人数下限 + 校验全员准备 + 超员转观战），
    /// 走同一条路才与"房主手点开始"完全一致。
    ///
    /// `Priority.Last`：必须在上游那个记录准备顺序的 Postfix（`_readyOrder`，供超员转观战用）
    /// 之后跑。AGENTS.md §5：有顺序依赖就必须显式写 priority。
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.HandleReady))]
    internal static class PatchAutoStart
    {
        [HarmonyPostfix]
        private static void Postfix(GameRoom __instance, bool isReady)
        {
            if (!Engine.Enabled<HostSpectateFeature>())
                return;
            if (!HostSpectateFeature.AutoStartWhenAllReady)
                return;
            if (!isReady || __instance.State != EGameState.Lobby)
                return;

            // 人数门槛：非观战者不足 LOBBY_MIN_PLAYER 时不自动开。
            // ⚠️ 此刻房主**还不是观战者**（他的观战标记要等进入选人之前才打），
            // 所以这里的"非观战者"是**含房主**的。
            if (__instance.Players.Count(p => !p.IsSpectator) < Define.LOBBY_MIN_PLAYER)
                return;

            GamePlayer host = __instance.Host;
            if (host == null)
                return;
            if (!HostSpectateLogic.InvokeCheckAllPlayerReady(__instance))
                return;

            Log.Info<HostSpectateFeature>("房主观战：真实玩家已全部准备，自动开始（等同房主点开始）");
            __instance.HandleStart(host);
        }
    }
}
