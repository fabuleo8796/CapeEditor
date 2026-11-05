using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EHS.Skins;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// The Cape Editor menu (O). Two pages on one panel at the left of the screen, so Flippy stays in view:
/// - Home: the base-game capes you can wear (the game's own icons; click one to put it on), your custom capes (import
///   a picture, wear, edit, delete) and the on/off switch.
/// - Editor: the picture with a crop frame shaped like the cape (drag to move, scroll or the slider to zoom), Fill /
///   Fit / Turn / Mirror, the inside and see-through options, the edge colour, and a live preview. Changes show on
///   your Flippy's cape as you make them (a quick 512 px draft while you drag, the full texture when you stop).
/// Built once a few seconds after launch and only switched on and off. While it's open it frees the cursor and
/// pauses the game's controls; its texts refresh twice a second.
internal static class Menu
{
    const float W = 820f, H = 956f, Pad = 26f, Inner = W - 2 * Pad;
    const float CanvasSize = 500f, SideX = Pad + CanvasSize + 20f, SideW = W - Pad - SideX;
    const int Cols = 5, PerPage = Cols * 2 - 1; // the first card of every page is "Import picture"
    const float CardW = (Inner - (Cols - 1) * 16f) / Cols, ThumbH = 194f;
    static readonly uint[] Swatches = { 0xFF000000, 0xFFFFFFFF, 0xFFFF0000, 0xFF00FF00, 0xFF3DFF7A, 0xFF1E5BFF, 0xFFFFD21E, 0xFF9B3DFF };

    static GameObject root;
    static Canvas canvas;
    static RectTransform panel, home, editor;
    static TextMeshProUGUI title, statusText, menuKeyText, hintText;
    static RectTransform icon, backButton, titleRect;
    static Ui.Switch master;
    static CursorLockMode oldLock;
    static bool oldVisible;
    static float nextRefresh;

    public static bool IsOpen => root != null && root.activeSelf;

    // ---------------------------------------------------------------- build (once)

    public static void Build()
    {
        if (root != null) return;
        Ui.Build();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            root = Ui.MakeCanvas("Cape Editor Menu", 30030);
            canvas = root.GetComponent<Canvas>();
            panel = Ui.Node(root.transform, "Panel");
            Ui.Anchor(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(W, H));
            Ui.Fill(panel, Ui.rounded, Ui.Back, true); // catches clicks so they never fall through

            BuildHeader();
            home = Ui.At(panel, "Home", 0f, 0f, W, H);
            editor = Ui.At(panel, "Editor", 0f, 0f, W, H);
            BuildHome();
            BuildEditor();
            editor.gameObject.SetActive(false);
            root.SetActive(false);
            Plugin.Logger.LogInfo($"Cape Editor menu ready ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("couldn't build the menu: " + e);
        }
    }

    static void BuildHeader()
    {
        // a little cape: a collar bar over a green panel (the back button takes its place in the editor)
        icon = Ui.At(panel, "Icon", Pad, 20f, 34f, 36f);
        Ui.Fill(Ui.At(icon, "Cape", 4f, 6f, 26f, 30f), Ui.small, Ui.Accent);
        Ui.Fill(Ui.At(icon, "Collar", 0f, 0f, 34f, 9f), Ui.small, new Color(1f, 1f, 1f, 0.9f));
        backButton = Ui.At(panel, "Back", Pad, 18f, 40f, 40f);
        var backImage = Ui.Fill(backButton, Ui.small, Ui.Glass, true);
        Ui.Label(backButton, "‹", Ui.semiBold, 30f, Color.white, 0f, -2f, 40f, 40f, TextAlignmentOptions.Center);
        Ui.Click(backButton.gameObject, backImage, () => CancelEdit());
        backButton.gameObject.SetActive(false);

        title = Ui.Label(panel, "Cape Editor", Ui.semiBold, 28f, Color.white, Pad + 48f, 16f, 420f, 42f);
        titleRect = title.rectTransform;
        title.overflowMode = TextOverflowModes.Ellipsis;
        Ui.Label(panel, "Custom cape", Ui.medium, 17f, Ui.Ink, W - Pad - 44f - 66f - 112f, 18f, 106f, 40f, TextAlignmentOptions.MidlineRight);
        master = new Ui.Switch(panel, W - Pad - 44f - 66f, 22f, ToggleEnabled);
        var close = Ui.At(panel, "Close", W - Pad - 40f, 20f, 40f, 36f);
        var closeBack = Ui.Fill(close, Ui.small, new Color(1f, 1f, 1f, 0.2f), true);
        menuKeyText = Ui.Label(close, "O", Ui.semiBold, 17f, Color.white, 0f, 0f, 40f, 36f, TextAlignmentOptions.Center);
        Ui.Click(close.gameObject, closeBack, Close);
        statusText = Ui.Label(panel, "", Ui.medium, 16f, Ui.Dim, Pad, 66f, Inner, 30f);
        statusText.overflowMode = TextOverflowModes.Ellipsis;
        statusText.richText = true;
    }

    // ---------------------------------------------------------------- home page

    sealed class BaseTile
    {
        public RectTransform Rect;
        public Image Back, Icon, Ring;
        public TextMeshProUGUI Name, Lock;
        public string Key, Title;
        public bool Unlocked;
    }

    sealed class Card
    {
        public RectTransform Rect;
        public Image Back, Ring;
        public RawImage Thumb;
        public TextMeshProUGUI Name, Badge;
        public RectTransform BadgeChip;
        public Design Design;
    }

    const int MaxBase = 9;
    static readonly BaseTile[] tiles = new BaseTile[MaxBase];
    static readonly Card[] cards = new Card[PerPage];
    static TextMeshProUGUI baseEmpty, pageText, wearText, deleteText;
    static Image wearBack, deleteBack, prevBack, nextBack;
    static CanvasGroup actions;
    static int page;
    static Design selected;
    static float deleteArmedUntil;
    static SkinsManager skins;

