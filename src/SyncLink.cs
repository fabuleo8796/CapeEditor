using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using EHS;
using EHS.Skins;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// Other players' custom capes through FIH Sync (a separate, optional mod).
/// - Sent: your finished cape picture for the cape family you wear, as raw pixels (512×512 at most). The other
///   players' games already show your base cape (the game syncs skins), so only its picture changes.
/// - Received: painted on that player's own cape renderers with a copy of their material, like your own cape. Nothing
///   is decoded: the pixels are copied as they are, and the family must match the cape they really wear.
/// Without FIH Sync installed this does nothing; the calls into it sit in NoInlining methods.
internal static class SyncLink
{
    const int MaxSize = 512, Magic = 0x50414346; // "FCAP"

    sealed class Remote
    {
        public int Owner;
        public CapeLayout Layout;
        public Texture2D Texture;
        public IntPtr HandlerPtr, InstancePtr;
        public readonly List<(Renderer r, Material original, Material copy)> Parts = new();
    }

    static readonly Dictionary<int, Remote> remote = new();
    static bool linked;
    static float nextCheck;
    static int mainTex = -1, emissiveTex;

    internal static void Start()
    {
        try { Link(); linked = true; }
        catch (Exception) { Plugin.Logger.LogInfo("FIH Sync isn't installed: other players won't see your cape"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Link() => FIHSync.Share.Register("cape", Version, Data, Received, Gone);

    // ---------------------------------------------------------------- sending

    /// The equipped design, its settings and the family you wear: any change re-sends.
    static string Version()
    {
        var layout = CapeSwap.WornLayout;
        var d = Plugin.Enabled.Value ? Library.Find(Plugin.Equipped.Value) : null;
        return layout == null || d == null ? null : d.CachePath(d.Edit, layout, CapeTextures.Size);
    }

    static byte[] Data()
    {
        var layout = CapeSwap.WornLayout;
        var d = Plugin.Enabled.Value ? Library.Find(Plugin.Equipped.Value) : null;
        if (layout == null || d == null) return null;
        // your own picture at the size you wear it (Cape Editor keeps one cached size per family: asking for another
        // would replace it), read from its cache, then halved down to the sharing size
        var tex = Worker.CapeTexture(d, d.Edit, layout, CapeTextures.Size);
        if (tex == null || tex.Width * tex.Height * 4 > tex.Data.Length) return null;
        var (px, w, h) = Shrink(tex.Data, tex.Width, tex.Height); // the top mip only, BGRA
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            bw.Write(Magic);
            bw.Write(layout.Id);
            bw.Write(w);
            bw.Write(h);
        }
        using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
            z.Write(px, 0, w * h * 4);
        return ms.ToArray();
    }

    /// 2×2 averages until it fits MaxSize. Reads only the first w×h×4 bytes (the top mip).
    static (byte[] px, int w, int h) Shrink(byte[] src, int w, int h)
    {
        var px = src;
        while (w > MaxSize || h > MaxSize)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var o = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int a = ((y * 2) * w + x * 2) * 4, b = a + 4, c = a + w * 4, e = c + 4;
                    for (int k = 0; k < 4; k++) o[(y * nw + x) * 4 + k] = (byte)((px[a + k] + px[b + k] + px[c + k] + px[e + k] + 2) / 4);
                }
            px = o; w = nw; h = nh;
        }
        return (px, w, h);
    }

    // ---------------------------------------------------------------- receiving

