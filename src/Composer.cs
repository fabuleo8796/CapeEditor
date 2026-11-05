using System;

namespace CapeEditor;

/// The player's choices for one custom cape: how the picture sits on the cape. Saved in the cape's design.txt.
internal struct Edit
{
    /// 1 fills the cape (the auto-crop: the picture covers it, the rest is cut off). Above 1 zooms in. Below 1 shows
    /// more of the picture, down to all of it, with the edge colour round it ("Fit").
    public float Zoom;
    /// The middle of the crop frame, 0..1 across the turned picture, from its top-left corner.
    public float CenterX, CenterY;
    /// Quarter turns clockwise.
    public int Turns;
    public bool Mirror;
    /// The inside of the cape (the side against the phone) is plain edge colour instead of the picture.
    public bool PlainInside;
    /// See-through parts of the picture (and the space round it) cut holes in the cape, instead of showing the edge colour.
    public bool CutOut;
    /// 0xFFRRGGBB, or 0 for the picture's own average colour.
    public uint EdgeColor;

    public static Edit Default => new() { Zoom = 1f, CenterX = 0.5f, CenterY = 0.5f };

    public bool Same(in Edit o) => Zoom == o.Zoom && CenterX == o.CenterX && CenterY == o.CenterY && Turns == o.Turns &&
                                   Mirror == o.Mirror && PlainInside == o.PlainInside && CutOut == o.CutOut && EdgeColor == o.EdgeColor;

    /// A short fingerprint for cache file names.
    public string Hash(int sourceWidth, int sourceHeight, int size)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(long v) { h = (h ^ (ulong)v) * 1099511628211UL; }
            Mix(BitConverter.SingleToInt32Bits(Zoom)); Mix(BitConverter.SingleToInt32Bits(CenterX)); Mix(BitConverter.SingleToInt32Bits(CenterY));
            Mix(Turns); Mix(Mirror ? 1 : 0); Mix(PlainInside ? 1 : 0); Mix(CutOut ? 1 : 0); Mix(EdgeColor);
            Mix(sourceWidth); Mix(sourceHeight); Mix(size);
            return h.ToString("x16").Substring(0, 10);
        }
    }
}

/// Pixels ready for a Unity texture: BGRA, Unity's row order (bottom row first), every mip level one after another.
internal sealed class TexData
{
    public int Width, Height, Mips;
    public byte[] Data;
}

/// The cape editor's maths and drawing: where the crop frame is, and painting the picture into a cape texture.
/// Pure C# plus GDI+, so it runs on the worker thread and in the offline test.
internal static class Composer
{
    /// Size of the picture once turned.
    public static void ViewSize(int sw, int sh, int turns, out float vw, out float vh)
    {
        bool sideways = (turns & 1) == 1;
        vw = sideways ? sh : sw;
        vh = sideways ? sw : sh;
    }

    /// The zoom at which the whole picture fits inside the frame.
    public static float FitZoom(float vw, float vh, float aspect) => Math.Min(vw, aspect * vh) / Math.Max(vw, aspect * vh);

    public const float MaxZoom = 8f;

    /// The crop frame in turned-picture pixels (top-left origin). Fixes the edit's centre so the frame never leaves
    /// the picture (when it's smaller than the picture) or stays centred on it (when it's bigger).
    public static void Frame(float vw, float vh, float aspect, ref Edit e, out float fx, out float fy, out float fw, out float fh)
    {
        e.Zoom = Math.Clamp(e.Zoom, FitZoom(vw, vh, aspect), MaxZoom);
        fw = Math.Min(vw, aspect * vh) / e.Zoom;
        fh = fw / aspect;
        float x = e.CenterX * vw, y = e.CenterY * vh;
        x = fw <= vw ? Math.Clamp(x, fw / 2f, vw - fw / 2f) : vw / 2f;
        y = fh <= vh ? Math.Clamp(y, fh / 2f, vh - fh / 2f) : vh / 2f;
        e.CenterX = x / vw;
        e.CenterY = y / vh;
        fx = x - fw / 2f;
        fy = y - fh / 2f;
    }

    /// Source pixel → turned (and mirrored) picture pixel.
    public static void ToView(float x, float y, int sw, int sh, int turns, bool mirror, out float vx, out float vy)
    {
        switch (turns & 3)
        {
            case 1: vx = sh - y; vy = x; break;
            case 2: vx = sw - x; vy = sh - y; break;
            case 3: vx = y; vy = sw - x; break;
            default: vx = x; vy = y; break;
        }
        if (mirror) vx = ((turns & 1) == 1 ? sh : sw) - vx;
    }

    /// Turned picture pixel → source pixel.
    public static void ToSource(float vx, float vy, int sw, int sh, int turns, bool mirror, out float x, out float y)
    {
        if (mirror) vx = ((turns & 1) == 1 ? sh : sw) - vx;
        switch (turns & 3)
        {
            case 1: x = vy; y = sh - vx; break;
            case 2: x = sw - vx; y = sh - vy; break;
            case 3: x = sw - vy; y = vx; break;
            default: x = vx; y = vy; break;
        }
    }

