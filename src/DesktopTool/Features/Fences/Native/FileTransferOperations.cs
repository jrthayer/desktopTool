using System.Runtime.InteropServices;

namespace DesktopTool.Features.Fences.Native;

/// <summary>
/// Moves or copies dropped files into a folder with Explorer's own default drag rule: a move when
/// source and destination share a drive, a copy otherwise. Uses SHFileOperationW for the same
/// reason RecycleBinOperations does - one P/Invoke, and Explorer's own conflict/progress/error UI.
/// </summary>
internal static class FileTransferOperations
{
    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);

    /// <summary>Transfers every path into destinationDir in one batched operation. Paths already
    /// directly inside destinationDir are skipped (dropping a file onto its own folder is a no-op,
    /// not a "file (2)" duplicate). Returns false if nothing was transferred.</summary>
    public static bool TransferInto(IntPtr ownerHwnd, IReadOnlyList<string> paths, string destinationDir)
    {
        var destFull = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sources = paths
            .Select(Path.GetFullPath)
            .Where(p => !string.Equals(
                Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                destFull, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sources.Count == 0)
            return false;

        var sameDrive = sources.All(p => string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(destFull), StringComparison.OrdinalIgnoreCase));

        // pFrom/pTo need double-null-terminated buffers, built manually (see RecycleBinOperations).
        var pFrom = Marshal.StringToHGlobalUni(string.Join('\0', sources) + "\0\0");
        var pTo = Marshal.StringToHGlobalUni(destFull + "\0\0");
        try
        {
            var fileOp = new SHFILEOPSTRUCTW
            {
                hwnd = ownerHwnd,
                wFunc = sameDrive ? FO_MOVE : FO_COPY,
                pFrom = pFrom,
                pTo = pTo,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR,
            };
            var result = SHFileOperationW(ref fileOp);
            return result == 0 && !fileOp.fAnyOperationsAborted;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
            Marshal.FreeHGlobal(pTo);
        }
    }
}
