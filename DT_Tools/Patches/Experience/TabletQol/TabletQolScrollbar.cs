using DT_Tools.Core;
using UnityEngine;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.TabletQol
{
    /// <summary>
    /// ① 滚动条：运行时给一个 `ScrollRect` 补上可拖拽的滚动条。
    ///
    /// 游戏**从不用** `UnityEngine.UI.Scrollbar`（0.1.17a 反编译转储全文 0 处），没有现成 prefab 可复用，
    /// 只能自己拼：轨道（背景 `Image`）+ 手柄（子 `Image`）+ `Scrollbar`，交给 `ScrollRect` 驱动。
    ///
    /// 默认**贴边**（`BarOffset = 0`）；`BarTrackClickPages` 打开时轨道可点（点空白翻一页，更省事），
    /// 关闭时轨道 `raycastTarget = false` ⇒ 不抢底下按钮的点击、也不会误触跳页（只留手柄可拖）。
    ///
    /// 轴以 `ScrollRect` 当前真实轴为准；**两个轴都没开时按竖排处理**而不是跳过 ——
    /// 平板列表都是竖排（`verticalNormalizedPosition` / `UI_SmoothScroll.ScrollToVertical`，
    /// 转储 :52730 / :53667 / :54830），而"都没开"通常只是内容还短/还没刷新 ⇒ 跳过就永远挂不上
    /// （实机踩过：同一块 `ClueSrollRect` 会议阶段有条、调查阶段没条）。
    /// </summary>
    internal static class TabletQolScrollbar
    {
        private const string BarName = "DT_TabletScrollbar";

        private const float TrackAlpha = 0.10f;
        private const float HandleAlpha = 0.45f;

        public static bool Attach(ScrollRect scroll, string label, string reason)
        {
            if (scroll == null)
                return false;
            if (scroll.verticalScrollbar != null || scroll.horizontalScrollbar != null)
                return false;                        // 已挂（被销毁时 Unity 的 null 语义会让这里重新挂）

            bool axisForced = !scroll.vertical && !scroll.horizontal;
            bool vertical = scroll.vertical || axisForced;

            int width = Mathf.Clamp(TabletQolFeature.BarWidth, 4, 40);
            int inset = Mathf.Clamp(TabletQolFeature.BarOffset, 0, 60);
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
                rt.anchoredPosition = new Vector2(-inset, 0f);
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
            // 可点轨道 ⇒ 点空白翻一页（更省事）；不可点 ⇒ 不抢点击、不误触跳页
            track.raycastTarget = TabletQolFeature.BarTrackClickPages;

            GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            RectTransform hrt = (RectTransform)handle.transform;
            hrt.SetParent(rt, false);
            hrt.localScale = Vector3.one;
            hrt.anchorMin = Vector2.zero;
            hrt.anchorMax = Vector2.one;
            hrt.offsetMin = new Vector2(1f, 1f);
            hrt.offsetMax = new Vector2(-1f, -1f);
            Image hi = handle.GetComponent<Image>();
            hi.color = new Color(1f, 1f, 1f, HandleAlpha);
            hi.raycastTarget = true;                 // 手柄始终可拖

            Scrollbar bar = go.GetComponent<Scrollbar>();
            bar.handleRect = hrt;
            bar.targetGraphic = hi;
            bar.direction = vertical ? Scrollbar.Direction.BottomToTop : Scrollbar.Direction.LeftToRight;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };

            ScrollRect.ScrollbarVisibility visibility = TabletQolFeature.BarAlwaysVisible
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

            Log.Info<TabletQolFeature>(
                $"平板列表已挂滚动条：{label}（{reason}；{(vertical ? "右侧竖条" : "底部横条")}，宽 {width}px，内缩 {inset}px，" +
                $"{(TabletQolFeature.BarAlwaysVisible ? "常驻" : "内容超出时才出现")}，" +
                $"{(TabletQolFeature.BarTrackClickPages ? "轨道可点翻页" : "轨道不吃点击")}；" +
                $"当时轴=竖{scroll.vertical}/横{scroll.horizontal}{(axisForced ? "（都没开，按竖排处理）" : "")}）");
            return true;
        }
    }
}
