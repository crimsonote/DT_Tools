using DT_Tools.Core;
using HarmonyLib;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// ② 拦下「提交线索」按钮的点击，改成先弹确认栏；确认后由 <see cref="TabletQolConfirm"/> 调用原版提交函数。
    ///
    /// 目标：`UI_ClueSubItem.OnClickPropositionButton(PointerEventData)`（**私有**，0.1.17a 反编译转储 :86488，
    /// 绑定处 :86135 —— `GetObject(3)` 就是卡片上的 `PropositionButton`）⇒ 私有方法用字符串名定位。
    ///
    /// 内嵌在确认栏里的那张卡片只作展示，它的提交按钮已被关掉（`SetActive(false)`），
    /// 所以点不到、也就不会再弹一次确认栏。
    ///
    /// 不拦的两种情况：功能没开（或 `ConfirmSubmit` 关了）；`Suppress`（确认后的重入）。
    /// </summary>
    [HarmonyPatch(typeof(UI_ClueSubItem), "OnClickPropositionButton")]
    internal static class PatchTabletQolConfirmSubmit
    {
        [HarmonyPrefix]
        private static bool Prefix(UI_ClueSubItem __instance)
        {
            if (!Engine.Enabled<TabletQolFeature>())
                return true;
            if (!TabletQolFeature.ConfirmSubmit)
                return true;
            if (TabletQolConfirm.Suppress)
                return true;
            if (!CanSubmit(__instance))
                return true;

            TabletQolConfirm.Show(__instance);
            return false;
        }

        /// <summary>
        /// 卡片自己的"现在能不能提交"判据（`CanSubmitProposition()`，私有，转储 :86295 ——
        /// 讨论阶段 + 存活 + 尚未提交）。读不到就返回 false ⇒ Prefix 放行原版，不做任何拦截。
        ///
        /// 这里**只查这一条**：原版自己还有 `IsValid(Clue.Info)`（命题完整）那道门（:86490），
        /// 由原版负责判断 —— 确认栏不该去猜它的结论。
        /// </summary>
        private static bool CanSubmit(UI_ClueSubItem card)
        {
            try
            {
                return Traverse.Create(card).Method("CanSubmitProposition").GetValue() is bool can && can;
            }
            catch (global::System.Exception ex)
            {
                Log.Warn<TabletQolFeature>("读 CanSubmitProposition 失败，按原版走（不弹确认栏）：" + ex.Message);
                return false;
            }
        }
    }
}
