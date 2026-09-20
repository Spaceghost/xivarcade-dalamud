using XivArcade.Core.Arcade;

namespace XivArcade.Core.Tests;

/// <summary>The Wine-to-Linux path mapping is derived, not assumed; XDG_CONFIG_HOME is honoured; ghostty's focus replies parse totally.</summary>
public sealed class PathsAndFocusTests
{
    private static Func<string, bool> Dirs(params string[] present) => p => present.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheWineRootIsFoundByLookingNotAssumed()
    {
        Assert.Equal(("Z:", true), HostPaths.DetectWineRoot(Dirs(@"Z:\proc", @"Z:\etc")));
        Assert.Equal(("Y:", true), HostPaths.DetectWineRoot(Dirs(@"Y:\proc", @"Y:\etc", @"Z:\etc")));
        Assert.Equal((HostPaths.UnixNamespace, true), HostPaths.DetectWineRoot(Dirs(@"\\?\unix\proc", @"\\?\unix\etc")));
        Assert.Equal(("Z:", false), HostPaths.DetectWineRoot(Dirs()));                       // nothing answered: a guess, and it says so
        Assert.Equal(("Q:", true), HostPaths.DetectWineRoot(Dirs(), " Q:\\ "));              // the player's override wins
    }

    [Fact]
    public void ADriveThatHoldsSomeOtherRootIsSkippedWhenTheHomeIsKnown()
        => Assert.Equal(("X:", true), HostPaths.DetectWineRoot(Dirs(@"Z:\proc", @"Z:\etc", @"X:\proc", @"X:\etc", @"X:\home\name"), null, "/home/name"));

    [Fact]
    public void PathsMapThroughTheDerivedRoot()
    {
        Assert.Equal(@"Y:\home\name\.config\xiv-arcade", HostPaths.Wine("/home/name", "Y:").ToLocal("/home/name/.config/xiv-arcade"));
        Assert.Equal(@"\\?\unix\home\name", HostPaths.Wine("/home/name", HostPaths.UnixNamespace).ToLocal("/home/name"));
        Assert.Equal(@"Z:\home\name", HostPaths.Wine("/home/name").ToLocal("/home/name"));
        Assert.Equal("/home/name/x", HostPaths.LinuxFromWine(@"Y:\home\name\x", "Y:"));
        Assert.Null(HostPaths.LinuxFromWine(@"Y:\home\name\x"));                              // not the root drive
        Assert.Equal("/home/name", HostPaths.LinuxFromWine(@"\??\unix\home\name"));          // newer Wine's WINEHOMEDIR
        Assert.Equal("/home/name", HostPaths.LinuxFromWine(@"\??\Z:\home\name"));
    }

    [Fact]
    public void TheHomeIsDerivedFromWhateverWineGives()
    {
        Assert.Equal("/var/home/name", HostPaths.GuessHome("/var/home/name/", null, null));
        Assert.Equal("/home/name", HostPaths.GuessHome(@"C:\users\name", @"\??\unix\home\name", null));
        Assert.Equal("/home/name", HostPaths.GuessHome(null, null, @"Y:\home\name\.xlcore\pluginConfigs\XivArcade", "Y:"));
        Assert.Equal("", HostPaths.GuessHome(null, null, @"C:\somewhere"));
    }

    [Fact]
    public void XdgConfigHomeIsHonouredAndAnExistingDefaultSetupStaysWhereItIs()
    {
        var paths = HostPaths.Native("/home/name");
        Assert.Equal("/home/name/.config/xiv-arcade", paths.ConfigDir(null, _ => false));
        Assert.Equal("/home/name/.config/xiv-arcade", paths.ConfigDir("relative/dir", _ => false));   // invalid per the XDG spec
        Assert.Equal("/x/cfg/xiv-arcade", paths.ConfigDir("/x/cfg/", _ => false));
        Assert.Equal("/home/name/.config/xiv-arcade", paths.ConfigDir("/x/cfg", p => p == "/home/name/.config/xiv-arcade/config.json"));
        Assert.Equal("/x/cfg/xiv-arcade", paths.ConfigDir("/x/cfg", _ => true));                       // both set up: the XDG one
    }

