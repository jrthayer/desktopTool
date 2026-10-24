using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DesktopTool.Features.Layouts.Native;

namespace DesktopTool.Features.Fences.Native;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

// The shell's system image lists (as opposed to SHGFI_ICON's small/large icons, which are capped
// at 32x32) are what Explorer itself uses to render sharp large icons - this is the only way to
// get a genuinely high-resolution icon for an arbitrary file rather than upscaling a blurry 32px one.
[ComImport]
[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
}

internal static class ShellIcons
{
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_PIDL = 0x8;
    private const int SHIL_JUMBO = 0x4; // 256x256
    private const int SHIL_EXTRALARGE = 0x2; // 48x48
    private const int ILD_TRANSPARENT = 0x1;
    private const int CSIDL_BITBUCKET = 0x000a; // the Recycle Bin
    private static readonly Guid IID_IImageList = typeof(IImageList).GUID;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // Same native function as above - a distinct managed overload (IntPtr instead of string) for
    // SHGFI_PIDL mode, where pszPath is actually an absolute PIDL rather than a path string. Used
    // for the Recycle Bin: unlike an ordinary file/folder, its "::{CLSID}" shell-namespace string
    // isn't reliably resolved by the string-based overload above (confirmed by testing - it simply
    // returns no icon), but a real PIDL from SHGetSpecialFolderLocation works.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pszPidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Reads an icon straight out of its source file's resources (exe/dll/ico), scaled to the
    // requested size - no system image list or icon cache involved. Returns S_OK (0) on success,
    // S_FALSE (1) if the file has no icon at that index.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    private const uint DirectIconSize = 256;

    /// <summary>
    /// Extracts a large (up to 256x256) shell icon for the given file/folder path. Returns null if
    /// the path is inaccessible or the shell can't produce one - callers should fall back to
    /// Icon.ExtractAssociatedIcon in that case.
    /// </summary>
    public static Icon? ExtractLargeIcon(string path)
    {
        // Tried first whenever the icon lives in a file we can read directly (an exe, or a .lnk
        // whose icon/target is one): the system image list below goes through the per-user icon
        // cache, and a bad entry there hands back the generic blank-page icon for an otherwise
        // perfectly good exe - observed for Microsoft Edge on a freshly created Windows profile,
        // where both Edge's .lnk and msedge.exe itself came back blank while reading msedge.exe's
        // own icon resource worked fine. That result isn't null, so nothing downstream could tell
        // it apart from a real icon and fall back on its own.
        if (ExtractDirect(path) is { } direct)
            return direct;

        var shfi = new SHFILEINFO();
        var size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, 0, ref shfi, size, SHGFI_SYSICONINDEX) == IntPtr.Zero)
            return null;

        // Not every icon actually has 256x256 source art - when the underlying .ico only embeds
        // up to some smaller native resolution (observed for a Steam-generated shortcut icon whose
        // .ico was a few KB, versus a few hundred KB for one with real jumbo art), the jumbo list
        // doesn't stretch that smaller frame to fill the request; it blits it unscaled into one
        // corner of an otherwise-transparent 256x256 canvas. FenceForm then scales that whole
        // (mostly empty) canvas down to IconSize, so what's drawn ends up a fraction of the cell -
        // requireFullBleed rejects that case in favor of the extra-large (48x48) list, which asks
        // for a size the shell can actually deliver as a fully-filled frame instead of padding it.
        return GetIcon(shfi.iIcon, SHIL_JUMBO, requireFullBleed: true)
            ?? GetIcon(shfi.iIcon, SHIL_EXTRALARGE, requireFullBleed: false);
    }

    /// <summary>
    /// Extracts the Recycle Bin's own large icon (empty/full-aware, reflecting its current
    /// contents) via its special-folder PIDL rather than a path string - see the SHGFI_PIDL
    /// overload's own comment for why the ordinary path-based ExtractLargeIcon can't be used for
    /// this. Returns null if the PIDL lookup or the icon extraction itself fails.
    /// </summary>
    public static Icon? ExtractRecycleBinIcon()
    {
        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, out var pidl) != 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            var shfi = new SHFILEINFO();
            var size = (uint)Marshal.SizeOf<SHFILEINFO>();
            if (SHGetFileInfo(pidl, 0, ref shfi, size, SHGFI_PIDL | SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;

            return GetIcon(shfi.iIcon, SHIL_JUMBO, requireFullBleed: true)
                ?? GetIcon(shfi.iIcon, SHIL_EXTRALARGE, requireFullBleed: false);
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }

    private static Icon? ExtractDirect(string path)
    {
        if (ResolveIconSource(path) is not var (file, index))
            return null;

        if (SHDefExtractIcon(file, index, 0, out var large, out var small, DirectIconSize) != 0 || large == IntPtr.Zero)
            return null;

        try
        {
            // Same ownership dance as GetIcon below - Clone() so the native handle can go.
            using var temp = Icon.FromHandle(large);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(large);
            if (small != IntPtr.Zero)
                DestroyIcon(small);
        }
    }

    /// <summary>The file (and icon index) path's icon is actually stored in, when that's something
    /// SHDefExtractIcon can read directly - null for everything else (documents, folders, .url
    /// shortcuts, shell-namespace/UWP links), which only the shell itself knows how to resolve. A
    /// .lnk's own explicit icon location wins over its target, so a shortcut with a custom icon
    /// still shows that rather than its target exe's.</summary>
    private static (string File, int Index)? ResolveIconSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return File.Exists(path) ? (path, 0) : null;

        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ShortcutResolver.Resolve(path) is not { } info)
            return null;

        if (info.IconPath is { } iconPath)
            return IsDirectIconFile(iconPath) && File.Exists(iconPath) ? (iconPath, info.IconIndex) : null;

        return info.Target is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)
            ? (target, 0)
            : null;
    }

    private static bool IsDirectIconFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".ico";

    private static Icon? GetIcon(int iIcon, int shilList, bool requireFullBleed)
    {
        var iid = IID_IImageList;
        if (SHGetImageList(shilList, ref iid, out var imageList) != 0)
            return null;

        try
        {
            if (imageList.GetIcon(iIcon, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            try
            {
                // Icon.FromHandle doesn't take ownership of hIcon - Clone() copies the image data
                // into a managed Icon we can keep, so the original handle can be destroyed here.
                using var temp = Icon.FromHandle(hIcon);
                if (requireFullBleed && !FillsCanvas(temp))
                    return null;
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(imageList);
        }
    }

    /// <summary>True if icon's actual (non-transparent) artwork spans most of its own canvas,
    /// rather than being a smaller native-resolution frame padded out with empty space - see
    /// ExtractLargeIcon's own comment for why that distinction matters.</summary>
    private static bool FillsCanvas(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width == 0 || height == 0)
            return false;

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (bytes[row + x * 4 + 3] <= 10)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0)
                return false;

            // A genuine full-resolution icon's artwork typically fills most of its canvas (game/app
            // icons rarely leave more than ~20% empty padding); a smaller frame blitted unscaled
            // into one corner instead leaves most of it transparent - 60% catches that gap cleanly.
            return (maxX - minX + 1) >= width * 0.6 && (maxY - minY + 1) >= height * 0.6;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
