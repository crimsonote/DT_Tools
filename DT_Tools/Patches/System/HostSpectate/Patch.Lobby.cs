using System;
using System.Collections;
using System.Linq;
using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Server.Game;
using UnityEngine;
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
        /// <summary>延迟开局的预约协程（`0` 秒延迟时不使用）。</summary>
        private static Coroutine _pending;

        /// <summary>预约代号：准备状态一变就 +1，旧协程醒来发现代号不符即自行退出。</summary>
        private static int _token;

        [HarmonyPostfix]
        private static void Postfix(GameRoom __instance, bool isReady)
        {
            if (!Engine.Enabled<HostSpectateFeature>())
                return;
            if (!HostSpectateFeature.AutoStartWhenAllReady)
                return;
            if (!isReady || __instance.State != EGameState.Lobby)
                return;

            GamePlayer host = __instance.Host;
            if (host == null)
                return;

            // 任何一次准备状态变化都作废上一份预约：有人在延迟窗口里取消准备，就得真的把这次开局
            // 拦下来；又点回准备，则重新计时。
            _token++;

            // 这里**只判断"是不是所有人都准备好了"**，不自己判人数门槛：
            // 门槛（房主不计入、阈值取 `[HostSpectate] MinParticipants`）统一由上游那个
            // 整替 `HandleStart` 的 Prefix 判定 —— 它是"房主点开始"与"自动开始"共用的
            // 唯一判定点；两处各写一份迟早会漂。被挡下时那里会记一行 Debug（进 WebUI 日志）。
            //
            // 注意房主此刻**还不是观战者**（标记要等进入选人之前才打），所以
            // "参与者"要显式排除他，否则房主会被算成开局规模的一部分。
            if (!HostSpectateLogic.InvokeCheckAllPlayerReady(__instance))
                return;

            int participants = __instance.Players.Count(p => !p.IsSpectator && p != host);
            int delay = Math.Clamp(HostSpectateFeature.AutoStartDelaySeconds, 0, 60);
            if (delay <= 0)
            {
                Log.Info<HostSpectateFeature>(
                    $"房主观战：参与者 {participants} 人已全部准备，立即自动请求开局（判定同房主点开始）");
                __instance.HandleStart(host);
                return;
            }

            int token = _token;
            Log.Info<HostSpectateFeature>(
                $"房主观战：参与者 {participants} 人已全部准备，{delay} 秒后自动开局（期间有人取消准备会重新计时）");
            CoroutineHost.Stop(_pending);
            _pending = CoroutineHost.Start(CoStartAfter(delay, token));
        }

        /// <summary>
        /// 延迟到期后重新校验一次再开局。
        ///
        /// 这几秒里可能发生：有人取消准备（`_token` 已变）、房主自己手点了开始或人走光了
        /// （房间已不在大厅 / 已不在原状态）、人数掉到门槛以下。任一情况都放弃本次预约 ——
        /// 排到期的动作不能"凭几秒前的判断"动手。
        /// </summary>
        private static IEnumerator CoStartAfter(int delay, int token)
        {
            yield return new WaitForSeconds(delay);

            if (token != _token)
                yield break;
            GameRoom room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Lobby)
                yield break;
            GamePlayer host = room.Host;
            if (host == null || !HostSpectateLogic.InvokeCheckAllPlayerReady(room))
                yield break;

            Log.Info<HostSpectateFeature>("房主观战：延迟结束，自动请求开局（判定同房主点开始）");
            room.HandleStart(host);
        }
    }
}
