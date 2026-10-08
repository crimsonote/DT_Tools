using DT_Tools.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletScrollbar
{
    /// <summary>
    /// 会议阶段平板（`UI_GameTablet`）的列表挂滚动条。
    ///
    /// 挂点选 `Init()` 的 Postfix：三个 `ScrollRect` 都是在**这一个方法里**取到的
    /// （0.1.17a 反编译转储 :51879 `_missionScroll`、:51880 `_chatScroll`、:51974 `ClueSrollRect`），
    /// Postfix 时三者都已就位，不需要再猜时机、也不用等开面板。
    ///
    /// 取法：`ClueSrollRect` 是公开属性（转储 :51556）直接用；
    /// `_chatScroll` / `_missionScroll` 是私有字段（转储 :51270 / :51268）⇒ Traverse 取。
    ///
    /// ⚠️ 行号口径：本机没有按文件拆分的 `0.1.17a/` 源码树，上面引的是
    /// `.tmps/decomp/Assembly-CSharp-0.1.17a.decompiled.cs`（单文件转储）的行号；
    /// 定方法时以方法名（`UI_GameTablet.Init`）为准。
    /// </summary>
    [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Init))]
    internal static class PatchTabletScrollbar
    {
        [HarmonyPostfix]
        private static void Postfix(UI_GameTablet __instance)
        {
            if (!Engine.Enabled<TabletScrollbarFeature>())
                return;

            if (TabletScrollbarFeature.ChatList)
            {
                ScrollRect chat = Traverse.Create(__instance).Field("_chatScroll").GetValue<ScrollRect>();
                TabletScrollbarLogic.Attach(chat, "聊天");
            }

            if (TabletScrollbarFeature.ClueList)
                TabletScrollbarLogic.Attach(__instance.ClueSrollRect, "线索");

            if (TabletScrollbarFeature.MissionList)
            {
                ScrollRect mission = Traverse.Create(__instance).Field("_missionScroll").GetValue<ScrollRect>();
                TabletScrollbarLogic.Attach(mission, "任务");
            }
        }
    }
}
