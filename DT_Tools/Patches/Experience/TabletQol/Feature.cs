using DT_Tools.Core.Attributes;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// 游戏内几处列表的客户端体验优化，**合成一个配置段**：
    ///
    /// ① **滚动条**：给列表挂可拖拽滚动条。
    ///    游戏自己**从不用** `UnityEngine.UI.Scrollbar`（0.1.17a 反编译转储全文 0 处），列表只靠
    ///    `ScrollRect` 的拖拽/滚轮（`UI_ScrollDrag`，转储 :68540 附近）⇒ 长列表翻页很累，
    ///    而且有几处连拖拽都没有 ⇒ 干脆自己拼一根条挂上。
    ///    默认**贴边**（`BarOffset = 0`）、**可点轨道翻页**（`BarTrackClickPages`，与"只有手柄能拖"相比更省事）。
    ///
    ///    挂了四处，各自的依据：
    ///    - 平板「聊天 / 线索」两个列表（`UI_GameTablet._chatScroll` :51270 / `ClueSrollRect` :51556）——
    ///      会议(Trial)与调查(Detective)共用同一块 UI，两阶段默认都开「线索」页 ⇒ 挂两个点
    ///      （`Init` 转储 :51880/:51974 + `Section` setter :51472/:54084）才能两阶段都生效。
    ///    - 发信机聊天栏（`UI_ChatDevicePopup._scroll` :46894）—— 公共频道与秘密频道是**同一个**滚动区
    ///      （`Open(device, isSecret)` :46989 只是换 `_isSecret`），最多 100 行（:46878 / :47039）。
    ///      prefab 侧核对过：`ChatScrollView` 的 ScrollRect 是**启用**的、只是没配滚动条。
    ///    - 设置→举报页玩家列表（`UI_Setting_Report.Refresh` :59797 → `GetObject(1)` 的父级 ScrollRect，:59897 同款取法）——
    ///      prefab 侧核对过：这根 `ReportScrollView` 上的 ScrollRect **m_Enabled = 0**，
    ///      它自带那根 `Scrollbar Vertical` 的轨道/手柄两张 Image 的 alpha 都是 0（看不见）却 `raycastTarget = 1`
    ///      ⇒ 原版举报页**根本滚不动**，人多时列表被 Viewport 的 Mask 裁掉、看不全。
    ///      所以这里先把 `enabled` 打开，再用 `replaceExisting` 换成我们这根可见的条，并把原版那根藏掉。
    ///
    ///    **没做**：大厅/局内直聊（`UI_DirectChat_Lobby` :75157 / `UI_DirectChat` :74838）——
    ///    它的 `DirectChatList` 只有 LayoutGroup + ContentSizeFitter，**没有 ScrollRect / 视口 / 遮罩**，
    ///    且 `MaxItems` 只有 5（:75163，局内 3），超出即 `Destroy`（:74966-74970）⇒ 没有可滚的内容，
    ///    硬做等于把这套聊天重做一遍，暂不做。
    ///
    /// ② **「提交线索」二次确认**：卡片上的 `PropositionButton` 一点就提交且不可撤回
    ///    （`UI_ClueSubItem.OnClickPropositionButton` 直接发 `C_SUBMIT_PROPOSITION`，转储 :86488-86500）
    ///    ⇒ 拦下来先弹确认栏；确认栏里**内嵌一张原版线索卡片**（`Managers.UI.MakeSubItem<UI_ClueSubItem>`
    ///    + `SetInfo` + `InitPopupClue`，照抄游戏自己的用法，转储 :52838-52840），按钮也是**克隆原版按钮**
    ///    ⇒ 视觉与原版一致，不再自己拼图。
    ///
    /// 纯客户端（`FeatureSide.Client`）：只改自己屏幕上的 UI，不影响别人，也不需要房主装。
    /// </summary>
    [PatchFeature(
        "客户端体验优化：① 给平板聊天/线索列表、发信机聊天栏（公共与秘密频道）、设置→举报页玩家列表挂可拖拽滚动条（贴边、可点轨道翻页；举报页的滚动区在游戏内本是禁用状态，挂条时会一并打开）；② 点「提交线索」先弹二次确认栏（内嵌一张原版线索卡片，确认后才提交）。两项可分别开关（客户端，自己装生效）。",
        defaultEnabled: false,
        side: FeatureSide.Client,
        Author = "crimsonote")]
    public sealed class TabletQolFeature
    {
        // ── ① 滚动条 ──────────────────────────────────────────────────
        [Config("① 滚动条：给游戏内列表加可拖拽滚动条。")]
        public static bool Scrollbar = true;

        [Config("① 滚动条：给平板「聊天」列表挂。")]
        public static bool ScrollbarChat = true;

        [Config("① 滚动条：给平板「线索」列表挂。")]
        public static bool ScrollbarClue = true;

        [Config("① 滚动条：给发信机聊天栏挂（公共频道与秘密频道是同一个滚动区，一处挂上两处都能滚）。")]
        public static bool ScrollbarChatDevice = true;

        [Config("① 滚动条：给设置→举报页的玩家列表挂（原版这个滚动区在游戏内是禁用状态，人多时列表被裁掉、看不全；挂条时会一并打开它）。")]
        public static bool ScrollbarReport = true;

        [Config("① 滚动条：宽度（像素）。", Min = 4, Max = 40)]
        public static int BarWidth = 10;

        [Config("① 滚动条：与列表边缘的距离（像素，0 = 贴边）。", Min = 0, Max = 60)]
        public static int BarOffset = 0;

        [Config("① 滚动条：常驻显示。关闭 = 只在列表内容超出可视范围时出现。")]
        public static bool BarAlwaysVisible = true;

        [Config("① 滚动条：点轨道空白处翻一页（更省事）。关闭 = 只能拖手柄，且轨道不抢点击。")]
        public static bool BarTrackClickPages = true;

        // ── ② 提交线索二次确认 ────────────────────────────────────────
        [Config("② 二次确认：点「提交线索」先弹确认栏（提交不可撤回）。")]
        public static bool ConfirmSubmit = true;

        [Config("② 二次确认：确认栏里内嵌一张原版线索卡片（显示该线索的谁/在哪/何时与正文）。")]
        public static bool ConfirmShowCard = true;

        [Config("② 二次确认：遮罩不透明度（0–1）。", Min = 0f, Max = 1f)]
        public static float DimAlpha = 0.45f;

        [Config("② 二次确认：确认栏面板底色（HTML 颜色）。默认 #22303F = 从游戏内平板线索卡片上取到的同一支蓝。")]
        public static string PanelColorHex = "#22303F";

        [Config("② 二次确认：确认栏面板的不透明度（0–1，1 = 完全不透明）。", Min = 0f, Max = 1f)]
        public static float PanelAlpha = 0.86f;

        [Config("② 二次确认：两个按钮的不透明度（0–1，1 = 纯白实心）。", Min = 0.2f, Max = 1f)]
        public static float ButtonAlpha = 0.85f;
    }
}
