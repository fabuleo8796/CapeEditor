using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// Puts the custom texture on the player's own cape, and only there.
/// - Which capes: the AccessoryBack skin of your own Flippy, of the phone in the customization screen and of the menu
///   phone. Other players' capes are never touched (this is client-side only; they see your normal cape).
/// - How: each cape renderer (LOD0, LOD1, LOD2) gets a copy of its own material with our picture as _MainTex.
///   Nothing else changes: the mesh, the bones and the Magica Cloth component that swings them are the game's, so the
///   cape moves exactly like the vanilla one. The game's fade and glow keep working (they're property blocks, which
///   apply to any material).
/// - When: every frame the capes already found are checked for a new cape object (one field read each), so a cape the
///   game swaps in is painted the frame it appears. A check twice a second finds new phones (a new scene, a respawn)
///   and catches the game swapping materials. No Harmony hook: SkinPrefabHandler.OnSkinApplied's base body is empty and
///   shared with every other empty method of the game build, so patching it crashed the game.
internal static class CapeSwap
{
    sealed class Part
    {
        public Renderer Renderer;
        public Material Original, Copy;
    }

    sealed class Slot
    {
        public SkinPrefabHandler Handler;
        public IntPtr HandlerPtr, InstancePtr;
        public bool Local; // the player's own phone in the world (not a menu phone)
        public CapeLayout Layout;
        public bool HasRenderers; // a back item is on, painted or not
        public readonly List<Part> Parts = new();
    }

    static readonly List<Slot> slots = new();
    // original material → our copy of it (shared by the LODs that share the material)
    static readonly Dictionary<IntPtr, (Material Original, Material Copy)> copies = new();
    static readonly Dictionary<IntPtr, IntPtr> copyTexture = new(); // copy → texture it shows
    static int mainTex = -1, emissiveTex;
    static float nextTick, nextFind;

    // ---------------------------------------------------------------- what the menu shows

    /// The family of the cape your own Flippy wears (or the customization phone's), null if it can't be painted.
    public static CapeLayout WornLayout { get; private set; }
    /// Something is on the back (a cape we can't paint shows as WornLayout null).
    public static bool WearingBackItem { get; private set; }
    /// The custom texture is on right now.
    public static bool Showing { get; private set; }

    // ---------------------------------------------------------------- every frame

    /// Main thread, every frame (a pointer compare per cape; real work twice a second).
    public static void Tick()
    {
        Watch();
        float now = Time.unscaledTime;
        if (now < nextTick) return;
        nextTick = now + 0.5f;
        bool deep = now >= nextFind;
        // the menu phones are searched for (a scene-wide search) every 3 s in menus, every 15 s while playing
        if (deep) nextFind = now + (PlayerRef.LocalPlayer == null || Menu.IsOpen ? 3f : 15f);
        Discover(deep);
        Refresh();
    }

    /// A cape the game just put on (or took off) a phone already found: painted at once, not at the next check.
    static void Watch()
    {
        bool changed = false;
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            try
            {
                if (!Alive(slot.Handler)) continue; // dropped by the next Refresh
                var instance = slot.Handler.instanceRef;
                var ptr = Alive(instance) ? instance.Pointer : IntPtr.Zero;
                if (ptr == slot.InstancePtr) continue;
                RefreshSlot(slot);
                changed = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("cape check failed: " + e.Message);
                slots.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) Summarize();
    }

