using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using XivArcade.Core.Arcade;

namespace XivArcade.Plugin;

/// <summary>
/// A hook on the game's own gamepad poll (PadDevice::Update, named Poll in older FFXIVClientStructs: the function Dalamud itself hooks to keep the pad
/// from the game while its gamepad navigation is on; Dalamud offers plugins no switch for that, only the
/// read-only IGamepadState). The game polls first; then, only while <see cref="PadSwitch.Active"/> says the
/// player is playing an arcade game, the result is blanked so FFXIV sees an idle pad. It never writes a button
/// or a stick value other than zero: it cannot press anything. If the hook cannot be installed the game is
/// left exactly as it was and <see cref="Problem"/> says why; a fault inside the detour turns it off for good.
/// </summary>
public sealed unsafe class PadHook : IPadGate
{
    private const GamepadButtonsFlags Chord = GamepadButtonsFlags.Start | GamepadButtonsFlags.Select;

    private readonly PadSwitch padSwitch;
    private readonly IPluginLog log;
    private Hook<PadDevice.Delegates.Update>? hook;
    private volatile bool faulted;
    private volatile uint buttons;

    public PadHook(IGameInteropProvider interop, IPluginLog log, PadSwitch padSwitch)
    {
        this.padSwitch = padSwitch;
        this.log = log;
        try
        {
            var table = PadDevice.StaticVirtualTablePointer;
            var address = table == null ? 0 : (nint)table->Update;
            if (address == 0)
                throw new InvalidOperationException("the game's gamepad poll was not found (the game was updated and Dalamud's signatures have not caught up)");
            hook = interop.HookFromAddress<PadDevice.Delegates.Update>(address, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            log.Warning(ex, "XivArcade: the gamepad hook was not installed; FFXIV keeps the gamepad");
            hook?.Dispose();
            hook = null;
        }
    }

    public bool Installed => hook is { IsDisposed: false, IsEnabled: true } && !faulted;

    public string Problem { get; private set; } = "";

    /// <summary>What the player is really holding, read before anything is hidden: the Start+Select escape works while captured.</summary>
    public bool ChordHeld => ((GamepadButtonsFlags)buttons & Chord) == Chord;

    public bool AnyButton => buttons != 0;

    private void Detour(PadDevice* device)
    {
        hook!.Original(device);
        if (faulted || device == null)
            return;
        try
        {
            ref var pad = ref device->GamepadInputData;
            buttons = (uint)pad.Buttons;
            if (!padSwitch.Active(Environment.TickCount64))
                return;
            pad.LeftStickX = 0;
            pad.LeftStickY = 0;
            pad.RightStickX = 0;
            pad.RightStickY = 0;
            pad.Buttons = 0;
            pad.ButtonsPressed = 0;
            pad.ButtonsReleased = 0;
            pad.ButtonsRepeat = 0;
        }
        catch (Exception ex)
        {
            faulted = true;
            padSwitch.Off();
            Problem = "the gamepad hook failed and was turned off: " + ex.Message;
            log.Error(ex, "XivArcade: gamepad hook fault; capture is off until the plugin reloads");
        }
    }

    public void Dispose()
    {
        padSwitch.Off();
        hook?.Disable();
        hook?.Dispose();
        hook = null;
    }
}