    static void BuildHome()
    {
        Ui.Section(home, "BASE CAPE · CLICK TO PUT ON", Pad, 110f, Inner);
        float size = 76f, gap = (Inner - MaxBase * size) / (MaxBase - 1);
        for (int i = 0; i < MaxBase; i++)
        {
            var t = new BaseTile();
            int index = i;
            t.Rect = Ui.At(home, "Base", Pad + i * (size + gap), 140f, size, size + 24f);
            t.Back = Ui.Fill(Ui.At(t.Rect, "Back", 0f, 0f, size, size), Ui.small, Ui.Soft, true);
            t.Icon = Ui.At(t.Rect, "Icon", 6f, 6f, size - 12f, size - 12f).gameObject.AddComponent<Image>();
            t.Icon.preserveAspect = true;
            t.Icon.raycastTarget = false;
            t.Ring = Ui.Fill(Ui.At(t.Rect, "Ring", 0f, 0f, size, size), Ui.ring, Ui.Accent);
            t.Lock = Ui.Label(t.Rect, "LOCKED", Ui.semiBold, 12f, Color.white, 0f, size - 22f, size, 18f, TextAlignmentOptions.Center);
            t.Name = Ui.Label(t.Rect, "", Ui.medium, 13f, Ui.Ink, -6f, size + 3f, size + 12f, 20f, TextAlignmentOptions.Center);
            t.Name.overflowMode = TextOverflowModes.Ellipsis;
            Ui.Click(t.Back.gameObject, t.Back, () => PutOn(tiles[index]));
            t.Rect.gameObject.SetActive(false);
            tiles[i] = t;
        }
        baseEmpty = Ui.Label(home, "", Ui.medium, 16f, Ui.Dim, Pad, 150f, Inner, 60f);
        baseEmpty.enableWordWrapping = true;

        Ui.Section(home, "MY CAPES", Pad, 268f, 300f);
        prevBack = Ui.TextButton(home, "‹", W - Pad - 160f, 260f, 40f, 36f, Ui.Glass, Color.white, () => Page(-1), out _);
        pageText = Ui.Label(home, "1 / 1", Ui.medium, 16f, Ui.Ink, W - Pad - 116f, 260f, 72f, 36f, TextAlignmentOptions.Center);
        nextBack = Ui.TextButton(home, "›", W - Pad - 40f, 260f, 40f, 36f, Ui.Glass, Color.white, () => Page(1), out _);

        for (int i = 0; i <= PerPage; i++)
        {
            int col = i % Cols, row = i / Cols;
            float x = Pad + col * (CardW + 16f), y = 304f + row * (ThumbH + 44f);
            if (i == 0)
            {
                var add = Ui.At(home, "Import", x, y, CardW, ThumbH);
                var addBack = Ui.Fill(add, Ui.small, Ui.Soft, true);
                Ui.Fill(Ui.At(add, "Ring", 0f, 0f, CardW, ThumbH), Ui.ring, new Color(1f, 1f, 1f, 0.25f));
                Ui.Label(add, "+", Ui.semiBold, 56f, Ui.Accent, 0f, 40f, CardW, 70f, TextAlignmentOptions.Center);
                Ui.Label(add, "Import\npicture", Ui.semiBold, 17f, Ui.Ink, 0f, 112f, CardW, 50f, TextAlignmentOptions.Center);
                Ui.Click(add.gameObject, addBack, Import);
                continue;
            }
            var c = new Card();
            c.Rect = Ui.At(home, "Card", x, y, CardW, ThumbH + 30f);
            c.Back = Ui.Fill(Ui.At(c.Rect, "Back", 0f, 0f, CardW, ThumbH), Ui.small, new Color(0f, 0f, 0f, 0.3f), true);
            c.Thumb = Ui.At(c.Rect, "Thumb", 6f, 6f, CardW - 12f, ThumbH - 12f).gameObject.AddComponent<RawImage>();
            c.Thumb.raycastTarget = false;
            c.Ring = Ui.Fill(Ui.At(c.Rect, "Ring", 0f, 0f, CardW, ThumbH), Ui.ring, Color.white);
            c.BadgeChip = Ui.At(c.Rect, "Badge", 10f, 10f, 80f, 24f);
            Ui.Fill(c.BadgeChip, Ui.small, Ui.Accent);
            c.Badge = Ui.Label(c.BadgeChip, "WEARING", Ui.semiBold, 12f, Ui.AccentText, 0f, 0f, 80f, 24f, TextAlignmentOptions.Center);
            c.Name = Ui.Label(c.Rect, "", Ui.medium, 15f, Ui.Ink, 0f, ThumbH + 4f, CardW, 24f, TextAlignmentOptions.Center);
            c.Name.overflowMode = TextOverflowModes.Ellipsis;
            var card = c;
            Ui.Click(c.Back.gameObject, c.Back, () => Select(card.Design));
            cards[i - 1] = c;
        }

        var row2 = Ui.At(home, "Actions", Pad, 790f, Inner, 54f);
        actions = row2.gameObject.AddComponent<CanvasGroup>();
        wearBack = Ui.TextButton(row2, "Wear this cape", 0f, 0f, 330f, 54f, Ui.Accent, Ui.AccentText, Wear, out wearText);
        wearText.fontSize = 20f;
        Ui.TextButton(row2, "Edit", 342f, 0f, 200f, 54f, Ui.Glass, Color.white, () => { if (selected != null) StartEdit(selected); }, out var editText);
        editText.fontSize = 20f;
        deleteBack = Ui.TextButton(row2, "Delete", 554f, 0f, Inner - 554f, 54f, Ui.Glass, Color.white, Delete, out deleteText);
        deleteText.fontSize = 20f;

        hintText = Ui.Label(home, "", Ui.medium, 15f, Ui.Dim, Pad, 862f, Inner, 64f);
        hintText.enableWordWrapping = true;
        hintText.richText = true;
    }