    static void Received(int ownerId, byte[] data, string folder)
    {
        Gone(ownerId);
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadInt32() != Magic) return;
        var layout = CapeLayout.ById(r.ReadString());
        int w = r.ReadInt32(), h = r.ReadInt32();
        if (layout == null || w <= 0 || h <= 0 || w > MaxSize || h > MaxSize) return;
        var px = new byte[w * h * 4];
        using (var z = new DeflateStream(r.BaseStream, CompressionMode.Decompress))
        {
            int got = 0, n;
            while (got < px.Length && (n = z.Read(px, got, px.Length - got)) > 0) got += n;
            if (got != px.Length) return;
        }
        var colors = new Color32[w * h];
        for (int i = 0, k = 0; i < colors.Length; i++, k += 4)
            colors[i] = new Color32 { b = px[k], g = px[k + 1], r = px[k + 2], a = px[k + 3] };
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false)
        {
            name = "Cape Editor (player " + ownerId + ")",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        tex.SetPixels32(colors);
        tex.Apply(true, true);
        remote[ownerId] = new Remote { Owner = ownerId, Layout = layout, Texture = tex };
        Plugin.Logger.LogInfo($"player {ownerId}'s custom cape arrived ({layout.Name}, {w}×{h})");
    }

    static void Gone(int ownerId)
    {
        if (!remote.TryGetValue(ownerId, out var r)) return;
        Restore(r);
        if (r.Texture != null) Object.Destroy(r.Texture);
        remote.Remove(ownerId);
    }

    static bool Alive(Object o) => o != null && !o.WasCollected;

    static void Restore(Remote r)
    {
        foreach (var (rend, original, copy) in r.Parts)
        {
            if (Alive(rend) && Alive(original) && rend.sharedMaterial != null && rend.sharedMaterial.Pointer == copy.Pointer) rend.sharedMaterial = original;
            if (Alive(copy)) Object.Destroy(copy);
        }
        r.Parts.Clear();
        r.InstancePtr = IntPtr.Zero;
    }

    /// Twice a second: each player's cape object (it changes on respawns, level changes and cape swaps), painted when
    /// its family matches the picture they sent; put back when your custom capes are off.
    internal static void Tick()
    {
        if (!linked || remote.Count == 0 || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        if (mainTex < 0) { mainTex = Shader.PropertyToID("_MainTex"); emissiveTex = Shader.PropertyToID("_EmissiveTex"); }
        foreach (var r in remote.Values)
        {
            try { Check(r); }
            catch (Exception e) { Plugin.Logger.LogWarning($"player {r.Owner}'s cape: {e.Message}"); Restore(r); }
        }
    }

    static void Check(Remote r)
    {
        PlayerRef p = null;
        try { if (!PlayerRef.TryGetConnectedPlayerByClientId(r.Owner, out p)) p = null; }
        catch { p = null; }
        if (p != null && PlayerRef.IsLocalPlayer(p)) p = null;
        SkinPrefabHandler back = null;
        var handlers = p?.playerSkins?.handlers;
        if (handlers != null)
            for (int i = 0; i < handlers.Count; i++)
                if (handlers[i] != null && handlers[i].category == SkinCategory.AccessoryBack) { back = handlers[i]; break; }
        var instance = back != null ? back.instanceRef : null;
        var ptr = Alive(instance) ? instance.Pointer : IntPtr.Zero;
        if (ptr != r.InstancePtr || (back != null ? back.Pointer : IntPtr.Zero) != r.HandlerPtr)
        {
            Restore(r);
            r.HandlerPtr = back != null ? back.Pointer : IntPtr.Zero;
            r.InstancePtr = ptr;
            if (ptr != IntPtr.Zero) Scan(r, instance);
        }
        foreach (var (rend, original, copy) in r.Parts)
        {
            if (!Alive(rend) || !Alive(original)) continue;
            var want = Plugin.Enabled.Value ? copy : original; // Shift+O hides other players' custom capes too
            var current = rend.sharedMaterial;
            if (current != null && (current.Pointer == original.Pointer || current.Pointer == copy.Pointer) && current.Pointer != want.Pointer)
                rend.sharedMaterial = want;
        }
    }

    static void Scan(Remote r, SkinInstanceRef instance)
    {
        foreach (var rend in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (rend == null) continue;
            Mesh mesh = null;
            var skinned = rend.TryCast<SkinnedMeshRenderer>();
            if (skinned != null) mesh = skinned.sharedMesh;
            else
            {
                var filter = rend.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            if (mesh == null || CapeLayout.ForMesh(mesh.name) != r.Layout) continue; // another cape now: their picture won't fit it
            var material = rend.sharedMaterial;
            if (material == null || !material.HasProperty(mainTex)) continue;
            var copy = new Material(material) { name = material.name + " (Cape Editor, player " + r.Owner + ")", hideFlags = HideFlags.HideAndDontSave };
            if (copy.HasProperty(emissiveTex)) copy.SetTexture(emissiveTex, Texture2D.blackTexture);
            copy.SetTexture(mainTex, r.Texture);
            r.Parts.Add((rend, material, copy));
        }
        if (r.Parts.Count > 0) Plugin.Logger.LogInfo($"showing player {r.Owner}'s custom cape ({r.Parts.Count} renderers)");
    }
}
