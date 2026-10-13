using DT_Tools.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// ① 滚动条：设置窗口「举报」页的玩家列表挂滚动条。
    ///
    /// 目标：`UI_Setting_Report.Refresh()`（0.1.17a 反编译转储 :59797）—— 进这个页签
    /// （`StartSection` :59776）与切换举报历史（`OnHistoryChanged` :59829）都会走它；
    /// 之后每秒钟刷新当前玩家那次走的是 `BuildGrid`（:59879），列表已挂好就无需重挂。
    ///
    /// 列表取法照游戏自己：`GetObject(1)` = `ReportPlayerContent`，再 `GetComponentInParent<ScrollRect>()`
    /// （同 :59897）。`GetObject` 是 protected ⇒ 走 Traverse。
    ///
    /// ⚠️ 关键：prefab 里这根 `ReportScrollView` 上的 ScrollRect **m_Enabled = 0**
    /// （`common_prefab_assets_all` bundle 转储），且它自带那根 `Scrollbar Vertical` 的
    /// 轨道/手柄两张 Image 的 alpha 都是 0（看不见）却 `raycastTarget = 1`
    /// ⇒ 原版举报页**根本滚不动**，人多时列表被 `Viewport` 的 Mask 裁掉、看不全。
    /// 所以：先把 `enabled` 打开，再用 `replaceExisting` 换成我们这根可见的条，
    /// 并把原版那根隐藏掉（否则它会继续占着右侧 20px 吃点击）。
    /// </summary>
    [HarmonyPatch(typeof(UI_Setting_Report), nameof(UI_Setting_Report.Refresh))]
    internal static class PatchTabletQolScrollbarReport
    {
        [HarmonyPostfix]
        private static void Postfix(UI_Setting_Report __instance)
        {
            if (!Engine.Enabled<TabletQolFeature>())
                return;
            if (!TabletQolFeature.ScrollbarReport)
                return;

            // protected 方法，按名定位（0.1.17a 反编译转储 :90405）
            GameObject content = Traverse.Create(__instance).Method("GetObject", 1).GetValue<GameObject>();
            ScrollRect scroll = content == null
                ? null
                : content.GetComponentInParent<ScrollRect>(true);   // 与 :59897 同款取法； inactive 也算

            if (scroll == null)
            {
                Log.Warn<TabletQolFeature>("举报页没找到滚动区（ReportPlayerContent 的父级没有 ScrollRect），不挂滚动条。");
                return;
            }

            scroll.enabled = true;
            TabletQolScrollbar.Attach(scroll, "举报页", "打开举报页", replaceExisting: true);

            // ⚠️ 副作用：enabled 打开后不随功能热关闭而还原（挂在平板列表上的条同理），
            // 要复原只能重启游戏。真要做"关功能即还原"，得配 OnDisabled 统一收拾（AGENTS.md §8）。
        }
    }
}
