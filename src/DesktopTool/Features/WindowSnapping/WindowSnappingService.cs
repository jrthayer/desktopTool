using DesktopTool.Features.WindowSnapping.Native;

namespace DesktopTool.Features.WindowSnapping;

/// <summary>Owns the app-wide Window Snapping switch (Widget Manager's Window Snapping row) and,
/// while it's on, the WindowSnapper that does the work plus the WindowsSnapOverride that turns
/// off Windows' own stretch-to-full-height and drag-to-edge docking, which fight it.
/// Turning it off disposes both: nothing is hooked, and Windows' own setting is put back. Create it
/// on the UI thread - WindowSnapper's WinEvent callbacks arrive through that thread's message loop.</summary>
public sealed class WindowSnappingService : IDisposable
{
    private readonly WindowSnappingStore _store = new();
    private readonly WindowSnappingSettings _settings;
    private WindowSnapper? _snapper;
    private WindowsSnapOverride? _windowsSnapOverride;

    public WindowSnappingService()
    {
        _settings = _store.Load();
        if (_settings.Enabled)
            Start();
    }

    public bool Enabled => _settings.Enabled;

    /// <summary>A no-op if it didn't actually change, same as SnapLineManager.SetEnabled.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _settings.Enabled)
            return;

        _settings.Enabled = enabled;
        _store.Save(_settings);

        if (enabled)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        _snapper ??= new WindowSnapper();
        _windowsSnapOverride ??= new WindowsSnapOverride();
    }

    private void Stop()
    {
        _snapper?.Dispose();
        _snapper = null;
        _windowsSnapOverride?.Dispose();
        _windowsSnapOverride = null;
    }

    public void Dispose() => Stop();
}
