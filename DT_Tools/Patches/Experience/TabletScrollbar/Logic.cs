using DT_Tools.Core;
using UnityEngine;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletScrollbar
{
    /// <summary>
    /// 运行时给一个 `ScrollRect` 补上可拖拽的滚动条。
    ///
    /// 为什么要自己构造：游戏**从不用** `UnityEngine.UI.Scrollbar`（0.1.17a 反编译转储全文 0 处），
    /// 没有现成的滚动条 prefab 可复用，只能自己拼一个：
    /// 轨道（背景 `Image`）+ 手柄（子 `Image`）+ `Scrollbar`，然后交给 `ScrollRect` 驱动。
    ///
    /// 方向按 `ScrollRect` 的真实轴选：竖排列表给右侧竖条，横排列表给底部横条。
    /// 平板那三个列表都是竖排（`_missionScroll` / `_chatScroll` / `ClueSrollRect` 用的都是
    /// `verticalNormalizedPosition`、`UI_SmoothScroll.ScrollToVertical`，转储 :52730 / :53667 / :54830），
    /// 所以实际都是右侧竖条。
    ///
    /// **防误触（关键设计）**：轨道 `Image` 的 `raycastTarget = false`，只有**手柄**可点。
    ///   - 这样滚动条**不会抢走**它覆盖区域的点击 —— 底下若是"提交线索"之类的按钮，点它照样生效；
    ///   - 顺带也去掉了"点轨道空白处跳一页"这个最容易误触的行为（Unity 的 `Scrollbar.OnPointerDown`
    ///     在点不到手柄时会整页跳）。
    ///   - 手柄仍然能拖：Unity 输入模块找拖拽处理者走的是 `GetEventHandler<IDragHandler>`（**沿父级向上找**），
    ///     指针事件本身也会从手柄冒泡到 `Scrollbar`，所以"只让手柄可点"不影响拖动。
    ///
    /// 幂等：`ScrollRect` 上已经有竖/横滚动条就直接跳过 —— 既防重复挂，也防别的插件已经挂过。
    /// </summary>
    internal static class TabletScrollbarLogic
    {
        private const string BarName = "DT_TabletScrollbar";

        /// <summary>轨道底色透明度（白，压在游戏 UI 上）。</summary>
        private const float TrackAlpha = 0.10f;

        /// <summary>手柄透明度。</summary>
        private const float HandleAlpha = 0.45f;

        /// <summary>给列表挂滚动条；已挂过则跳过。返回是否本次新建。</summary>
        public static bool Attach(ScrollRect scroll, string label, string reason)
        {
            if (scroll == null)
                return false;
            if (scroll.verticalScrollbar != null || scroll.horizontalScrollbar != null)
                return false;                        // 已挂（被销毁时 Unity 的 null 语义会让这里重新挂）

            // 轴：以 ScrollRect 当前的真实轴为准；**两个轴都没开时按竖排处理，而不是跳过** ——
            // 平板这三个列表都是竖排（`verticalNormalizedPosition` / `UI_SmoothScroll.ScrollToVertical`，
            // 转储 :52730 / :53667 / :54830），而"两个轴都没开"通常只是内容还短/还没刷新过 ⇒
            // 跳过就会永远挂不上（实机踩过：同一块 `ClueSrollRect` 在会议阶段有条、调查阶段没条）。
            bool axisForced = !scroll.vertical && !scroll.horizontal;
            bool vertical = scroll.vertical || axisForced;

            int width = Mathf.Clamp(TabletScrollbarFeature.BarWidth, 4, 40);
            int inset = Mathf.Clamp(TabletScrollbarFeature.BarOffset, 0, 60);
            Transform parent = scroll.viewport != null ? scroll.viewport : scroll.transform;

            GameObject go = new GameObject(BarName, typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            if (vertical)
            {
                rt.anchorMin = new Vector2(1f, 0f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.sizeDelta = new Vector2(width, 0f);
                rt.anchoredPosition = new Vector2(-inset, 0f);   // 正值 = 向列表内侧让开，避开右缘的按钮
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(0f, width);
                rt.anchoredPosition = new Vector2(0f, inset);
            }

            Image track = go.GetComponent<Image>();
            track.color = new Color(1f, 1f, 1f, TrackAlpha);
            track.raycastTarget = false;             // ★ 不抢点击：见类注释"防误触"

            GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            RectTransform hrt = (RectTransform)handle.transform;
            hrt.SetParent(rt, false);
            hrt.localScale = Vector3.one;
            hrt.anchorMin = Vector2.zero;
            hrt.anchorMax = Vector2.one;
            hrt.offsetMin = new Vector2(1f, 1f);     // 内缩 1px，视觉上不贴死轨道
            hrt.offsetMax = new Vector2(-1f, -1f);
            Image hi = handle.GetComponent<Image>();
            hi.color = new Color(1f, 1f, 1f, HandleAlpha);
            hi.raycastTarget = true;                 // 只有手柄吃指针

            Scrollbar bar = go.GetComponent<Scrollbar>();
            bar.handleRect = hrt;
            bar.targetGraphic = hi;
            bar.direction = vertical ? Scrollbar.Direction.BottomToTop : Scrollbar.Direction.LeftToRight;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };

            ScrollRect.ScrollbarVisibility visibility = TabletScrollbarFeature.AlwaysVisible
                ? ScrollRect.ScrollbarVisibility.Permanent
                : ScrollRect.ScrollbarVisibility.AutoHide;
            if (vertical)
            {
                scroll.verticalScrollbar = bar;
                scroll.verticalScrollbarVisibility = visibility;
                scroll.verticalScrollbarSpacing = 2f;
            }
            else
            {
                scroll.horizontalScrollbar = bar;
                scroll.horizontalScrollbarVisibility = visibility;
                scroll.horizontalScrollbarSpacing = 2f;
            }

            Log.Info<TabletScrollbarFeature>(
                $"平板列表已挂滚动条：{label}（{reason}；{(vertical ? "右侧竖条" : "底部横条")}，宽 {width}px，内缩 {inset}px，" +
                $"{(TabletScrollbarFeature.AlwaysVisible ? "常驻" : "内容超出时才出现")}；" +
                $"当时轴=竖{scroll.vertical}/横{scroll.horizontal}{(axisForced ? "（都没开，按竖排处理）" : "")}；" +
                $"轨道不吃点击、只有手柄可拖）");
            return true;
        }
    }
}
