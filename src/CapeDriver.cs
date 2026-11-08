using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CapeEditor;

/// Cape Editor's one MonoBehaviour. Each frame: hand finished picture work to Unity, check the capes (a pointer compare each, a full check twice a second),
/// read the two hotkeys and run the menu while it's open. With the menu closed that's a few microseconds.
public class CapeDriver : MonoBehaviour
{
    public CapeDriver(IntPtr ptr) : base(ptr) { }

    bool built;
    float lastError;

    void Update()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime - lastError > 5f) Plugin.Logger.LogError("Cape Editor: " + e);
            lastError = Time.unscaledTime;
        }
    }

    [HideFromIl2Cpp]
    void Tick()
    {
        Worker.Drain();
        CapeSwap.Tick();
        SyncLink.Tick();
        Ui.Tick();
        if (!built && Time.unscaledTime > 4f)
        {
            // the menu is built ahead of time, so the first press of O opens it at once (kit trap 4)
            built = true;
            Menu.Build();
            Menu.LoadThumbs();
        }

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        GameControls.Tick(keyboard, mouse);
        Menu.TakePick();
        if (keyboard != null && !FilePicker.Busy)
        {
            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            if (shift && keyboard[Plugin.ToggleKey.Value].wasPressedThisFrame) Menu.ToggleEnabled();
            else if (!shift && keyboard[Plugin.MenuKey.Value].wasPressedThisFrame) Menu.Toggle();
            else if (Menu.IsOpen && keyboard.escapeKey.wasPressedThisFrame) Menu.Back();
        }
        if (Menu.IsOpen && !FilePicker.Busy) Menu.Tick(mouse);
    }

    void OnDestroy()
    {
        try { CapeSwap.RestoreAll(); } catch { /* shutting down */ }
    }
}
