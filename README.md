# Cape Editor for Flipping is Hard

Put your own picture on a base-game cape, right inside the game. Pick a picture from your PC, crop it in the in-game
editor, and wear it. The cape keeps the game's own cloth physics, so it swings and collides exactly like the vanilla
one.

A BepInEx 6 (IL2CPP) plugin: one DLL, `CapeEditor.dll`.

## Install

1. Flipping is Hard needs **BepInEx 6 IL2CPP** (bleeding edge, build 755 or newer). Start the game once with it, so it
   makes `BepInEx\interop`.
2. Put `CapeEditor.dll` in `<game>\BepInEx\plugins\CapeEditor\`. Any folder under `plugins` works.
3. Start the game. `BepInEx\LogOutput.log` should say `Cape Editor 1.0.2 loaded`.

## Use it

| Key | What it does |
|---|---|
| **O** | Opens and closes the Cape Editor menu |
| **Shift + O** | Turns your custom cape on or off, anywhere |
| **Esc** | In the editor: back. In the menu: close |

Both keys can be changed in `BepInEx\config\com.fabuleo8796.capeeditor.cfg`.

**The menu (left of the screen, so Flippy stays in view)**

- **Base cape**: the game's own capes this mod can paint, with the game's icons. Click one to put it on. It goes
  through the game's own equip, so only capes you've unlocked work.
- **My capes**: your custom capes.
  - **Import picture** opens Windows' file picker. PNG, JPG, BMP, GIF and TIFF all work, and phone photos come in the
    right way up.
  - Select a card, then **Wear this cape** / **Take it off**, **Edit** or **Delete** (click twice to confirm).
- **Custom cape** switch (top right): on or off, the same as Shift + O.

**The editor**

- The crop frame has the shape of the cape. Your picture is **auto-cropped** to fill it (the biggest frame of that
  shape) and resized to the cape texture, so any size or shape of picture works.
- **Drag** the frame to choose the part you want, and **scroll** (or use the slider) to zoom.
- **Fill** is the auto-crop. **Fit** shows the whole picture with the edge colour around it. **Turn 90°**, **Mirror**
  and **Reset** do what they say.
- **Inside of the cape**: the same picture, or a plain colour.
- **See-through parts**: transparent areas of a PNG show the edge colour, or **cut holes** in the cape (the cape's
  shader is alpha-tested).
- **Edge colour**: Auto (the picture's average colour) or one of 8 swatches. It colours the cape's thin edges and any
  space around the picture.
- Every change shows **live on your Flippy's cape** while you edit. **Save & wear** keeps it; Cancel goes back.

## What it paints

The base capes come in three shape families. Each family shares one mesh, so it shares one texture layout:

| Family | Capes | Crop shape (w:h) |
|---|---|---|
| Classic | Red, Black, Polkadot, Plix | 0.69 |
| Wizard | Wizard | 0.91 |
| Tattered | Webby, DanBay, RossBurger, Sasavot | 0.68 |

The Gold cape (a frilled mesh), wings, backpacks and other back items aren't flat capes, so the menu doesn't offer
them.

Other players see your normal cape, unless you both run [FIH Sync](https://github.com/Fabuleo8796/FIHSync) (0.1.2+): then modded players see each other's custom
capes (painted on the base cape each of you wears).

## How it works (and why the physics can't break)

- **The swap.** A cape is a `SkinnedMeshRenderer` (LOD0, LOD1, LOD2) whose bones are moved by **Magica Cloth 2**. Cape
  Editor gives each of *your* cape renderers a copy of its own material, with your picture as `_MainTex`. Nothing else
  is touched: not the mesh, not the bones, not the Magica Cloth component and not the colliders. So the cloth
  simulation is exactly the game's. The copy keeps the cape's shader, keywords, normal map and MSAO map, so folds and
  shading stay. Its emission map is switched off, so an old design can't glow through your picture. The game's fade
  and x-ray keep working, because they use property blocks, which apply to any material.
- **Finding your cape.** Every frame, each cape already found is checked for a new cape object (one pointer compare),
  so a cape the game swaps in is painted the frame it appears. A check twice a second finds new phones, like after a
  respawn or in a new scene. It covers your Flippy, the customization-screen phone and the menu phone. Other players'
  capes are never touched. There's no Harmony hook: the game's `SkinPrefabHandler.OnSkinApplied` is an empty method
  that shares its code with every other empty method in the game, so hooking it crashed the game (fixed in 1.0.1).
- **The pictures.** All picture work runs on one background thread, using Windows' own picture library (GDI+):
  reading the file, turning phone photos upright (EXIF), auto-cropping, high-quality bicubic resizing, building the
  sRGB-correct mip levels and writing PNGs. The game's main thread only uploads finished pixels, about 1–2 ms once per
  change.
- **While you edit:** a 512 px draft about 12 times a second while something moves, then the full 1024 px texture
  0.25 s after you stop.
- **With the menu closed:** a few microseconds a frame, plus the twice-a-second check.
- **Texture layout.** Measured from the game's meshes (UV islands against vertex positions and normals), then checked
  by rendering the real cape meshes with a test picture. On Classic and Wizard capes the panels are stored rotated
  180°. The mod handles that, so your picture is upright and not mirrored, outside and inside.
- **Texture size.** 1024×1024 by default (the game's own capes are 512×512). `TextureSize = 2048` in the cfg makes it
  sharper and uses 4× the memory.
- **Saved capes.** `BepInEx\config\CapeEditor\capes\<id>\` holds a copy of your picture, `design.txt` (your
  crop and options), a thumbnail, and the finished cape texture as a cache, so it loads fast next time. Deleting the
  folder removes that cape.

## Build it yourself

You need the game with BepInEx 6 IL2CPP (run once, for `BepInEx\interop`).

**Without the .NET SDK** (uses the C# compiler that ships with any recent Unity editor):

```
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -GameDir "D:\Games\Flipping is Hard" -Install
```

The DLL lands in `build\CapeEditor.dll`. `-Install` also copies it to `BepInEx\plugins\CapeEditor\`.
Close the game first, because it locks plugin DLLs.

**With the .NET SDK (6 or newer):**

```
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Flipping is Hard"
```

The project file is written for this, but only `build.ps1` has been run on the developer's PC (it has no .NET SDK).

**References:**
- BepInEx's `core` DLLs: BepInEx.Core, BepInEx.Unity.IL2CPP, BepInEx.Unity.Common, 0Harmony,
  Il2CppInterop.Runtime and Il2CppInterop.Common.
- Every DLL in `BepInEx\interop` except `UnityEngine.dll`, a 3 KB facade that makes Unity types ambiguous.
- C# 10, unsafe code on.

## Source files

| File | What's in it |
|---|---|
| `src/Plugin.cs` | Entry point and config |
| `src/CapeDriver.cs` | The one MonoBehaviour: hotkeys, menu, results from the worker |
| `src/CapeSwap.cs` | Finds your capes and swaps the material copies (never the physics) |
| `src/CapeLayout.cs` | The three cape families: panel positions and orientation on the texture |
| `src/CapeTextures.cs` | Builds, caches and uploads the cape textures; the live draft |
| `src/Composer.cs` | Crop maths (Fill/Fit/zoom/turn/mirror) and painting the cape texture, mip levels |
| `src/Gdip.cs` | Windows GDI+ through its C API: load (with EXIF), resize, read pixels, save PNG |
| `src/Worker.cs` | The background thread for all picture work |
| `src/Library.cs` | Your saved capes on disk |
| `src/Menu.cs` | The in-game menu and editor |
| `src/Ui.cs` | UI building blocks (Geist font, rounded shapes, buttons, sliders, toasts) |
| `src/FilePicker.cs` | Windows' Open window, on its own thread |
| `src/GameControls.cs` | Pauses the game's controls while the menu is open |
| `tests/ComposeTest` | Offline test of the picture pipeline (no game needed) |

The Geist font is by Vercel, under the SIL Open Font License (`src/Resources/OFL-Geist.txt`).
