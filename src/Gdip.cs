using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CapeEditor;

/// Windows' own picture library (GDI+, in every Windows since XP) through its flat C API. It reads PNG, JPEG, BMP,
/// GIF and TIFF, resizes with high-quality bicubic filtering and writes PNG, all on Cape Editor's worker thread, so
/// the game never waits for a picture. Only the worker thread touches these bitmaps.
internal static unsafe class Gdip
{
    const string Dll = "gdiplus.dll";
    const int Argb32 = 0x0026200A; // PixelFormat32bppARGB: bytes are B, G, R, A
    const int UnitPixel = 2;
    const int LockRead = 1, LockWrite = 2;
    const int InterpolationHighQualityBicubic = 7, PixelOffsetHighQuality = 2, CompositingHighQuality = 2;
    const int WrapTileFlipXY = 3;
    const int ExifOrientation = 0x0112;
    static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInput
    {
        public uint Version;
        public IntPtr DebugCallback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LockRect { public int X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapData
    {
        public uint Width, Height;
        public int Stride, PixelFormat;
        public IntPtr Scan0, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointF
    {
        public float X, Y;
        public PointF(float x, float y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyItem
    {
        public int Id;
        public uint Length;
        public ushort Type;
        public IntPtr Value;
    }

    [DllImport(Dll)] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int GdipCreateBitmapFromFile(string file, out IntPtr bitmap);
    [DllImport(Dll)] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [DllImport(Dll)] static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport(Dll)] static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport(Dll)] static extern int GdipGetPropertyItemSize(IntPtr image, int id, out uint size);
    [DllImport(Dll)] static extern int GdipGetPropertyItem(IntPtr image, int id, uint size, IntPtr buffer);
    [DllImport(Dll)] static extern int GdipImageRotateFlip(IntPtr image, int type);
    [DllImport(Dll)] static extern int GdipDisposeImage(IntPtr image);
    [DllImport(Dll)] static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport(Dll)] static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport(Dll)] static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);
    [DllImport(Dll)] static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
    [DllImport(Dll)] static extern int GdipSetCompositingQuality(IntPtr graphics, int quality);
    [DllImport(Dll)] static extern int GdipGraphicsClear(IntPtr graphics, uint argb);
    [DllImport(Dll)] static extern int GdipSetClipRect(IntPtr graphics, float x, float y, float w, float h, int combine);
    [DllImport(Dll)] static extern int GdipResetClip(IntPtr graphics);
    [DllImport(Dll)] static extern int GdipDrawImagePointsRect(IntPtr graphics, IntPtr image, PointF* points, int count,
        float srcX, float srcY, float srcW, float srcH, int unit, IntPtr attributes, IntPtr callback, IntPtr callbackData);
    [DllImport(Dll)] static extern int GdipCreateImageAttributes(out IntPtr attributes);
    [DllImport(Dll)] static extern int GdipDisposeImageAttributes(IntPtr attributes);
    [DllImport(Dll)] static extern int GdipSetImageAttributesWrapMode(IntPtr attributes, int wrap, uint argb, bool clamp);
    [DllImport(Dll)] static extern int GdipSetImageAttributesColorMatrix(IntPtr attributes, int type, bool enable, float* matrix, float* grey, int flags);
    [DllImport(Dll)] static extern int GdipBitmapLockBits(IntPtr bitmap, ref LockRect rect, int flags, int format, ref BitmapData data);
    [DllImport(Dll)] static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);
    [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int GdipSaveImageToFile(IntPtr image, string file, ref Guid encoder, IntPtr parameters);

    static IntPtr token;

    public static void Start()
    {
        if (token != IntPtr.Zero) return;
        var input = new StartupInput { Version = 1 };
        Check(GdiplusStartup(out token, ref input, IntPtr.Zero), "GdiplusStartup");
    }

    /// A 32-bit picture owned by GDI+. Free it with Dispose (on the worker thread).
    public sealed class Bitmap : IDisposable
    {
        public IntPtr Handle;
        public readonly int Width, Height;

        public Bitmap(IntPtr handle, int width, int height)
        {
            Handle = handle; Width = width; Height = height;
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            GdipDisposeImage(Handle);
            Handle = IntPtr.Zero;
        }
    }

    public static Bitmap Create(int width, int height)
    {
        Check(GdipCreateBitmapFromScan0(width, height, 0, Argb32, IntPtr.Zero, out var handle), "CreateBitmap");
        return new Bitmap(handle, width, height);
    }

    /// Reads a picture file, turns it the way the camera held it (phone photos store that separately, in EXIF), and
    /// shrinks it so its longer side is at most maxSide. `fullWidth`/`fullHeight` are its size before shrinking.
    public static Bitmap Load(string path, int maxSide, out int fullWidth, out int fullHeight)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The picture file isn't there any more", path);
        int status = GdipCreateBitmapFromFile(path, out var image);
        if (status != 0) throw new InvalidDataException(status == 2 || status == 10 || status == 13
            ? "Windows can't read this kind of picture (use PNG, JPG, BMP, GIF or TIFF)"
            : $"Windows couldn't open the picture (GDI+ error {status})");
        try
        {
            int turn = ExifTurn(image);
            if (turn != 0) GdipImageRotateFlip(image, turn);
            GdipGetImageWidth(image, out var w);
            GdipGetImageHeight(image, out var h);
            fullWidth = (int)w; fullHeight = (int)h;
            if (w == 0 || h == 0) throw new InvalidDataException("The picture is empty");
            float scale = Math.Min(1f, maxSide / (float)Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
            var copy = Create(tw, th);
            try
            {
                using var g = new Canvas(copy);
                g.Clear(0);
                g.Draw(new Bitmap(image, (int)w, (int)h), 0f, 0f, w, h, new PointF(0, 0), new PointF(tw, 0), new PointF(0, th));
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }
        finally
        {
            GdipDisposeImage(image); // also lets go of the file
        }
    }

    /// A PNG's pixels exactly as stored (no turning, no resizing): for Cape Editor's own cached textures.
    public static byte[] ReadExact(string path, out int width, out int height)
    {
        int status = GdipCreateBitmapFromFile(path, out var image);
        if (status != 0) throw new InvalidDataException($"couldn't read {Path.GetFileName(path)} (GDI+ error {status})");
        try
        {
            GdipGetImageWidth(image, out var w);
            GdipGetImageHeight(image, out var h);
            width = (int)w; height = (int)h;
            return Pixels(new Bitmap(image, width, height));
        }
        finally
        {
            GdipDisposeImage(image);
        }
    }

    /// GDI+'s RotateFlipType for an EXIF orientation (1 = as stored).
    static int ExifTurn(IntPtr image)
    {
        if (GdipGetPropertyItemSize(image, ExifOrientation, out var size) != 0 || size < (uint)sizeof(PropertyItem) + 2) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GdipGetPropertyItem(image, ExifOrientation, size, buffer) != 0) return 0;
            var item = (PropertyItem*)buffer;
            if (item->Value == IntPtr.Zero || item->Length < 2) return 0;
            return *(ushort*)item->Value switch
            {
                2 => 4, // mirrored
                3 => 2, // upside down
                4 => 6,
                5 => 5,
                6 => 1, // turned right (the usual portrait phone photo)
                7 => 7,
                8 => 3,
                _ => 0,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// Drawing into a bitmap, with the best resampling GDI+ has.
    public sealed class Canvas : IDisposable
    {
        IntPtr graphics, attributes;

        public Canvas(Bitmap target)
        {
            Check(GdipGetImageGraphicsContext(target.Handle, out graphics), "GraphicsContext");
            GdipSetInterpolationMode(graphics, InterpolationHighQualityBicubic);
            GdipSetPixelOffsetMode(graphics, PixelOffsetHighQuality);
            GdipSetCompositingQuality(graphics, CompositingHighQuality);
            // mirror the picture past its edges instead of fading to clear, so the borders stay sharp
            Check(GdipCreateImageAttributes(out attributes), "ImageAttributes");
            GdipSetImageAttributesWrapMode(attributes, WrapTileFlipXY, 0, false);
        }

        /// Fills everything (or only the clip area) with one colour, replacing what was there, alpha included.
        public void Clear(uint argb) => GdipGraphicsClear(graphics, argb);

        public void Clip(float x, float y, float w, float h) => GdipSetClipRect(graphics, x, y, w, h, 0);

        public void Unclip() => GdipResetClip(graphics);

        /// Draws the source rectangle (in source pixels) into the parallelogram whose corners are where the source's
        /// top-left, top-right and bottom-left corners land. That one call crops, scales, turns and mirrors.
        public void Draw(Bitmap source, float sx, float sy, float sw, float sh, PointF topLeft, PointF topRight, PointF bottomLeft)
        {
            var points = stackalloc PointF[3];
            points[0] = topLeft; points[1] = topRight; points[2] = bottomLeft;
            Check(GdipDrawImagePointsRect(graphics, source.Handle, points, 3, sx, sy, sw, sh, UnitPixel, attributes, IntPtr.Zero, IntPtr.Zero), "DrawImage");
        }

        /// From now on every drawn pixel keeps its alpha but takes this colour (for a plain-coloured cut-out).
        public void Tint(uint rgb)
        {
            var m = stackalloc float[25];
            m[3 * 5 + 3] = 1f; // alpha stays
            m[4 * 5 + 0] = ((rgb >> 16) & 255) / 255f;
            m[4 * 5 + 1] = ((rgb >> 8) & 255) / 255f;
            m[4 * 5 + 2] = (rgb & 255) / 255f;
            m[4 * 5 + 4] = 1f;
            GdipSetImageAttributesColorMatrix(attributes, 0, true, m, null, 0);
        }

        public void Untint() => GdipSetImageAttributesColorMatrix(attributes, 0, false, null, null, 0);

        public void Dispose()
        {
            if (attributes != IntPtr.Zero) GdipDisposeImageAttributes(attributes);
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            attributes = graphics = IntPtr.Zero;
        }
    }

    /// The bitmap's pixels, top row first, 4 bytes each in B, G, R, A order (Unity's BGRA32).
    public static byte[] Pixels(Bitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        var rect = new LockRect { Width = bitmap.Width, Height = bitmap.Height };
        var data = new BitmapData();
        Check(GdipBitmapLockBits(bitmap.Handle, ref rect, LockRead, Argb32, ref data), "LockBits");
        try
        {
            int row = bitmap.Width * 4;
            fixed (byte* dst = bytes)
                for (int y = 0; y < bitmap.Height; y++)
                    Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, dst + (long)y * row, row, row);
        }
        finally
        {
            GdipBitmapUnlockBits(bitmap.Handle, ref data);
        }
        return bytes;
    }

    /// A bitmap from BGRA pixels, top row first.
    public static Bitmap FromPixels(byte[] bytes, int width, int height)
    {
        var bitmap = Create(width, height);
        var rect = new LockRect { Width = width, Height = height };
        var data = new BitmapData();
        Check(GdipBitmapLockBits(bitmap.Handle, ref rect, LockWrite, Argb32, ref data), "LockBits");
        try
        {
            int row = width * 4;
            fixed (byte* src = bytes)
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * row, (byte*)data.Scan0 + (long)y * data.Stride, row, row);
        }
        finally
        {
            GdipBitmapUnlockBits(bitmap.Handle, ref data);
        }
        return bitmap;
    }

    /// Writes a PNG; a temp file first, so a half-written file never replaces a good one.
    public static void SavePng(Bitmap bitmap, string path)
    {
        var temp = path + ".tmp";
        var encoder = PngEncoder;
        Check(GdipSaveImageToFile(bitmap.Handle, temp, ref encoder, IntPtr.Zero), "SavePng");
        File.Move(temp, path, true);
    }

    /// The picture's average colour as 0xFFRRGGBB (from a 32×32 version, weighted by alpha).
    public static uint Average(Bitmap bitmap)
    {
        using var small = Create(32, 32);
        using (var g = new Canvas(small))
        {
            g.Clear(0);
            g.Draw(bitmap, 0, 0, bitmap.Width, bitmap.Height, new PointF(0, 0), new PointF(32, 0), new PointF(0, 32));
        }
        var px = Pixels(small);
        double r = 0, gr = 0, b = 0, a = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double w = px[i + 3];
            b += px[i] * w; gr += px[i + 1] * w; r += px[i + 2] * w; a += w;
        }
        if (a <= 0) return 0xFF202020;
        return 0xFF000000 | (uint)(r / a) << 16 | (uint)(gr / a) << 8 | (uint)(b / a);
    }

    static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed (GDI+ error {status})");
    }
}
