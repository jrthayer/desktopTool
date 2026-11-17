namespace DesktopTool.Features.Layouts;

/// <summary>Where an entry lands on its target monitor - always resolved against that monitor's
/// live WorkingArea at run time (see WindowPlacer.ResolveRect), so a saved layout still makes sense
/// after a resolution change or on a differently-sized monitor. Custom is the odd one out - see
/// LayoutEntry's own Custom* fields - everything else is a fixed preset with nothing further to
/// store.</summary>
public enum LayoutPlacement
{
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    TopLeftQuarter,
    TopRightQuarter,
    BottomLeftQuarter,
    BottomRightQuarter,
    Maximized,

    /// <summary>Only ever produced by WindowPlacer.CaptureCurrentLayout ("Save Current Layout") -
    /// an exact rect a window was actually sitting at, rather than one of the fixed presets above.
    /// Picking it by hand in the editor (nothing to capture from) falls back to the full working
    /// area, same as Maximized but via SetWindowPos instead of SW_MAXIMIZE.</summary>
    Custom,
}

/// <summary>One program a LayoutProfile launches (or reuses, if already running - see
/// WindowPlacer) and places on a specific monitor.</summary>
public sealed class LayoutEntry
{
    /// <summary>Launched the same way FenceForm.OpenItem opens a fenced shortcut - a .exe, a
    /// .lnk, or anything else ShellExecute can resolve. A captured entry (see
    /// WindowPlacer.CaptureCurrentLayout) always stores the real .exe Process.MainModule reported,
    /// never a shortcut - there's no ambiguity to resolve the way ShortcutResolver exists for.</summary>
    public string ProgramPath { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    /// <summary>Captured window title, best-effort - a soft hint WindowPlacer.RunAsync prefers when
    /// choosing among several unclaimed windows that all belong to the same exe (e.g. two Notepad
    /// windows), never a hard requirement, since a title can drift after capture (a browser tab, an
    /// editor's unsaved-changes marker) in ways that don't mean the window stopped being the right
    /// one. Null/empty only if the captured window itself had no title.</summary>
    public string? WindowTitleHint { get; set; }

    /// <summary>Browser-only: page(s) to open when this entry runs, one URL per line - see
    /// WindowPlacer.BuildNewWindowArgs, which opens every line as its own tab inside the same
    /// forced new window rather than one new window per URL. Always launched with --new-window -
    /// see WindowPlacer.IsBrowserExecutable. Every mainstream browser accepts --new-window alongside
    /// one or more URLs to force a genuinely new top-level window even when an instance is already
    /// running, rather than reusing one as a new tab, so RunAsync can poll for it exactly like a
    /// not-yet-running entry instead of needing a separate already-running path. Null/empty for a
    /// non-browser entry, or a browser entry that should just be brought to its placement without
    /// navigating anywhere.</summary>
    public string? Url { get; set; }

    /// <summary>Terminal-only: command(s) to run when this entry runs, one per line - see
    /// WindowPlacer.BuildTerminalCommandArgs, which chains every line into a single argument string
    /// (the syntax is per-shell) and leaves the window open afterward, the terminal equivalent of
    /// Url's "several tabs, one placed window". Always forces a fresh launch when set - see
    /// WindowPlacer.IsTerminalExecutable and RunAsync's hasCommand handling - since unlike a
    /// browser's --new-window there's no way to hand a command to an already-open terminal window,
    /// and no single-instance messaging to force past anyway (every terminal launch is already its
    /// own separate process/window). Null/empty for a non-terminal entry, or a terminal entry that
    /// should just be brought to its placement without running anything.</summary>
    public string? Command { get; set; }

    /// <summary>Only meaningful when ProgramPath resolves to WindowsTerminal.exe (see
    /// WindowPlacer.IsWindowsTerminalProgram) - which underlying shell exe (powershell.exe,
    /// pwsh.exe, cmd.exe) Command's lines should run in. WindowsTerminal.exe isn't itself a shell,
    /// and there's no way to tell from a captured window alone which one it was actually running
    /// (see WindowPlacer.BuildTerminalCommandArgs), so LayoutEditorForm exposes this as an explicit
    /// picker rather than guessing. Null defaults to powershell.exe - Windows Terminal's own default
    /// profile on a stock install. Ignored for a directly-captured cmd.exe/powershell.exe/pwsh.exe
    /// entry, where the shell is already implied by ProgramPath itself.</summary>
    public string? TerminalShellExe { get; set; }

    /// <summary>File Explorer-only (see WindowPlacer.IsExplorerProgram): the folder this entry's
    /// window opens at - captured from the live window via ExplorerWindows.TryGetFolderPath, or
    /// picked by hand in LayoutEditorForm. Passed straight to explorer.exe as its argument, so a
    /// virtual-folder "::{GUID}" path (This PC, say) works the same as a real directory. Null/empty
    /// opens Explorer's default view.</summary>
    public string? FolderPath { get; set; }

    /// <summary>Screen.DeviceName, not an index into Screen.AllScreens or its Bounds (unlike
    /// SnapLineModel.MonitorBounds) - a plugged-in monitor's device name is more likely to survive
    /// a resolution change or the monitors being reordered in Windows' own display settings than
    /// its bounds are. Empty matches the primary screen (see WindowPlacer.ResolveScreen).</summary>
    public string TargetMonitor { get; set; } = string.Empty;

    public LayoutPlacement Placement { get; set; } = LayoutPlacement.Maximized;

    /// <summary>Independent of Placement, not an alternative to it - the window is still placed
    /// normally first (see WindowPlacer.PlaceWindow), then minimized on top of that, so its restore
    /// size/position (what you get back on un-minimizing it) is whatever Placement/TargetMonitor
    /// already resolved to rather than whatever size the window happened to launch at.</summary>
    public bool Minimized { get; set; }

    // Only meaningful when Placement is Custom - the captured window's rect as 0-1 fractions of its
    // target monitor's WorkingArea at capture time (X/Y/Width/Height, not a RectangleF - same
    // "doesn't round-trip through plain System.Text.Json" reasoning FenceModel.TintColor's own
    // comment gives for storing a Color as an int instead of the struct directly). Fractions, not
    // raw pixels, for the same resolution-independence every preset already gets for free.
    public double CustomX { get; set; }
    public double CustomY { get; set; }
    public double CustomWidth { get; set; } = 1.0;
    public double CustomHeight { get; set; } = 1.0;
}

/// <summary>A named, saved set of programs-and-positions - see WindowPlacer for what actually
/// runs one.</summary>
public sealed class LayoutProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Layout";
    public List<LayoutEntry> Entries { get; set; } = new();
}
