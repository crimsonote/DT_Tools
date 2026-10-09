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
    /// ② 「提交线索」二次确认栏。
    ///
    /// **视觉全部来自原版，不再自己拼**：
    ///   · 主体是一张**原版线索卡片** —— 照抄游戏自己的用法
    ///     （`Managers.UI.MakeSubItem&lt;UI_ClueSubItem&gt;` → `SetInfo(clue)` → `InitPopupClue()`，
    ///     转储 :52838-52840），"谁/在哪/何时 + 正文"整张卡片由原版渲染；
    ///   · 两个按钮是**克隆卡片上的原版按钮**（`GetObject(3)` = `PropositionButton`）；
    ///   · 面板背景借**卡片自己的背景**（`GetObject(1)`，退化 `GetObject(0)`）的 `sprite`/`type` ——
    ///     注意是 GameObject 上的 `Image`，不是 `Images` 枚举（那个枚举里 1/3 是"谁/什么"的**图标**，
    ///     之前就是照它取的，才会出现一坨人影和花草）。
    ///
    /// **尺寸跟着内容走**（实机反馈"相比卡片，UI 太大了"）：先量出卡片的实际尺寸，再据此算面板宽高
    /// （卡片过宽时整体等比缩放到平板宽的 80% 以内），最后把标题/卡片/按钮按算好的尺寸摆进去。
    ///
    /// **半透明**（实机反馈"完全不透明"）：面板用 `PanelAlpha`（默认 0.86）而不是纯色实心；
    /// 遮罩用 `DimAlpha`（默认 0.45）。
    ///
    /// 挂在**平板自己的 transform** 下 ⇒ 平板一关，确认栏跟着消失。
    /// </summary>
    internal static class TabletQolConfirm
    {
        private const string RootName = "DT_ClueSubmitConfirm";

        private const float Padding = 26f;      // 面板内边距
        private const float TitleH = 62f;       // 标题 + 副标题占高
        private const float Gap = 16f;          // 标题 / 卡片 / 按钮之间
        private const float MinPanelW = 420f;   // 面板最小宽度

        /// <summary>为 true 时放行原方法（确认后重入用），避免递归弹框。</summary>
        public static bool Suppress;

        private static GameObject _root;

        /// <summary>
        /// 这张卡片是不是**确认栏自己内嵌**的那张。
        /// 内嵌卡片只作展示，它的「提出命题」按钮虽然已被关掉，这里再兜一道：
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
            float maxPanelW = parentW * 0.80f;

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

            // ── 先把原版卡片挂到根下量尺寸（稍后再挪进面板）────────────────
            Vector2 cardSize = Vector2.zero;
            UI_ClueSubItem embedded = null;
            if (TabletQolFeature.ConfirmShowCard)
                embedded = EmbedCard(card, rootRt, maxPanelW, out cardSize);

            // ── 按内容算面板尺寸 ─────────────────────────────────────────
            float btnH = Mathf.Clamp(cardSize.y > 1f ? cardSize.y * 0.9f : 60f, 48f, 96f);
            float contentW = Mathf.Max(cardSize.x, MinPanelW * 0.62f);
            float panelW = Mathf.Min(contentW + Padding * 2f, maxPanelW);
            float panelH = Padding + TitleH + Gap + (cardSize.y > 1f ? cardSize.y : 0f) + Gap + btnH + Padding;

            // ── 面板（半透明 + 借卡片背景的 sprite）──────────────────────
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
            Image cardBg = ObjectImage(card, 1) ?? ObjectImage(card, 0);
            if (cardBg != null && cardBg.sprite != null)
            {
                panelImg.sprite = cardBg.sprite;         // 卡片自己的背景美术 ⇒ 原生观感
                panelImg.type = cardBg.type;
            }
            float alpha = Mathf.Clamp01(TabletQolFeature.PanelAlpha);
            Color baseColor = cardBg != null ? cardBg.color : new Color(0.05f, 0.06f, 0.08f, 1f);
            panelImg.color = new Color(baseColor.r * 0.9f, baseColor.g * 0.9f, baseColor.b * 0.95f, alpha);
            panelImg.raycastTarget = true;               // 面板吞点击：点面板不当作取消

            // 把"到底借到了什么"记下来：预制体里的 sprite 引用离线解不出来，实机一眼可查
            Log.Info<TabletQolFeature>(
                $"面板背景取自卡片 GetObject(1/0)：sprite={(cardBg != null && cardBg.sprite != null ? cardBg.sprite.name : "<null>")}，" +
                $"type={(cardBg != null ? cardBg.type.ToString() : "-")}，" +
                $"color={(cardBg != null ? cardBg.color.ToString() : "-")}");

            // ── 标题（小一点，别抢卡片的视觉）─────────────────────────────
            MakeLabel(panelRt, "确认提交该线索？", 28f, new Color(1f, 1f, 1f, 1f), Padding + 2f);
            MakeLabel(panelRt, "提交后不可撤回", 20f, new Color(1f, 0.78f, 0.35f, 1f), Padding + 36f);

            // ── 把卡片挪进面板并摆好 ─────────────────────────────────────
            if (embedded != null && cardSize.y > 1f)
            {
                RectTransform crt = (RectTransform)embedded.transform;
                crt.SetParent(panelRt, false);
                crt.anchorMin = new Vector2(0.5f, 1f);
                crt.anchorMax = new Vector2(0.5f, 1f);
                crt.pivot = new Vector2(0.5f, 1f);
                crt.anchoredPosition = new Vector2(0f, -(Padding + TitleH + Gap));
            }

            // ── 两个按钮（克隆原版按钮），按内容宽度摆 ────────────────────
            float btnW = Mathf.Min(panelW * 0.32f, 260f);
            float btnY = Padding;
            MakeCloneButton(card, panelRt, "确认提交", new Vector2(-btnW * 0.58f, btnY), new Vector2(btnW, btnH),
                () => ConfirmAndSubmit(card));
            MakeCloneButton(card, panelRt, "取消", new Vector2(btnW * 0.58f, btnY), new Vector2(btnW, btnH), Hide);

            Log.Info<TabletQolFeature>(
                $"已弹出「提交线索」确认栏（卡片={card.name}；卡片 {cardSize.x:F0}×{cardSize.y:F0}，" +
                $"面板 {panelW:F0}×{panelH:F0}，面板不透明度 {alpha:F2}）");
        }

        /// <summary>把原版卡片嵌进来量尺寸（只作展示：关掉它自己的提交按钮）。</summary>
        private static UI_ClueSubItem EmbedCard(UI_ClueSubItem source, RectTransform parent, float maxW, out Vector2 size)
        {
            size = Vector2.zero;
            try
            {
                UI_ClueSubItem embedded = Managers.UI.MakeSubItem<UI_ClueSubItem>(parent);
                if (embedded == null)
                    return null;

                var clue = source.Clue;                   // 类型是嵌套类型，用 var 免去命名
                embedded.SetInfo(clue);
                embedded.InitPopupClue();                 // 与游戏自己的线索浮窗同一套初始化（转储 :52838-52840）

                // 只作展示 ⇒ 关掉它自己的「提交线索」按钮，并置为不可交互（双保险）
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
                rt.anchoredPosition = Vector2.zero;
                rt.localScale = Vector3.one;

                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                float w = rt.rect.width;
                float h = rt.rect.height;
                if (w < 1f || h < 1f)
                {
                    w = 640f;
                    h = 120f;                             // 量不出来时的兜底尺寸
                }

                float scale = (w > maxW && maxW > 1f) ? (maxW / w) : 1f;
                if (scale < 1f)
                    rt.localScale = new Vector3(scale, scale, 1f);
                size = new Vector2(w * scale, h * scale);
                return embedded;
            }
            catch (Exception ex)
            {
                Log.Warn<TabletQolFeature>("内嵌原版线索卡片失败（已忽略，确认栏仍可用）：" + ex.Message);
                return null;
            }
        }

        private static void MakeLabel(Transform parent, string text, float size, Color color, float topOffset)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-Padding * 2f, size + 10f);
            rt.anchoredPosition = new Vector2(0f, -topOffset);
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
        /// 日志里能直接看出是哪道闸门挡的。
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

        /// <summary>取卡片上某个 GameObject 的 Image（`GetObject` 是 protected ⇒ Traverse）。</summary>
        private static Image ObjectImage(UI_ClueSubItem card, int idx)
        {
            try
            {
                GameObject go = Traverse.Create(card).Method("GetObject", new object[] { idx }).GetValue<GameObject>();
                return go == null ? null : go.GetComponent<Image>();
            }
            catch (Exception ex)
            {
                Log.Warn<TabletQolFeature>($"取卡片 GameObject[{idx}] 的 Image 失败（已忽略）：" + ex.Message);
                return null;
            }
        }
    }
}
