using System.ComponentModel;
using System.Diagnostics;

namespace DesktopTool.Features.GetShitDone;

/// <summary>
/// Keeps a list of programs closed while a requirement isn't met. There's no way to stop a program
/// from starting without administrator rights, so this polls for the listed process names and ends
/// whatever it finds - the program does appear for a moment first. A speed bump, not a lock: it
/// can't end anything running elevated, and quitting Desktop Tool turns it off.
/// Not constructed anywhere yet.
/// </summary>
internal sealed class ProgramGate : IDisposable
{
    private readonly IReadOnlyCollection<string> _processNames;
    private readonly Func<string?> _unmet;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2_000 };
    private readonly int _sessionId;

    /// <summary>Process name and what's still missing, once per poll that actually ended something.</summary>
    public event Action<string, string>? Blocked;

    /// <summary>processNames are as Task Manager's Details tab shows them, with or without ".exe".
    /// unmet returns null once the programs are allowed - see GateRule.Unmet.</summary>
    public ProgramGate(IEnumerable<string> processNames, Func<string?> unmet)
    {
        _processNames = processNames
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _unmet = unmet;
        using var self = Process.GetCurrentProcess();
        _sessionId = self.SessionId;
        _poll.Tick += (_, _) => Sweep();
    }

    public void Start()
    {
        Sweep();
        _poll.Start();
    }

    public void Stop() => _poll.Stop();

    private void Sweep()
    {
        if (_unmet() is not { } reason)
            return;

        foreach (var name in _processNames)
        {
            var ended = false;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        // Another user's copy on the same machine isn't this gate's business.
                        if (process.SessionId != _sessionId)
                            continue;
                        process.Kill();
                        ended = true;
                    }
                    // Elevated or already gone - either way nothing more to do about it.
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                    }
                }
            }

            if (ended)
                Blocked?.Invoke(name, reason);
        }
    }

    public void Dispose() => _poll.Dispose();
}
