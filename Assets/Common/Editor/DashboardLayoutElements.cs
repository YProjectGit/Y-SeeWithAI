using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Editorレイアウト生成用の共通部品。画面固有の配置は各Builderに残す。</summary>
public sealed class DashboardLayoutElements
{
    readonly TMP_FontAsset font;
    static readonly Color Panel = new Color(.14f, .15f, .18f);
    static readonly Color Well = new Color(.09f, .10f, .12f);
    public DashboardLayoutElements(TMP_FontAsset font)
    {
        this.font = font;
    }
    public RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }
    public Image Fill(RectTransform rt, Color color)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }
    public RectTransform Box(string name, Transform p, float x, float y, float w, float h, Color? c = null)
    {
        var r = Rect(name, p, x, y, w, h);
        Fill(r, c ?? Panel);
        return r;
    }
    public TMP_Text Text(string value, Transform p, float x, float y, float w, float h, float size, Color? color = null, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var rt = Rect(value.Length > 25 ? value.Substring(0, 25) : value, p, x, y, w, h);
        var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = size;
        text.color = color ?? new Color(.92f, .94f, .97f);
        text.text = value;
        text.alignment = align;
        text.raycastTarget = false;
        text.richText = false;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }
    public Button Button(string label, Transform p, float x, float y, float w, float h, float size, Color color)
    {
        var rt = Rect(label, p, x, y, w, h);
        var img = Fill(rt, color);
        img.raycastTarget = true;
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = img;
        var colors = button.colors;
        colors.disabledColor = new Color(.5f, .5f, .5f, .7f);
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        Text(label, rt, 8, 5, w - 16, h - 10, size, null, TextAlignmentOptions.Center);
        return button;
    }

    public TMP_InputField Input(TMP_InputField template, Transform p, float x, float y, float w, float h, float size)
    {
        var field = UnityEngine.Object.Instantiate(template, p);
        field.name = "Input";
        // Remove copied layout constraints so the fixed design rectangle is respected.
        foreach (var layout in field.GetComponents<LayoutElement>())
            UnityEngine.Object.DestroyImmediate(layout);
        var rt = (RectTransform)field.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.textComponent.fontSize = size;
        field.textComponent.richText = false;
        field.textComponent.color = new Color(.92f, .94f, .97f);
        field.lineType = TMP_InputField.LineType.MultiLineNewline;
        var img = field.GetComponent<Image>();
        if (img != null)
            img.color = Well;
        var viewport = field.textViewport;
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(12, 8);
        viewport.offsetMax = new Vector2(-12, -8);
        field.textComponent.rectTransform.anchorMin = Vector2.zero;
        field.textComponent.rectTransform.anchorMax = Vector2.one;
        field.textComponent.rectTransform.offsetMin = Vector2.zero;
        field.textComponent.rectTransform.offsetMax = Vector2.zero;
        field.readOnly = false;
        return field;
    }
    public TMP_Text ScrollText(Transform p, string name, float x, float y, float w, float h, float size, out ScrollRect scroll)
    {
        var rt = Box(name, p, x, y, w, h, Well);
        rt.GetComponent<Image>().raycastTarget = true;
        scroll = rt.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28;
        var viewport = Rect("Viewport", rt, 10, 8, w - 32, h - 16);
        viewport.gameObject.AddComponent<RectMask2D>();
        var text = Text("", viewport, 0, 0, w - 32, h - 16, size);
        text.name = "Content";
        text.overflowMode = TextOverflowModes.Overflow;
        var content = text.rectTransform;
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.sizeDelta = new Vector2(0, 0);
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport;
        scroll.content = content;
        var rail = Rect("Scrollbar", rt, w - 12, 8, 5, h - 16);
        var railImg = Fill(rail, Panel);
        railImg.raycastTarget = true;
        var handle = Rect("Handle", rail, 0, 0, 5, h - 16);
        var handleImg = Fill(handle, new Color(.39f, .43f, .50f));
        handleImg.raycastTarget = true;
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.offsetMin = Vector2.zero;
        handle.offsetMax = Vector2.zero;
        var bar = rail.gameObject.AddComponent<Scrollbar>();
        bar.handleRect = handle;
        bar.targetGraphic = handleImg;
        bar.direction = Scrollbar.Direction.BottomToTop;
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scroll.verticalNormalizedPosition = 0;
        return text;
    }
}
