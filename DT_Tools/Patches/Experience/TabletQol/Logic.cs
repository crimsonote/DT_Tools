using System;
using System.Reflection;
using DT_Tools.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

    /// <summary>
    /// ② 「提交线索」二次确认栏。
    ///
    /// **不再自己拼视觉**：确认栏里内嵌一张**原版线索卡片** —— 照抄游戏自己的用法
    /// （`Managers.UI.MakeSubItem&lt;UI_ClueSubItem&gt;` → `SetInfo(clue)` → `InitPopupClue()`，
    /// 转储 :52838-52840），所以"谁/在哪/何时 + 正文"整张卡片都是原版渲染；
    /// 两个按钮也是**克隆卡片上的原版按钮**（`GetObject(3)` = `PropositionButton`）⇒ 配色/圆角/字体全原生。
    /// 卡片只作展示：克隆出来的按钮不带原监听（Unity 的 `Instantiate` 不复制运行时加的监听），
    /// 内嵌卡片自己的提交按钮会被关掉。
    ///
    /// 面板底色用纯色（不借任何美术资源）；确认栏挂在**平板自己的 transform** 下 ⇒ 平板一关就跟着消失。
    /// </summary>
    internal static class TabletQolConfirm
    {
        private const string RootName = "DT_ClueSubmitConfirm";

        /// <summary>面板宽度占平板宽度的比例。</summary>
        private const float PanelWidthRatio = 0.66f;

        /// <summary>为 true 时放行原方法（确认后重入用），避免递归弹框。</summary>
        public static bool Suppress;

        private static GameObject _root;

        /// <summary>
        /// 这张卡片是不是**确认栏自己内嵌**的那张。
        /// 内嵌卡片只作展示，它的「提出命题」按钮虽然已被关掉，但这里再兜一道：
        /// 来自确认栏内部的点击一律不拦，免得出现"确认栏里再弹确认栏"的嵌套。
        /// </summary>
        public static bool IsInsideDialog(UI_ClueSubItem card)
        {
            return card != null && _root != null && card.transform.IsChildOf(_root.transform);
        }

        public static void Hide()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        public static void Show(UI_ClueSubItem card)
        {
            Hide();
            if (card == null)
                return;

            Transform parent = null;
            try
            {
                if (Managers.Tablet != null && Managers.Tablet.Tablet != null)
                    parent = Managers.Tablet.Tablet.transform;
            }
            catch (Exception ex)
            {
                Log.Warn<TabletQolFeature>("取平板根失败，退回卡片根节点：" + ex.Message);
            }
            if (parent == null)
                parent = card.transform.root;

            RectTransform parentRect = parent as RectTransform;
            float parentW = (parentRect != null && parentRect.rect.width > 1f) ? parentRect.rect.width : 900f;
            float panelW = parentW * PanelWidthRatio;
            float panelH = panelW * 0.72f;

            // ── 根 + 遮罩 ────────────────────────────────────────────────
            _root = new GameObject(RootName, typeof(RectTransform));
            RectTransform rootRt = (RectTransform)_root.transform;
            rootRt.SetParent(parent, false);
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;
            rootRt.localScale = Vector3.one;
            rootRt.SetAsLastSibling();

            GameObject dim = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            RectTransform dimRt = (RectTransform)dim.transform;
            dimRt.SetParent(rootRt, false);
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = Vector2.zero;
            dimRt.offsetMax = Vector2.zero;
            dimRt.localScale = Vector3.one;
            Image dimImg = dim.GetComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, Mathf.Clamp01(TabletQolFeature.DimAlpha));
            dimImg.raycastTarget = true;
            UI_Base.BindEvent(dim, _ => Hide());          // 点空白 = 取消

            // ── 面板（纯色，不借美术资源）─────────────────────────────────
            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            RectTransform panelRt = (RectTransform)panel.transform;
            panelRt.SetParent(rootRt, false);
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(panelW, panelH);
            panelRt.anchoredPosition = Vector2.zero;
            panelRt.localScale = Vector3.one;
            Image panelImg = panel.GetComponent<Image>();
            panelImg.color = new Color(0.05f, 0.06f, 0.08f, 0.98f);
            panelImg.raycastTarget = true;                // 面板吞点击：点面板不当作取消

            // ── 标题 ─────────────────────────────────────────────────────
            MakeLabel(panelRt, "确认提交该线索？", 36f, new Color(1f, 1f, 1f, 1f), -34f);
            MakeLabel(panelRt, "提交后不可撤回", 24f, new Color(1f, 0.78f, 0.35f, 1f), -86f);

            // ── 内嵌一张原版线索卡片（照抄游戏的用法）────────────────────
            if (TabletQolFeature.ConfirmShowCard)
                EmbedCard(card, panelRt, panelW);

            // ── 两个按钮（克隆卡片上的原版按钮）──────────────────────────
            float btnW = panelW * 0.30f;
            float btnH = Mathf.Max(56f, panelH * 0.14f);
            MakeCloneButton(card, panelRt, "确认提交", new Vector2(-btnW * 0.62f, 52f), new Vector2(btnW, btnH),
                () => ConfirmAndSubmit(card));
            MakeCloneButton(card, panelRt, "取消", new Vector2(btnW * 0.62f, 52f), new Vector2(btnW, btnH), Hide);

            Log.Info<TabletQolFeature>($"已弹出「提交线索」确认栏（卡片={card.name}，面板 {panelW:F0}×{panelH:F0}）");
        }

        /// <summary>把原版卡片嵌进确认栏（只作展示：关掉它自己的提交按钮）。</summary>
        private static void EmbedCard(UI_ClueSubItem source, RectTransform panelRt, float panelW)
        {
            try
            {
                UI_ClueSubItem embedded = Managers.UI.MakeSubItem<UI_ClueSubItem>(panelRt);
                if (embedded == null)
                    return;

                var clue = source.Clue;                   // 类型是嵌套类型，用 var 免去命名
                embedded.SetInfo(clue);
                embedded.InitPopupClue();                 // 与游戏自己的线索浮窗同一套初始化（转储 :52838-52840）

                // 只作展示 ⇒ 关掉它自己的「提交线索」按钮（它由原版刷新逻辑控制显隐，这里直接关掉最稳）；
                // 再顺手把 Button 置为不可交互，双保险（万一别的逻辑把它 SetActive(true) 回来）。
                GameObject submitGo = Traverse.Create(embedded).Method("GetObject", new object[] { 3 }).GetValue<GameObject>();
                if (submitGo != null)
                {
                    submitGo.SetActive(false);
                    Button submitBtn = submitGo.GetComponent<Button>();
                    if (submitBtn != null)
                        submitBtn.interactable = false;
                }

                RectTransform rt = (RectTransform)embedded.transform;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, -6f);
                rt.localScale = Vector3.one;

                // 卡片比面板宽时整体缩放，避免溢出
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                float cardW = rt.rect.width;
                if (cardW > 1f)
                {
                    float maxW = panelW * 0.88f;
                    if (cardW > maxW)
                        rt.localScale = new Vector3(maxW / cardW, maxW / cardW, 1f);
                }
            }
            catch (Exception ex)
            {
                Log.Warn<TabletQolFeature>("内嵌原版线索卡片失败（已忽略，确认栏仍可用）：" + ex.Message);
            }
        }

        private static void MakeLabel(Transform parent, string text, float size, Color color, float y)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-60f, size + 12f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.localScale = Vector3.one;

            TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.color = color;
            t.raycastTarget = false;
        }

        /// <summary>克隆卡片上的原版按钮（`GetObject(3)` = PropositionButton），换标签、绑自己的动作。</summary>
        private static void MakeCloneButton(UI_ClueSubItem source, Transform parent, string label,
            Vector2 pos, Vector2 size, Action onClick)
        {
            GameObject clone = null;
            try
            {
                GameObject src = Traverse.Create(source).Method("GetObject", new object[] { 3 }).GetValue<GameObject>();
                if (src != null)
                    clone = UnityEngine.Object.Instantiate(src, parent);
            }
            catch (Exception ex)
            {
                Log.Warn<TabletQolFeature>($"克隆原版按钮失败，改用纯色按钮：{ex.Message}");
            }

            if (clone == null)
            {
                clone = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
                clone.transform.SetParent(parent, false);
                clone.GetComponent<Image>().color = new Color(0.22f, 0.24f, 0.28f, 1f);
            }

            RectTransform rt = (RectTransform)clone.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            rt.localScale = Vector3.one;

            // 换标签：克隆体上的第一个文本就是按钮文字
            TMP_Text[] texts = clone.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0)
                texts[0].text = label;

            // 清掉可能被序列化带过来的监听（运行时加的监听本来就不会被 Instantiate 复制，这里再保险一次）
            Button btn = clone.GetComponent<Button>();
            if (btn != null)
                btn.onClick = new Button.ButtonClickedEvent();

            UI_Base.BindEvent(clone, _ => onClick());
        }

        /// <summary>
        /// 确认：关掉确认栏，然后**执行卡片原本的提交逻辑**。
        ///
        /// 用 `AccessTools.Method` + `MethodInfo.Invoke`，**不用 `Traverse.Method(name, args)`** ——
        /// 后者按"参数个数 + 值"挑重载，传 `null`（`PointerEventData` 用不到）时可能**静默匹配不上**，
        /// 表现为"确认了但什么都没发生"（实机踩过）。这里连结果一起记日志，万一还没发出去，
        /// 日志里能直接看出是哪道闸门挡的（`IsAlive` / `IsSubmitProposition` / `CanSubmit` / `IsValid`）。
        /// </summary>
        private static void ConfirmAndSubmit(UI_ClueSubItem card)
        {
            Hide();
            if (card == null)
                return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(UI_ClueSubItem), "OnClickPropositionButton");
                if (target == null)
                {
                    Log.Error<TabletQolFeature>("找不到 UI_ClueSubItem.OnClickPropositionButton，无法提交");
                    return;
                }

                Log.Info<TabletQolFeature>(
                    $"确认提交前：IsAlive={Managers.Game.IsAlive} IsSubmitProposition={Managers.Game.IsSubmitProposition} " +
                    $"CanSubmit={ReadBool(card, "CanSubmitProposition")} IsValid={ReadIsValid(card)} " +
                    $"IsTrialDiscussPhase={ReadBool(card, "IsTrialDiscussPhase")}");

                Suppress = true;
                target.Invoke(card, new object[] { null });   // 原方法体不使用该参数（转储 :86488-86500）
                Log.Info<TabletQolFeature>(
                    $"确认提交：已调用原方法（调用后 IsSubmitProposition={Managers.Game.IsSubmitProposition}）");
            }
            catch (Exception ex)
            {
                Log.Error<TabletQolFeature>("确认后执行原提交失败：" + ex.Message);
            }
            finally
            {
                Suppress = false;
            }
        }

        /// <summary>诊断用：读一个无参私有 bool 方法的结果。</summary>
        private static string ReadBool(UI_ClueSubItem card, string method)
        {
            try
            {
                object value = Traverse.Create(card).Method(method).GetValue();
                return value == null ? "null" : value.ToString();
            }
            catch (Exception ex)
            {
                return "读取失败(" + ex.Message + ")";
            }
        }

        /// <summary>诊断用：`IsValid(PropositionInfo)` 需要一个参数，单独读。</summary>
        private static string ReadIsValid(UI_ClueSubItem card)
        {
            try
            {
                object info = card.Clue == null ? null : card.Clue.Info;
                if (info == null)
                    return "Info=null";
                object value = Traverse.Create(card).Method("IsValid", new object[] { info }).GetValue();
                return value == null ? "null" : value.ToString();
            }
            catch (Exception ex)
            {
                return "读取失败(" + ex.Message + ")";
            }
        }
    }
}