    /// Where the crop frame's top-left, top-right and bottom-left corners go in a target bitmap.
    public struct Target
    {
        public Gdip.PointF TopLeft, TopRight, BottomLeft;

        public Gdip.PointF At(float s, float t) => new(
            TopLeft.X + s * (TopRight.X - TopLeft.X) + t * (BottomLeft.X - TopLeft.X),
            TopLeft.Y + s * (TopRight.Y - TopLeft.Y) + t * (BottomLeft.Y - TopLeft.Y));

        /// A panel on a size×size cape texture. GDI+ rows go top-down, and they become Unity's rows bottom-up, so in
        /// GDI+ pixels v=0 is row 0: an upright picture has its top at v1.
        public static Target Panel(UvRect r, bool turned, int size)
        {
            float x0 = r.U0 * size, x1 = r.U1 * size, y0 = r.V0 * size, y1 = r.V1 * size;
            return turned
                ? new Target { TopLeft = new(x1, y0), TopRight = new(x0, y0), BottomLeft = new(x1, y1) }
                : new Target { TopLeft = new(x0, y1), TopRight = new(x1, y1), BottomLeft = new(x0, y0) };
        }

        /// An ordinary upright w×h picture.
        public static Target Flat(float w, float h) => new() { TopLeft = new(0, 0), TopRight = new(w, 0), BottomLeft = new(0, h) };
    }

    /// Paints the part of the picture inside the crop frame onto `target` (the frame fills the target), cropped to
    /// the picture, turned and mirrored as the edit says.
    public static void DrawFrame(Gdip.Canvas g, Gdip.Bitmap src, Edit e, float fx, float fy, float fw, float fh, Target target)
    {
        ViewSize(src.Width, src.Height, e.Turns, out float vw, out float vh);
        float cx0 = Math.Max(fx, 0f), cy0 = Math.Max(fy, 0f), cx1 = Math.Min(fx + fw, vw), cy1 = Math.Min(fy + fh, vh);
        if (cx1 - cx0 < 0.01f || cy1 - cy0 < 0.01f) return;
        // the visible part as a source rectangle
        ToSource(cx0, cy0, src.Width, src.Height, e.Turns, e.Mirror, out float ax, out float ay);
        ToSource(cx1, cy1, src.Width, src.Height, e.Turns, e.Mirror, out float bx, out float by);
        float sl = Math.Min(ax, bx), st = Math.Min(ay, by), sr = Math.Max(ax, bx), sb = Math.Max(ay, by);
        Gdip.PointF Land(float x, float y)
        {
            ToView(x, y, src.Width, src.Height, e.Turns, e.Mirror, out float vx, out float vy);
            return target.At((vx - fx) / fw, (vy - fy) / fh);
        }
        g.Draw(src, sl, st, sr - sl, sb - st, Land(sl, st), Land(sr, st), Land(sl, sb));
    }

    /// The edge colour this edit uses.
    public static uint EdgeColor(in Edit e, uint auto) => e.EdgeColor != 0 ? e.EdgeColor : auto != 0 ? auto : 0xFF202020;

    /// A whole cape texture (size×size) in GDI+ row order: the picture on the outside panel (and on the inside, unless
    /// that's plain), everything else in the edge colour.
    public static Gdip.Bitmap Cape(Gdip.Bitmap src, Edit e, uint autoColor, CapeLayout layout, int size)
    {
        ViewSize(src.Width, src.Height, e.Turns, out float vw, out float vh);
        Frame(vw, vh, layout.Aspect, ref e, out float fx, out float fy, out float fw, out float fh);
        uint edge = EdgeColor(e, autoColor);
        var cape = Gdip.Create(size, size);
        try
        {
            using var g = new Gdip.Canvas(cape);
            // Opaque edge colour under the picture, so its see-through parts show that colour. For a cut-out it's clear
            // instead (a hole: the cape's shader alpha-tests at 0.5), keeping the colour so blurry mip levels don't go dark.
            g.Clear(e.CutOut ? edge & 0x00FFFFFF : edge);
            foreach (bool outside in new[] { true, false })
            {
                var r = outside ? layout.Outside : layout.Inside;
                if (!outside && e.PlainInside)
                {
                    if (!e.CutOut) continue; // already edge colour
                    g.Tint(edge); // the inside is cut to the same shape, in plain colour
                }
                DrawFrame(g, src, e, fx, fy, fw, fh, Target.Panel(r, layout.Turned, size));
                g.Untint();
            }
            return cape;
        }
        catch
        {
            cape.Dispose();
            throw;
        }
    }

