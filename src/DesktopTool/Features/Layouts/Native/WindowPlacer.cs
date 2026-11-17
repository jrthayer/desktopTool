using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DesktopTool.Features.Layouts;
using DesktopTool.Native;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Launches (or reuses, if already running) each entry in a layout and places its window on its
/// target monitor. The one genuinely hard part: Windows has no API to ask "which window did the
/// process I just launched create" - Process.Start's own returned Process can't be trusted for this
/// (ShellExecute on a .lnk, or an app that re-launches itself through a different child process
/// before creating its real window, both mean the eventual owning process's PID often isn't the one
/// Start() handed back). Matching is done by executable name instead - snapshot which windows
/// already match before launching, then poll for a new one that appears after.
/// </summary>
internal static class WindowPlacer
{
    private const int PollIntervalMs = 200;
    private const int LaunchTimeoutMs = 15000;

    // How long a single-instance launcher (see HelperHostedApps) gets to produce a brand-new window
    // before its already-open window is taken instead - relaunching Steam while it's open just
    // brings the existing window forward, so no new one would ever appear.
    private const int SingleInstanceGraceMs = 3000;

    // Shell chrome that isn't owned by explorer.exe (so the blanket explorer.exe skip in
    // CaptureCurrentLayout doesn't already catch it) but would otherwise pass its visible/unowned/
    // non-iconic filter same as any real app window - the Start menu/Search/Action Center/Widgets
    // flyouts, all one shared window class, each hosted by its own separate process.
    private static readonly HashSet<string> ExcludedCaptureClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.UI.Core.CoreWindow",
    };

    // Processes whose windows are never worth capturing because launching the exe itself does
    // nothing useful. ApplicationFrameHost is the frame Windows wraps around UWP/Store apps
    // (Settings, Calculator, ...); the real app lives in a different process, and running
    // ApplicationFrameHost.exe directly opens nothing.
    private static readonly HashSet<string> ExcludedCaptureExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe",
    };

    /// <summary>Whether DWM is hiding the window even though IsWindowVisible says it's visible - a
    /// suspended UWP frame, or a window on another virtual desktop. Neither is something the user
    /// can see, so it's skipped both when capturing and when matching a launched window.</summary>
    internal static bool IsCloaked(IntPtr hWnd) =>
        NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
        && cloaked != 0;

    // Recognized so LayoutEntry.Url has something to act on - every one of these accepts
    // --new-window alongside a URL to force a genuinely new top-level window even when an
    // instance is already running, rather than reusing one as a new tab - see RunAsync's
    // navigatesBrowser handling, which relies on that new window actually appearing to poll for.
    private static readonly HashSet<string> BrowserExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
    };

    private static bool IsBrowserExecutable(string exeName) => BrowserExeNames.Contains(exeName);

    /// <summary>Whether programPath resolves to a recognized browser exe - used by LayoutEditorForm
    /// to decide whether to show the URL field, via the same .lnk-aware resolution RunAsync itself
    /// uses (see ResolveExeName) rather than a naive extension check.</summary>
    public static bool IsBrowserProgram(string programPath) =>
        !string.IsNullOrWhiteSpace(programPath) && IsBrowserExecutable(ResolveExeName(programPath));

    /// <summary>Turns LayoutEntry.Url's raw (possibly multi-line - one URL per line) text into a
    /// "--new-window url1 url2 ..." argument string. Every mainstream Chromium/Firefox-family
    /// browser opens one tab per URL passed alongside --new-window, all inside that same forced new
    /// window, rather than one new window per URL - exactly the "several tabs, one placed window"
    /// result a multi-URL entry wants.</summary>
    private static string BuildNewWindowArgs(string rawUrls)
    {
        var urls = rawUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return urls.Length == 0
            ? "--new-window"
            : "--new-window " + string.Join(' ', urls.Select(u => $"\"{u}\""));
    }

    // Recognized so LayoutEntry.Command has something to act on - unlike BrowserExeNames, none of
    // these are single-instance apps: every Process.Start of cmd/powershell/pwsh always creates its
    // own brand new process and window, so (unlike browsers) there's no --new-window-style flag
    // needed to avoid accidentally reusing/tabbing into an already-running one. WindowsTerminal.exe
    // is included too - on Windows 11 with Windows Terminal set as the default terminal app, a
    // window that looks like a plain PowerShell/cmd console is actually owned by
    // WindowsTerminal.exe, not the shell exe itself (see CaptureWindow's GetOwningExePath, which
    // resolves the top-level window's owning process), so leaving it out would mean Select
    // Window/Save Current Layout basically never detect a terminal on a stock Win11 setup.
    private const string WindowsTerminalExeName = "WindowsTerminal.exe";

    private static readonly HashSet<string> TerminalExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", WindowsTerminalExeName,
    };

    private static bool IsTerminalExecutable(string exeName) => TerminalExeNames.Contains(exeName);

    // File Explorer - recognized so LayoutEntry.FolderPath has something to act on, and so its
    // window matching only ever considers real folder windows (see SnapshotWindows) rather than
    // the taskbars and other shell windows explorer.exe also owns.
    private const string ExplorerExeName = "explorer.exe";

    private static bool IsExplorerExecutable(string exeName) =>
        string.Equals(exeName, ExplorerExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether programPath resolves to explorer.exe - used by LayoutEditorForm to decide
    /// whether to show the Folder field, via the same .lnk-aware resolution IsBrowserProgram uses.</summary>
    public static bool IsExplorerProgram(string programPath) =>
        !string.IsNullOrWhiteSpace(programPath) && IsExplorerExecutable(ResolveExeName(programPath));

    /// <summary>Whether programPath resolves to a recognized terminal exe - used by LayoutEditorForm
    /// to decide whether to show the Commands field, via the same .lnk-aware resolution
    /// IsBrowserProgram uses for the URL field.</summary>
    public static bool IsTerminalProgram(string programPath) =>
        !string.IsNullOrWhiteSpace(programPath) && IsTerminalExecutable(ResolveExeName(programPath));

    /// <summary>Whether programPath resolves specifically to WindowsTerminal.exe, as opposed to a
    /// directly-captured cmd.exe/powershell.exe/pwsh.exe - used by LayoutEditorForm to decide
    /// whether to show the shell picker (see LayoutEntry.TerminalShellExe), since WindowsTerminal.exe
    /// itself isn't a shell and BuildTerminalCommandArgs otherwise has no way to know which one a
    /// captured window was actually running.</summary>
    public static bool IsWindowsTerminalProgram(string programPath) =>
        !string.IsNullOrWhiteSpace(programPath)
        && string.Equals(ResolveExeName(programPath), WindowsTerminalExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns LayoutEntry.Command's raw (possibly multi-line - one command per line, with
    /// blank lines splitting separate tabs, see LayoutEditorForm.AddTabSeparator) text into an
    /// argument string that runs each tab's commands in order and leaves the window open afterward.
    /// Only WindowsTerminal.exe can actually open more than one tab; a directly-captured
    /// cmd.exe/powershell.exe/pwsh.exe console has no tab concept of its own, so every line there
    /// (blank separators included) just folds into one sequential run instead. Each shell has its
    /// own stay-open flag and command-separator syntax, so building one tab's args is per-exe rather
    /// than one-size-fits-all; an unrecognized shell exe (shouldn't happen - RunAsync only calls this
    /// when IsTerminalExecutable already matched) falls back to running nothing.
    ///
    /// exeName being WindowsTerminal.exe is a special case: it isn't itself a shell, so the command
    /// line it needs is the target shell's own exe name followed by that shell's normal arguments
    /// (e.g. "powershell.exe -NoExit -Command ...") rather than a flag of WindowsTerminal's own -
    /// entry.TerminalShellExe (set via LayoutEditorForm's shell picker) says which shell that is,
    /// defaulting to powershell.exe (Windows Terminal's own default profile on a stock install) if
    /// never chosen.</summary>
    private static string BuildTerminalCommandArgs(string exeName, LayoutEntry entry)
    {
        var tabs = SplitIntoTabs(entry.Command);
        if (tabs.Count == 0)
            return string.Empty;

        var isWindowsTerminal = string.Equals(exeName, WindowsTerminalExeName, StringComparison.OrdinalIgnoreCase);
        var shellExe = isWindowsTerminal ? entry.TerminalShellExe ?? "powershell.exe" : exeName;

        if (!isWindowsTerminal)
            return BuildShellArgs(shellExe, tabs.SelectMany(tab => tab));

        // WindowsTerminal.exe treats ';' in its own command line as a separator between multiple wt
        // actions (e.g. "new-tab ; split-pane"), splitting on it before the quoted -Command string
        // ever reaches the shell - so a semicolon-joined multi-command tab (see BuildShellArgs's
        // "; " join) gets torn apart, with the tail fragment then launched as if it were its own
        // program (surfacing as a "file not found" error for that fragment). "\;" is wt's documented
        // escape for a literal semicolon (see Microsoft's command-line-arguments docs), so each
        // tab's own args get backslash-escaped before being joined with wt's real, unescaped ";" -
        // the only semicolons that reach wt unescaped are the ones actually meant to open a new tab.
        // No "--" marker is needed - it isn't a real wt option; per Microsoft's own examples a shell
        // name followed directly by its own flags (e.g. "new-tab PowerShell -c Start-Service") is
        // handled fine.
        var tabArgs = tabs
            .Select(tab => BuildShellArgs(shellExe, tab))
            .Where(args => args.Length > 0)
            .Select(args => $"new-tab {shellExe} {args}".Replace(";", "\\;"));

        return string.Join(" ; ", tabArgs);
    }

    private static string BuildShellArgs(string shellExe, IEnumerable<string> commands) => shellExe.ToLowerInvariant() switch
    {
        "cmd.exe" => $"/K \"{string.Join(" && ", commands)}\"",
        "powershell.exe" or "pwsh.exe" => $"-NoExit -Command \"{string.Join("; ", commands)}\"",
        _ => string.Empty,
    };

    /// <summary>Splits Command's raw text into one command list per tab, on blank lines - the only
    /// place a blank line can come from is LayoutEditorForm.AddTabSeparator, never free typing (the
    /// Commands editor is a row list, not a free-text box). A separator with nothing between it and
    /// the next one (or the start/end of the text) starts no new tab at all, rather than an empty
    /// one - current only turns into an entry in tabs once a real command line lands in it.</summary>
    private static List<List<string>> SplitIntoTabs(string? raw)
    {
        var tabs = new List<List<string>>();
        List<string>? current = null;
        foreach (var rawLine in (raw ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = new List<string>();
                tabs.Add(current);
            }

            current.Add(line);
        }

        return tabs;
    }

    /// <summary>Launches every entry up front (not one at a time, then waits) so a layout with
    /// several slow-starting apps doesn't take launch-time times entry-count to finish, then polls
    /// all of them together until each either shows up or the shared deadline passes. An entry
    /// that fails to launch, or whose window never appears in time, doesn't stop the rest of the
    /// layout from running - but is no longer silently skipped either; its own program file name is
    /// collected into the returned list so a caller can actually tell (see LayoutManager.
    /// RunLayoutAsync's own LaunchFailed event) instead of the failure being indistinguishable from
    /// "ran fine, just slow to place."
    ///
    /// Every entry always gets a fresh launch, never an already-running window handed to it - a
    /// layout is meant to reliably produce the same set of windows on every run, and reusing
    /// whatever happened to already be open lets two entries for the same exe fight over one
    /// window (only one of them ever gets placed; the other silently finds nothing left to claim
    /// and never moves) whenever that exe is already running when the layout starts.
    ///
    /// claimed tracks every window handle already handed to some entry in this run, checked by
    /// ClaimWindow before any entry takes one - without it, two entries for the same exe (two
    /// Notepad windows, say) would each independently pick "the largest matching window" and both
    /// grab the same one, leaving the other never placed. See LayoutEntry.WindowTitleHint for how
    /// ties between several unclaimed candidates are broken.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<LayoutEntry> entries)
    {
        var claimed = new HashSet<IntPtr>();
        var pending = new List<PendingLaunch>();
        var failures = new List<string>();

        // Each entry waits its own launcher's timeout (15s unless HelperHostedApps says longer, e.g.
        // Steam's cold start), all capped by one run-wide deadline sized to the slowest of them - so a
        // layout without Steam in it behaves exactly as it did with the single shared 15s deadline.
        var runStart = Environment.TickCount64;
        var runDeadline = runStart + entries
            .Where(e => !string.IsNullOrWhiteSpace(e.ProgramPath))
            .Select(e => HelperHostedApps.TimeoutFor(ResolveExeName(HelperHostedApps.ToLauncherPath(e.ProgramPath))) ?? LaunchTimeoutMs)
            .DefaultIfEmpty(LaunchTimeoutMs)
            .Max();

        // Two entries for the same exe can't be launched together: both would be polling for a
        // new window (see "pending" below), and a freshly-added entry has no reliable
        // WindowTitleHint to tell them apart (even a captured one's title can be generic/transient
        // right as the window opens), so ClaimWindow falls back to "largest window" - a guess that
        // has nothing to do with which process asked for which window. That guess is
        // deterministic, not random, so the result isn't an occasional glitch but every run
        // consistently handing each window the wrong entry's URL/placement. Queued per exe name
        // instead: only the first entry for a given exe launches here, the rest wait until their
        // predecessor resolves (claimed, or otherwise finished) before launching, so each one's
        // "before" snapshot always already accounts for every earlier entry's window and the diff
        // stays unambiguous.
        var queuedEntries = new Dictionary<string, Queue<LayoutEntry>>(StringComparer.OrdinalIgnoreCase);
        var launchingExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void LaunchNext(string exeName)
        {
            if (queuedEntries.TryGetValue(exeName, out var queue) && queue.Count > 0)
                Launch(queue.Dequeue(), exeName);
        }

        void Launch(LayoutEntry entry, string exeName)
        {
            var isBrowser = IsBrowserExecutable(exeName);
            var isTerminal = IsTerminalExecutable(exeName);
            var hasUrl = !string.IsNullOrWhiteSpace(entry.Url);
            var hasCommand = !string.IsNullOrWhiteSpace(entry.Command);

            // An entry saved before capture knew about helper-hosted apps still names the helper
            // (steamwebhelper.exe) - launch the real launcher instead, and match windows by the
            // helper, which is what actually owns them.
            var programPath = HelperHostedApps.ToLauncherPath(entry.ProgramPath);
            var matchExe = HelperHostedApps.WindowExeFor(exeName);
            var before = SnapshotWindows(matchExe);

            // A browser always gets --new-window here, with its URL if it has one and bare
            // otherwise, so a genuinely new, pollable top-level window appears - without it,
            // launching a browser exe that's already running elsewhere in this same layout run can
            // just add a tab to another entry's window (or do nothing detectable at all) instead
            // of the separate window this entry needs to place. A terminal always launches its own
            // new process/window regardless (unlike a browser, it's never single-instance), so it
            // needs no equivalent flag.
            // Explorer always opens a new folder window per launch (the new explorer.exe process
            // just hands the request to the running shell and exits), so the folder is all it needs.
            var arguments = isBrowser
                ? (hasUrl ? BuildNewWindowArgs(entry.Url!) : "--new-window")
                : isTerminal && hasCommand
                    ? BuildTerminalCommandArgs(exeName, entry)
                    : IsExplorerExecutable(exeName)
                        ? ExplorerWindows.BuildLaunchArgs(entry.FolderPath)
                        : entry.Arguments ?? string.Empty;

            // Windows Terminal honours its own "windowingBehavior" setting, which can route a
            // launch into an already-open window as a new tab - no new window would appear to poll
            // for. "-w new" forces a separate top-level window regardless of that setting.
            if (string.Equals(exeName, WindowsTerminalExeName, StringComparison.OrdinalIgnoreCase))
                arguments = string.IsNullOrWhiteSpace(arguments) ? "-w new" : $"-w new {arguments}";

            try
            {
                // Store/MSIX apps (Windows Terminal included) are started through their alias or
                // package identity rather than their version-stamped WindowsApps path, which
                // disappears on every app update - see PackagedAppLauncher. Null means "not a
                // packaged app", so it's launched normally.
                switch (PackagedAppLauncher.TryLaunch(programPath, arguments))
                {
                    case false:
                        throw new Win32Exception();
                    case null:
                        Process.Start(new ProcessStartInfo(programPath)
                        {
                            Arguments = arguments,
                            UseShellExecute = true,
                        });
                        break;
                }
            }
            catch (Win32Exception)
            {
                // Same "file may have been moved/deleted" case FenceForm.OpenItem already shrugs
                // off - nothing to place if it never launched. Recorded now (rather than silently
                // dropped) so the caller can actually surface it instead of a program just quietly
                // never showing up.
                failures.Add(Path.GetFileName(programPath));
                LaunchNext(exeName);
                return;
            }

            var launchedAt = Environment.TickCount64;
            var deadline = Math.Min(runDeadline, launchedAt + (HelperHostedApps.TimeoutFor(exeName) ?? LaunchTimeoutMs));
            pending.Add(new PendingLaunch(entry, exeName, matchExe, programPath, before, launchedAt, deadline));
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ProgramPath))
                continue;

            var exeName = ResolveExeName(HelperHostedApps.ToLauncherPath(entry.ProgramPath));

            if (!launchingExes.Add(exeName))
            {
                if (!queuedEntries.TryGetValue(exeName, out var queue))
                    queuedEntries[exeName] = queue = new Queue<LayoutEntry>();
                queue.Enqueue(entry);
                continue;
            }

            Launch(entry, exeName);
        }

        while (Environment.TickCount64 < runDeadline
            && (pending.Count > 0 || queuedEntries.Values.Any(q => q.Count > 0)))
        {
            await Task.Delay(PollIntervalMs);
            var now = Environment.TickCount64;

            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                var current = SnapshotWindows(p.MatchExe);
                var after = new HashSet<IntPtr>(current);
                after.ExceptWith(p.Before);

                // A single-instance launcher that was already open just brought its existing window
                // forward - once the grace period passes without a new one, take that window.
                if (after.Count == 0
                    && HelperHostedApps.IsSingleInstance(p.ExeName)
                    && now - p.LaunchedAt >= SingleInstanceGraceMs)
                {
                    after = current;
                }

                var claim = after.Count == 0 ? IntPtr.Zero : ClaimWindow(after, p.Entry.WindowTitleHint, claimed);
                if (claim == IntPtr.Zero)
                {
                    if (now < p.Deadline)
                        continue;

                    // Out of time for this entry (launched fine, but nothing matching showed up) -
                    // fail it now rather than at the run-wide deadline, and free its exe's queue.
                    failures.Add(Path.GetFileName(p.ProgramPath));
                    pending.RemoveAt(i);
                    LaunchNext(p.ExeName);
                    continue;
                }

                PlaceWindow(claim, p.Entry);
                pending.RemoveAt(i);

                // This exe's window is spoken for now, so it's safe to let the next queued entry
                // for the same exe launch - its own "before" snapshot will include the window just
                // claimed above, keeping the two from ever racing each other.
                LaunchNext(p.ExeName);
            }
        }

        // Whatever's still pending never got a window placed within the deadline (launched fine,
        // but nothing matching ever showed up); whatever's still queued never even got launched at
        // all (its predecessor for the same exe was still pending when time ran out) - both read as
        // "didn't launch" from the caller's own perspective, same as an outright Win32Exception.
        failures.AddRange(pending.Select(p => Path.GetFileName(p.ProgramPath)));
        failures.AddRange(queuedEntries.Values.SelectMany(q => q)
            .Select(entry => Path.GetFileName(HelperHostedApps.ToLauncherPath(entry.ProgramPath))));

        return failures;
    }

    private sealed record PendingLaunch(
        LayoutEntry Entry,
        string ExeName,
        string MatchExe,
        string ProgramPath,
        HashSet<IntPtr> Before,
        long LaunchedAt,
        long Deadline);

    /// <summary>Picks one still-unclaimed window out of candidates for a single entry, and marks it
    /// claimed so no later entry in the same RunAsync call can also take it. Prefers an exact title
    /// match against titleHint (the entry's captured WindowTitleHint) when one exists among the
    /// unclaimed candidates; otherwise falls back to the same "largest window" heuristic used
    /// before claim-tracking existed - still a reasonable guess when there's no hint, or the titled
    /// window already changed (e.g. a browser tab that navigated since capture).</summary>
    private static IntPtr ClaimWindow(IEnumerable<IntPtr> candidates, string? titleHint, HashSet<IntPtr> claimed)
    {
        var unclaimed = candidates.Where(h => !claimed.Contains(h)).ToList();
        if (unclaimed.Count == 0)
            return IntPtr.Zero;

        var chosen = IntPtr.Zero;
        if (!string.IsNullOrEmpty(titleHint))
            chosen = unclaimed.Find(h => string.Equals(GetWindowTitle(h), titleHint, StringComparison.Ordinal));

        if (chosen == IntPtr.Zero)
            chosen = LargestWindow(unclaimed);

        if (chosen != IntPtr.Zero)
            claimed.Add(chosen);

        return chosen;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>"Save Current Layout" - one LayoutEntry per currently visible, real top-level window
    /// (see CaptureWindow for the per-window rules), captured Maximized if the window already is (or,
    /// for a minimized window, was before it got minimized - see TryGetCaptureBounds), Custom (its
    /// exact restored rect, as fractions of its monitor's WorkingArea) otherwise. A minimized window
    /// is captured the same as any other, using its restore position rather than its current
    /// off-screen one - a saved layout describes where a program's window belongs, not whether it
    /// happened to be minimized at capture time.
    ///
    /// Two windows belonging to the same exe still both become separate entries here, but RunAsync's
    /// claim-tracking (see ClaimWindow) now keeps them from fighting over the same window on replay -
    /// each entry's WindowTitleHint (this window's title at capture time) lets it prefer its own
    /// window back over whichever one another entry already claimed.</summary>
    public static List<LayoutEntry> CaptureCurrentLayout()
    {
        var entries = new List<LayoutEntry>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (CaptureWindow(hWnd) is { } entry)
                entries.Add(entry);
            return true;
        }, IntPtr.Zero);

        return entries;
    }

    /// <summary>Builds the LayoutEntry a single window would contribute to CaptureCurrentLayout -
    /// also used directly by WindowPickerOverlay ("Select Window" in the layout editor) to capture
    /// one specific window the user clicked on, with the exact same rules for what's usable and where
    /// it currently sits. Returns null for anything CaptureCurrentLayout would have skipped: not a
    /// real visible top-level window, known shell chrome (see ExcludedCaptureClassNames), this app's
    /// own windows, cloaked windows (see IsCloaked), hosts that can't be relaunched (see
    /// ExcludedCaptureExeNames), anything whose owning exe can't be resolved, or any explorer.exe window that
    /// isn't a File Explorer folder window (see the explorer.exe check below). A folder window is
    /// captured along with the folder it's showing (LayoutEntry.FolderPath).</summary>
    internal static LayoutEntry? CaptureWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero
            || !NativeMethods.IsWindowVisible(hWnd)
            || NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero
            || IsCloaked(hWnd)
            || ExcludedCaptureClassNames.Contains(GetClassName(hWnd)))
        {
            return null;
        }

        if (!TryGetCaptureBounds(hWnd, out var bounds, out var isMaximized))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out var ownerPid);
        if (ownerPid == Environment.ProcessId)
        {
            // This app's own windows (the fences themselves, chiefly) - compared by PID rather
            // than by matching exePath against Environment.ProcessPath as strings, since that
            // string comparison can silently fail to match (path casing/normalization) and let a
            // fence window slip through as a bogus "DesktopTool.exe" capture entry.
            return null;
        }

        var exePath = GetOwningExePath(hWnd);
        if (string.IsNullOrEmpty(exePath) || ExcludedCaptureExeNames.Contains(Path.GetFileName(exePath)))
            return null;

        var isExplorer = IsExplorerExecutable(Path.GetFileName(exePath));
        if (isExplorer && !IsExplorerFolderWindow(hWnd))
        {
            // Everything else explorer.exe owns - the desktop, every taskbar, and stray shell-chrome
            // windows ExcludedCaptureClassNames' exact list doesn't catch (confirmed via testing:
            // secondary-monitor taskbars showed up under a class name not in that list). Only a
            // real File Explorer folder window is worth capturing, and it's recognizable by class.
            return null;
        }

        var screen = Screen.FromRectangle(bounds);
        var title = GetWindowTitle(hWnd);
        var entry = new LayoutEntry
        {
            // A helper-hosted window (Steam's steamwebhelper.exe) is stored as its launcher, since
            // starting the helper itself opens nothing - see HelperHostedApps.
            ProgramPath = HelperHostedApps.ToLauncherPath(exePath),
            TargetMonitor = screen.DeviceName,
            WindowTitleHint = string.IsNullOrEmpty(title) ? null : title,
            FolderPath = isExplorer ? ExplorerWindows.TryGetFolderPath(hWnd) : null,
        };

        if (isMaximized)
        {
            entry.Placement = LayoutPlacement.Maximized;
        }
        else
        {
            var area = screen.WorkingArea;
            entry.Placement = LayoutPlacement.Custom;
            entry.CustomX = area.Width > 0 ? Math.Clamp((bounds.X - area.X) / (double)area.Width, 0.0, 1.0) : 0.0;
            entry.CustomY = area.Height > 0 ? Math.Clamp((bounds.Y - area.Y) / (double)area.Height, 0.0, 1.0) : 0.0;
            entry.CustomWidth = area.Width > 0 ? Math.Clamp(bounds.Width / (double)area.Width, 0.05, 1.0) : 1.0;
            entry.CustomHeight = area.Height > 0 ? Math.Clamp(bounds.Height / (double)area.Height, 0.05, 1.0) : 1.0;
        }

        return entry;
    }

    /// <summary>Resolves the rect to record and whether it counts as maximized. For a normal window
    /// this is just GetWindowRect/IsZoomed, same as before; for a minimized one, GetWindowRect
    /// returns its off-screen iconic position instead of anywhere useful, so GetWindowPlacement's
    /// rcNormalPosition (its restore rect) is used instead, along with WPF_RESTORETOMAXIMIZED to
    /// tell a minimized-from-maximized window apart from a minimized-from-normal one - IsZoomed only
    /// reflects the window's current physical style bits, which a minimized window never carries
    /// even if maximized is what it'll restore to.</summary>
    private static bool TryGetCaptureBounds(IntPtr hWnd, out Rectangle bounds, out bool isMaximized)
    {
        if (NativeMethods.IsIconic(hWnd))
        {
            var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!NativeMethods.GetWindowPlacement(hWnd, ref placement))
            {
                bounds = Rectangle.Empty;
                isMaximized = false;
                return false;
            }

            var rect = placement.rcNormalPosition;
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                bounds = Rectangle.Empty;
                isMaximized = false;
                return false;
            }

            bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            isMaximized = (placement.flags & NativeMethods.WPF_RESTORETOMAXIMIZED) != 0;
            return true;
        }

        // The visible frame, not GetWindowRect - PlaceWindow lines the visible frame up with the
        // resolved rect, so capturing the outer rect instead would grow every window by its
        // invisible borders on each save/replay round trip.
        if (!TryGetVisibleFrame(hWnd, out var winRect) || winRect.Right <= winRect.Left || winRect.Bottom <= winRect.Top)
        {
            bounds = Rectangle.Empty;
            isMaximized = false;
            return false;
        }

        bounds = new Rectangle(winRect.Left, winRect.Top, winRect.Right - winRect.Left, winRect.Bottom - winRect.Top);
        isMaximized = NativeMethods.IsZoomed(hWnd);
        return true;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ResolveExeName(string programPath)
    {
        if (string.Equals(Path.GetExtension(programPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            && ShortcutResolver.ResolveTarget(programPath) is { Length: > 0 } target)
        {
            return Path.GetFileName(target);
        }

        return Path.GetFileName(programPath);
    }

    private static bool IsExplorerFolderWindow(IntPtr hWnd) =>
        string.Equals(GetClassName(hWnd), ExplorerWindows.FolderWindowClass, StringComparison.Ordinal);

    /// <summary>Every visible, unowned top-level window owned by exeName. For explorer.exe only
    /// folder windows count - otherwise a taskbar or other shell window appearing mid-run could be
    /// the "new window" an Explorer entry claims and moves.</summary>
    private static HashSet<IntPtr> SnapshotWindows(string exeName)
    {
        var matches = new HashSet<IntPtr>();
        var explorerOnly = IsExplorerExecutable(exeName);
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd)
                && NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) == IntPtr.Zero
                && (!explorerOnly || IsExplorerFolderWindow(hWnd))
                && !IsCloaked(hWnd)
                && string.Equals(GetOwningExeName(hWnd), exeName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);
        return matches;
    }

    private static string? GetOwningExeName(IntPtr hWnd) =>
        GetOwningExePath(hWnd) is { } path ? Path.GetFileName(path) : null;

    private static string? GetOwningExePath(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Process exited between enumeration and lookup, or MainModule is inaccessible - an
            // elevated/protected process, or a 32/64-bit bitness mismatch with this process. Either
            // way, just not usable.
            return null;
        }
    }

    private static IntPtr LargestWindow(IEnumerable<IntPtr> handles)
    {
        var best = IntPtr.Zero;
        var bestArea = -1L;
        foreach (var hWnd in handles)
        {
            if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                continue;

            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
        }

        return best;
    }

    private static void PlaceWindow(IntPtr hWnd, LayoutEntry entry)
    {
        if (hWnd == IntPtr.Zero)
            return;

        var screen = ResolveScreen(entry.TargetMonitor);

        // Un-minimize/un-maximize first - SetWindowPos alone doesn't restore a minimized window,
        // and a still-maximized one can ignore the resize that follows.
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        if (entry.Placement == LayoutPlacement.Maximized)
        {
            // Move onto the target monitor *before* maximizing - Windows maximizes onto whichever
            // monitor the window already mostly overlaps, not wherever this call happens to run
            // from, so a bare SW_MAXIMIZE here could maximize onto the wrong screen entirely.
            NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, screen.WorkingArea.X, screen.WorkingArea.Y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MAXIMIZE);
        }
        else
        {
            var rect = ResolveRect(screen, entry);
            NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            // rect is where the *visible* frame should land. Win10/11 windows carry invisible
            // resize borders outside it (part of GetWindowRect/SetWindowPos's rect), so without
            // this two side-by-side windows show a ~14px gap instead of touching like snapped
            // windows do. The insets can only be read once the window is on its target monitor
            // (they scale with its DPI), hence the second SetWindowPos.
            if (TryGetFrameInsets(hWnd, out var insets)
                && (insets.Left != 0 || insets.Top != 0 || insets.Right != 0 || insets.Bottom != 0))
            {
                NativeMethods.SetWindowPos(hWnd, IntPtr.Zero,
                    rect.X - insets.Left,
                    rect.Y - insets.Top,
                    rect.Width + insets.Left + insets.Right,
                    rect.Height + insets.Top + insets.Bottom,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        // Minimized last, on top of whatever placement just ran above - not an alternative to it
        // (see LayoutEntry.Minimized) - so the window's restore rect is the placement just set
        // rather than whatever size the window happened to launch at.
        if (entry.Minimized)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);
    }

    /// <summary>The window's visible frame (DWM's extended frame bounds), falling back to
    /// GetWindowRect when DWM can't report one.</summary>
    internal static bool TryGetVisibleFrame(IntPtr hWnd, out RECT frame)
    {
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<RECT>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            return true;
        }

        return NativeMethods.GetWindowRect(hWnd, out frame);
    }

    /// <summary>How far GetWindowRect extends past the visible frame on each side - the invisible
    /// resize borders. Each field is non-negative in the direction it extends (Left = how far the
    /// outer rect's left edge sits left of the frame, and so on).</summary>
    internal static bool TryGetFrameInsets(IntPtr hWnd, out RECT insets)
    {
        insets = default;
        if (!NativeMethods.GetWindowRect(hWnd, out var outer)
            || NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
        {
            return false;
        }

        insets = new RECT
        {
            Left = Math.Max(0, frame.Left - outer.Left),
            Top = Math.Max(0, frame.Top - outer.Top),
            Right = Math.Max(0, outer.Right - frame.Right),
            Bottom = Math.Max(0, outer.Bottom - frame.Bottom),
        };
        return true;
    }

    private static Screen ResolveScreen(string deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = Array.Find(Screen.AllScreens, s => s.DeviceName == deviceName);
            if (match is not null)
                return match;
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    /// <summary>Always resolved against the target monitor's live WorkingArea, never a stored
    /// pixel rect - see LayoutPlacement's own doc comment for why - Custom included, which stores
    /// fractions of that same WorkingArea rather than raw pixels for exactly this reason.</summary>
    internal static Rectangle ResolveRect(Screen screen, LayoutEntry entry)
    {
        var area = screen.WorkingArea;
        var halfWidth = area.Width / 2;
        var halfHeight = area.Height / 2;

        return entry.Placement switch
        {
            LayoutPlacement.LeftHalf => new Rectangle(area.X, area.Y, halfWidth, area.Height),
            LayoutPlacement.RightHalf => new Rectangle(area.X + halfWidth, area.Y, area.Width - halfWidth, area.Height),
            LayoutPlacement.TopHalf => new Rectangle(area.X, area.Y, area.Width, halfHeight),
            LayoutPlacement.BottomHalf => new Rectangle(area.X, area.Y + halfHeight, area.Width, area.Height - halfHeight),
            LayoutPlacement.TopLeftQuarter => new Rectangle(area.X, area.Y, halfWidth, halfHeight),
            LayoutPlacement.TopRightQuarter => new Rectangle(area.X + halfWidth, area.Y, area.Width - halfWidth, halfHeight),
            LayoutPlacement.BottomLeftQuarter => new Rectangle(area.X, area.Y + halfHeight, halfWidth, area.Height - halfHeight),
            LayoutPlacement.BottomRightQuarter => new Rectangle(area.X + halfWidth, area.Y + halfHeight, area.Width - halfWidth, area.Height - halfHeight),
            LayoutPlacement.Custom => new Rectangle(
                area.X + (int)(entry.CustomX * area.Width),
                area.Y + (int)(entry.CustomY * area.Height),
                (int)(entry.CustomWidth * area.Width),
                (int)(entry.CustomHeight * area.Height)),
            _ => area, // Maximized is handled separately in PlaceWindow via SW_MAXIMIZE
        };
    }
}
