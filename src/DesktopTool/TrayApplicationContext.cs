using DesktopTool.Features.ClaudePipeline;
using DesktopTool.Features.ClaudePipeline.UI;
using DesktopTool.Features.Fences;
using DesktopTool.Features.FolderFences;
using DesktopTool.Features.Layouts;
using DesktopTool.Features.Layouts.UI;
using DesktopTool.Features.Readme.UI;
using DesktopTool.Features.WidgetManager;
using DesktopTool.Features.WidgetManager.UI;
using DesktopTool.UI;

namespace DesktopTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly FenceManager _fenceManager = new();
    // Only ever hands _fenceManager to a FolderFenceForm's own base constructor, for the shared
    // snap-against-other-widgets behavior every LayeredWidgetForm gets for free - see
    // FolderFenceManager's own constructor doc comment.
    private readonly FolderFenceManager _folderFenceManager;
    private readonly LayoutManager _layoutManager = new();

    private readonly LayoutLauncherStore _layoutLauncherStore = new();
    private readonly WidgetManagerStore _widgetManagerStore = new();

    private readonly ClaudePipelineFeatureStore _claudePipelineFeatureStore = new();
    private readonly ClaudePipelineWidgetStore _claudePipelineWidgetStore = new();
    private readonly ClaudePipelineManager _claudePipelineManager;

    // At most one editor open at a time - OnManageLayouts activates this instead of opening a
    // second copy, the same "don't duplicate, just surface the existing one" idea FenceManager's
    // own SnapLines edit mode already follows for its overlay/panel pair.
    private LayoutEditorForm? _layoutEditor;

    // Same "create once, reuse/activate the existing one" idea as _layoutEditor above.
    private ClaudePipelineEditorForm? _pipelineEditor;

    // Same "create once, reuse/activate the existing one" idea as _layoutEditor above - opened via
    // Widget Manager's own "?" button (see WidgetManagerWidget.HelpRequested/OpenReadme). Unlike
    // _layoutLauncher/_widgetManager below, never shown at startup and fully disposed on close
    // rather than hidden-and-reused - see ReadmeWidget's own class comment for why.
    private ReadmeWidget? _readmeForm;

    // Unlike _layoutEditor, created once up front (see the constructor) and never recreated for the
    // rest of the process - this is meant to be a persistent desktop element like a Fence, not a
    // window opened fresh each time from the tray. No tray item of its own toggles its Visible state
    // any more - only Widget Manager's own Layout Launcher row does (LayoutLauncherWidget.
    // ToggleVisible), same "created once, never disposed" object either way.
    private readonly LayoutLauncherWidget _layoutLauncher;

    // Same "created once up front, never recreated" reasoning as _layoutLauncher above - toggled via
    // Widget Manager's own Claude Pipeline row rather than a top-level tray item of its own.
    private readonly ClaudePipelineWidget _claudePipeline;

    // Same "created once up front, never recreated" reasoning as _layoutLauncher above - toggled via
    // the tray's own top-level "Widget Manager" item rather than opened fresh each time.
    private readonly WidgetManagerWidget _widgetManager;

    public TrayApplicationContext()
    {
        _folderFenceManager = new FolderFenceManager(_fenceManager);
        // Dropping a single folder onto an empty fence converts it into a folder fence instead of
        // adding the folder as an ordinary shortcut - see FenceForm.FolderDroppedOnEmptyFence's own
        // doc comment for why this lives here rather than on either manager directly.
        _fenceManager.FolderDroppedOnEmptyFence += (_, e) =>
        {
            if (_fenceManager.TakeForConversion(e.FenceId) is { } source)
                _folderFenceManager.ConvertFromFence(source, e.FolderPath);
        };

        _layoutManager.Load();
        _layoutManager.LaunchFailed += OnLayoutLaunchFailed;

        var layoutLauncherModel = _layoutLauncherStore.Load();
        _layoutLauncher = new LayoutLauncherWidget(_layoutManager, _fenceManager, layoutLauncherModel, _layoutLauncherStore);
        _layoutLauncher.ManageLayoutsRequested += (_, profileId) => OpenLayoutEditor(profileId);

        _claudePipelineManager = new ClaudePipelineManager(_claudePipelineFeatureStore);
        _claudePipelineManager.Load();
        var claudePipelineModel = _claudePipelineWidgetStore.Load();
        _claudePipeline = new ClaudePipelineWidget(_claudePipelineManager, _fenceManager, claudePipelineModel, _claudePipelineWidgetStore);
        _claudePipeline.ManageFeaturesRequested += (_, featureId) => OpenPipelineEditor(featureId);

        // Needs _layoutLauncher/_claudePipeline to already exist - their own rows read/toggle those
        // widgets' Visible directly rather than through a separate manager class.
        var widgetManagerModel = _widgetManagerStore.Load();
        _widgetManager = new WidgetManagerWidget(_fenceManager, _layoutLauncher, _claudePipeline, _folderFenceManager, widgetManagerModel, _widgetManagerStore);
        _widgetManager.EditLayoutsRequested += (_, _) => OpenLayoutEditor(null);
        _widgetManager.EditFeaturesRequested += (_, _) => OpenPipelineEditor(null);
        _widgetManager.HelpRequested += (_, _) => OpenReadme();

        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = AppTheme.Body,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Font,
            // No item carries an icon, so the reserved image-margin strip down the left edge would
            // otherwise just be an empty light-gray band next to every row. ShowCheckMargin defaults
            // to false, not true - without turning it on explicitly here, suppressing the image
            // margin left no margin at all for a checkmark to render into, silently dropping the
            // checkmark on the two toggles below entirely instead of just moving it.
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };
        // At the top since it's the entry point into everything else this menu used to list
        // directly (New Fence/Manage Snap Lines.../Layout Launcher toggle/Add Recycle Bin all moved
        // onto Widget Manager's own rows instead - see WidgetManagerWidget). CheckOnClick would fight
        // ToggleVisible's own idea of the current state (it flips Checked itself before the Click
        // handler runs, same as startupItem/hiddenFilesItem below already avoid) - toggled and
        // reflected explicitly instead, still "read fresh every open" like those.
        var widgetManagerItem = new ToolStripMenuItem("Widget Manager");
        widgetManagerItem.Click += (_, _) => _widgetManager.ToggleVisible();
        menu.Opening += (_, _) => widgetManagerItem.Checked = _widgetManager.Visible;
        menu.Items.Add(widgetManagerItem);
        menu.Items.Add("Show/Hide All", null, OnShowHideAll);
        menu.Items.Add(new ToolStripSeparator());
        // Checked reflects the registry Run key's actual current state (see StartupManager) rather
        // than a separately-persisted flag - read fresh every time the menu opens so an external
        // change (e.g. a user manually editing the Run key) never leaves this showing stale.
        var startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        startupItem.Click += OnToggleStartup;
        menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled;
        menu.Items.Add(startupItem);
        // Same "checked reflects the actual current state, read fresh every open" approach as
        // Start with Windows above - this is a system-wide Explorer setting, not something Fence
        // Tool owns, so a user (or another app) changing it outside this menu should never leave
        // the checkbox showing stale.
        var hiddenFilesItem = new ToolStripMenuItem("Show Hidden Files") { CheckOnClick = true };
        hiddenFilesItem.Click += OnToggleHiddenFiles;
        menu.Opening += (_, _) => hiddenFilesItem.Checked = HiddenFilesManager.IsEnabled;
        menu.Items.Add(hiddenFilesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Fence Tool",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += OnShowHideAll;

        _fenceManager.LoadAndShowAll();
        _folderFenceManager.LoadAndShowAll();

        // Only actually shown if it was left visible last session - Show() itself doesn't touch
        // Visible in the model the way ToggleVisible does, so this doesn't re-persist a value that's
        // already exactly what was just loaded.
        if (layoutLauncherModel.Visible)
            _layoutLauncher.Show();
        if (claudePipelineModel.Visible)
            _claudePipeline.Show();
        if (widgetManagerModel.Visible)
            _widgetManager.Show();

        // Widget Manager's own Fences/Layout Launcher rows were painted (see its constructor's own
        // forced RenderAndPresent) before LoadAndShowAll/Show above ran - see RefreshRowStates' own
        // doc comment for why that leaves them showing stale "Off" without this.
        _widgetManager.RefreshRowStates();
    }

    // CheckOnClick already flipped the item's own Checked before this fires - just persist
    // whatever it now shows.
    private void OnToggleStartup(object? sender, EventArgs e) =>
        StartupManager.SetEnabled(((ToolStripMenuItem)sender!).Checked);

    // Doesn't force the desktop to visually pick this up - see README's Tray menu limitations.
    private void OnToggleHiddenFiles(object? sender, EventArgs e) =>
        HiddenFilesManager.SetEnabled(((ToolStripMenuItem)sender!).Checked);

    /// <summary>The only current subscriber to LayoutManager.LaunchFailed - a balloon tip is enough
    /// for something that isn't blocking (the rest of the layout still ran), and doesn't need its own
    /// dialog to dismiss. Named programs, not just a generic "something failed" - see WindowPlacer.
    /// RunAsync's own doc comment for the two ways an entry ends up here (never launched at all, or
    /// launched but no window ever showed up in time).</summary>
    private void OnLayoutLaunchFailed(object? sender, (string ProfileName, IReadOnlyList<string> FailedPrograms) e)
    {
        var programs = string.Join(", ", e.FailedPrograms);
        _trayIcon.ShowBalloonTip(5000, $"Layout \"{e.ProfileName}\" didn't fully start",
            $"Didn't launch: {programs}", ToolTipIcon.Warning);
    }

    private void OpenLayoutEditor(Guid? initialProfileId)
    {
        if (_layoutEditor is { IsDisposed: false })
        {
            if (initialProfileId is { } id)
                _layoutEditor.SelectProfileById(id);
            _layoutEditor.Activate();
            return;
        }

        _layoutEditor = new LayoutEditorForm(_layoutManager, initialProfileId);
        _layoutEditor.FormClosed += (_, _) => _layoutEditor = null;
        _layoutEditor.Show();
    }

    private void OpenPipelineEditor(Guid? initialFeatureId)
    {
        if (_pipelineEditor is { IsDisposed: false })
        {
            if (initialFeatureId is { } id)
                _pipelineEditor.SelectFeatureById(id);
            _pipelineEditor.Activate();
            return;
        }

        _pipelineEditor = new ClaudePipelineEditorForm(_claudePipelineManager, initialFeatureId);
        _pipelineEditor.FormClosed += (_, _) => _pipelineEditor = null;
        _pipelineEditor.Show();
    }

    private void OpenReadme()
    {
        if (_readmeForm is { IsDisposed: false })
        {
            _readmeForm.Activate();
            return;
        }

        _readmeForm = new ReadmeWidget(_fenceManager);
        _readmeForm.FormClosed += (_, _) => _readmeForm = null;
        _readmeForm.Show();
    }

    private void OnShowHideAll(object? sender, EventArgs e)
    {
        var visible = !(_fenceManager.AnyVisible || _folderFenceManager.AnyVisible);
        _fenceManager.SetAllVisible(visible);
        _folderFenceManager.SetAllVisible(visible);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _layoutEditor?.Dispose();
        _pipelineEditor?.Dispose();
        _readmeForm?.Dispose();
        _layoutLauncher.Shutdown();
        _claudePipeline.Shutdown();
        _widgetManager.Shutdown();
        _fenceManager.Dispose();
        _folderFenceManager.Dispose();
        ExitThread();
    }
}