    /// The outside panel as it looks on Flippy, upright, w pixels wide: the library card's picture.
    public static Gdip.Bitmap Thumb(Gdip.Bitmap src, Edit e, uint autoColor, CapeLayout layout, int w)
    {
        ViewSize(src.Width, src.Height, e.Turns, out float vw, out float vh);
        Frame(vw, vh, layout.Aspect, ref e, out float fx, out float fy, out float fw, out float fh);
        int h = (int)Math.Round(w / layout.Aspect);
        var thumb = Gdip.Create(w, h);
        try
        {
            using var g = new Gdip.Canvas(thumb);
            uint edge = EdgeColor(e, autoColor);
            g.Clear(e.CutOut ? 0u : edge);
            DrawFrame(g, src, e, fx, fy, fw, fh, Target.Flat(w, h));
            return thumb;
        }
        catch
        {
            thumb.Dispose();
            throw;
        }
    }

    /// The turned and mirrored picture, at most maxSide pixels: what the editor shows you to crop.
    public static Gdip.Bitmap View(Gdip.Bitmap src, int turns, bool mirror, int maxSide)
    {
        ViewSize(src.Width, src.Height, turns, out float vw, out float vh);
        float k = Math.Min(1f, maxSide / Math.Max(vw, vh));
        int w = Math.Max(1, (int)Math.Round(vw * k)), h = Math.Max(1, (int)Math.Round(vh * k));
        var view = Gdip.Create(w, h);
        try
        {
            using var g = new Gdip.Canvas(view);
            g.Clear(0);
            var e = new Edit { Turns = turns, Mirror = mirror };
            DrawFrame(g, src, e, 0, 0, vw, vh, Target.Flat(w, h));
            return view;
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- Unity texture data

    /// BGRA pixels with their mip levels, ready for Texture2D.LoadRawTextureData. GDI+'s top-down rows go in as they
    /// are when `gdiRowsAreUnityRows` (the cape texture is laid out that way on purpose), or flipped so the picture
    /// shows upright in a UI RawImage.
    public static TexData ForUnity(byte[] top, int w, int h, bool mips, bool gdiRowsAreUnityRows)
    {
        byte[] level0;
        if (gdiRowsAreUnityRows) level0 = top;
        else
        {
            level0 = new byte[top.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(top, y * row, level0, (h - 1 - y) * row, row);
        }
        int count = 1;
        if (mips) for (int s = Math.Max(w, h); s > 1; s >>= 1) count++;
        long total = 0;
        for (int i = 0, lw = w, lh = h; i < count; i++, lw = Math.Max(1, lw / 2), lh = Math.Max(1, lh / 2)) total += (long)lw * lh * 4;
        var data = new byte[total];
        Buffer.BlockCopy(level0, 0, data, 0, level0.Length);
        int offset = level0.Length, pw = w, ph = h, prev = 0;
        for (int i = 1; i < count; i++)
        {
            int nw = Math.Max(1, pw / 2), nh = Math.Max(1, ph / 2);
            Halve(data, prev, pw, ph, offset, nw, nh);
            prev = offset;
            offset += nw * nh * 4;
            pw = nw; ph = nh;
        }
        return new TexData { Width = w, Height = h, Mips = count, Data = data };
    }

    static readonly float[] toLinear = BuildToLinear();
    static readonly byte[] toSrgb = BuildToSrgb();

    static float[] BuildToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    static byte[] BuildToSrgb()
    {
        var t = new byte[4096];
        for (int i = 0; i < 4096; i++)
        {
            double l = i / 4095.0;
            double c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            t[i] = (byte)Math.Clamp((int)Math.Round(c * 255), 0, 255);
        }
        return t;
    }

    /// One mip level from the one above: a 2×2 box filter in linear light (the texture is sRGB), alpha-weighted so
    /// cut-out edges don't pull in the colour of the holes.
    static void Halve(byte[] d, int src, int sw, int sh, int dst, int dw, int dh)
    {
        for (int y = 0; y < dh; y++)
        {
            int y0 = Math.Min(y * 2, sh - 1), y1 = Math.Min(y * 2 + 1, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                int x0 = Math.Min(x * 2, sw - 1), x1 = Math.Min(x * 2 + 1, sw - 1);
                int p0 = src + (y0 * sw + x0) * 4, p1 = src + (y0 * sw + x1) * 4, p2 = src + (y1 * sw + x0) * 4, p3 = src + (y1 * sw + x1) * 4;
                float a0 = d[p0 + 3], a1 = d[p1 + 3], a2 = d[p2 + 3], a3 = d[p3 + 3];
                float aw = a0 + a1 + a2 + a3;
                int o = dst + (y * dw + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float l = aw > 0f
                        ? (toLinear[d[p0 + c]] * a0 + toLinear[d[p1 + c]] * a1 + toLinear[d[p2 + c]] * a2 + toLinear[d[p3 + c]] * a3) / aw
                        : (toLinear[d[p0 + c]] + toLinear[d[p1 + c]] + toLinear[d[p2 + c]] + toLinear[d[p3 + c]]) * 0.25f;
                    d[o + c] = toSrgb[(int)(l * 4095f + 0.5f)];
                }
                d[o + 3] = (byte)((aw + 2f) / 4f);
            }
        }
    }
}
