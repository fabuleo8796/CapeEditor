using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem.UI;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// The building blocks of the menu, in the look of Fabio's other mods (dark glass, Geist, green accent), built in
/// code with uGUI: nodes laid out from their parent's top-left corner, rounded fills, labels, buttons, segmented
/// controls, switches, sliders and toasts.
internal static class Ui
{
    internal static readonly Color Back = new(0.05f, 0.08f, 0.11f, 0.92f);
    internal static readonly Color Soft = new(1f, 1f, 1f, 0.08f);
    internal static readonly Color Glass = new(1f, 1f, 1f, 0.14f);
    internal static readonly Color Dim = new(1f, 1f, 1f, 0.5f);
    internal static readonly Color Ink = new(1f, 1f, 1f, 0.92f);
    internal static readonly Color Accent = new(0.239f, 1f, 0.478f, 1f); // #3DFF7A
    internal static readonly Color AccentText = new(0.024f, 0.17f, 0.08f, 1f);
    internal static readonly Color Danger = new(1f, 0.27f, 0.23f, 1f);

    internal static TMP_FontAsset semiBold, medium;
    internal static Sprite rounded, small, circle, ring;
    static GameObject toast;
    static RectTransform toastChip;
    static TextMeshProUGUI toastText;
    static CanvasGroup toastGroup;
    static float toastUntil;
    static bool built;

    public static void Build()
    {
        if (built) return;
        built = true;
        semiBold = Font("SemiBold");
        medium = Font("Medium");
        rounded = MakeRounded(64, 24, 0);
        small = MakeRounded(40, 12, 0);
        circle = MakeRounded(64, 32, 0);
        ring = MakeRounded(64, 18, 4);

        var canvas = MakeCanvas("Cape Editor Toast", 30040);
        toastChip = Node(canvas.transform, "Toast");
        Anchor(toastChip, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(100f, 50f));
        var back = Fill(toastChip, rounded, new Color(0.05f, 0.08f, 0.11f, 0.88f));
        back.raycastTarget = false;
        toastText = Label(toastChip, "", medium, 20f, Color.white, 0f, 0f, 100f, 50f, TextAlignmentOptions.Center);
        Stretch(toastText.rectTransform);
        toast = toastChip.gameObject;
        toastGroup = toast.AddComponent<CanvasGroup>();
        toastGroup.blocksRaycasts = false;
        toast.SetActive(false);
    }

    internal static GameObject MakeCanvas(string name, int order)
    {
        var go = new GameObject(name);
        Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return go;
    }

    public static void Toast(string text)
    {
        if (!built) Build();
        if (toast == null) return;
        toastText.text = text;
        float w = toastText.GetPreferredValues(text).x;
        toastChip.sizeDelta = new Vector2(Mathf.Min(w + 48f, 1500f), 50f);
        toastGroup.alpha = 1f;
        toast.SetActive(true);
        toastUntil = Time.unscaledTime + 3f;
    }

    /// Only the toast animates (its 0.4 s fade-out).
    public static void Tick()
    {
        if (toast == null || !toast.activeSelf) return;
        float left = toastUntil - Time.unscaledTime;
        if (left > 0f) return;
        if (left < -0.4f) { toast.SetActive(false); return; }
        toastGroup.alpha = 1f + left / 0.4f;
    }

