using DT_Tools.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletScrollbar
{
    /// <summary>
    /// 会议/调查阶段平板（`UI_GameTablet`）的列表挂滚动条。
    ///
    /// **为什么挂两个点**（都不做判断、只做"确保挂上"，重复调用安全）：
    ///   1. `Init()` 的 Postfix —— 三个 `ScrollRect` 都是在**这一个方法里**取到的
    ///      （0.1.17a 反编译转储 :51879 `_missionScroll`、:51880 `_chatScroll`、:51974 `ClueSrollRect`），
    ///      这是最早的可用时机。
    ///   2. `Section` setter 的 Postfix —— 每次切页签（含打开平板时 `Clear()` → `_section = DefaultSection()`
    ///      之后 `StartSection` 走的 setter，转储 :51472-51476 / :54084）都会重跑一遍。
    ///      为什么必须补这个点：`Init` 是**一次性**的，而列表内容与 `ScrollRect` 的可滚动轴是**后来**才变的
    ///      （同一个 `ClueSrollRect` 在会议阶段有条、调查阶段没条就是这个时机的锅）⇒
    ///      挂载必须"每次看这个页签时都确保一遍"。
    ///
    /// 取法：`ClueSrollRect` 是公开属性（转储 :51556）直接用；
    /// `_chatScroll` / `_missionScroll` 是私有字段（转储 :51270 / :51268）⇒ Traverse 取。
    ///
    /// 页签可访问性（决定哪些列表真会被看到，转储 :52086-52100）：
    ///   调查/会议（`IsInvestigation` = Detective 或 Trial，转储 :51479-51486）里
    ///   **默认开「线索」页**（`DefaultSection()` :52064），「任务」页在这两个阶段**不可访问**
    ///   ⇒ `MissionList` 开关实际上用不到，留作完整性与将来版本变化。
    ///
    /// ⚠️ 行号口径：本机没有按文件拆分的 `0.1.17a/` 源码树，上面引的是
    /// `.tmps/decomp/Assembly-CSharp-0.1.17a.decompiled.cs`（单文件转储）的行号；
    /// 定方法时以方法名（`UI_GameTablet.Init` / `UI_GameTablet.Section`）为准。
    /// </summary>
    [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Init))]
    internal static class PatchTabletScrollbar
    {
        [HarmonyPostfix]
        private static void Postfix(UI_GameTablet __instance)
        {
            if (!Engine.Enabled<TabletScrollbarFeature>())
                return;

            AttachConfigured(__instance, "Init");
        }

        /// <summary>按配置确保三个列表都挂上滚动条；已挂过是空操作。</summary>
        internal static void AttachConfigured(UI_GameTablet tablet, string reason)
        {
            if (tablet == null)
                return;

            if (TabletScrollbarFeature.ChatList)
            {
                ScrollRect chat = Traverse.Create(tablet).Field("_chatScroll").GetValue<ScrollRect>();
                TabletScrollbarLogic.Attach(chat, "聊天", reason);
            }

            if (TabletScrollbarFeature.ClueList)
                TabletScrollbarLogic.Attach(tablet.ClueSrollRect, "线索", reason);

            if (TabletScrollbarFeature.MissionList)
            {
                ScrollRect mission = Traverse.Create(tablet).Field("_missionScroll").GetValue<ScrollRect>();
                TabletScrollbarLogic.Attach(mission, "任务", reason);
            }
        }
    }

    /// <summary>
    /// 切页签（`Section` setter）时再确保一次 —— 见 <see cref="PatchTabletScrollbar"/> 的类注释。
    /// </summary>
    [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Section), MethodType.Setter)]
    internal static class PatchTabletScrollbarOnSection
    {
        [HarmonyPostfix]
        private static void Postfix(UI_GameTablet __instance)
        {
            if (!Engine.Enabled<TabletScrollbarFeature>())
                return;

            PatchTabletScrollbar.AttachConfigured(__instance, "切换页签");
        }
    }
}