    static void RefreshHome()
    {
        // base capes: the supported ones from the game's skin list, the one on your back ringed
        if (!Alive(skins))
        {
            skins = Object.FindObjectOfType<SkinsManager>();
            found.Clear();
        }
        string wornKey = "";
        if (skins != null)
        {
            try
            {
                wornKey = skins.GetEquippedCategorySkin(SkinCategory.AccessoryBack) ?? "";
                if (found.Count == 0 || Time.unscaledTime >= foundUntil || !found.TrueForAll(f => Alive(f.Skin))) ReadBaseCapes();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("couldn't read the game's capes: " + e.Message);
            }
        }
        if (wornKey.Length == 0) wornKey = CapeSwap.WornKey();
        for (int i = 0; i < MaxBase; i++)
        {
            var t = tiles[i];
            if (i >= found.Count) { t.Rect.gameObject.SetActive(false); continue; }
            var (k, s) = found[i];
            t.Key = s.addressableKey;
            t.Title = CapeLayout.KnownCapes[k].Name;
            bool unlocked = true;
            try { unlocked = skins.IsSkinUnlocked(t.Key) && skins.CanUseSkinKey(t.Key, false); } catch { /* treat as usable */ }
            t.Unlocked = unlocked;
            t.Icon.sprite = s.sprite;
            t.Icon.color = unlocked ? Color.white : new Color(1f, 1f, 1f, 0.3f);
            t.Lock.gameObject.SetActive(!unlocked);
            t.Ring.gameObject.SetActive(t.Key == wornKey);
            t.Name.text = t.Title;
            t.Rect.gameObject.SetActive(true);
        }
        baseEmpty.text = found.Count == 0 ? "The game's cape list isn't loaded here. Open the menu in a world, or put a cape on in the game's customization screen." : "";

        // my capes
        int pages = Math.Max(1, (Library.Designs.Count + PerPage - 1) / PerPage);
        page = Math.Clamp(page, 0, pages - 1);
        pageText.text = $"{page + 1} / {pages}";
        prevBack.color = page > 0 ? Ui.Glass : Ui.Soft;
        nextBack.color = page < pages - 1 ? Ui.Glass : Ui.Soft;
        if (selected != null && !Library.Designs.Contains(selected)) selected = null;
        if (selected == null && Library.Designs.Count > 0) selected = Library.Find(Plugin.Equipped.Value) ?? Library.Designs[0];
        string equipped = Plugin.Equipped.Value;
        for (int i = 0; i < PerPage; i++)
        {
            var c = cards[i];
            int index = page * PerPage + i;
            c.Design = index < Library.Designs.Count ? Library.Designs[index] : null;
            c.Rect.gameObject.SetActive(c.Design != null);
            if (c.Design == null) continue;
            c.Thumb.texture = c.Design.Thumb;
            c.Thumb.color = c.Design.Thumb != null ? Color.white : Color.clear;
            if (c.Design.Thumb != null) FitThumb(c.Thumb, c.Design.Thumb, CardW - 12f, ThumbH - 12f);
            c.Name.text = c.Design.Name;
            c.Ring.gameObject.SetActive(c.Design == selected);
            bool wearing = c.Design.Id == equipped;
            c.BadgeChip.gameObject.SetActive(wearing);
            c.Badge.text = Plugin.Enabled.Value ? "WEARING" : "OFF";
        }

        bool has = selected != null;
        Ui.SetGroup(actions, has);
        bool on = has && selected.Id == equipped && Plugin.Enabled.Value;
        wearText.text = !has ? "Wear this cape" : on ? "Take it off" : selected.Id == equipped ? "Turn it back on" : "Wear this cape";
        wearBack.color = on ? Ui.Glass : Ui.Accent;
        wearText.color = on ? Color.white : Ui.AccentText;
        bool armed = Time.unscaledTime < deleteArmedUntil;
        deleteText.text = armed ? "Sure? Click again" : "Delete";
        deleteBack.color = armed ? Ui.Danger : Ui.Glass;

        string menuKey = KeyName(Plugin.MenuKey.Value), toggle = KeyName(Plugin.ToggleKey.Value);
        hintText.text = $"<color=#3DFF7A>{menuKey}</color> opens this menu · <color=#3DFF7A>Shift+{toggle}</color> turns your custom cape on and off anywhere.\n" +
                        $"Your cape keeps the game's own cloth physics. Only you see it (other players see your normal cape). Textures {CapeTextures.Size}×{CapeTextures.Size}.";
    }

    // the base capes the game has, in KnownCapes order (read again every 10 s at most, the list is a few hundred skins)
    static readonly List<(int Index, SkinUISO Skin)> found = new();
    static float foundUntil;

    static void ReadBaseCapes()
    {
        found.Clear();
        foundUntil = Time.unscaledTime + 10f;
        var all = skins.skinAddressableLibrary?.allLoadedSkinRefs;
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (s == null || s.skinCategory != SkinCategory.AccessoryBack) continue;
            int k = CapeLayout.KnownIndex(s.addressableKey);
            if (k >= 0 && !found.Exists(f => f.Index == k)) found.Add((k, s));
        }
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    static void FitThumb(RawImage image, Texture tex, float w, float h)
    {
        float aspect = tex.width / (float)Math.Max(1, tex.height);
        var size = aspect > w / h ? new Vector2(w, w / aspect) : new Vector2(h * aspect, h);
        image.rectTransform.sizeDelta = size;
        image.rectTransform.anchoredPosition = new Vector2(6f + (w - size.x) / 2f, -6f - (h - size.y) / 2f);
    }

