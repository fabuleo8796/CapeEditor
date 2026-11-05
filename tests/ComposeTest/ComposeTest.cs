// Offline test of Cape Editor's picture pipeline (no game needed): makes a test picture with a big "F" (it shows any
// wrong turn or mirror), loads it the way the mod does, builds every cape layout's texture, thumbnail and editor view,
// and checks the crop maths. Output goes to the folder given as the first argument.
using System;
using System.IO;
using CapeEditor;

static class ComposeTest
{
    static int failures;

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(dir);
        Gdip.Start();

        // 1200×1600 portrait: blue with a white F, a red band on top, a green band at the bottom, yellow down the left
        int w = 1200, h = 1600;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) c = (40, 70, 200);
                if (y < 160) c = (220, 30, 30);
                else if (y >= h - 160) c = (30, 190, 60);
                else if (x < 120) c = (240, 210, 40);
                bool stem = x >= 380 && x < 540 && y >= 360 && y < 1240;
                bool top = x >= 380 && x < 860 && y >= 360 && y < 500;
                bool mid = x >= 380 && x < 740 && y >= 740 && y < 860;
                if (stem || top || mid) c = (255, 255, 255);
                int i = (y * w + x) * 4;
                px[i] = c.b; px[i + 1] = c.g; px[i + 2] = c.r; px[i + 3] = 255;
            }
        using (var made = Gdip.FromPixels(px, w, h)) Gdip.SavePng(made, Path.Combine(dir, "test_picture.png"));

        using var src = Gdip.Load(Path.Combine(dir, "test_picture.png"), 2048, out int fw0, out int fh0);
        Expect(fw0 == w && fh0 == h && src.Width == w && src.Height == h, $"load keeps size ({src.Width}x{src.Height})");
        using (var small = Gdip.Load(Path.Combine(dir, "test_picture.png"), 400, out _, out _))
            Expect(small.Width == 300 && small.Height == 400, $"load shrinks to 400 ({small.Width}x{small.Height})");
        uint avg = Gdip.Average(src);
        Console.WriteLine($"average colour {avg:X8}");

        // crop maths
        var e = Edit.Default;
        Composer.ViewSize(w, h, 0, out float vw, out float vh);
        Composer.Frame(vw, vh, 0.69f, ref e, out float fx, out float fy, out float fwid, out float fhei);
        Expect(Math.Abs(fwid - 1104f) < 0.5f && Math.Abs(fhei - 1600f) < 0.5f, $"fill frame is the biggest 0.69 frame ({fwid}x{fhei})");
        var fit = Edit.Default; fit.Zoom = 0.01f;
        Composer.Frame(vw, vh, 0.69f, ref fit, out _, out _, out float fitW, out float fitH);
        Expect(fitW >= vw - 0.5f && fitH >= vh - 0.5f && Math.Abs(fitW - 1200f) < 0.5f, $"fit frame holds the whole picture ({fitW}x{fitH}, zoom {fit.Zoom})");
        var off = Edit.Default; off.Zoom = 2f; off.CenterX = 0f; off.CenterY = 1f;
        Composer.Frame(vw, vh, 0.69f, ref off, out float ox, out float oy, out float ow, out float oh);
        Expect(ox == 0f && Math.Abs(oy + oh - vh) < 0.5f, $"frame stays inside the picture ({ox},{oy} {ow}x{oh})");
        for (int t = 0; t < 4; t++)
            foreach (bool m in new[] { false, true })
            {
                Composer.ToView(100, 300, w, h, t, m, out float ax, out float ay);
                Composer.ToSource(ax, ay, w, h, t, m, out float bx, out float by);
                Expect(Math.Abs(bx - 100) < 0.01f && Math.Abs(by - 300) < 0.01f, $"turn {t} mirror {m} round trip");
            }

        foreach (var layout in CapeLayout.All)
        {
            foreach (var (name, edit) in new[]
                     {
                         ("fill", Edit.Default),
                         ("fit", new Edit { Zoom = 0.01f, CenterX = 0.5f, CenterY = 0.5f }),
                         ("turned_plain", new Edit { Zoom = 1.4f, CenterX = 0.3f, CenterY = 0.5f, Turns = 1, PlainInside = true, EdgeColor = 0xFFFF00FF }),
                     })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using var cape = Composer.Cape(src, edit, avg, layout, 1024);
                var bytes = Gdip.Pixels(cape);
                var tex = Composer.ForUnity(bytes, 1024, 1024, true, true);
                long ms = watch.ElapsedMilliseconds;
                Gdip.SavePng(cape, Path.Combine(dir, $"cape_{layout.Id}_{name}.png"));
                Expect(tex.Mips == 11 && tex.Data.Length == 1398101 * 4, $"{layout.Id} {name}: mips {tex.Mips}, {tex.Data.Length} bytes, {ms} ms");
                // cape textures are opaque unless cut out
                bool opaque = true;
                for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 255) { opaque = false; break; }
                Expect(opaque, $"{layout.Id} {name}: opaque");
                using var thumb = Composer.Thumb(src, edit, avg, layout, 120);
                Gdip.SavePng(thumb, Path.Combine(dir, $"thumb_{layout.Id}_{name}.png"));
            }
        }

        // cut-out: a picture with a clear hole makes a clear hole
        var holed = (byte[])px.Clone();
        for (int y = 900; y < 1100; y++) for (int x = 700; x < 1000; x++) holed[(y * w + x) * 4 + 3] = 0;
        using (var hb = Gdip.FromPixels(holed, w, h))
        {
            var cut = Edit.Default; cut.CutOut = true;
            using var cape = Composer.Cape(hb, cut, avg, CapeLayout.Classic, 1024);
            Gdip.SavePng(cape, Path.Combine(dir, "cape_classic_cutout.png"));
            var b = Gdip.Pixels(cape);
            int clear = 0;
            for (int i = 3; i < b.Length; i += 4) if (b[i] < 128) clear++;
            Expect(clear > 1000 && clear < 1024 * 1024 / 2, $"cut-out leaves holes ({clear} clear pixels)");
        }

        using (var view = Composer.View(src, 1, true, 512))
        {
            Expect(view.Width == 512 && view.Height == 384, $"editor view turned ({view.Width}x{view.Height})");
            Gdip.SavePng(view, Path.Combine(dir, "view_turn1_mirror.png"));
        }

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
        if (!ok) failures++;
    }
}
