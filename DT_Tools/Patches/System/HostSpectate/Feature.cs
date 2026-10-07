using DT_Tools.Core;
using DT_Tools.Core.Attributes;
using DT_Tools.Patches.System.SpectatorJoin;

namespace DT_Tools.Patches.System.HostSpectate
{
    /// <summary>
    /// 房主观战（房主围观 / 挂机）：让**房主本人**不参与对局，以观战者身份围观。
    ///
    /// **统一模型：房主观战者 = 一个"在局中变成观战者"的玩家。**
    /// 本功能不自己发明一套"观战者该怎么被对待"，只做两件事：
    ///
    ///   1. 在**进入选人之前**把房主标成 `IsSpectator = true`（见 `Patch.Spectator.cs`），
    ///      其余全部交给**游戏原版语义**与上游 `SpectatorJoin` 已有的那套规则补丁
    ///      （人数口径、准备判定、选人不占位、不进存活池、开局由 `EnterSpectator` 转幽灵观战）。
    ///   2. 按收件人分包 roster，让**别人**当房主不存在，同时让**房主自己的客户端**知道
    ///      自己成了观战者（见 `Patch.Roster.cs` —— 那里是本功能唯一容易写错的地方）。
    ///
    /// **为什么不复制上游的开局流程**：上游对 `StartPick` / `GameStart` 是**整替整个方法**的
    /// （原版把 `Players` 全量当事与者，没有观战者概念）。Harmony 下多个 Prefix 都会执行，
    /// 再复制一份就会出现**开局流程跑两遍**（出生点分配两次、任务启动两次、`S_DEAD` 双发）。
    ///
    /// 上游那套补丁的判据是"本功能或它自己启用有其一"（`ShouldHandleSpectators`），
    /// 本功能启用时置位那个请求标志 —— 用户**只需要开这一个段**，
    /// 上游 `[SpectatorJoin]` 开着关着都不影响。
    ///
    /// **一条硬约束**：绝不在局内把房主还原成活人 —— 那等于让已出局的人复活。
    /// 回大厅的复位交给游戏自己的 `GameRoom.StartLobby`（0.1.16b GameRoom.cs:StartLobby，
    /// ACS:170251-170255 把观战者逐个复位并置脏 roster），本功能不需要自己做收尾。
    /// </summary>
    [PatchFeature(
        "房主观战：房主以观战者身份围观，不参与对局（不计人数、不需准备、开局转幽灵观战；默认仍进选人界面，能看到其他人的选择）。",
        defaultEnabled: false,
        side: FeatureSide.Host,
        Author = "crimsonote")]
    public sealed class HostSpectateFeature
    {
        [Config("房主在大厅是否对其他玩家可见。开启（默认）= 大厅里别人能看到他、也能和他说话；关闭 = 从进房起别人就当他不存在（房间人数标签也不含他）。选人阶段起一律隐藏（隐藏房主格子是硬要求）。")]
        public static bool HostVisibleInLobby = true;

        [Config("开始条件。关闭（默认）= 需房主点开始；开启 = 所有真实玩家准备好后自动开始（房主不必操作）。")]
        public static bool AutoStartWhenAllReady = false;

        [Config("房主是否进入选人界面。开启（默认）= 房主照常进选人界面、能看到其他人的选择（他自己的选择会被服务端忽略，其他人选完即倒计时）；关闭 = 与原版旁观者一致，不弹选人界面。")]
        public static bool ShowPickWindow = true;

        /// <summary>
        /// 配置绑定完成后：把"需要处理观战者"的请求同步给上游。
        ///
        /// 上游 `SpectatorJoin` 的那些规则补丁（人数口径 / 准备判定 / 选人不占位 /
        /// 不进存活池 / 开局转幽灵）原本只看它自己的段开关；本功能通过
        /// `SpectatorJoinFeature.HostSpectateRequested` 请求放行，
        /// 于是**只开 [HostSpectate] 也能生效**，无需用户同时开启 [SpectatorJoin]。
        ///
        /// ⚠️ 这里不能省：段开关是启动读档时读的，而 `OnEnabled` 只在热切换时触发
        /// （AGENTS.md §8「OnEnabled 启动时不触发」）。
        /// </summary>
        private static void OnLoaded() => SyncHandlingRequest();

        /// <summary>热开启：立刻让上游的观战者规则生效。</summary>
        private static void OnEnabled() => SyncHandlingRequest();

        /// <summary>
        /// 热关闭：撤销请求。
        ///
        /// **故意不在局内还原房主** —— 那等于让已出局的人复活。
        /// 还原由游戏自己的 `GameRoom.StartLobby` 在回到大厅时完成；若此刻房主还没被标记
        /// （大厅阶段），关掉就等于什么都没发生。
        /// </summary>
        private static void OnDisabled()
        {
            SyncHandlingRequest();
            Log.Info<HostSpectateFeature>("房主观战：段已关闭，房主将在回到大厅时由游戏复位");
        }

        /// <summary>把本功能的启用状态同步给上游（依赖方向：HostSpectate → SpectatorJoin，向下）。</summary>
        private static void SyncHandlingRequest()
        {
            bool enabled = Engine.Enabled<HostSpectateFeature>();
            SpectatorJoinFeature.HostSpectateRequested = enabled;
            SpectatorJoinFeature.ShowPickWindowForSpectator = enabled && HostSpectateFeature.ShowPickWindow;
        }
    }
}
