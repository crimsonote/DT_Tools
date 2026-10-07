using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data;
using HarmonyLib;
using Protocol;
using Server;
using Server.Game;
using UnityEngine;
using GamePlayer = Server.Game.Player;
using DT_Tools.Core;

namespace DT_Tools.Patches.System.SpectatorJoin
{
    public sealed partial class SpectatorJoinFeature
    {
        /// <summary>
        /// GameStart：观战者**照常落位到出生点**，然后转幽灵观战（EnterSpectator）。
        /// 其余与原版 + LobbyMaxPlayers 对齐：StartPosList 打乱后分配，人数超出
        /// 出生点时 i % Count 循环复用；RoundStartPlayerCount 只统计真实玩家，
        /// 不影响黑方击杀上限。
        /// </summary>
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.GameStart))]
        [HarmonyPrefix]
        private static bool PrefixGameStart(GameRoom __instance)
        {
            if (!ShouldHandleSpectators)
                return true;

            Server.Game.ItemManager.Instance.ClearWithDespawn();

            __instance.CorpseMetas.Clear();
            __instance.RoundStartPlayerCount = __instance.Players.Count(p => !p.IsSpectator);
            Log.Info<SpectatorJoinFeature>(
                $"Kill: 回合开始人数 {__instance.RoundStartPlayerCount} → 黑方击杀上限 {__instance.BlackKillLimit} 次 (SpectatorJoin)");

            foreach (GamePlayer player in __instance.Players)
                player.Clear();

            List<PosInfo> list = Managers.Data.MapData.StartPosList != null
                ? Managers.Data.MapData.StartPosList.ToList()
                : new List<PosInfo>();

            if (list.Count == 0)
            {
                PosInfo fallback = Managers.Data.MapData.ErrorPos ?? Managers.Data.MapData.LobbyPos;
                if (fallback == null)
                    fallback = new PosInfo { X = 0f, Y = 0f };
                Log.Warn<SpectatorJoinFeature>("StartPosList 为空，回退 ErrorPos/LobbyPos");
                foreach (GamePlayer player in __instance.Players)
                {
                    if (player.IsSpectator)
                        StartSpectator(__instance, player, fallback);
                    else
                        player.GameStart(fallback);
                }
            }
            else
            {
                list = Util.Shuffle(list, list.Count);
                int idx = 0;
                foreach (GamePlayer player in __instance.Players)
                {
                    // 观战者也占一个出生点：与普通人同款"先落位、再转观战"，
                    // 位置与区域包因此走的是完全相同的那条路（见 StartSpectator 的说明）。
                    PosInfo spawn = list[idx % list.Count];
                    if (player.IsSpectator)
                        StartSpectator(__instance, player, spawn);
                    else
                        player.GameStart(spawn);
                    idx++;
                }

                if (idx > list.Count)
                    Log.Info<SpectatorJoinFeature>($"玩家数={idx} 大于出生点={list.Count}，循环复用");
            }

            Server.Game.DeviceManager.Instance.InitDevices();
            Server.Game.DeviceManager.Instance.InitStorage();

            InvokeMissionStart();

            if (!TryRestoreFirstBlood(__instance, 0))
                Traverse.Create(__instance).Property("FirstBloodVictimId").SetValue(0);

            InvokeAwardStartRound();

            return false;
        }

        /// <summary>
        /// 观战者进入幽灵观战模式：`EnterSpectator`（幽灵化 + 观战引导包 + 幽灵可见性）。
        /// 私有方法用字符串定位：0.1.16b GameRoom.cs:EnterSpectator（ACS:170631）。
        /// "通知客户端已出局"的 `S_DEAD` 不在这里发 —— 它必须晚于所有区域包，
        /// 顺序理由见 <see cref="StartSpectator"/>。
        /// </summary>
        private static void InvokeEnterSpectator(GameRoom room, GamePlayer player)
        {
            MethodInfo m = AccessTools.Method(typeof(GameRoom), "EnterSpectator");
            if (m == null)
            {
                Log.Error<SpectatorJoinFeature>("EnterSpectator 不可用，观战者保持大厅状态");
                return;
            }
            m.Invoke(room, new object[] { player });
        }

        /// <summary>
        /// 观战者开局：**先按普通玩家的方式落位，再转幽灵观战**。
        ///
        /// 三步顺序不能换，每一步都有它的机制理由：
        ///
        ///   1. `Player.Move(spawn, force: true)`（ACS:175883）
        ///      —— 更新 `PublicInfo.Pos` / `EffectivePosition`，并因 `force` 广播 `S_RESPAWN`
        ///      把新坐标告诉客户端（ACS:175899-175900）；此刻服务端 `IsAlive` 仍是 true
        ///      （`IsSpectator` 与 `IsAlive` 是两回事，标记观战者不动 `IsAlive`），
        ///      所以它还会走 `ChangeArea(pos)`（ACS:175893）→ `Area.EnterPlayer`
        ///      → `SendAreaInfo`（ACS:173357/173363）→ 下发**游戏区** `S_AREA_PUBLIC`。
        ///
        ///      ⚠️ **不能只调 `Player.ChangeArea(pos, force)`**：那只切服务端区域并下发区域包，
        ///      不更新 `PublicInfo.Pos`、也不发 `S_RESPAWN`，房主客户端仍停在大厅坐标
        ///      （`GameRoom.StartLobby` 里 `Move(LobbyPos, force:true)` 写下的那个，ACS:170284-170287），
        ///      表现就是"人在地图外/地图上方"。
        ///
        ///      ⚠️ **也不能用 `Player.GameStart(PosInfo)` 顶替 `Move`**：那个方法会跑
        ///      `SkillComponent.ResetRoundState()`（ACS:175784-175785）等"活人开局"的状态复位，
        ///      而观战者在选人阶段没有角色（`SkillComponent.Data` 可能为空），不该走那条路。
        ///
        ///   2. `EnterSpectator`（ACS:170631，原版方法）
        ///      —— `MakeSpectatorGhost` 幽灵化（`IsAlive=false`、`State=Hide`、`IsGhost=true`、
        ///      速度 840）+ `AlivePlayers`→`DeadPlayers` 记账 + `SendSpectatorBootstrap`
        ///      观战引导（S_CURRENT_MAP / S_TIME / 任务状态 / 取证 / 审判 / 照片 / 画板）
        ///      + `EnterGhostVisibility` 幽灵可见性。**这就是"原版旁观者逻辑"本身**，
        ///      本功能与房主观战都复用同一个方法，没有第二套实现。
        ///      它内部的 `SendSpectatorBootstrap` 会再发一次同区域的 `S_AREA_PUBLIC` ——
        ///      客户端 `MapManager.ChangeRoom` 对同一房间是幂等的（ACS:29674/29695/29704
        ///      三段判断全部跳过），所以没有副作用。
        ///
        ///   3. `S_DEAD`（最后才发，原版 `Handle_S_DEAD` → `GameManagerEX.Dead`，ACS:42403-42407）
        ///      —— 这才是"我出局了"的那一步：`IsAlive=false`、`CanControl=false`、
        ///      隐藏自己的实体、`HideAll`。
        ///      它**必须晚于**前两步的所有区域包：客户端 `MapManager.ChangeArea(S_AREA_PUBLIC)`
        ///      在"非存活 且 已有 CurrentArea"时会**整包丢弃**（ACS:29575-29603），
        ///      而房主的当前区域此刻还是大厅房间；一旦先 Dead 就再也收不到游戏区包（地板不显示）。
        ///      先落位时客户端也还活着（客户端 `IsAlive` 只在 `Handle_S_ENTER_GAME` 的观战分支
        ///      与 `S_DEAD` 处置 false，ACS:42056/42406），正好走原版"存活玩家换区"那条路。
        ///      另外 `Dead()` 里的死亡提示与语音频道由 `IsSpectator` 区分
        ///      （ACS:28974-28979），所以观战者不会看到死亡提示 —— 前提是客户端的
        ///      `Managers.Game.IsSpectator` 已被 roster 同步（见 `HostSpectate/Patch.Roster.cs`）。
        /// </summary>
        private static void StartSpectator(GameRoom room, GamePlayer player, PosInfo spawn)
        {
            player.Move(spawn, force: true);

            InvokeEnterSpectator(room, player);

            player.Session.Send(new S_DEAD());

            Log.Info<SpectatorJoinFeature>(
                $"观战者开局：pid={player.PublicInfo.PlayerId} 落位=({spawn.X},{spawn.Y}) " +
                $"区域={player.CurrentArea?.Info.RoomId}");
        }

        /// <summary>
        /// StartPick：观战者不进入存活池、不参与角色颜色分配、不会被随机为黑方/主谋。
        /// 其余与原版一致。
        /// </summary>
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(GameRoom), "StartPick")]
        [HarmonyPrefix]
        private static bool PrefixStartPick(GameRoom __instance)
        {
            if (!ShouldHandleSpectators)
                return true;

            var t = Traverse.Create(__instance);
            t.Field("_pickReady").SetValue(false);
            t.Field("_pickPlayers").SetValue(new List<int>());
            t.Field("_pickCharacters").SetValue(new List<int>());
            t.Field("_randomPickPlayers").SetValue(new HashSet<int>());

            __instance.SyncAllPlayer(delegate
            {
                SessionManager.Instance.UpdateIsStart(isStart: true);
                __instance.Broadcast(new S_FADE_IN());
                __instance.AlivePlayers.Clear();
                __instance.DeadPlayers.Clear();

                List<GamePlayer> players = __instance.Players.Where(p => !p.IsSpectator).ToList();
                __instance.AlivePlayers.AddRange(players);
                foreach (GamePlayer p in players)
                    p.Color = EPlayerColor.White;
                if (players.Count > 0)
                {
                    int randomNumber = Util.GetRandomNumber(0, players.Count);
                    players[randomNumber].Color = EPlayerColor.Dark;
                    __instance.SetMasterMind(players[randomNumber]);
                }

                t.Field("_pickReady").SetValue(true);
                TimeManager.Instance.ResetStopWatch();
                InvokePickCharacterTick(__instance);
            });

            return false;
        }

        private static void InvokePickCharacterTick(GameRoom room)
        {
            AccessTools.Method(typeof(GameRoom), "PickCharacterTick")?.Invoke(room, null);
        }

        private static bool TryRestoreFirstBlood(GameRoom room, int victimId)
        {
            MethodInfo m = AccessTools.Method(typeof(GameRoom), "RestoreFirstBloodVictimId");
            if (m == null)
                return false;
            m.Invoke(room, new object[] { victimId });
            return true;
        }

        private static void InvokeMissionStart()
        {
            Type mmType = AccessTools.TypeByName("Server.Game.MissionManager");
            if (mmType == null)
            {
                Log.Error<SpectatorJoinFeature>("找不到 Server.Game.MissionManager");
                return;
            }

            object instance = AccessTools.PropertyGetter(mmType, "Instance")?.Invoke(null, null);
            MethodInfo start = AccessTools.Method(mmType, "StartMission", Type.EmptyTypes)
                ?? AccessTools.Method(mmType, "StartMission");
            if (instance == null || start == null)
            {
                Log.Error<SpectatorJoinFeature>("MissionManager.Instance / StartMission 不可用");
                return;
            }

            start.Invoke(instance, start.GetParameters().Length == 0 ? null : new object[start.GetParameters().Length]);
        }

        private static void InvokeAwardStartRound()
        {
            Type t = AccessTools.TypeByName("Server.Game.AwardManager");
            if (t == null)
            {
                Log.Warn<SpectatorJoinFeature>("找不到 Server.Game.AwardManager");
                return;
            }
            object inst = AccessTools.PropertyGetter(t, "Instance")?.Invoke(null, null);
            AccessTools.Method(t, "StartRound")?.Invoke(inst, null);
        }
    }
}
