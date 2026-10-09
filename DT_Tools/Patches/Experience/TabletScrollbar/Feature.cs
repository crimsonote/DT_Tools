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
    /// **调查阶段也覆盖（实机踩过）**：会议（Trial）与调查（Detective）用的是**同一个** `UI_GameTablet`，
    /// 而且两个阶段默认都开**线索页**（`DefaultSection()` 在 `IsInvestigation`（= Detective 或 Trial）时
    /// 返回 `ClueSection`，转储 :52064-52071 / :51479-51486）⇒ 看到的是同一块 `ClueSrollRect`。
    /// 但 `Init` 只在 UI 创建时跑一次，而列表内容与可滚动轴是**后来**才变的 ⇒ 只挂 `Init` 就会出现
    /// "会议阶段有条、调查阶段没条"。所以**挂两个点**：`Init` + `Section` setter（每次切页签/打开都确保一遍），
    /// 并且两个轴都没开时按竖排处理而不是跳过（详见 `Patch.cs` / `Logic.cs`）。
    ///
    /// 「任务」页在调查/会议阶段**不可访问**（`IsSectionAccessible`，转储 :52090-52094）⇒
    /// `MissionList` 开关实际上用不到，保留只为完整性与将来版本变化。
    ///
    /// 三个列表（都在 `UI_GameTablet.Init()` 里取到，转储 :51879 / :51880 / :51974）：
    /// 任务 `_missionScroll`、聊天 `_chatScroll`、线索 `ClueSrollRect`，且**都是竖排**
    /// （用的都是 `verticalNormalizedPosition` / `UI_SmoothScroll.ScrollToVertical`）。
    /// 默认给**聊天 + 线索**挂条。
    ///
    /// **防误触**：滚动条的轨道不吃点击，只有手柄可拖（详见 `Logic.cs`）——
    /// 这样它压在"提交线索"之类的按钮附近时，按钮照样点得到，也不会出现"点空白处跳一页"。
    ///
    /// 纯客户端本地（`FeatureSide.Client`）：只改自己屏幕上的 UI，不影响别人，也不需要房主装。
    /// </summary>
    [PatchFeature(
        "会议/调查阶段平板的列表加可拖拽滚动条：聊天 / 线索（可分别开关，另有任务列表开关），拖手柄即可快速翻页；轨道不吃点击，不会挡住下面的按钮（客户端，自己装生效）。",
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
        public static int BarWidth = 10;

        [Config("滚动条与列表边缘的距离（像素）：正值 = 向列表内侧让开，用来避开右缘/下缘的按钮。", Min = 0, Max = 60)]
        public static int BarOffset = 6;

        [Config("滚动条常驻显示。关闭 = 只在列表内容超出可视范围时出现。")]
        public static bool AlwaysVisible = true;
    }
}
