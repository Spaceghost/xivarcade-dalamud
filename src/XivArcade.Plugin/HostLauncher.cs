using System.Diagnostics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using XivArcade.Core;
using XivArcade.Shared;

namespace XivArcade.Plugin;

/// <summary>
/// The two ways a helper command reaches the Linux host. Through ghostty-dalamud (its Call gate, or the older
/// Post gate) the command starts inside the host agent's compositor, so an emulator it opens is a game panel.
/// Without ghostty, Wine's own "start /unix" runs it on the Linux desktop: good enough for housekeeping
/// (setup, scan, sync), and for games only when the player asked for that. XivArcade references neither
/// ghostty-dalamud nor XivDesktop; these are IPC names looked up at run time.
/// </summary>
public sealed class HostLauncher
{
    public const string GhosttyMissing = "ghostty-dalamud is not installed (or its host agent is not running), so games cannot appear as panels in the world.";

    private readonly ICallGateSubscriber<string, string> call;
    private readonly ICallGateSubscriber<string, object> post;

    public HostLauncher(IDalamudPluginInterface pi)
    {
        call = pi.GetIpcSubscriber<string, string>(IpcContract.GhosttyCall);
        post = pi.GetIpcSubscriber<string, object>(IpcContract.GhosttyPost);
    }

    /// <summary>ghostty-dalamud answers: a launched game becomes a panel.</summary>
    public bool Panels => Has(() => call.HasFunction) || Has(() => post.HasAction);

    /// <summary>
    /// Starts a shell line inside the agent's compositor. Throws when ghostty refuses. Returns the window.open
    /// request number (the panel it becomes is looked up with it), or null through the older Post gate.
    /// </summary>
    public long? RunInCompositor(string shellLine)
    {
        if (Has(() => call.HasFunction))
        {
            var reply = call.InvokeFunc(GhosttyWire.OpenRun(shellLine));
            if (GhosttyWire.ReplyError(reply) is { } error)
                throw new InvalidOperationException(error);
            return GhosttyWire.OpenRequest(reply);
        }

        post.InvokeAction(GhosttyWire.PostLine(shellLine));
        return null;
    }

    /// <summary>A read (focus.get, window.list) through the Call gate; null when ghostty is not there or threw.</summary>
    public string? Query(string request)
    {
        try
        {
            return call.HasFunction ? call.InvokeFunc(request) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Starts a program on the Linux desktop through Wine. Throws when Wine cannot reach the host.</summary>
    public static void RunOnHostDesktop(IReadOnlyList<string> argv)
    {
        using var process = Process.Start(new ProcessStartInfo(WineHost.FileName, WineHost.Arguments(argv))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static bool Has(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch
        {
            return false;
        }
    }
}