    static void PutOn(BaseTile t)
    {
        if (t == null || string.IsNullOrEmpty(t.Key)) return;
        if (!t.Unlocked)
        {
            Ui.Toast($"You haven't unlocked the {t.Title} cape yet");
            return;
        }
        bool ok = false;
        try { ok = skins != null && skins.TryEquipLocalPlayerSkin(SkinCategory.AccessoryBack, t.Key); }
        catch (Exception e) { Plugin.Logger.LogWarning("couldn't put the cape on: " + e.Message); }
        Ui.Toast(ok ? $"Putting on the {t.Title} cape" : $"The game didn't let the {t.Title} cape go on here");
        nextRefresh = Time.unscaledTime + 0.3f; // the ring moves once the game has it on
    }

    static void Page(int step)
    {
        page += step;
        Refresh();
    }

    static void Select(Design d)
    {
        if (d == null) return;
        selected = d;
        deleteArmedUntil = 0f;
        Refresh();
    }

    static void Wear()
    {
        if (selected == null) return;
        if (selected.Id == Plugin.Equipped.Value && Plugin.Enabled.Value) Plugin.Equipped.Value = "";
        else
        {
            Plugin.Equipped.Value = selected.Id;
            Plugin.Enabled.Value = true;
        }
        CapeSwap.Refresh();
        Refresh();
    }

    static void Delete()
    {
        if (selected == null) return;
        if (Time.unscaledTime >= deleteArmedUntil)
        {
            deleteArmedUntil = Time.unscaledTime + 3f;
            Refresh();
            return;
        }
        deleteArmedUntil = 0f;
        var d = selected;
        if (d.Id == Plugin.Equipped.Value) Plugin.Equipped.Value = "";
        Library.Delete(d);
        selected = null;
        CapeSwap.Refresh();
        Ui.Toast($"Deleted “{d.Name}”");
        Refresh();
    }

    internal static void ToggleEnabled()
    {
        Plugin.Enabled.Value = !Plugin.Enabled.Value;
        CapeSwap.Refresh();
        var d = Library.Find(Plugin.Equipped.Value);
        Ui.Toast(Plugin.Enabled.Value
            ? d != null ? $"Custom cape ON · “{d.Name}”" : "Custom cape ON · pick one of your capes in the menu"
            : "Custom cape OFF · the game's own cape");
        if (IsOpen) Refresh();
    }

    // ---------------------------------------------------------------- importing a picture

    static bool importing;