    internal static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("Cape Editor Events");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        Plugin.Logger.LogInfo("No EventSystem in this scene: added one for the Cape Editor menu");
    }

    // ---------------------------------------------------------------- layout

    internal static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    internal static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    internal static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// A child laid out from the parent's top-left corner, y going down.
    internal static RectTransform At(Transform parent, string name, float x, float y, float w, float h)
    {
        var rt = Node(parent, name);
        Anchor(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
        return rt;
    }

    internal static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    internal static Image Fill(RectTransform rt, Sprite sprite, Color color, bool raycast = false)
    {
        var image = rt.gameObject.AddComponent<Image>();
        if (sprite != null) { image.sprite = sprite; image.type = Image.Type.Sliced; }
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    internal static TextMeshProUGUI Label(Transform parent, string text, TMP_FontAsset font, float size, Color color, float x, float y, float w, float h,
                                          TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var rt = At(parent, "Text", x, y, w, h);
        var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = align;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    internal static void Section(Transform parent, string text, float x, float y, float w)
    {
        var label = Label(parent, text, semiBold, 15f, new Color(1f, 1f, 1f, 0.55f), x, y, w, 22f);
        label.characterSpacing = 6f;
    }

    internal static Button Click(GameObject go, Graphic target, Action click)
    {
        var button = go.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.targetGraphic = target;
        target.raycastTarget = true;
        button.onClick.AddListener((UnityAction)new Action(() => Safe(click)));
        return button;
    }

    /// A rounded text button. Returns its background so callers can recolour it.
    internal static Image TextButton(Transform parent, string text, float x, float y, float w, float h, Color back, Color ink, Action click, out TextMeshProUGUI label)
    {
        var rt = At(parent, text, x, y, w, h);
        var image = Fill(rt, small, back, true);
        label = Label(rt, text, semiBold, 18f, ink, 0f, 0f, w, h, TextAlignmentOptions.Center);
        Click(rt.gameObject, image, click);
        return image;
    }

    internal static void SetGroup(CanvasGroup group, bool on)
    {
        group.alpha = on ? 1f : 0.38f;
        group.interactable = on;
        group.blocksRaycasts = on;
    }

    internal static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"Cape Editor menu: {e}"); }
    }

    // ---------------------------------------------------------------- widgets

    internal sealed class Segmented
    {
        readonly Image[] backs;
        readonly TextMeshProUGUI[] labels;

        public Segmented(Transform parent, float x, float y, float w, float h, string[] names, Action<int> pick)
        {
            var bar = At(parent, "Segmented", x, y, w, h);
            Fill(bar, small, Soft);
            backs = new Image[names.Length];
            labels = new TextMeshProUGUI[names.Length];
            float sw = (w - 8f) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var seg = At(bar, names[i], 4f + i * sw, 4f, sw, h - 8f);
                backs[i] = Fill(seg, small, Color.clear, true);
                labels[i] = Label(seg, names[i], semiBold, 17f, Ink, 0f, 0f, sw, h - 8f, TextAlignmentOptions.Center);
                Click(seg.gameObject, backs[i], () => pick(index));
            }
        }

        public void Select(int index)
        {
            for (int i = 0; i < backs.Length; i++)
            {
                backs[i].color = i == index ? Accent : Color.clear;
                labels[i].color = i == index ? AccentText : Ink;
            }
        }
    }

    internal sealed class Switch
    {
        readonly Image track;
        readonly RectTransform knob;

        public Switch(Transform parent, float x, float y, Action click)
        {
            var hit = At(parent, "Switch", x, y, 58f, 32f);
            track = Fill(hit, small, Accent, true);
            knob = At(hit, "Knob", 3f, 3f, 26f, 26f);
            Fill(knob, circle, Color.white);
            Click(hit.gameObject, track, click);
        }

        public void Set(bool on)
        {
            track.color = on ? Accent : new Color(1f, 1f, 1f, 0.2f);
            knob.anchoredPosition = new Vector2(on ? 29f : 3f, -3f);
        }
    }

    internal static Slider MakeSlider(Transform parent, float x, float y, float w, Action<float> changed)
    {
        var area = At(parent, "Slider", x, y, w, 28f);
        Fill(area, null, Color.clear, true);
        Fill(At(area, "Track", 0f, 10f, w, 8f), small, new Color(1f, 1f, 1f, 0.16f));
        var fillArea = At(area, "Fill Area", 0f, 10f, w, 8f);
        var fill = Node(fillArea, "Fill");
        Fill(fill, small, Accent);
        fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f); fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
        var handleArea = At(area, "Handle Area", 12f, 0f, w - 24f, 28f);
        var knob = Node(handleArea, "Knob");
        var knobImage = Fill(knob, circle, Color.white);

        var slider = area.gameObject.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.fillRect = fill;
        slider.handleRect = knob;
        slider.targetGraphic = knobImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        knob.sizeDelta = new Vector2(24f, -4f); // the slider stretches the knob over the area's height: 28 - 4 = 24
        knob.anchoredPosition = Vector2.zero;
        slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(v => Safe(() => changed(v))));
        return slider;
    }

    // ---------------------------------------------------------------- fonts and sprites (from StickerSwap / Tripod Cam)

    /// Geist as a dynamic TMP font; falls back to the game's own font.
    static TMP_FontAsset Font(string weight)
    {
        TMP_FontAsset game = null;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && (game == null || f.name.StartsWith("Jersey10"))) game = f;
            var folder = Path.Combine(Plugin.Folder, "fonts");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Geist-{weight}.ttf");
            if (!File.Exists(path)) File.WriteAllBytes(path, Resource($"Geist-{weight}.ttf"));
            var license = Path.Combine(folder, "OFL-Geist.txt");
            if (!File.Exists(license)) File.WriteAllBytes(license, Resource("OFL-Geist.txt"));
            var font = TMP_FontAsset.CreateFontAsset(path, 0, 72, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (font == null) return game;
            font.name = $"Geist-{weight} (CapeEditor)";
            font.hideFlags = HideFlags.HideAndDontSave;
            var shader = font.material != null ? font.material.shader : null;
            if ((shader == null || !shader.isSupported) && game != null && game.material != null) font.material.shader = game.material.shader;
            if (game != null) font.fallbackFontAssetTable?.Add(game);
            return font;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't load Geist {weight}, using the game's font: {e.Message}");
            return game;
        }
    }

    static byte[] Resource(string name)
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    /// A white rounded square for 9-slicing; with `line` > 0 only an outline that thick.
    internal static Sprite MakeRounded(int size, int radius, int line)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
            var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            float a = Mathf.Clamp01(radius - d + 0.5f);
            if (line > 0)
            {
                // distance from the outer edge, rounded corners included
                float edge = Mathf.Min(Mathf.Min(x + 0.5f, size - x - 0.5f), Mathf.Min(y + 0.5f, size - y - 0.5f));
                float inside = d > 0f ? radius - d : edge;
                a *= Mathf.Clamp01(line - inside + 0.5f);
            }
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var border = new Vector4(radius, radius, radius, radius);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
