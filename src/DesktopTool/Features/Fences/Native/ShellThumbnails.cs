using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopTool.Features.Fences.Native;

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(ThumbnailSize size, int flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ThumbnailSize
{
    public int cx;
    public int cy;
}

/// <summary>
/// The same thumbnails Explorer shows for a file (going through its own thumbnail cache, and
/// covering whatever image formats have a codec installed - webp/heic included when present).
/// Extraction runs on one shared background STA thread: some thumbnail handlers are
/// apartment-threaded, and generating a first-time thumbnail for a large photo can take a while,
/// which must never stall a fence's paint. Requests queue up and are served one at a time.
/// </summary>
internal static class ShellThumbnails
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_THUMBNAILONLY = 0x8;
    private const uint DIB_RGB_COLORS = 0;
    private static readonly Guid IID_IShellItemImageFactory = typeof(IShellItemImageFactory).GUID;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private static readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<Bitmap?> Result)> Queue = new();
    private static readonly object StartLock = new();
    private static Thread? _worker;

    /// <summary>Resolves to the file's thumbnail at roughly size x size (aspect preserved), or null
    /// when the shell has no real thumbnail for it - never a generic file-type icon.</summary>
    public static Task<Bitmap?> GetThumbnailAsync(string path, int size)
    {
        EnsureWorker();
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((path, size, tcs));
        return tcs.Task;
    }

    private static void EnsureWorker()
    {
        lock (StartLock)
        {
            if (_worker is not null)
                return;
            _worker = new Thread(() =>
            {
                foreach (var (path, size, result) in Queue.GetConsumingEnumerable())
                {
                    Bitmap? bitmap = null;
                    try
                    {
                        bitmap = GetThumbnail(path, size);
                    }
                    catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException or InvalidCastException)
                    {
                    }
                    result.TrySetResult(bitmap);
                }
            })
            {
                IsBackground = true,
                Name = "ShellThumbnails",
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static Bitmap? GetThumbnail(string path, int size)
    {
        var iid = IID_IShellItemImageFactory;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;

        try
        {
            var requested = new ThumbnailSize { cx = size, cy = size };
            if (factory.GetImage(requested, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;

            try
            {
                return ToBitmap(hbm);
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies the HBITMAP out as 32bpp premultiplied ARGB - Image.FromHbitmap would drop
    /// the alpha channel, turning a transparent PNG's background black. GetDIBits with a negative
    /// height always hands the rows back top-down, whichever way the source DIB is stored.</summary>
    private static Bitmap? ToBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0)
            return null;

        var width = info.bmWidth;
        var height = Math.Abs(info.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var stride = width * 4;
        var bytes = new byte[stride * height];

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)height, bytes, ref header, DIB_RGB_COLORS) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // An opaque photo's thumbnail sometimes comes back with every alpha byte zero (the source
        // DIB simply had no alpha) - treat that as fully opaque rather than fully invisible.
        var anyAlpha = false;
        for (var i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }
        if (!anyAlpha)
        {
            for (var i = 3; i < bytes.Length; i += 4)
                bytes[i] = 255;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(bytes, y * stride, data.Scan0 + y * data.Stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