    /// Re-applies at once (after the picture, the equipped cape or the on/off switch changed).
    public static void Refresh()
    {
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            if (!Alive(slot.Handler))
            {
                Restore(slot);
                slots.RemoveAt(i);
                continue;
            }
            try { RefreshSlot(slot); }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("cape check failed: " + e.Message);
                slots.RemoveAt(i);
            }
        }
        Summarize();
    }

    /// Puts every cape back to the game's own material (the plugin is being turned off).
    public static void RestoreAll()
    {
        foreach (var slot in slots) Restore(slot);
        Summarize();
    }

    // ---------------------------------------------------------------- finding your capes

    static void Discover(bool deep)
    {
        try
        {
            var local = PlayerRef.LocalPlayer;
            if (local != null)
            {
                var skins = local.playerSkins;
                if (skins != null) AddFrom(skins.handlers, true);
            }
            if (!deep) return;
            // the customization screen's phone and the menu's phone: few objects, searched every 3 s only
            foreach (var dummy in Object.FindObjectsOfType<PlayerVisualsDummy>())
                if (dummy != null) AddFrom(dummy.handlers, false);
            foreach (var shown in Object.FindObjectsOfType<EquippedPlayerSkins>())
                if (shown != null) AddFrom(shown.handlers, false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("looking for your cape failed: " + e.Message);
        }
    }

    static void AddFrom(Il2CppSystem.Collections.Generic.List<SkinPrefabHandler> handlers, bool local)
    {
        if (handlers == null) return;
        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            if (h != null && h.category == SkinCategory.AccessoryBack) Track(h, local);
        }
    }

    static Slot Track(SkinPrefabHandler handler, bool local)
    {
        var ptr = handler.Pointer;
        foreach (var s in slots)
        {
            if (s.HandlerPtr != ptr) continue;
            s.Local |= local;
            return s;
        }
        var slot = new Slot { Handler = handler, HandlerPtr = ptr, Local = local };
        slots.Add(slot);
        return slot;
    }

    static bool Alive(Object o) => o != null && !o.WasCollected;

    // ---------------------------------------------------------------- painting

    static void RefreshSlot(Slot slot)
    {
        var instance = slot.Handler.instanceRef;
        var ptr = Alive(instance) ? instance.Pointer : IntPtr.Zero;
        if (ptr != slot.InstancePtr)
        {
            Restore(slot);
            slot.Parts.Clear();
            slot.Layout = null;
            slot.HasRenderers = false;
            slot.InstancePtr = ptr;
            if (ptr != IntPtr.Zero) Scan(slot, instance);
        }
        var want = slot.Layout != null ? CapeTextures.For(slot.Layout) : null;
        if (want == null) Restore(slot);
        else Paint(slot, want);
    }

    /// The cape's own renderers (never the black outline shells) and its family, from the mesh.
    static void Scan(Slot slot, SkinInstanceRef instance)
    {
        Ids();
        PurgeCopies();
        foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            slot.HasRenderers = true;
            Mesh mesh = null;
            var skinned = r.TryCast<SkinnedMeshRenderer>();
            if (skinned != null) mesh = skinned.sharedMesh;
            else
            {
                var filter = r.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            var layout = mesh != null ? CapeLayout.ForMesh(mesh.name) : null;
            if (layout == null) continue;
            var material = r.sharedMaterial;
            if (material == null || !material.HasProperty(mainTex)) continue;
            slot.Layout = layout;
            slot.Parts.Add(new Part { Renderer = r, Original = material });
        }
        if (slot.Local || WornLayout == null)
            Plugin.Logger.LogInfo(slot.Layout != null
                ? $"cape found on {(slot.Local ? "your Flippy" : "a menu phone")}: {slot.Layout.Name} family, {slot.Parts.Count} renderers"
                : $"back item on {(slot.Local ? "your Flippy" : "a menu phone")} isn't a cape Cape Editor can paint");
    }

    static void Paint(Slot slot, Texture2D tex)
    {
        foreach (var part in slot.Parts)
        {
            var r = part.Renderer;
            if (!Alive(r) || !Alive(part.Original)) continue;
            var current = r.sharedMaterial;
            if (current == null) continue;
            if (part.Copy == null || !Alive(part.Copy)) part.Copy = CopyOf(part.Original);
            if (current.Pointer == part.Original.Pointer) r.sharedMaterial = part.Copy;
            else if (current.Pointer != part.Copy.Pointer) continue; // the game put an effect material on: leave it be
            if (!copyTexture.TryGetValue(part.Copy.Pointer, out var shown) || shown != tex.Pointer)
            {
                part.Copy.SetTexture(mainTex, tex);
                copyTexture[part.Copy.Pointer] = tex.Pointer;
            }
        }
    }

    static void Restore(Slot slot)
    {
        foreach (var part in slot.Parts)
        {
            if (part.Copy == null || !Alive(part.Renderer) || !Alive(part.Original)) continue;
            var current = part.Renderer.sharedMaterial;
            if (current != null && current.Pointer == part.Copy.Pointer) part.Renderer.sharedMaterial = part.Original;
        }
    }

    /// Our copy of a cape material: the same shader, keywords and maps (normal, MSAO), with the emission map off so
    /// the old design can't glow through the picture.
    static Material CopyOf(Material original)
    {
        if (copies.TryGetValue(original.Pointer, out var entry) && Alive(entry.Copy)) return entry.Copy;
        var copy = new Material(original) { name = original.name + " (Cape Editor)", hideFlags = HideFlags.HideAndDontSave };
        if (copy.HasProperty(emissiveTex)) copy.SetTexture(emissiveTex, Texture2D.blackTexture);
        copies[original.Pointer] = (original, copy);
        return copy;
    }

    /// Copies whose game material is gone (its cape was unloaded).
    static void PurgeCopies()
    {
        List<IntPtr> dead = null;
        foreach (var pair in copies)
            if (!Alive(pair.Value.Original)) (dead ??= new List<IntPtr>()).Add(pair.Key);
        if (dead == null) return;
        foreach (var key in dead)
        {
            var copy = copies[key].Copy;
            if (Alive(copy))
            {
                copyTexture.Remove(copy.Pointer);
                Object.Destroy(copy);
            }
            copies.Remove(key);
        }
    }

    static void Ids()
    {
        if (mainTex >= 0) return;
        mainTex = Shader.PropertyToID("_MainTex");
        emissiveTex = Shader.PropertyToID("_EmissiveTex");
    }

    static void Summarize()
    {
        Slot best = null;
        foreach (var s in slots)
            if (s.InstancePtr != IntPtr.Zero && (best == null || s.Local && !best.Local)) best = s;
        WornLayout = best?.Layout;
        WearingBackItem = best != null && best.HasRenderers;
        bool showing = false;
        if (best != null)
            foreach (var part in best.Parts)
                if (part.Copy != null && Alive(part.Renderer) && part.Renderer.sharedMaterial?.Pointer == part.Copy.Pointer) { showing = true; break; }
        Showing = showing;
    }

    /// The skin key on your own Flippy's back ("" when there's none), for the menu.
    public static string WornKey()
    {
        try
        {
            var skins = PlayerRef.LocalPlayer?.playerSkins;
            if (skins == null) return "";
            var handlers = skins.handlers;
            for (int i = 0; i < handlers.Count; i++)
                if (handlers[i] != null && handlers[i].category == SkinCategory.AccessoryBack) return handlers[i].CurrentSkinKey ?? "";
        }
        catch { /* the player is being rebuilt */ }
        return "";
    }
}
