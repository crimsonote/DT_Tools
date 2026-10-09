using DT_Tools.Core.Attributes;

namespace DT_Tools.Patches.Experience.ClueSubmitConfirm
{
    /// <summary>
    /// 「提交线索」二次确认栏（带线索卡片概要）。
    ///
    /// **起因**：在平板里滚动线索列表时容易手滑点到卡片上的「提交线索」—— 而它**不可撤回**：
    /// `UI_ClueSubItem.OnClickPropositionButton`（私有，0.1.17a 反编译转储 :86488-86500）直接发
    /// `C_SUBMIT_PROPOSITION`、置 `Managers.Game.IsSubmitProposition = true`、并把平板关掉。
    ///
    /// **做法**：拦下那个方法（Prefix 返回 false），先弹一个**与平板同风格**的确认栏 ——
    /// 面板 sprite / 按钮 sprite / 字体 / 配色全部从**卡片自己身上借**
    /// （`GetImage`/`GetText` 是 protected，用 Traverse 取，转储 :90405-90425），所以看起来是原生的；
    /// 面板尺寸按平板 rect 的比例算，跟着平板一起被关闭。
    /// 确认栏里显示**线索卡片概要**（线索正文 + 谁/在哪/何时，直接取卡片当前显示的文本，
    /// Texts 顺序见转储 :86095-86107），确认后才真正执行原方法，取消则什么都不做。
    ///
    /// **只在按钮本来就能提交时才弹**：复用卡片自己的 `CanSubmitProposition()`（私有，转储 :86295），
    /// 所以非讨论阶段 / 已提交 / 已死的情况下不会多弹一个框，行为与原版一致。
    ///
    /// 纯客户端（`FeatureSide.Client`）：只是本地多一道确认；发包时机与内容完全交给原版。
    /// </summary>
    [PatchFeature(
        "「提交线索」加二次确认：弹一个与平板同风格的确认栏，带线索卡片概要（线索正文 + 谁/在哪/何时）；确认后才提交，取消则不动（客户端，自己装生效）。",
        defaultEnabled: false,
        side: FeatureSide.Client,
        Author = "crimsonote")]
    public sealed class ClueSubmitConfirmFeature
    {
        [Config("确认栏里显示线索卡片概要（线索正文 + 谁/在哪/何时）。关闭 = 只显示一句确认提示。")]
        public static bool ShowSummary = true;

        [Config("确认栏遮罩的不透明度（0–1）。", Min = 0f, Max = 1f)]
        public static float DimAlpha = 0.55f;

        [Config("确认栏宽度占平板宽度的比例（0.3–0.95）。", Min = 0.3f, Max = 0.95f)]
        public static float PanelWidthRatio = 0.62f;
    }
}
