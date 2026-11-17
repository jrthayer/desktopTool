using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTool.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

// Used by WindowPlacer.CaptureCurrentLayout to get a minimized window's restore position -
// GetWindowRect on a minimized window returns its off-screen iconic rect, not where it'll land
// once un-minimized, so rcNormalPosition (plus the WPF_RESTORETOMAXIMIZED flag, since a window
// that was maximized before being minimized reports showCmd/style as merely minimized, not
// maximized) is the only way to capture its real placement.
[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr hwndTrack;
    public uint dwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
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

[StructLayout(LayoutKind.Sequential)]
internal struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] rgbReserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEASUREITEMSTRUCT
{
    public uint CtlType;
    public uint CtlID;
    public uint itemID;
    public uint itemWidth;
    public uint itemHeight;
    public IntPtr itemData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DRAWITEMSTRUCT
{
    public uint CtlType;
    public uint CtlID;
    public uint itemID;
    public uint itemAction;
    public uint itemState;
    public IntPtr hwndItem;
    public IntPtr hDC;
    public RECT rcItem;
    public IntPtr itemData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MENUINFO
{
    public uint cbSize;
    public uint fMask;
    public uint dwStyle;
    public uint cyMax;
    public IntPtr hbrBack;
    public uint dwContextHelpID;
    public IntPtr dwMenuData;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TOOLINFO
{
    public uint cbSize;
    public uint uFlags;
    public IntPtr hwnd;
    public IntPtr uId;
    public RECT rect;
    public IntPtr hinst;
    public string lpszText;
    public IntPtr lParam;
    public IntPtr lpReserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LVITEM
{
    public uint mask;
    public int iItem;
    public int iSubItem;
    public uint state;
    public uint stateMask;
    public IntPtr pszText;
    public int cchTextMax;
    public int iImage;
    public IntPtr lParam;
    public int iIndent;
    public int iGroupId;
    public uint cColumns;
    public IntPtr puColumns;
    public IntPtr piColFmt;
    public int iGroup;
}

internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

internal static class NativeMethods
{
    public const uint WM_SPAWN_WORKER = 0x052C;

    public const uint LVM_FIRST = 0x1000;
    public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    public const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
    public const uint LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    public const uint LVM_REDRAWITEMS = LVM_FIRST + 21;
    public const uint LVM_UPDATE = LVM_FIRST + 42;
    public const uint LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    public const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;

    public const uint LVIF_TEXT = 0x0001;

    public const int GWL_STYLE = -16;
    public const long LVS_AUTOARRANGE = 0x0100;
    public const uint LVS_EX_SNAPTOGRID = 0x00080000;
    public const uint LVS_EX_AUTOAUTOARRANGE = 0x01000000;

    public const uint SPI_GETICONMETRICS = 0x002D;

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int ERROR_ACCESS_DENIED = 5;

    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    // Sent repeatedly by the OS's own interactive move/resize loop (entered via DefWndProc after
    // WM_NCLBUTTONDOWN on an HTCAPTION/resize hit-test code) with lParam pointing at a RECT the
    // window proc can mutate before it's applied - the standard mechanism for live drag snapping.
    // WM_SIZING's wParam is one of the WMSZ_* values below, identifying which edge(s) are being
    // dragged - a flat enumeration, not a bitfield (the four corners don't decompose into their
    // two edges by OR-ing the single-edge values together).
    public const int WM_MOVING = 0x0216;
    public const int WM_SIZING = 0x0214;
    public const int WMSZ_LEFT = 1;
    public const int WMSZ_RIGHT = 2;
    public const int WMSZ_TOP = 3;
    public const int WMSZ_TOPLEFT = 4;
    public const int WMSZ_TOPRIGHT = 5;
    public const int WMSZ_BOTTOM = 6;
    public const int WMSZ_BOTTOMLEFT = 7;
    public const int WMSZ_BOTTOMRIGHT = 8;

    // WinForms' own OnMouseEnter/OnMouseLeave already track TME_LEAVE for the CLIENT area - these
    // two plus TrackMouseEvent(TME_LEAVE | TME_NONCLIENT) are what FenceForm needs on top of that to
    // also notice hover over the margin/resize band, which mostly generates non-client mouse
    // messages instead (see FenceForm.HitTest returning HTLEFT/HTCAPTION/etc. there).
    public const int WM_NCMOUSEMOVE = 0x00A0;
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const uint TME_LEAVE = 0x00000002;
    public const uint TME_NONCLIENT = 0x00000010;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CHILD = 0x40000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_BORDER = 0x00800000;
    public const int ES_AUTOHSCROLL = 0x0080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const byte LWA_ALPHA = 0x2;
    public const int SW_HIDE = 0;
    public const int SW_MINIMIZE = 6;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;

    // WINDOWPLACEMENT.flags - set when a currently-minimized window should restore to maximized
    // rather than its rcNormalPosition rect (see WINDOWPLACEMENT's own doc comment).
    public const uint WPF_RESTORETOMAXIMIZED = 0x0002;

    public const uint GW_OWNER = 4;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_UNCHECKED = 0x0000;
    public const uint MF_OWNERDRAW = 0x0100;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_DISABLED = 0x0002;
    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    public const int WM_DRAWITEM = 0x002B;
    public const int WM_MEASUREITEM = 0x002C;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCRBUTTONDOWN = 0x00A4;
    public const uint ODT_MENU = 1;
    public const uint MIM_BACKGROUND = 0x00000002;
    public const uint MIM_APPLYTOSUBMENUS = 0x80000000;
    public const uint ODS_SELECTED = 0x0001;
    public const uint ODS_CHECKED = 0x0008;
    public const int WM_CTLCOLOREDIT = 0x0133;
    public const int WM_MENUSELECT = 0x011F;
    public const uint MF_POPUP_FLAG = 0x0010; // WM_MENUSELECT's HIWORD flags, not an AppendMenu flag - distinct from the MF_POPUP used there despite the same bit

    public const int WM_USER = 0x0400;
    public const int TTM_TRACKACTIVATE = WM_USER + 17;
    public const int TTM_TRACKPOSITION = WM_USER + 18;
    public const int TTM_ADDTOOLW = WM_USER + 50;
    public const int TTM_UPDATETIPTEXTW = WM_USER + 57;
    public const int TTM_SETTIPBKCOLOR = WM_USER + 19;
    public const int TTM_SETTIPTEXTCOLOR = WM_USER + 20;
    public const uint TTS_ALWAYSTIP = 0x01;
    public const uint TTS_NOPREFIX = 0x02;
    public const uint TTF_TRACK = 0x0020;
    public const uint TTF_ABSOLUTE = 0x0080;

    public const int EM_SETSEL = 0x00B1;

    public const uint GA_PARENT = 1;

    // Used by WindowPickerOverlay to walk from whatever WindowFromPoint hit (which can be a
    // child/owned control, e.g. a button inside a dialog) up to the actual unowned top-level window
    // - GA_ROOT stops at the first ancestor with no parent but can still have an owner, which
    // WindowPlacer.CaptureWindow's own GW_OWNER check would then reject.
    public const uint GA_ROOTOWNER = 3;

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0x0;
    public const byte AC_SRC_ALPHA = 0x1;
    public const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TOOLINFO lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    // GW_OWNER distinguishes a true top-level window from an owned popup/tool window (a plain
    // owned window still passes IsWindowVisible/EnumWindows just fine, but isn't a real
    // application window worth matching against when hunting for one - see WindowPlacer).
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // Non-zero when DWM is hiding ("cloaking") a window that IsWindowVisible still reports as
    // visible - suspended UWP app frames, windows on another virtual desktop.
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // The window's visible frame, without the invisible resize borders Win10/11 add around it -
    // GetWindowRect includes those (~7px each side), so two windows placed edge-to-edge by their
    // GetWindowRect show a visible gap. See WindowPlacer.TryGetFrameInsets.
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Used by WindowPlacer.CaptureCurrentLayout to tell a minimized window apart from a normal one
    // (IsWindowVisible alone doesn't - a minimized window keeps WS_VISIBLE set, just with an
    // iconic/off-screen GetWindowRect that isn't its real on-screen position - GetWindowPlacement's
    // rcNormalPosition is used instead for those) and to record a currently-maximized one as
    // LayoutPlacement.Maximized instead of an approximate Custom rect.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // Used by EditBox to translate a WM_KEYDOWN's virtual-key code into the character it represents
    // and insert it directly, instead of relying on a companion WM_CHAR ever arriving - see EditBox's
    // own class comment for why that can't be trusted in this app's message loop.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    // Overload resolved by the lpNewItem argument's type - used for MF_OWNERDRAW items instead of
    // the string overload above, so a small app-defined tag can be carried in itemData reliably
    // (see MEASUREITEMSTRUCT/DRAWITEMSTRUCT.itemData): the string overload's marshaled buffer is
    // freed once AppendMenu returns, which is fine when nothing reads it back, but itemData here
    // needs to stay meaningful for as long as the menu is shown.
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, IntPtr lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // Used by WindowPickerOverlay to find whatever window is beneath the clicked screen point -
    // called only after that overlay hides itself, since it's otherwise the topmost window at every
    // point on screen and would just report itself.
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // COLORREF (0x00bbggrr) at the given device-context coordinates - used by EyedropperOverlay
    // against the whole-screen DC (GetDC(IntPtr.Zero)) to sample whatever's actually displayed at a
    // point, since there's no WinForms-level API for reading a live-rendered screen pixel.
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpcmi);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll")]
    public static extern uint SetBkColor(IntPtr hdc, uint crColor);

    // Opts a specific control out of visual-styles theming - needed for the menu-item tooltip,
    // since a themed tooltip draws its background/text via UxTheme and silently ignores
    // TTM_SETTIPBKCOLOR/TTM_SETTIPTEXTCOLOR, which only affect the older classic GDI rendering path.
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    // Switches a top-level window's own native non-client chrome (title bar/border) to the dark
    // variant - without this, a plain stock Form (SnapLinePanel) keeps its caption bar rendered in
    // Windows' light-theme white regardless of how dark the client area underneath is painted,
    // which would read as two different apps glued together rather than one consistent window.
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
}
