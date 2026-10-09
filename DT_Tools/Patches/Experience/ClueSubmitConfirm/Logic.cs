using System;
using DT_Tools.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DT_Tools.Patches.Experience.ClueSubmitConfirm
{
    /// <summary>
    /// 「提交线索」二次确认栏的构建与显示。
    ///
    /// **视觉上像不像平板，取决于"借"得对不对**：面板背景 sprite 借线索卡片自己的
    /// （`GetImage(1)` = `Clue`，退化到 `GetImage(0)` = `MousePanel`），按钮 sprite 借卡片上的
    /// 「提交线索」按钮（`GetImage(3)` = `PropositionButton`），字体借卡片正文的
    /// `TMP_Text.font` / `fontSharedMaterial` ⇒ 配色、字体、圆角与平板里的卡片一致。
    /// （`GetImage` / `GetText` 是 `protected`，转储 :90405-90425 ⇒ 用 `Traverse` 取。）
    ///
    /// 尺寸不写死：面板宽 = 平板宽 × `PanelWidthRatio`，随分辨率/平板缩放自适应。
    /// 挂在**平板自己的 transform** 下（`Managers.Tablet.Tablet`，公开属性）⇒ 平板一关，确认栏跟着消失，
    /// 不会留下孤儿 UI。
    ///
    /// 交互：点遮罩空白处 = 取消；面板本体吞掉点击（不会误判成取消）；
    /// 「确认提交」才真正调用原方法（`Suppress` 期间放行，避免再弹一次），「取消」什么都不做。
    /// </summary>
    internal static class ClueSubmitConfirmLogic
    {
        private const string RootName = "DT_ClueSubmitConfirm";

        /// <summary>为 true 时放行原方法（确认后重入用），避免递归弹框。</summary>
        public static bool Suppress;

        private static GameObject _root;

        /// <summary>关掉当前确认栏（没有就空操作）。</summary>
        public static void Hide()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        /// <summary>弹确认栏；确认后执行卡片原本的提交逻辑。</summary>
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
                Log.Warn<ClueSubmitConfirmFeature>("取平板根失败，退回卡片根节点：" + ex.Message);
            }
            if (parent == null)
                parent = card.transform.root;

            RectTransform parentRect = parent as RectTransform;
            float parentW = (parentRect != null && parentRect.rect.width > 1f) ? parentRect.rect.width : 900f;
            float panelW = parentW * Mathf.Clamp(ClueSubmitConfirmFeature.PanelWidthRatio, 0.3f, 0.95f);
            float panelH = panelW * 0.62f;

            // ── 借视觉资源 ────────────────────────────────────────────────
            Image cardBg = ImageAt(card, 1) ?? ImageAt(card, 0);
            Image submitStyle = ImageAt(card, 3);
            TMP_Text fontStyle = TextAt(card, 0);

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
            dimImg.color = new Color(0f, 0f, 0f, Mathf.Clamp01(ClueSubmitConfirmFeature.DimAlpha));
            dimImg.raycastTarget = true;
            UI_Base.BindEvent(dim, _ => Hide());            // 点空白 = 取消

            // ── 面板 ─────────────────────────────────────────────────────
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
            if (cardBg != null && cardBg.sprite != null)
            {
                panelImg.sprite = cardBg.sprite;
                panelImg.type = cardBg.type;
            }
            panelImg.color = new Color(0.06f, 0.07f, 0.09f, 0.97f);
            panelImg.raycastTarget = true;                  // 面板吞点击：点面板不当作取消

            // ── 标题 ─────────────────────────────────────────────────────
            TMP_Text title = MakeText(panelRt, "Title", "确认提交该线索？", fontStyle, 34f, TextAlignmentOptions.Center, Color.white);
            RectTransform titleRt = (RectTransform)title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-80f, 56f);
            titleRt.anchoredPosition = new Vector2(0f, -36f);

            TMP_Text warn = MakeText(panelRt, "Warn", "提交后不可撤回", fontStyle, 24f, TextAlignmentOptions.Center,
                new Color(1f, 0.78f, 0.35f, 1f));
            RectTransform warnRt = (RectTransform)warn.transform;
            warnRt.anchorMin = new Vector2(0f, 1f);
            warnRt.anchorMax = new Vector2(1f, 1f);
            warnRt.pivot = new Vector2(0.5f, 1f);
            warnRt.sizeDelta = new Vector2(-80f, 34f);
            warnRt.anchoredPosition = new Vector2(0f, -96f);

            // ── 线索卡片概要 ──────────────────────────────────────────────
            if (ClueSubmitConfirmFeature.ShowSummary)
            {
                string meta = JoinMeta(TextAt(card, 2), TextAt(card, 3), TextAt(card, 4));
                TMP_Text body = TextAt(card, 0);
                string summary = meta;
                if (body != null && !string.IsNullOrWhiteSpace(body.text))
                    summary = string.IsNullOrEmpty(summary) ? body.text : (summary + "\n\n" + body.text);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    TMP_Text sum = MakeText(panelRt, "Summary", summary, fontStyle, 26f,
                        TextAlignmentOptions.TopLeft, new Color(0.92f, 0.94f, 0.98f, 1f));
                    RectTransform sumRt = (RectTransform)sum.transform;
                    sumRt.anchorMin = new Vector2(0f, 0f);
                    sumRt.anchorMax = new Vector2(1f, 1f);
                    sumRt.offsetMin = new Vector2(48f, 150f);
                    sumRt.offsetMax = new Vector2(-48f, -140f);
                }
            }

            // ── 两个按钮 ─────────────────────────────────────────────────
            float btnW = panelW * 0.34f;
            float btnH = 68f;
            MakeButton(panelRt, "确认提交", submitStyle, fontStyle, new Vector2(btnW, btnH),
                new Vector2(-btnW * 0.58f, 56f), () => ConfirmAndSubmit(card));
            MakeButton(panelRt, "取消", submitStyle, fontStyle, new Vector2(btnW, btnH),
                new Vector2(btnW * 0.58f, 56f), Hide);

            Log.Info<ClueSubmitConfirmFeature>($"已弹出「提交线索」确认栏（卡片={card.name}，面板 {panelW:F0}×{panelH:F0}）");
        }

        /// <summary>
        /// 确认：关掉确认栏，然后**执行卡片原本的提交逻辑**。
        ///
        /// `Suppress = true` 期间放行 Prefix，避免"确认 → 又弹一次框"的递归；
        /// 传 `null` 作为 `PointerEventData` 是安全的 —— 原方法体不使用该参数（转储 :86488-86500）。
        /// </summary>
        private static void ConfirmAndSubmit(UI_ClueSubItem card)
        {
            Hide();
            if (card == null)
                return;
            try
            {
                Suppress = true;
                Traverse.Create(card).Method("OnClickPropositionButton", new object[] { null }).GetValue();
            }
            catch (Exception ex)
            {
                Log.Error<ClueSubmitConfirmFeature>("确认后执行原提交失败：" + ex.Message);
            }
            finally
            {
                Suppress = false;
            }
        }

        // ── 构造原语 ─────────────────────────────────────────────────────

        private static TMP_Text MakeText(Transform parent, string name, string text, TMP_Text style,
            float size, TextAlignmentOptions align, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;

            TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
            if (style != null)
            {
                t.font = style.font;                       // 借卡片的字体与材质 ⇒ 看起来是原生 UI
                t.fontSharedMaterial = style.fontSharedMaterial;
            }
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            return t;
        }

        private static GameObject MakeButton(Transform parent, string label, Image style, TMP_Text fontStyle,
            Vector2 size, Vector2 pos, Action onClick)
        {
            GameObject go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            rt.localScale = Vector3.one;

            Image img = go.GetComponent<Image>();
            if (style != null && style.sprite != null)
            {
                img.sprite = style.sprite;
                img.type = style.type;
                img.color = style.color;
            }
            else
            {
                img.color = new Color(0.22f, 0.24f, 0.28f, 1f);
            }
            img.raycastTarget = true;

            TMP_Text t = MakeText(rt, "Label", label, fontStyle, 28f, TextAlignmentOptions.Center, Color.white);
            RectTransform trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            UI_Base.BindEvent(go, _ => onClick());
            return go;
        }

        /// <summary>把"谁 / 在哪 / 何时"三段拼成一行（空段跳过）。</summary>
        private static string JoinMeta(TMP_Text who, TMP_Text where, TMP_Text when)
        {
            string result = "";
            TMP_Text[] parts = new TMP_Text[3] { who, where, when };
            foreach (TMP_Text p in parts)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.text))
                    continue;
                result = string.IsNullOrEmpty(result) ? p.text : (result + "   " + p.text);
            }
            return result;
        }

        // ── 取游戏 UI 的 protected 成员（转储 :90405-90425）───────────────
        private static Image ImageAt(UI_ClueSubItem card, int idx)
        {
            try
            {
                return Traverse.Create(card).Method("GetImage", new object[] { idx }).GetValue<Image>();
            }
            catch (Exception ex)
            {
                Log.Warn<ClueSubmitConfirmFeature>($"取卡片 Image[{idx}] 失败（已忽略）：" + ex.Message);
                return null;
            }
        }

        private static TMP_Text TextAt(UI_ClueSubItem card, int idx)
        {
            try
            {
                return Traverse.Create(card).Method("GetText", new object[] { idx }).GetValue<TMP_Text>();
            }
            catch (Exception ex)
            {
                Log.Warn<ClueSubmitConfirmFeature>($"取卡片 Text[{idx}] 失败（已忽略）：" + ex.Message);
                return null;
            }
        }
    }
}
