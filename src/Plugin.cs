using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace CapeEditor;

/// Cape Editor for Flipping is Hard: put your own picture on a base-game cape. O opens the menu (pick and put on a
/// cape, import a picture, crop it in the editor, wear it); Shift+O turns the custom cape on and off.
/// Only the cape's texture changes: its mesh, bones and Magica Cloth physics are the game's own, so it swings and
/// collides exactly like the vanilla cape. Client-side only: other players see your normal cape.
[BepInPlugin(Guid, "Cape Editor", Version)]
[BepInDependency("com.fabuleo8796.fihsync", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.capeeditor", Version = "1.1.0";
    internal static ManualLogSource Logger;
    internal static ConfigEntry<Key> MenuKey, ToggleKey;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<string> Equipped, LastFolder;
    internal static ConfigEntry<int> TextureSize;

    public override void Load()
    {
        Logger = Log;
        MenuKey = Config.Bind("Keys", "Menu", Key.O, "Opens and closes the Cape Editor menu.");
        ToggleKey = Config.Bind("Keys", "Toggle", Key.O, "Shift + this key turns your custom cape on or off.");
        Enabled = Config.Bind("Cape", "Enabled", true, "Show your custom cape (the menu's switch, or Shift + Toggle key).");
        Equipped = Config.Bind("Cape", "Equipped", "", "The custom cape you wear: its folder in BepInEx\\config\\CapeEditor\\capes. Empty = the game's own cape.");
        TextureSize = Config.Bind("Cape", "TextureSize", 1024, new ConfigDescription(
            "Size of the cape texture. 1024 is sharp (the game's own capes are 512); 2048 is sharper and uses 4x the memory.",
            new AcceptableValueList<int>(512, 1024, 2048)));
        LastFolder = Config.Bind("Cape", "LastFolder", "", "Where the picture picker opens (the folder of the last picture you imported).");

        Library.Load();
        Worker.Start();
        ClassInjector.RegisterTypeInIl2Cpp<CapeDriver>();
        var host = new GameObject("CapeEditor");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<CapeDriver>();
        SyncLink.Start();
        // No Harmony hook on SkinPrefabHandler.OnSkinApplied: its base body is empty, and the game build folds every
        // empty method into one shared function (16768 callers), so a hook there runs for thousands of unrelated
        // methods and crashed the game. CapeSwap watches the capes each frame instead.
        Log.LogInfo($"Cape Editor {Version} loaded: {MenuKey.Value} opens the menu, Shift+{ToggleKey.Value} turns the custom cape on/off. " +
                    $"{Library.Designs.Count} custom cape(s) in {Library.Root}");
    }

    static string folder;

    /// BepInEx\config\CapeEditor. Up to 1.0.1 it was config\ClaudeCapeEditor: that folder moves over once,
    /// so saved capes stay.
    internal static string Folder => folder ??= MoveOldFolder();

    static string MoveOldFolder()
    {
        var now = Path.Combine(Paths.ConfigPath, "CapeEditor");
        var old = Path.Combine(Paths.ConfigPath, "ClaudeCapeEditor");
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"couldn't move {old} to {now}: {e.Message}");
        }
        return now;
    }

    /// Pictures are kept at most this big while editing (twice the texture: room to zoom in).
    internal static int SourceMax => Math.Clamp(TextureSize.Value * 2, 1024, 4096);
}
