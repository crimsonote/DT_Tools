using DT_Tools.Core.Attributes;

namespace DT_Tools.Patches.Experience.TabletScrollbar
{
    /// <summary>
    /// 会议阶段平板（`UI_GameTablet`）的列表加**可拖拽滚动条**。
    ///
    /// 为什么需要：游戏自己**从不用** `UnityEngine.UI.Scrollbar`（0.1.17a 反编译转储全文 0 处），
    /// 平板里的列表只靠 `ScrollRect` 的拖拽/滚轮滚动（`UI_ScrollDrag`，转储 :68540 附近），
    /// 长列表（聊天、线索）要翻很多次 ⇒ 这里补一个能直接拖的条。
    ///
    /// 三个列表（都在 `UI_GameTablet.Init()` 里取到，转储 :51879 / :51880 / :51974）：
    /// 任务 `_missionScroll`、聊天 `_chatScroll`、线索 `ClueSrollRect`。
    /// 默认给**聊天 + 线索**挂条，任务列表留开关。
    ///
    /// 纯客户端本地（`FeatureSide.Client`）：只改自己屏幕上的 UI，不影响别人，也不需要房主装。
    /// </summary>
    [PatchFeature(
        "会议阶段平板的列表加可拖拽滚动条：聊天 / 线索（可分别开关，另有任务列表开关），拖动滚动条即可快速翻页（客户端，自己装生效）。",
        defaultEnabled: false,
        side: FeatureSide.Client,
        Author = "crimsonote")]
    public sealed class TabletScrollbarFeature
    {
        [Config("给「聊天」列表加滚动条。")]
        public static bool ChatList = true;

        [Config("给「线索」列表加滚动条。")]
        public static bool ClueList = true;

        [Config("给「任务」列表加滚动条。")]
        public static bool MissionList = false;

        [Config("滚动条宽度（像素）。", Min = 4, Max = 40)]
        public static int BarWidth = 12;

        [Config("滚动条常驻显示。关闭 = 只在列表内容超出可视范围时出现。")]
        public static bool AlwaysVisible = true;
    }
}
