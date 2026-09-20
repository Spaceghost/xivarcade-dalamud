using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using XivArcade.Core;
using XivArcade.Core.Arcade;

namespace XivArcade.Plugin;

/// <summary>
/// While the player plays an arcade game, FFXIV must not also act on his gamepad (the emulator reads the pad
/// on the Linux host directly). Each frame this gathers facts (is the emulator still running, does its panel
/// have the focus per ghostty-dalamud's focus.get, is he in combat, a cutscene, between zones, logged out, is
/// Esc or Start+Select held) and lets <see cref="PadCapture"/> decide; the hook does the hiding. It draws a
/// banner nobody can miss while the pad is captured. It sends no input anywhere.
/// </summary>
public sealed class PadCaptureService : IDisposable
{
    private const long FocusPollMs = 200;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    private readonly ArcadeService arcade;
    private readonly HostLauncher host;
    private readonly Configuration config;
    private readonly PadHook hook;
    private readonly PadCaptureController controller;
    private long lastFocusPoll = long.MinValue / 2;
    private long panel;
    private bool panelFocused;
    private bool wasRunning;

    public PadCaptureService(IFramework framework, IGameInteropProvider interop, ICondition condition, IClientState clientState, IKeyState keys,
        IPluginLog log, ArcadeService arcade, HostLauncher host, Configuration config)
    {
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.keys = keys;
        this.log = log;
        this.arcade = arcade;
        this.host = host;
        this.config = config;
        var padSwitch = new PadSwitch();
        hook = new PadHook(interop, log, padSwitch);
        controller = new PadCaptureController(hook, padSwitch);
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.Logout += OnLogout;
        framework.Update += OnUpdate;
    }

    /// <summary>The player turned arcade mode on by hand (no panel to watch: a game on the Linux desktop).</summary>
    public bool ArcadeMode { get; private set; }

    public bool Capturing => controller.Capture.Capturing;

    /// <summary>Raised on the framework thread with a line for the chat when the pad changes hands.</summary>
    public event Action<string>? Changed;

    /// <summary>The first-run style check the Arcade window lists with the others.</summary>
    public ArcadeCheck Check => new("gamepad", "Gamepad goes to the arcade game while you play", controller.Installed || !config.PadCapture,
        !config.PadCapture ? "Turned off: FFXIV also reacts to the gamepad while you play. /arcade pad auto turns it back on."
        : controller.Installed ? "While an arcade game's panel has the focus, FFXIV ignores the gamepad. Hold Start+Select for a second, or press Esc, to take it back."
        : "Not available, FFXIV was left untouched and will also react to the gamepad: " + controller.Problem,
        "/arcade pad");

    public string Status => !config.PadCapture ? "gamepad capture is off (/arcade pad auto turns it on)"
        : !controller.Installed ? "gamepad capture is not available: " + controller.Problem
        : Capturing ? "the gamepad belongs to the arcade game; hold Start+Select 1 s or press Esc to take it back"
        : "FFXIV has the gamepad" + (controller.Capture.LastRelease != PadRelease.None ? " (" + PadCapture.Describe(controller.Capture.LastRelease) + ")" : "");

    /// <summary><c>/arcade pad on|off|auto</c>.</summary>
    public string Command(string arg)
    {
        switch (arg)
        {
            case "on":
                config.PadCapture = true;
                ArcadeMode = true;
                controller.Capture.Rearm();
                return arcade.Playing ? "ok: arcade mode on" : "ok: arcade mode on; it takes the gamepad once a game is running";
            case "off":
                config.PadCapture = false;
                ArcadeMode = false;
                controller.Release(PadRelease.TurnedOff);
                return "ok: FFXIV keeps the gamepad";
            case "auto":
                config.PadCapture = true;
                ArcadeMode = false;
                controller.Capture.Rearm();
                return "ok: the gamepad follows the game panel's focus";
            default:
                return "ok: " + Status;
        }
    }

    private void OnTerritoryChanged(uint territory) => ForceRelease(PadRelease.ZoneChange);

    private void OnLogout(int type, int code) => ForceRelease(PadRelease.Logout);

    private void ForceRelease(PadRelease why)
    {
        var was = Capturing;
        controller.Release(why);
        ArcadeMode = false;
        if (was)
            Changed?.Invoke(PadCapture.Describe(why));
    }

    private void OnUpdate(IFramework fw)
    {
        try
        {
            var now = Environment.TickCount64;
            var running = arcade.Playing;
            if (running && !wasRunning)
                panel = 0;
            wasRunning = running;
            if (running && config.PadCapture && controller.Installed && now - lastFocusPoll >= FocusPollMs)
            {
                lastFocusPoll = now;
                panelFocused = PollFocus();
            }
            else if (!running)
            {
                panelFocused = false;
            }

            var was = Capturing;
            var changed = controller.Tick(new PadInputs(
                now, config.PadCapture, true, running, panelFocused, ArcadeMode, clientState.IsLoggedIn,
                condition[ConditionFlag.InCombat],
                condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78],
                condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
                keys[VirtualKey.ESCAPE], hook.ChordHeld, hook.AnyButton));
            if (!changed)
                return;
            if (was)
                ArcadeMode = false; // any release ends arcade mode: it never comes back on by itself
            Changed?.Invoke(Capturing ? "the gamepad now belongs to the arcade game; hold Start+Select 1 s or press Esc to take it back"
                : PadCapture.Describe(controller.Capture.LastRelease));
        }
        catch (Exception ex)
        {
            controller.Release(PadRelease.HookUnavailable);
            log.Error(ex, "XivArcade: gamepad capture tick failed; the gamepad was given back");
        }
    }

    /// <summary>Does the arcade game's panel have the keyboard? Its id comes from the window.open request that started it.</summary>
    private bool PollFocus()
    {
        if (host.Query(GhosttyWire.FocusGet) is not { } reply || GhosttyWire.Focus(reply) is not { } focus || focus.Id == 0 || !focus.IsWindow)
            return false;
        if (panel == 0 && arcade.LastOpenRequest is { } request && host.Query(GhosttyWire.WindowList) is { } list)
            panel = GhosttyWire.PanelOfRequest(list, request) ?? 0;

        // Without a known panel (the older Post gate, or a plugin reload mid-game) any focused window panel counts.
        return panel == 0 || focus.Id == panel;
    }

    /// <summary>The banner: drawn over everything, every frame the pad is captured.</summary>
    public void Draw()
    {
        if (!controller.Capture.Capturing)
            return;
        var scale = ImGuiHelpers.GlobalScale;
        var text = "GAMEPAD → ARCADE   ·   FFXIV ignores the controller   ·   hold Start+Select 1 s, or press Esc, to take it back";
        var draw = ImGui.GetForegroundDrawList();
        var size = ImGui.CalcTextSize(text);
        var view = ImGui.GetMainViewport();
        var pad = new Vector2(18, 9) * scale;
        var min = new Vector2(view.Pos.X + ((view.Size.X - size.X) / 2) - pad.X, view.Pos.Y + (28 * scale));
        var max = min + size + (pad * 2);
        var pulse = 0.75f + (0.25f * MathF.Sin(Environment.TickCount64 / 300f));
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.10f, 0.05f, 0.20f, 0.92f)), 8 * scale);
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(1.00f, 0.72f, 0.20f, pulse)), 8 * scale, ImDrawFlags.None, 2.5f * scale);
        draw.AddText(min + pad, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.Logout -= OnLogout;
        controller.Dispose();
    }
}
