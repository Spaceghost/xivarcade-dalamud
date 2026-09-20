using Dalamud.Configuration;

namespace XivArcade.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Linux home directory override (e.g. "/home/name"). Empty: detect it from $HOME / WINEHOMEDIR / the config path.</summary>
    public string HomeOverride { get; set; } = "";

    /// <summary>
    /// Without ghostty-dalamud, start games on the Linux desktop through Wine's "start /unix" instead of refusing.
    /// Off by default: the game then opens outside FFXIV, not as a panel, and the player should choose that.
    /// </summary>
    public bool PlayOnHostDesktop { get; set; }

    /// <summary>
    /// While an arcade game's panel has the focus, hide the gamepad from FFXIV (the emulator reads the pad on the
    /// host, so without this the character moves too). On by default; it only ever withholds the player's own input.
    /// </summary>
    public bool PadCapture { get; set; } = true;

    /// <summary>What this Wine prefix calls the Linux root ("Z:"). Empty: found by looking (see HostPaths.DetectWineRoot).</summary>
    public string WineRootOverride { get; set; } = "";
}
