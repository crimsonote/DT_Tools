using DT_Tools.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// ① 滚动条：给平板列表挂滚动条。
    ///
    /// **两个挂点**（都只做"确保挂上"，重复调用安全）：
    ///   1. `Init()` 的 Postfix —— 两个 `ScrollRect` 都是在**这一个方法里**取到的
    ///      （0.1.17a 反编译转储 :51880 `_chatScroll`、:51974 `ClueSrollRect`），最早可用时机。
    ///   2. `Section` setter 的 Postfix —— 每次切页签（含打开平板时 `Clear()` 后的
    ///      `StartSection` 走的 setter，转储 :51472-51476 / :54084）都重跑一遍。
    ///      为什么必须补：`Init` 只跑一次，而列表内容与可滚动轴是**后来**才变的
    ///      （同一个 `ClueSrollRect` 会议阶段有条、调查阶段没条，就是时机的锅）。
    ///
    /// 取法：`ClueSrollRect` 是公开属性（转储 :51556）直接用；`_chatScroll` 是私有字段（:51270）⇒ Traverse。
    ///
    /// ⚠️ 行号口径：本机没有按文件拆分的 `0.1.17a/` 源码树，上面引的是
    /// `.tmps/decomp/Assembly-CSharp-0.1.17a.decompiled.cs`（单文件转储）的行号；定方法以方法名为准。
    /// </summary>
    [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Init))]
    internal static class PatchTabletQolScrollbar
    {
        [HarmonyPostfix]
        private static void Postfix(UI_GameTablet __instance)
        {
            if (!Engine.Enabled<TabletQolFeature>())
                return;

            AttachConfigured(__instance, "Init");
        }

        /// <summary>按配置确保列表都挂上滚动条；已挂过是空操作。</summary>
        internal static void AttachConfigured(UI_GameTablet tablet, string reason)
        {
            if (tablet == null || !TabletQolFeature.Scrollbar)
                return;

            if (TabletQolFeature.ScrollbarChat)
            {
                ScrollRect chat = Traverse.Create(tablet).Field("_chatScroll").GetValue<ScrollRect>();
                TabletQolScrollbar.Attach(chat, "聊天", reason);
            }

            if (TabletQolFeature.ScrollbarClue)
                TabletQolScrollbar.Attach(tablet.ClueSrollRect, "线索", reason);
        }
    }

    /// <summary>切页签（`Section` setter）时再确保一次 —— 见 <see cref="PatchTabletQolScrollbar"/>。</summary>
    [HarmonyPatch(typeof(UI_GameTablet), nameof(UI_GameTablet.Section), MethodType.Setter)]
    internal static class PatchTabletQolScrollbarOnSection
    {
        [HarmonyPostfix]
        private static void Postfix(UI_GameTablet __instance)
        {
            if (!Engine.Enabled<TabletQolFeature>())
                return;

            PatchTabletQolScrollbar.AttachConfigured(__instance, "切换页签");
        }
    }
}