    static void Import()
    {
        if (importing || FilePicker.Busy) return;
        string start = Plugin.LastFolder.Value;
        if (string.IsNullOrWhiteSpace(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        FilePicker.Show(start);
    }

    /// Every frame: once Windows' Open window closes, copy the picked picture into the library and open the editor.
    internal static void TakePick()
    {
        if (!FilePicker.TryTake(out var file, out var error)) return;
        if (error != null) { Ui.Toast(error); Plugin.Logger.LogWarning(error); return; }
        if (file == null) return;
        Plugin.LastFolder.Value = Path.GetDirectoryName(file) ?? "";
        importing = true;
        Ui.Toast("Reading the picture…");
        int max = Plugin.SourceMax;
        var lay = CapeSwap.WornLayout ?? CapeLayout.Classic;
        Worker.Run(() =>
        {
            Design d = null;
            TexData thumb = null;
            string problem = null;
            try
            {
                d = Library.Import(file, max);
                thumb = Worker.MakeThumb(d, d.Edit, lay, max);
            }
            catch (Exception e) { problem = e.Message; }
            Worker.Post(() =>
            {
                importing = false;
                if (d == null)
                {
                    Ui.Toast("Couldn't use that picture: " + problem);
                    Plugin.Logger.LogWarning($"import of {file} failed: {problem}");
                    return;
                }
                if (thumb != null) d.Thumb = CapeTextures.Upload(thumb, "Cape Editor thumb");
                Library.Designs.Add(d);
                page = (Library.Designs.Count - 1) / PerPage;
                selected = d;
                Plugin.Logger.LogInfo($"imported {file} as {d.Id} ({d.SourceWidth}×{d.SourceHeight})");
                if (IsOpen) StartEdit(d);
            });
        });
    }

    /// At start: the library cards' pictures, read on the worker.
    internal static void LoadThumbs()
    {
        int max = Plugin.SourceMax;
        foreach (var design in Library.Designs)
        {
            var d = design;
            Worker.Run(() =>
            {
                TexData data;
                try
                {
                    data = File.Exists(d.ThumbPath)
                        ? Composer.ForUnity(Gdip.ReadExact(d.ThumbPath, out int w, out int h), w, h, false, false)
                        : Worker.MakeThumb(d, d.Edit, CapeLayout.Classic, max);
                }
                catch (Exception e)
                {
                    Worker.Log($"no thumbnail for {d.Id}: {e.Message}");
                    return;
                }
                Worker.Post(() =>
                {
                    if (d.Thumb != null) Object.Destroy(d.Thumb);
                    d.Thumb = CapeTextures.Upload(data, "Cape Editor thumb");
                    if (IsOpen) Refresh();
                });
            });
        }
    }

    // ---------------------------------------------------------------- editor page

    static Design editing;
    static Edit edit;
    static CapeLayout layout;
    static RectTransform canvasBox, picture, frame;
    static readonly RectTransform[] dims = new RectTransform[4];
    static RawImage pictureImage, previewImage;
    static RectTransform previewBox;
    static Texture2D viewTex;
    static Slider zoomSlider;
    static TextMeshProUGUI zoomValue, infoText, layoutText, saveText, workText;
    static Ui.Segmented insideSeg, seeSeg;
    static readonly Image[] swatchRings = new Image[9];
    static Image autoSwatch, saveBack;
    // dirty: changed since the last draft; owedFull: a full-size draft is still to come once things stop moving
    static bool dragging, dirty, owedFull, saving;
    static Vector2 lastMouse;
    static float changedAt, lastDraftAt;
    static int draftVersion, shownVersion;

    static void BuildEditor()
    {
        // the picture and its crop frame
        canvasBox = Ui.At(editor, "Canvas", Pad, 100f, CanvasSize, CanvasSize);
        Ui.Fill(canvasBox, Ui.small, new Color(0f, 0f, 0f, 0.4f), true);
        var clip = Ui.At(canvasBox, "Clip", 0f, 0f, CanvasSize, CanvasSize);
        clip.gameObject.AddComponent<RectMask2D>();
        picture = Ui.At(clip, "Picture", 0f, 0f, 10f, 10f);
        pictureImage = picture.gameObject.AddComponent<RawImage>();
        pictureImage.raycastTarget = false;
        for (int i = 0; i < 4; i++)
        {
            dims[i] = Ui.At(clip, "Dim", 0f, 0f, 0f, 0f);
            Ui.Fill(dims[i], null, new Color(0f, 0f, 0f, 0.62f));
        }
        frame = Ui.At(clip, "Frame", 0f, 0f, 10f, 10f);
        var thirds = new Color(1f, 1f, 1f, 0.3f);
        foreach (float t in new[] { 1f / 3f, 2f / 3f })
        {
            var v = Ui.Node(frame, "Third");
            Ui.Fill(v, null, thirds);
            v.anchorMin = new Vector2(t, 0f); v.anchorMax = new Vector2(t, 1f); v.sizeDelta = new Vector2(2f, 0f); v.anchoredPosition = Vector2.zero;
            var h = Ui.Node(frame, "Third");
            Ui.Fill(h, null, thirds);
            h.anchorMin = new Vector2(0f, t); h.anchorMax = new Vector2(1f, t); h.sizeDelta = new Vector2(0f, 2f); h.anchoredPosition = Vector2.zero;
        }
        var border = Ui.Node(frame, "Border");
        Ui.Stretch(border);
        Ui.Fill(border, Ui.ring, Color.white);
        border.offsetMin = new Vector2(-3f, -3f); border.offsetMax = new Vector2(3f, 3f);
        Ui.Label(canvasBox, "Drag to move · Scroll to zoom", Ui.medium, 14f, new Color(1f, 1f, 1f, 0.7f), 0f, CanvasSize + 6f, CanvasSize, 20f, TextAlignmentOptions.Center);

        // right column: what the cape shows, and the numbers
        Ui.Section(editor, "ON THE CAPE", SideX, 100f, SideW);
        previewBox = Ui.At(editor, "Preview", SideX, 128f, SideW, 330f);
        Ui.Fill(previewBox, Ui.small, new Color(0f, 0f, 0f, 0.3f));
        previewImage = Ui.At(previewBox, "Image", 0f, 0f, SideW, 330f).gameObject.AddComponent<RawImage>();
        previewImage.raycastTarget = false;
        layoutText = Ui.Label(editor, "", Ui.semiBold, 15f, Ui.Accent, SideX, 466f, SideW, 22f);
        infoText = Ui.Label(editor, "", Ui.medium, 14f, Ui.Dim, SideX, 490f, SideW, 110f, TextAlignmentOptions.TopLeft);
        infoText.enableWordWrapping = true;
        infoText.richText = true;

        // zoom
        Ui.Label(editor, "Zoom", Ui.medium, 18f, Ui.Ink, Pad, 628f, 120f, 26f);
        zoomValue = Ui.Label(editor, "", Ui.medium, 18f, Ui.Ink, W - Pad - 160f, 628f, 160f, 26f, TextAlignmentOptions.MidlineRight);
        zoomSlider = Ui.MakeSlider(editor, Pad, 656f, Inner, v =>
        {
            Probe(out float vw, out float vh);
            float fit = Composer.FitZoom(vw, vh, layout.Aspect);
            edit.Zoom = fit * Mathf.Pow(Composer.MaxZoom / fit, v);
            if (Mathf.Abs(edit.Zoom - 1f) < 0.03f) edit.Zoom = 1f; // snap to Fill
            Changed(false);
        });

        // quick buttons
        string[] names = { "Fill", "Fit", "Turn 90°", "Mirror", "Reset" };
        Action[] clicks =
        {
            () => { edit.Zoom = 1f; edit.CenterX = edit.CenterY = 0.5f; Changed(true); },
            () => { edit.Zoom = 0.01f; edit.CenterX = edit.CenterY = 0.5f; Changed(true); },
            () => { edit.Turns = (edit.Turns + 1) & 3; edit.CenterX = edit.CenterY = 0.5f; RequestView(); Changed(true); },
            () => { edit.Mirror = !edit.Mirror; edit.CenterX = 1f - edit.CenterX; RequestView(); Changed(true); },
            () => { bool turned = edit.Turns != 0 || edit.Mirror; edit = Edit.Default; if (turned) RequestView(); Changed(true); },
        };
        float bw = (Inner - 4 * 10f) / 5f;
        for (int i = 0; i < 5; i++) Ui.TextButton(editor, names[i], Pad + i * (bw + 10f), 698f, bw, 44f, Ui.Glass, Color.white, clicks[i], out _);

        // options
        Ui.Label(editor, "Inside of the cape", Ui.medium, 17f, Ui.Ink, Pad, 756f, 220f, 40f);
        insideSeg = new Ui.Segmented(editor, Pad + 220f, 756f, Inner - 220f, 40f, new[] { "Same picture", "Plain colour" }, i => { edit.PlainInside = i == 1; Changed(true); });
        Ui.Label(editor, "See-through parts", Ui.medium, 17f, Ui.Ink, Pad, 804f, 220f, 40f);
        seeSeg = new Ui.Segmented(editor, Pad + 220f, 804f, Inner - 220f, 40f, new[] { "Edge colour", "Cut holes" }, i => { edit.CutOut = i == 1; Changed(true); });
        Ui.Label(editor, "Edge colour", Ui.medium, 17f, Ui.Ink, Pad, 852f, 220f, 40f);
        for (int i = 0; i < 9; i++)
        {
            int index = i;
            var sw = Ui.At(editor, "Swatch", Pad + 220f + i * 50f, 852f, 40f, 40f);
            var fill = Ui.Fill(Ui.At(sw, "Fill", 5f, 5f, 30f, 30f), Ui.circle, i == 0 ? Color.gray : Color32(Swatches[i - 1]), true);
            if (i == 0)
            {
                autoSwatch = fill;
                Ui.Label(sw, "A", Ui.semiBold, 15f, Color.white, 0f, 0f, 40f, 40f, TextAlignmentOptions.Center);
            }
            swatchRings[i] = Ui.Fill(sw, Ui.ring, Ui.Accent);
            Ui.Click(fill.gameObject, fill, () => { edit.EdgeColor = index == 0 ? 0u : Swatches[index - 1]; Changed(true); });
        }

        // save
        saveBack = Ui.TextButton(editor, "Save & wear", Pad, 892f, 320f, 50f, Ui.Accent, Ui.AccentText, Save, out saveText);
        saveText.fontSize = 20f;
        Ui.TextButton(editor, "Cancel", Pad + 332f, 892f, 160f, 50f, Ui.Glass, Color.white, () => CancelEdit(), out _);
        workText = Ui.Label(editor, "", Ui.medium, 15f, Ui.Dim, Pad + 508f, 892f, Inner - 508f, 50f);
    }

    static Color Color32(uint argb) => new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, 1f);

    static void StartEdit(Design d)
    {
        editing = d;
        edit = d.Edit;
        layout = CapeSwap.WornLayout ?? CapeLayout.Classic;
        dragging = false;
        saving = false;
        pictureImage.texture = null;
        pictureImage.color = Color.clear;
        previewImage.texture = null;
        home.gameObject.SetActive(false);
        editor.gameObject.SetActive(true);
        backButton.gameObject.SetActive(true);
        icon.gameObject.SetActive(false);
        titleRect.anchoredPosition = new Vector2(Pad + 52f, -16f);
        title.text = d.Name;
        RequestView();
        Changed(true);
        Refresh();
    }

    static void CancelEdit()
    {
        if (editing == null) return;
        EndEdit();
        Refresh();
    }

    static void EndEdit()
    {
        var d = editing;
        editing = null;
        dragging = false;
        dirty = owedFull = false;
        CapeTextures.SetDraft(null, null);
        previewImage.texture = null;
        pictureImage.texture = null;
        if (viewTex != null) Object.Destroy(viewTex);
        viewTex = null;
        editor.gameObject.SetActive(false);
        home.gameObject.SetActive(true);
        backButton.gameObject.SetActive(false);
        icon.gameObject.SetActive(true);
        titleRect.anchoredPosition = new Vector2(Pad + 48f, -16f);
        title.text = "Cape Editor";
        if (d != null)
        {
            string source = d.SourcePath;
            Worker.Run(() => Worker.Forget(source)); // frees the decoded picture
        }
    }

    /// The turned picture for the crop canvas (1024 px at most), from the worker.
    static void RequestView()
    {
        var d = editing;
        if (d == null) return;
        int turns = edit.Turns, max = Plugin.SourceMax;
        bool mirror = edit.Mirror;
        pictureImage.color = new Color(1f, 1f, 1f, 0.35f); // until the turned one arrives
        Worker.Run(() =>
        {
            TexData data;
            try
            {
                var src = Worker.Source(d, max);
                using var view = Composer.View(src, turns, mirror, 1024);
                data = Composer.ForUnity(Gdip.Pixels(view), view.Width, view.Height, true, false);
            }
            catch (Exception e)
            {
                Worker.Post(() => Ui.Toast("Couldn't read the picture: " + e.Message));
                return;
            }
            Worker.Post(() =>
            {
                if (editing != d || edit.Turns != turns || edit.Mirror != mirror) return;
                if (viewTex != null) Object.Destroy(viewTex);
                viewTex = CapeTextures.Upload(data, "Cape Editor view");
                pictureImage.texture = viewTex;
                pictureImage.color = Color.white;
            });
        });
    }

    static void Changed(bool now)
    {
        dirty = owedFull = true;
        changedAt = now ? -10f : Time.unscaledTime;
        LayoutCanvas();
        RefreshEditor();
    }

    /// The turned picture's size (in source pixels) and the frame maths' fixes to the edit.
    static void Probe(out float vw, out float vh)
    {
        Composer.ViewSize(editing.SourceWidth, editing.SourceHeight, edit.Turns, out vw, out vh);
    }

    static void LayoutCanvas()
    {
        if (editing == null) return;
        Probe(out float vw, out float vh);
        float a = layout.Aspect;
        Composer.Frame(vw, vh, a, ref edit, out float fx, out float fy, out float fw, out float fh);
        // scale so the biggest frame (Fit) always fits the canvas
        float cfw = Math.Max(vw, a * vh), cfh = cfw / a;
        float s = Math.Min(CanvasSize / cfw, CanvasSize / cfh) * 0.96f;
        float ox = (CanvasSize - vw * s) / 2f, oy = (CanvasSize - vh * s) / 2f;
        Ui.Place(picture, ox, oy, vw * s, vh * s);
        float x0 = ox + fx * s, y0 = oy + fy * s, x1 = x0 + fw * s, y1 = y0 + fh * s;
        Ui.Place(frame, x0, y0, x1 - x0, y1 - y0);
        Ui.Place(dims[0], 0f, 0f, CanvasSize, Math.Max(0f, y0));
        Ui.Place(dims[1], 0f, y1, CanvasSize, Math.Max(0f, CanvasSize - y1));
        Ui.Place(dims[2], 0f, y0, Math.Max(0f, x0), y1 - y0);
        Ui.Place(dims[3], x1, y0, Math.Max(0f, CanvasSize - x1), y1 - y0);
        canvasScale = s;
    }

    static float canvasScale = 1f;

    static void RefreshEditor()
    {
        if (editing == null) return;
        Probe(out float vw, out float vh);
        float fit = Composer.FitZoom(vw, vh, layout.Aspect);
        zoomSlider.SetValueWithoutNotify(Mathf.Log(edit.Zoom / fit) / Mathf.Log(Composer.MaxZoom / fit));
        zoomValue.text = Math.Abs(edit.Zoom - 1f) < 0.005f ? "Fill" : edit.Zoom <= fit + 0.001f ? "Fit (whole picture)" :
            edit.Zoom.ToString("0.00", CultureInfo.InvariantCulture) + "×";
        insideSeg.Select(edit.PlainInside ? 1 : 0);
        seeSeg.Select(edit.CutOut ? 1 : 0);
        autoSwatch.color = Color32(editing.AutoColor != 0 ? editing.AutoColor : 0xFF202020);
        for (int i = 0; i < 9; i++)
            swatchRings[i].gameObject.SetActive(i == 0 ? edit.EdgeColor == 0 : edit.EdgeColor == Swatches[i - 1]);

        int size = CapeTextures.Size;
        int pw = (int)Math.Round(layout.Outside.Width * size), ph = (int)Math.Round(layout.Outside.Height * size);
        bool exact = Math.Abs(vw / vh - layout.Aspect) < 0.01f && Math.Abs(edit.Zoom - 1f) < 0.005f;
        string fitNote = exact ? "Already the cape's shape: resized to fit, nothing cut off."
            : edit.Zoom < 1f ? "Shrunk to show more of it, with the edge colour round it."
            : "Auto-cropped to the cape's shape and resized. Drag the frame to choose the part you want.";
        infoText.text = $"Picture {editing.SourceWidth}×{editing.SourceHeight}\nCape panel {pw}×{ph} px\n{fitNote}";
        layoutText.text = $"{layout.Name} capes" + (CapeSwap.WornLayout == null ? " (preview)" : "");

        // the preview: the outside panel of the live texture, turned back upright
        var o = layout.Outside;
        previewImage.uvRect = layout.Turned ? new Rect(o.U1, o.V1, -o.Width, -o.Height) : new Rect(o.U0, o.V0, o.Width, o.Height);
        float pa = layout.Aspect, bwid = SideW, bhei = 330f;
        var psize = pa > bwid / bhei ? new Vector2(bwid, bwid / pa) : new Vector2(bhei * pa, bhei);
        previewImage.rectTransform.sizeDelta = psize;
        previewImage.rectTransform.anchoredPosition = new Vector2((bwid - psize.x) / 2f, -(bhei - psize.y) / 2f);
        previewImage.color = previewImage.texture != null ? Color.white : Color.clear;
        saveText.text = saving ? "Saving…" : "Save & wear";
        saveBack.color = saving ? Ui.Glass : Ui.Accent;
    }

    /// Mouse on the crop canvas: drag moves the frame, the wheel zooms.
    static void CanvasInput(Mouse mouse)
    {
        if (mouse == null || editing == null) return;
        var pos = mouse.position.ReadValue();
        bool over = RectTransformUtility.RectangleContainsScreenPoint(canvasBox, pos, null);
        if (mouse.leftButton.wasPressedThisFrame && over)
        {
            dragging = true;
            lastMouse = pos;
        }
        if (dragging)
        {
            if (!mouse.leftButton.isPressed)
            {
                dragging = false;
                changedAt = -10f; // let go: make the full-size texture now
            }
            else if (pos != lastMouse)
            {
                var delta = (pos - lastMouse) / Mathf.Max(0.01f, canvas.scaleFactor * canvasScale);
                lastMouse = pos;
                Probe(out float vw, out float vh);
                edit.CenterX += delta.x / vw;
                edit.CenterY -= delta.y / vh; // screen y goes up, picture y goes down
                Changed(false);
            }
        }
        if (over)
        {
            float y = mouse.scroll.ReadValue().y;
            if (y != 0f)
            {
                float notches = Mathf.Abs(y) >= 20f ? y / 120f : y; // the game's Input System reads ±1 a notch (kit trap 25)
                edit.Zoom *= Mathf.Pow(1.12f, notches);
                Changed(false);
            }
        }
    }

    /// Live drafts while editing: 512 px about 12 times a second while something moves, the full size once it stops.
    static void PumpDraft()
    {
        if (editing == null || saving) return;
        float now = Time.unscaledTime;
        bool moving = dragging || now - changedAt < 0.25f;
        if (moving)
        {
            if (!dirty || now - lastDraftAt < 0.08f) return; // nothing new, or one went out a moment ago
        }
        else if (!dirty && !owedFull) return;
        lastDraftAt = now;
        dirty = false;
        if (!moving) owedFull = false;
        var d = editing;
        var e = edit;
        var lay = layout;
        int size = moving ? Math.Min(512, CapeTextures.Size) : CapeTextures.Size, max = Plugin.SourceMax, version = ++draftVersion;
        workText.text = "Updating the cape…";
        Worker.RunLatest(() =>
        {
            TexData data;
            try
            {
                var src = Worker.Source(d, max);
                using var cape = Composer.Cape(src, e, d.AutoColor, lay, size);
                data = Composer.ForUnity(Gdip.Pixels(cape), size, size, true, true);
            }
            catch (Exception ex)
            {
                Worker.Log("draft failed: " + ex.Message, true);
                return;
            }
            Worker.Post(() =>
            {
                if (editing != d || version < shownVersion) return;
                shownVersion = version;
                var tex = CapeTextures.Upload(data, "Cape Editor draft");
                CapeTextures.SetDraft(tex, lay);
                previewImage.texture = tex;
                previewImage.color = Color.white;
                if (version == draftVersion) workText.text = CapeSwap.Showing ? "Live on your Flippy" : "";
            });
        });
    }

    static void Save()
    {
        if (editing == null || saving) return;
        saving = true;
        var d = editing;
        var e = edit;
        var lay = layout;
        int size = CapeTextures.Size, max = Plugin.SourceMax;
        d.Edit = e;
        try { Library.Save(d); }
        catch (Exception ex) { Ui.Toast("Couldn't save: " + ex.Message); saving = false; return; }
        RefreshEditor();
        Worker.Run(() =>
        {
            TexData cape = null, thumb = null;
            string problem = null;
            try
            {
                foreach (var old in Directory.GetFiles(d.Folder, "cape-*.png")) File.Delete(old); // other families rebuild on demand
                cape = Worker.CapeTexture(d, e, lay, size);
                thumb = Worker.MakeThumb(d, e, lay, max);
            }
            catch (Exception ex) { problem = ex.Message; }
            Worker.Post(() =>
            {
                saving = false;
                if (cape == null)
                {
                    Ui.Toast("Couldn't save the cape: " + problem);
                    if (editing == d) RefreshEditor();
                    return;
                }
                Plugin.Equipped.Value = d.Id;
                Plugin.Enabled.Value = true;
                CapeTextures.Adopt(d, lay, CapeTextures.Upload(cape, $"Cape Editor {d.Id} {lay.Id}"));
                if (thumb != null)
                {
                    if (d.Thumb != null) Object.Destroy(d.Thumb);
                    d.Thumb = CapeTextures.Upload(thumb, "Cape Editor thumb");
                }
                if (editing == d) EndEdit();
                selected = d;
                Ui.Toast(CapeSwap.WornLayout != null ? $"Wearing “{d.Name}”" : $"Saved “{d.Name}”: put on one of the base capes to see it");
                Refresh();
            });
        });
    }

    // ---------------------------------------------------------------- open, close, per frame

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public static void Open()
    {
        if (root == null) Build();
        if (root == null) return;
        Ui.EnsureEventSystem();
        deleteArmedUntil = 0f;
        root.SetActive(true);
        Refresh();
        GameControls.Pause();
        oldLock = Cursor.lockState; oldVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    public static void Close()
    {
        if (!IsOpen) return;
        if (editing != null) EndEdit();
        root.SetActive(false);
        GameControls.Resume();
        Cursor.lockState = oldLock; Cursor.visible = oldVisible;
    }

    /// Esc: out of the editor first, then out of the menu.
    public static void Back()
    {
        if (editing != null) CancelEdit();
        else Close();
    }

    public static void Tick(Mouse mouse)
    {
        if (!IsOpen) return;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None; // the game grabs it back
        if (!Cursor.visible) Cursor.visible = true;
        if (editing != null)
        {
            CanvasInput(mouse);
            PumpDraft();
        }
        if (Time.unscaledTime >= nextRefresh) Refresh();
    }

    static void Refresh()
    {
        nextRefresh = Time.unscaledTime + 0.5f;
        master.Set(Plugin.Enabled.Value);
        menuKeyText.text = KeyName(Plugin.MenuKey.Value);
        statusText.text = Status();
        if (editing != null)
        {
            // the cape family can change under the editor (the game put another cape on)
            var worn = CapeSwap.WornLayout ?? layout;
            if (worn != layout)
            {
                layout = worn;
                Changed(true);
            }
            RefreshEditor();
        }
        else RefreshHome();
    }

    static string Status()
    {
        var d = Library.Find(Plugin.Equipped.Value);
        var worn = CapeSwap.WornLayout;
        if (editing != null)
            return worn != null ? "<color=#3DFF7A>Editing live</color> · what you change shows on your Flippy's cape" : "Editing · put on a base cape to see it live on Flippy";
        if (!Plugin.Enabled.Value) return $"Custom cape is <color=#FF5A4F>OFF</color> · the switch or Shift+{KeyName(Plugin.ToggleKey.Value)} turns it on";
        if (!CapeSwap.WearingBackItem && CapeSwap.WornKey().Length == 0) return "No cape on · click one of the base capes to put it on";
        if (worn == null) return "That back item isn't a cape Cape Editor can paint · pick one of the base capes";
        if (d == null) return $"Your {worn.Name} cape shows the game's texture · import a picture or pick one of your capes";
        if (CapeTextures.Loading(worn)) return $"Getting “{d.Name}” ready…";
        return CapeSwap.Showing ? $"<color=#3DFF7A>Wearing “{d.Name}”</color> on your {worn.Name} cape" : $"“{d.Name}” goes on as soon as your cape shows";
    }

    static bool Alive(Object o) => o != null && !o.WasCollected;

    internal static string KeyName(Key key)
    {
        string s = key.ToString();
        if (s.StartsWith("Digit")) return s.Substring(5);
        if (s.StartsWith("Numpad")) return "Num " + s.Substring(6);
        return s switch
        {
            "Backquote" => "`", "Minus" => "-", "Equals" => "=", "LeftBracket" => "[", "RightBracket" => "]", "Semicolon" => ";",
            "Quote" => "'", "Comma" => ",", "Period" => ".", "Slash" => "/", "Backslash" => "\\", "PageUp" => "PgUp", "PageDown" => "PgDn",
            _ => s,
        };
    }
}
