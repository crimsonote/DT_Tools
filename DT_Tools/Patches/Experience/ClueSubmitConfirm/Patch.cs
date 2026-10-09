using DT_Tools.Core;
using HarmonyLib;

namespace DT_Tools.Patches.Experience.ClueSubmitConfirm
{
    /// <summary>
    /// 拦下「提交线索」按钮的点击，改成先弹确认栏。
    ///
    /// 目标：`UI_ClueSubItem.OnClickPropositionButton(PointerEventData)`（**私有**，转储 :86488，
    /// 绑定处 :86135 —— `GetObject(3)` 就是卡片上的 `PropositionButton`）⇒ 私有方法用字符串名定位。
    ///
    /// 三种情况**不拦**、保持原版行为：
    ///   1. 功能没开；
    ///   2. `Suppress`（确认栏点过"确认"，正在重入原方法）；
    ///   3. 卡片自己判定"现在不能提交"（`CanSubmitProposition()`，私有，转储 :86295 ——
    ///      它涵盖"是否讨论阶段 + 是否活着 + 是否已提交"）⇒ 不加多余的框。
    /// 其余情况返回 false 拦下，等确认栏的结果。
    /// </summary>
    [HarmonyPatch(typeof(UI_ClueSubItem), "OnClickPropositionButton")]
    internal static class PatchClueSubmitConfirm
    {
        [HarmonyPrefix]
        private static bool Prefix(UI_ClueSubItem __instance)
        {
            if (!Engine.Enabled<ClueSubmitConfirmFeature>())
                return true;
            if (ClueSubmitConfirmLogic.Suppress)
                return true;
            if (!CanSubmit(__instance))
                return true;

            ClueSubmitConfirmLogic.Show(__instance);
            return false;
        }

        /// <summary>读卡片自己的"能不能提交"判据；读不到就按原版放行（宁可少拦，不可错拦）。</summary>
        private static bool CanSubmit(UI_ClueSubItem card)
        {
            try
            {
                object value = Traverse.Create(card).Method("CanSubmitProposition").GetValue();
                return value is bool can && can;
            }
            catch (global::System.Exception ex)
            {
                Log.Warn<ClueSubmitConfirmFeature>("读 CanSubmitProposition 失败，按原版放行：" + ex.Message);
                return false;
            }
        }
    }
}