    [Fact]
    public void GhosttyFocusRepliesParseAndBadOnesAreNoAnswer()
    {
        Assert.Equal(1726732800001L, GhosttyWire.OpenRequest("""{"ok":true,"result":{"queued":true,"request":1726732800001}}"""));
        Assert.Null(GhosttyWire.OpenRequest("""{"ok":false,"error":"no player"}"""));
        const string list = """{"ok":true,"result":{"rev":7,"windows":[],"requests":[{"request":5,"method":"window.open","ok":false,"error":"x"},{"request":6,"method":"window.open","ok":true,"result":{"id":12}}]}}""";
        Assert.Equal(12L, GhosttyWire.PanelOfRequest(list, 6));
        Assert.Null(GhosttyWire.PanelOfRequest(list, 5));
        Assert.Null(GhosttyWire.PanelOfRequest(list, 7));
        Assert.Equal((12L, true), GhosttyWire.Focus("""{"ok":true,"result":{"id":12,"kind":"window"}}"""));
        Assert.Equal((3L, false), GhosttyWire.Focus("""{"ok":true,"result":{"id":3,"kind":"terminal"}}"""));
        Assert.Equal((0L, false), GhosttyWire.Focus("""{"ok":true,"result":{"id":0}}"""));
        foreach (var bad in new[] { null, "", "[]", "{", """{"ok":true}""", """{"ok":true,"result":[]}""", """{"ok":true,"result":{"id":"12"}}""", "\"\\ud800\"" })
        {
            Assert.Null(GhosttyWire.Focus(bad));
            Assert.Null(GhosttyWire.PanelOfRequest(bad, 1));
        }

        Assert.Contains("\"focus.get\"", GhosttyWire.FocusGet);
        Assert.Contains("\"window.list\"", GhosttyWire.WindowList);
    }

    [Fact]
    public void TheNewVerbsParseAndReachTheHelper()
    {
        Assert.Equal(new ArcadeRequest(ArcadeVerb.Pad, ""), ArcadeRequest.Parse("pad"));
        Assert.Equal(new ArcadeRequest(ArcadeVerb.Pad, "off"), ArcadeRequest.Parse("pad OFF"));
        Assert.Equal(ArcadeVerb.Play, ArcadeRequest.Parse("pad racer").Verb);                 // still a game name
        Assert.Equal(new[] { "ps2", "pcsx2" }, ArcadeCommands.HelperCall(ArcadeRequest.Parse("emulator ps2 pcsx2"))!.Value.Args);
        Assert.Equal(new[] { "final fantasy x", "retroarch:pcsx2" }, ArcadeCommands.EmulatorArgs("final fantasy x retroarch:pcsx2"));
        var saves = ArcadeCommands.HelperCall(ArcadeRequest.Parse("saves-folder /mnt/sync dir"))!.Value;
        Assert.Equal("setup", saves.Verb);
        Assert.Equal(new[] { "--saves", "/mnt/sync dir" }, saves.Args);
        Assert.Equal("python3 '/h/xiv-arcade' setup '--games' '/mnt/my games'", ArcadeCommands.For("/h/xiv-arcade", ArcadeRequest.Parse("games-folder /mnt/my games")));
        Assert.Equal("bios", ArcadeCommands.HelperCall(ArcadeRequest.Parse("bios"))!.Value.Verb);
    }

    [Fact]
    public void TheStateCarriesEmulatorsBiosAndTheHelperPid()
    {
        var state = ArcadeState.Parse("""
            {"at":1,"playing":{"id":"x","title":"X","pid":4242},"watch":["/sys/bios"],"notes":["kept where it was"],
             "systems":[{"id":"psx","name":"PlayStation","folder":"psx","exts":[".chd"],"core":"pcsx_rearmed","emulator":"RetroArch · pcsx_rearmed","preferred":"auto",
               "emulators":[{"id":"retroarch:pcsx_rearmed","label":"l","installed":true,"bios_free":true}],
               "bios":{"dir":"/sys/bios","required":true,"ok":false,"hle":true,"summary":"No BIOS needed","files":[{"name":"scph5501.bin","what":"w","size":524288,"md5":"490f","state":"missing","detail":""}]}},
              {"id":"snes","name":"SNES","folder":"snes","exts":[],"core":null,"bios":null}]}
            """);
        Assert.Equal(4242, state.PlayingPid);
        Assert.Equal(new[] { "/sys/bios" }, state.Watch);
        Assert.Single(state.Notes);
        var psx = state.Systems[0];
        Assert.True(psx.Bios is { Hle: true, Ok: false, Required: true } && psx.Bios.Files[0] is { Size: 524288, State: "missing" });
        Assert.True(psx.Emulators[0] is { BiosFree: true, Installed: true });
        Assert.Null(state.Systems[1].Bios);
        Assert.Equal("auto", state.Systems[1].Preferred);
        Assert.Equal(0, ArcadeState.Parse("""{"at":1,"playing":{"title":"X","pid":-5}}""").PlayingPid);
    }
}
