using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CapeEditor;

/// One background thread for all the picture work (reading files, cropping, resizing, mip levels, writing PNGs), so
/// the game's main thread only ever uploads finished pixels. Jobs run one after another; RunLatest jobs (the editor's
/// live preview) only run their newest request. Results come back to the main thread through Post → Drain.
internal static class Worker
{
    static readonly BlockingCollection<Action> jobs = new();
    static readonly ConcurrentQueue<Action> results = new();
    static readonly object gate = new();
    static Action latest;
    static bool latestQueued;
    static Thread thread;

    // worker thread only: decoded pictures, newest last
    static readonly List<(string Path, Gdip.Bitmap Bitmap)> sources = new();

    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(Loop) { IsBackground = true, Name = "Cape Editor pictures", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    static void Loop()
    {
        try
        {
            Gdip.Start();
        }
        catch (Exception e)
        {
            Log("Windows' picture library (GDI+) didn't start: " + e.Message, true);
        }
        foreach (var job in jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception e) { Log("picture job failed: " + e, true); }
        }
    }

    public static void Run(Action job) => jobs.Add(job);

    /// Only the newest job given here runs (older ones that haven't started are dropped).
    public static void RunLatest(Action job)
    {
        lock (gate)
        {
            latest = job;
            if (latestQueued) return;
            latestQueued = true;
        }
        jobs.Add(() =>
        {
            Action next;
            lock (gate)
            {
                next = latest;
                latest = null;
                latestQueued = false;
            }
            next?.Invoke();
        });
    }

    /// From the worker: run this on the main thread.
    public static void Post(Action action) => results.Enqueue(action);

    /// Main thread, every frame: at most two results a frame, so a burst of uploads never stacks up in one frame.
    public static void Drain()
    {
        for (int i = 0; i < 2 && results.TryDequeue(out var action); i++)
        {
            try { action(); }
            catch (Exception e) { Plugin.Logger.LogError("Cape Editor: " + e); }
        }
    }

    public static void Log(string message, bool error = false) =>
        Post(() => { if (error) Plugin.Logger.LogError(message); else Plugin.Logger.LogInfo(message); });

    /// Worker thread: the design's picture, read once and kept while it's being used (two at most).
    public static Gdip.Bitmap Source(Design d, int maxSide)
    {
        string path = d.SourcePath;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Path != path) continue;
            var hit = sources[i];
            sources.RemoveAt(i);
            sources.Add(hit);
            return hit.Bitmap;
        }
        var bitmap = Gdip.Load(path, maxSide, out _, out _);
        sources.Add((path, bitmap));
        while (sources.Count > 2)
        {
            sources[0].Bitmap.Dispose();
            sources.RemoveAt(0);
        }
        return bitmap;
    }

    /// Worker thread: lets go of a design's picture (it's being deleted or the editor closed).
    public static void Forget(string path)
    {
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(sources[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            sources[i].Bitmap.Dispose();
            sources.RemoveAt(i);
        }
    }

    /// Worker thread: the finished cape texture for a design on one cape family. Read from the cache PNG when it's
    /// there (fast), otherwise painted from the picture and cached for next time, with a fresh library thumbnail.
    public static TexData CapeTexture(Design d, Edit edit, CapeLayout layout, int size)
    {
        string cache = d.CachePath(edit, layout, size);
        if (File.Exists(cache))
        {
            try
            {
                var px = Gdip.ReadExact(cache, out int w, out int h);
                if (w == size && h == size) return Composer.ForUnity(px, size, size, true, true);
            }
            catch (Exception e)
            {
                Log($"cache {Path.GetFileName(cache)} unreadable, painting it again ({e.Message})");
            }
        }
        var src = Source(d, size * 2);
        using var cape = Composer.Cape(src, edit, d.AutoColor, layout, size);
        var pixels = Gdip.Pixels(cape);
        try
        {
            foreach (var old in Directory.GetFiles(d.Folder, $"cape-{layout.Id}-*.png")) File.Delete(old);
            Gdip.SavePng(cape, cache);
        }
        catch (Exception e)
        {
            Log("couldn't cache the cape texture: " + e.Message);
        }
        return Composer.ForUnity(pixels, size, size, true, true);
    }

    /// Worker thread: writes the library card picture and returns its pixels for the menu.
    public static TexData MakeThumb(Design d, Edit edit, CapeLayout layout, int maxSide)
    {
        var src = Source(d, maxSide);
        using var thumb = Composer.Thumb(src, edit, d.AutoColor, layout, ThumbWidth);
        try { Gdip.SavePng(thumb, d.ThumbPath); }
        catch (Exception e) { Log("couldn't save the thumbnail: " + e.Message); }
        return Composer.ForUnity(Gdip.Pixels(thumb), thumb.Width, thumb.Height, false, false);
    }

    public const int ThumbWidth = 144;
}
