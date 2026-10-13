using DT_Tools.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// ① 滚动条：发信机聊天栏挂滚动条。公共频道与秘密频道共用同一个滚动区。
    ///
    /// 目标：`UI_ChatDevicePopup.Open(ChatDevice, bool, bool)`（0.1.17a 反编译转储 :46989）——
    /// 每次交互打开发信机都会走它，且 `_isSecret` 此时已置好（:46991）⇒ 日志里能分开两个频道。
    ///
    /// 列表：私有字段 `_scroll`（:46894，在 `Init` 里由 `ChatList` 的父对象取到，:46935）。
    /// 最多 100 行（`MAX_ROWS` :46878，裁剪 :47039-47043）⇒ 长会话必须滚。
    ///
    /// prefab 侧核对过（`common_prefab_assets_all` bundle 转储）：
    /// `ChatScrollView`(ScrollRect，竖轴、**m_Enabled = 1**) → `Viewport`(RectMask2D)
    /// → `ChatList`(LayoutGroup + ContentSizeFitter)，与平板里的 `ChatScrollView` 同构；
    /// 差别只是原版**没给它配**滚动条，滚只能靠滚轮/拖动。
    /// </summary>
    [HarmonyPatch(typeof(UI_ChatDevicePopup), nameof(UI_ChatDevicePopup.Open))]
    internal static class PatchTabletQolScrollbarChatDevice
    {
        [HarmonyPostfix]
        private static void Postfix(UI_ChatDevicePopup __instance)
        {
            if (!Engine.Enabled<TabletQolFeature>())
                return;
            if (!TabletQolFeature.ScrollbarChatDevice)
                return;

            // 私有字段，按名定位（0.1.17a 反编译转储 :46894）
            ScrollRect scroll = Traverse.Create(__instance).Field("_scroll").GetValue<ScrollRect>();

            TabletQolScrollbar.Attach(
                scroll,
                __instance.IsSecretMode ? "发信机·秘密频道" : "发信机·公共频道",
                "打开发信机");
        }
    }
}
