using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CapeEditor;

/// One custom cape: a copy of the player's picture and how it sits on the cape. Lives in its own folder,
/// BepInEx\config\CapeEditor\capes\<id>\ (source picture, design.txt, thumb.png and cached cape textures).
internal sealed class Design
{
    public string Id, Folder, Name, SourceFile;
    /// The picture's full size, already turned the way the camera held it.
    public int SourceWidth, SourceHeight;
    /// The picture's average colour (the "Auto" edge colour).
    public uint AutoColor;
    public Edit Edit = Edit.Default;
    /// Main thread only: the library card's picture.
    public Texture2D Thumb;

    public string SourcePath => Path.Combine(Folder, SourceFile);
    public string ThumbPath => Path.Combine(Folder, "thumb.png");
    public string CachePath(in Edit e, CapeLayout layout, int size) =>
        Path.Combine(Folder, $"cape-{layout.Id}-{e.Hash(SourceWidth, SourceHeight, size)}.png");
}

/// The player's custom capes on disk.
internal static class Library
{
    public static readonly List<Design> Designs = new();
    public static string Root => Path.Combine(Plugin.Folder, "capes");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Design Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var d in Designs)
            if (d.Id == id) return d;
        return null;
    }

    /// Main thread, at start: reads every cape's design.txt (small text files), oldest first.
    public static void Load()
    {
        Designs.Clear();
        try
        {
            Directory.CreateDirectory(Root);
            var dirs = new DirectoryInfo(Root).GetDirectories();
            Array.Sort(dirs, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
            foreach (var dir in dirs)
            {
                var d = Read(dir.FullName);
                if (d != null) Designs.Add(d);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("couldn't read the cape library: " + e.Message);
        }
    }

    static Design Read(string folder)
    {
        var file = Path.Combine(folder, "design.txt");
        if (!File.Exists(file)) return null;
        var d = new Design { Id = Path.GetFileName(folder), Folder = folder, Name = Path.GetFileName(folder) };
        var e = Edit.Default;
        try
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "name": d.Name = value; break;
                    case "source": d.SourceFile = value; break;
                    case "size":
                        var wh = value.Split('x');
                        if (wh.Length == 2) { int.TryParse(wh[0], NumberStyles.Integer, Inv, out d.SourceWidth); int.TryParse(wh[1], NumberStyles.Integer, Inv, out d.SourceHeight); }
                        break;
                    case "auto": uint.TryParse(value, NumberStyles.HexNumber, Inv, out d.AutoColor); break;
                    case "zoom": e.Zoom = F(value, 1f); break;
                    case "center":
                        var xy = value.Split(',');
                        if (xy.Length == 2) { e.CenterX = F(xy[0], 0.5f); e.CenterY = F(xy[1], 0.5f); }
                        break;
                    case "turns": int.TryParse(value, NumberStyles.Integer, Inv, out e.Turns); e.Turns &= 3; break;
                    case "mirror": e.Mirror = value == "true"; break;
                    case "inside": e.PlainInside = value == "plain"; break;
                    case "cutout": e.CutOut = value == "true"; break;
                    case "edge": e.EdgeColor = value == "auto" ? 0u : uint.TryParse(value, NumberStyles.HexNumber, Inv, out var c) ? c | 0xFF000000 : 0u; break;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"couldn't read {file}: {ex.Message}");
            return null;
        }
        if (string.IsNullOrEmpty(d.SourceFile) || d.SourceWidth <= 0 || d.SourceHeight <= 0) return null;
        d.Edit = e;
        return d;
    }

    static float F(string s, float fallback) => float.TryParse(s, NumberStyles.Float, Inv, out var v) && float.IsFinite(v) ? v : fallback;

    /// Writes design.txt (any thread: it only reads the design's plain fields).
    public static void Save(Design d)
    {
        var e = d.Edit;
        var sb = new StringBuilder();
        sb.Append("# Cape Editor design (made in game; safe to delete the whole folder)\n");
        sb.Append("name=").Append(d.Name.Replace('\n', ' ')).Append('\n');
        sb.Append("source=").Append(d.SourceFile).Append('\n');
        sb.Append("size=").Append(d.SourceWidth.ToString(Inv)).Append('x').Append(d.SourceHeight.ToString(Inv)).Append('\n');
        sb.Append("auto=").Append(d.AutoColor.ToString("X8", Inv)).Append('\n');
        sb.Append("zoom=").Append(e.Zoom.ToString("R", Inv)).Append('\n');
        sb.Append("center=").Append(e.CenterX.ToString("R", Inv)).Append(',').Append(e.CenterY.ToString("R", Inv)).Append('\n');
        sb.Append("turns=").Append(e.Turns.ToString(Inv)).Append('\n');
        sb.Append("mirror=").Append(e.Mirror ? "true" : "false").Append('\n');
        sb.Append("inside=").Append(e.PlainInside ? "plain" : "same").Append('\n');
        sb.Append("cutout=").Append(e.CutOut ? "true" : "false").Append('\n');
        sb.Append("edge=").Append(e.EdgeColor == 0 ? "auto" : e.EdgeColor.ToString("X8", Inv)).Append('\n');
        var file = Path.Combine(d.Folder, "design.txt");
        File.WriteAllText(file + ".tmp", sb.ToString());
        File.Move(file + ".tmp", file, true);
    }

    /// Worker thread: copies the picked picture into a new cape folder and checks Windows can read it. Returns the
    /// new design (not in the list yet: the main thread adds it), or throws with a message for the player.
    public static Design Import(string picked, int maxSide)
    {
        var info = new FileInfo(picked);
        if (!info.Exists) throw new FileNotFoundException("That file isn't there any more");
        if (info.Length > 200L * 1024 * 1024) throw new InvalidDataException("That picture is too big (over 200 MB)");
        string id = "cape-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        string folder = Path.Combine(Root, id);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(Root, id + "-" + n);
        id = Path.GetFileName(folder);
        Directory.CreateDirectory(folder);
        try
        {
            string ext = info.Extension.ToLowerInvariant();
            if (ext.Length == 0 || ext.Length > 6) ext = ".img";
            var d = new Design { Id = id, Folder = folder, SourceFile = "source" + ext, Name = NameFrom(info.Name) };
            File.Copy(picked, d.SourcePath);
            using (var bitmap = Gdip.Load(d.SourcePath, maxSide, out d.SourceWidth, out d.SourceHeight))
                d.AutoColor = Gdip.Average(bitmap);
            Save(d);
            return d;
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { /* nothing useful to do */ }
            throw;
        }
    }

    static string NameFrom(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();
        if (name.Length == 0) name = "My cape";
        return name.Length > 22 ? name.Substring(0, 21) + "…" : name;
    }

    /// Main thread: forgets a design and removes its folder (on the worker, after it lets go of the picture).
    public static void Delete(Design d)
    {
        Designs.Remove(d);
        if (d.Thumb != null) UnityEngine.Object.Destroy(d.Thumb);
        d.Thumb = null;
        string folder = d.Folder, source = d.SourcePath;
        Worker.Run(() =>
        {
            Worker.Forget(source);
            try { Directory.Delete(folder, true); }
            catch (Exception e) { Worker.Log($"couldn't delete {folder}: {e.Message}"); }
        });
    }
}
