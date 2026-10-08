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
    /// 平板那三个列表都是竖排，但这样写不会在横排列表上画出方向错的条。
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
        public static bool Attach(ScrollRect scroll, string label)
        {
            if (scroll == null)
                return false;
            if (scroll.verticalScrollbar != null || scroll.horizontalScrollbar != null)
                return false;

            bool vertical = scroll.vertical;
            if (!vertical && !scroll.horizontal)
                return false;                        // 两个轴都不滚，挂条没有意义

            int width = Mathf.Clamp(TabletScrollbarFeature.BarWidth, 4, 40);
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
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(0f, width);
            }
            rt.anchoredPosition = Vector2.zero;

            Image track = go.GetComponent<Image>();
            track.color = new Color(1f, 1f, 1f, TrackAlpha);
            track.raycastTarget = true;              // 轨道也要吃点击，否则拖到轨道空白处没反应

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
            hi.raycastTarget = true;

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
                $"平板列表已挂滚动条：{label}（{(vertical ? "竖条" : "横条")}，宽 {width}px，" +
                $"{(TabletScrollbarFeature.AlwaysVisible ? "常驻" : "内容超出时才出现")}）");
            return true;
        }
    }
}
