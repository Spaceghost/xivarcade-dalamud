namespace XivArcade.Core.Arcade;

/// <summary>Why the gamepad went back to FFXIV.</summary>
public enum PadRelease
{
    None,
    Escape,
    PadChord,
    TurnedOff,
    HookUnavailable,
    EmulatorExited,
    FocusLost,
    Combat,
    Cutscene,
    ZoneChange,
    Logout,
}

/// <summary>One frame of facts. Every field is something the plugin observed; nothing here is sent to the game.</summary>
public readonly record struct PadInputs(
    long NowMs,
    bool Enabled,
    bool HookInstalled,
    bool EmulatorRunning,
    bool PanelFocused,
    bool ArcadeMode,
    bool LoggedIn,
    bool InCombat,
    bool InCutscene,
    bool ZoneChanging,
    bool EscapeKey,
    bool ChordHeld,
    bool AnyButton);

/// <summary>
/// Decides when FFXIV must not see the gamepad: only while the player is playing an arcade game (its panel has
/// the focus, or he turned arcade mode on) and nothing in FFXIV needs him. It never produces input; it only
/// says "hide the player's own pad from the game now" or "stop hiding it". Everything errs towards giving the
/// pad back: any doubt releases, and a forced release stays released until the player refocuses the panel.
/// </summary>
public sealed class PadCapture
{
    /// <summary>How long Start+Select must be held to take the pad back.</summary>
    public const long ChordMs = 1000;

    /// <summary>After a release, buttons still held stay hidden at most this long, so Start does not open the game's menu.</summary>
    public const long DrainMs = 2000;

    private long chordSince = -1;
    private long drainUntil = -1;
    private bool latched;

    public bool Capturing { get; private set; }

    /// <summary>Capturing, or draining the buttons that were down at the moment of release.</summary>
    public bool Suppress { get; private set; }

    public PadRelease LastRelease { get; private set; }

    /// <summary>The player asked for the pad again after a forced release (<c>/arcade pad on</c>).</summary>
    public void Rearm() => latched = false;

    /// <summary>The first reason the pad may not be captured right now, in the order the player should hear it.</summary>
    public static PadRelease Blocker(in PadInputs i)
    {
        if (!i.Enabled)
            return PadRelease.TurnedOff;
        if (!i.HookInstalled)
            return PadRelease.HookUnavailable;
        if (!i.LoggedIn)
            return PadRelease.Logout;
        if (i.ZoneChanging)
            return PadRelease.ZoneChange;
        if (i.InCutscene)
            return PadRelease.Cutscene;
        if (i.InCombat)
            return PadRelease.Combat;
        if (!i.EmulatorRunning)
            return PadRelease.EmulatorExited;
        return i.PanelFocused || i.ArcadeMode ? PadRelease.None : PadRelease.FocusLost;
    }

    /// <summary>Advances one frame. Returns true when <see cref="Capturing"/> changed.</summary>
    public bool Tick(in PadInputs i)
    {
        chordSince = i.ChordHeld ? (chordSince < 0 ? i.NowMs : chordSince) : -1;
        var chord = chordSince >= 0 && i.NowMs - chordSince >= ChordMs;
        var blocker = Blocker(i);
        if (blocker is PadRelease.EmulatorExited or PadRelease.FocusLost)
            latched = false; // he left the game panel: coming back to it may capture again

        var was = Capturing;
        if (Capturing)
        {
            var why = i.EscapeKey ? PadRelease.Escape : chord ? PadRelease.PadChord : blocker;
            if (why != PadRelease.None)
            {
                Capturing = false;
                LastRelease = why;
                latched = why is not (PadRelease.EmulatorExited or PadRelease.FocusLost);
                drainUntil = why == PadRelease.PadChord ? i.NowMs + DrainMs : -1;
            }
        }
        else if (blocker == PadRelease.None && !latched && !i.EscapeKey && !chord)
        {
            Capturing = true;
            drainUntil = -1;
        }

        if (drainUntil >= 0 && (!i.AnyButton || i.NowMs >= drainUntil || !i.HookInstalled))
            drainUntil = -1;
        Suppress = Capturing || drainUntil >= 0;
        return was != Capturing;
    }

    /// <summary>Gives the pad back at once (unload, dispose).</summary>
    public void Release(PadRelease why)
    {
        if (Capturing)
            LastRelease = why;
        Capturing = false;
        Suppress = false;
        drainUntil = -1;
        latched = true;
    }

    public static string Describe(PadRelease why) => why switch
    {
        PadRelease.Escape => "Esc: the gamepad is FFXIV's again",
        PadRelease.PadChord => "Start+Select: the gamepad is FFXIV's again",
        PadRelease.TurnedOff => "gamepad capture is turned off",
        PadRelease.HookUnavailable => "the gamepad hook is not available",
        PadRelease.EmulatorExited => "the game closed: the gamepad is FFXIV's again",
        PadRelease.FocusLost => "the game panel lost focus: the gamepad is FFXIV's again",
        PadRelease.Combat => "combat: the gamepad is FFXIV's again",
        PadRelease.Cutscene => "cutscene: the gamepad is FFXIV's again",
        PadRelease.ZoneChange => "zone change: the gamepad is FFXIV's again",
        PadRelease.Logout => "logged out: the gamepad is FFXIV's again",
        _ => "",
    };
}

/// <summary>
/// The switch the game-thread hook reads. It fails towards the game: the hook hides the pad only while the
/// plugin's frame tick said so within the last <see cref="StaleMs"/>, so a tick that stops (an exception, a
/// stalled plugin) gives the pad back by itself.
/// </summary>
public sealed class PadSwitch
{
    public const long StaleMs = 500;

    private long lastTick = long.MinValue / 2;
    private volatile bool suppress;

    public void Set(bool on, long nowMs)
    {
        Volatile.Write(ref lastTick, nowMs);
        suppress = on;
    }

    public void Off() => suppress = false;

    public bool Active(long nowMs) => suppress && nowMs - Volatile.Read(ref lastTick) < StaleMs;
}

/// <summary>What stands between the decision and the game: the plugin's hook on the game's gamepad poll.</summary>
public interface IPadGate : IDisposable
{
    /// <summary>The hook is installed and enabled. False: the game is untouched and <see cref="Problem"/> says why.</summary>
    bool Installed { get; }

    string Problem { get; }
}

/// <summary>Owns the gate and the decision together, so unloading always releases the pad and removes the hook.</summary>
public sealed class PadCaptureController : IDisposable
{
    private readonly IPadGate gate;
    private bool disposed;

    public PadCaptureController(IPadGate gate, PadSwitch padSwitch)
    {
        this.gate = gate;
        Switch = padSwitch;
    }

    public PadCapture Capture { get; } = new();

    public PadSwitch Switch { get; }

    public bool Installed => !disposed && gate.Installed;

    public string Problem => gate.Problem;

    /// <summary>Returns true when capturing started or stopped this frame.</summary>
    public bool Tick(PadInputs inputs)
    {
        if (disposed)
            return false;
        var changed = Capture.Tick(inputs with { HookInstalled = gate.Installed });
        Switch.Set(Capture.Suppress, inputs.NowMs);
        return changed;
    }

    public void Release(PadRelease why)
    {
        Capture.Release(why);
        Switch.Off();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Release(PadRelease.TurnedOff);
        gate.Dispose();
    }
}
