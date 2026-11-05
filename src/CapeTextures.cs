using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// The textures the capes show: the equipped design, built for each cape family the player wears (on the worker,
/// once, then cached as PNG), and while the editor is open its live draft. Main thread only.
internal static unsafe class CapeTextures
{
    static readonly Dictionary<string, Texture2D> worn = new(), stale = new();
    static readonly HashSet<string> loading = new(), failed = new();
    static string wornId;
    static int generation;
    static Texture2D draft;
    static CapeLayout draftLayout;

    public static int Size => Plugin.TextureSize.Value;

    /// What a cape of this family should show now, or null for the game's own texture. Starts building it if needed;
    /// until it's ready the previous design stays on (no flash of the vanilla cape when you switch).
    public static Texture2D For(CapeLayout layout)
    {
        if (layout == null || !Plugin.Enabled.Value) return null;
        if (draft != null && draftLayout == layout) return draft;
        var d = Library.Find(Plugin.Equipped.Value);
        if (d == null)
        {
            if (wornId != null) Invalidate();
            return null;
        }
        if (wornId != d.Id) Invalidate();
        wornId = d.Id;
        if (worn.TryGetValue(layout.Id, out var tex) && tex != null) return tex;
        Request(d, layout);
        return stale.TryGetValue(layout.Id, out var old) ? old : null;
    }

    /// True while the equipped design is still being built for this family.
    public static bool Loading(CapeLayout layout) => layout != null && loading.Contains(layout.Id);

    static void Request(Design d, CapeLayout layout)
    {
        if (failed.Contains(layout.Id) || !loading.Add(layout.Id)) return;
        var edit = d.Edit;
        int size = Size, gen = generation;
        Worker.Run(() =>
        {
            TexData data = null;
            string error = null;
            try { data = Worker.CapeTexture(d, edit, layout, size); }
            catch (Exception e) { error = e.Message; }
            Worker.Post(() =>
            {
                if (gen != generation) return; // the equipped cape changed meanwhile
                loading.Remove(layout.Id);
                if (data == null)
                {
                    failed.Add(layout.Id); // no retry until the design changes
                    Ui.Toast($"Couldn't build “{d.Name}”: {error}");
                    Plugin.Logger.LogWarning($"cape {d.Id} for {layout.Id}: {error}");
                    return;
                }
                worn[layout.Id] = Upload(data, $"Cape Editor {d.Id} {layout.Id}");
                Plugin.Logger.LogInfo($"custom cape “{d.Name}” ready for {layout.Name} capes ({size}×{size})");
                CapeSwap.Refresh();
                if (stale.Remove(layout.Id, out var old) && old != null) Object.Destroy(old); // after the capes moved off it
            });
        });
    }

    /// The equipped design or its settings changed: what was built becomes stale (kept on screen until the new one
    /// is ready, or dropped if nothing is equipped).
    public static void Invalidate()
    {
        generation++;
        loading.Clear();
        failed.Clear();
        bool keep = Library.Find(Plugin.Equipped.Value) != null;
        foreach (var pair in worn)
        {
            if (pair.Value == null) continue;
            if (keep && !stale.ContainsKey(pair.Key)) stale[pair.Key] = pair.Value;
            else Object.Destroy(pair.Value);
        }
        worn.Clear();
        if (!keep)
        {
            foreach (var tex in stale.Values)
                if (tex != null) Object.Destroy(tex);
            stale.Clear();
        }
        wornId = null;
    }

    /// A freshly saved design: its texture for this family is already built (by the editor), so wear it as it is.
    public static void Adopt(Design d, CapeLayout layout, Texture2D tex)
    {
        Invalidate();
        wornId = d.Id;
        worn[layout.Id] = tex;
        CapeSwap.Refresh();
        if (stale.Remove(layout.Id, out var old) && old != null && old != tex) Object.Destroy(old);
    }

    /// The editor's live result for one family (null to stop showing a draft).
    public static void SetDraft(Texture2D tex, CapeLayout layout)
    {
        var old = draft;
        draft = tex;
        draftLayout = tex != null ? layout : null;
        CapeSwap.Refresh();
        if (old != null && old != tex) Object.Destroy(old); // after the capes moved off it
    }

    /// Pixels from the worker → a GPU texture (sRGB, with the worker's mip levels). The CPU copy is dropped at once.
    public static Texture2D Upload(TexData data, string name)
    {
        var tex = new Texture2D(data.Width, data.Height, TextureFormat.BGRA32, data.Mips, false)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = data.Mips > 1 ? FilterMode.Trilinear : FilterMode.Bilinear,
            anisoLevel = data.Mips > 1 ? 4 : 1,
        };
        fixed (byte* p = data.Data) tex.LoadRawTextureData((IntPtr)p, data.Data.Length);
        tex.Apply(false, true);
        return tex;
    }
}
