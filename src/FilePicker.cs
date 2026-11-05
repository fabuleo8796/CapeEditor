using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CapeEditor;

/// Windows' own "Open" window, showing pictures only. It runs on a thread of its own, so the game keeps running
/// behind it, and the game window owns it, so it stays on top. COM is called through raw vtable slots (the way
/// Tripod Cam's folder picker does it). Start it with Show on the main thread and collect the answer with TryTake.
internal static unsafe class FilePicker
{
    static readonly Guid FileOpenDialog = new("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7");
    static readonly Guid IFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");
    static readonly Guid IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    const uint InProcServer = 1;
    const uint NoChangeDir = 0x8, ForceFileSystem = 0x40, PathMustExist = 0x800, FileMustExist = 0x1000;
    const uint FileSysPath = 0x80058000; // SIGDN_FILESYSPATH
    const int Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    [StructLayout(LayoutKind.Sequential)]
    struct FilterSpec { public char* Name, Spec; }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, out IntPtr obj);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, Guid* iid, out IntPtr item);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

    static readonly object gate = new();
    static volatile bool busy;
    static bool done;
    static string picked, failed;

    public static bool Busy => busy;

    /// Main thread. Opens the window in `start` (or the nearest folder above it that exists); false if it's already open.
    public static bool Show(string start)
    {
        if (busy) return false;
        busy = true;
        var owner = GetActiveWindow(); // only sees this thread's windows, which is why it's read here
        if (owner == IntPtr.Zero) owner = Process.GetCurrentProcess().MainWindowHandle;
        var thread = new Thread(() => Run(owner, start)) { IsBackground = true, Name = "Cape Editor file picker" };
        thread.SetApartmentState(ApartmentState.STA); // the shell's dialogs need a single-threaded apartment
        thread.Start();
        return true;
    }

    /// Main thread: true once the window has closed. Then `file` is the pick, or `error` says what went wrong; both
    /// null means the player cancelled.
    public static bool TryTake(out string file, out string error)
    {
        lock (gate)
        {
            file = picked; error = failed;
            bool closed = done;
            done = false; picked = failed = null;
            return closed;
        }
    }

    static void Run(IntPtr owner, string start)
    {
        string file = null, error = null;
        IntPtr dialog = IntPtr.Zero, item = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            Guid clsid = FileOpenDialog, dialogId = IFileDialog, itemId = IShellItem;
            Check(CoCreateInstance(&clsid, IntPtr.Zero, InProcServer, &dialogId, out dialog), "CoCreateInstance");
            fixed (char* picturesName = "Pictures (PNG, JPG, BMP, GIF, TIFF)")
            fixed (char* picturesSpec = "*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff")
            fixed (char* allName = "All files")
            fixed (char* allSpec = "*.*")
            {
                var specs = stackalloc FilterSpec[2];
                specs[0] = new FilterSpec { Name = picturesName, Spec = picturesSpec };
                specs[1] = new FilterSpec { Name = allName, Spec = allSpec };
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, FilterSpec*, int>)Slot(dialog, 4))(dialog, 2, specs), "SetFileTypes");
            }
            uint options;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, 10))(dialog, &options), "GetOptions");
            options |= NoChangeDir | ForceFileSystem | PathMustExist | FileMustExist;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, 9))(dialog, options), "SetOptions");
            fixed (char* title = "Pick a picture for your cape")
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 17))(dialog, title);
            fixed (char* ok = "Use this picture")
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 18))(dialog, ok);

            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start);
            if (!string.IsNullOrEmpty(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, &itemId, out item) >= 0)
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 12))(dialog, item); // SetFolder

            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 3))(dialog, owner); // Show: waits here
            if (hr == Cancelled) return;
            Check(hr, "Show");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dialog, 20))(dialog, &result), "GetResult");
            char* path;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, char**, int>)Slot(result, 5))(result, FileSysPath, &path), "GetDisplayName");
            file = new string(path);
            Marshal.FreeCoTaskMem((IntPtr)path);
        }
        catch (Exception e)
        {
            error = "The file picker didn't open: " + e.Message;
        }
        finally
        {
            Release(ref result);
            Release(ref item);
            Release(ref dialog);
            lock (gate) { picked = file; failed = error; done = true; busy = false; }
        }
    }

    static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);
        com = IntPtr.Zero;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }
}
