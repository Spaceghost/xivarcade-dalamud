using XivArcade.Core.Arcade;

namespace XivArcade.Core.Tests;

/// <summary>
/// The gamepad decision with a fake pad, a fake focus source and a fake hook: when FFXIV stops seeing the pad,
/// and every way it gets it back. Nothing here touches a game or a controller.
/// </summary>
public sealed class PadCaptureTests
{
    private static readonly PadInputs Playing = new(0, Enabled: true, HookInstalled: true, EmulatorRunning: true, PanelFocused: true, ArcadeMode: false,
        LoggedIn: true, InCombat: false, InCutscene: false, ZoneChanging: false, EscapeKey: false, ChordHeld: false, AnyButton: false);

    private sealed class FakeGate : IPadGate
    {
        public bool Installed { get; set; } = true;

        public string Problem { get; set; } = "";

        public int Disposed { get; private set; }

        public bool SuppressedWhenDisposed { get; private set; }

        public PadSwitch? Switch { get; set; }

        public void Dispose()
        {
            Disposed++;
            SuppressedWhenDisposed = Switch!.Active(0);
        }
    }

    private static PadCapture Captured()
    {
        var c = new PadCapture();
        Assert.True(c.Tick(Playing));
        Assert.True(c.Capturing && c.Suppress);
        return c;
    }

    [Fact]
    public void NothingIsCapturedUntilAGamePanelHasTheFocus()
    {
        var c = new PadCapture();
        Assert.False(c.Tick(Playing with { PanelFocused = false }));
        Assert.False(c.Tick(Playing with { EmulatorRunning = false }));
        Assert.False(c.Suppress);
        Assert.True(c.Tick(Playing));
    }

    [Fact]
    public void ArcadeModeCapturesWithoutAPanelButOnlyWhileAGameRuns()
    {
        var c = new PadCapture();
        Assert.False(c.Tick(Playing with { PanelFocused = false, ArcadeMode = true, EmulatorRunning = false }));
        Assert.True(c.Tick(Playing with { PanelFocused = false, ArcadeMode = true }));
    }

    public static TheoryData<PadRelease> Releases() =>
    [
        PadRelease.Escape, PadRelease.TurnedOff, PadRelease.HookUnavailable, PadRelease.EmulatorExited, PadRelease.FocusLost,
        PadRelease.Combat, PadRelease.Cutscene, PadRelease.ZoneChange, PadRelease.Logout,
    ];

    private static PadInputs With(PadRelease why) => why switch
    {
        PadRelease.Escape => Playing with { EscapeKey = true },
        PadRelease.TurnedOff => Playing with { Enabled = false },
        PadRelease.HookUnavailable => Playing with { HookInstalled = false },
        PadRelease.EmulatorExited => Playing with { EmulatorRunning = false },
        PadRelease.FocusLost => Playing with { PanelFocused = false },
        PadRelease.Combat => Playing with { InCombat = true },
        PadRelease.Cutscene => Playing with { InCutscene = true },
        PadRelease.ZoneChange => Playing with { ZoneChanging = true },
        PadRelease.Logout => Playing with { LoggedIn = false },
        _ => Playing,
    };

    [Theory]
    [MemberData(nameof(Releases))]
    public void EveryReleaseConditionGivesThePadBackOnTheSameFrame(PadRelease why)
    {
        var c = Captured();
        Assert.True(c.Tick(With(why)));
        Assert.False(c.Capturing);
        Assert.False(c.Suppress);
        Assert.Equal(why, c.LastRelease);
        Assert.NotEqual("", PadCapture.Describe(why));
    }

    [Theory]
    [InlineData(PadRelease.Escape)]
    [InlineData(PadRelease.Combat)]
    [InlineData(PadRelease.Cutscene)]
    [InlineData(PadRelease.ZoneChange)]
    [InlineData(PadRelease.Logout)]
    public void AForcedReleaseStaysReleasedUntilThePanelIsRefocused(PadRelease why)
    {
        var c = Captured();
        c.Tick(With(why));
        Assert.False(c.Tick(Playing));           // the condition passed, the panel still has focus: not taken again
        Assert.False(c.Capturing);
        c.Tick(Playing with { PanelFocused = false });
        Assert.True(c.Tick(Playing));            // he came back to the panel himself
    }

    [Fact]
    public void LosingFocusAndComingBackCapturesAgain()
    {
        var c = Captured();
        c.Tick(Playing with { PanelFocused = false });
        Assert.True(c.Tick(Playing));
    }

    [Fact]
    public void RearmLetsThePlayerAskForThePadAgain()
    {
        var c = Captured();
        c.Tick(Playing with { EscapeKey = true });
        c.Rearm();
        Assert.True(c.Tick(Playing));
    }

    [Fact]
    public void StartSelectMustBeHeldForASecond()
    {
        var c = Captured();
        Assert.False(c.Tick(Playing with { NowMs = 100, ChordHeld = true, AnyButton = true }));
        Assert.False(c.Tick(Playing with { NowMs = 1099, ChordHeld = true, AnyButton = true }));
        Assert.True(c.Tick(Playing with { NowMs = 1100, ChordHeld = true, AnyButton = true }));
        Assert.Equal(PadRelease.PadChord, c.LastRelease);
    }

    [Fact]
    public void LettingGoOfTheChordRestartsTheSecond()
    {
        var c = Captured();
        c.Tick(Playing with { NowMs = 0, ChordHeld = true });
        c.Tick(Playing with { NowMs = 900 });
        Assert.False(c.Tick(Playing with { NowMs = 1000, ChordHeld = true }));
        Assert.True(c.Capturing);
    }

    [Fact]
    public void AfterTheChordHeldButtonsStayHiddenUntilLetGoButNeverLongerThanTwoSeconds()
    {
        var c = Captured();
        c.Tick(Playing with { NowMs = 0, ChordHeld = true, AnyButton = true });
        c.Tick(Playing with { NowMs = 1000, ChordHeld = true, AnyButton = true });
        Assert.False(c.Capturing);
        Assert.True(c.Suppress);                 // Start is still down: FFXIV must not open its menu
        c.Tick(Playing with { NowMs = 1200, AnyButton = false });
        Assert.False(c.Suppress);

        var stuck = Captured();
        stuck.Tick(Playing with { NowMs = 0, ChordHeld = true, AnyButton = true });
        stuck.Tick(Playing with { NowMs = 1000, ChordHeld = true, AnyButton = true });
        stuck.Tick(Playing with { NowMs = 1000 + PadCapture.DrainMs, ChordHeld = true, AnyButton = true });
        Assert.False(stuck.Suppress);
    }

    [Fact]
    public void TheChordNeverTurnsCaptureOn()
    {
        var c = new PadCapture();
        c.Tick(Playing with { PanelFocused = false, NowMs = 0, ChordHeld = true });
        Assert.False(c.Tick(Playing with { PanelFocused = false, NowMs = 5000, ChordHeld = true }));
        Assert.False(c.Suppress);
    }

    [Fact]
    public void TheSwitchGoesStaleWhenTheTickStops()
    {
        var s = new PadSwitch();
        Assert.False(s.Active(0));
        s.Set(true, 1000);
        Assert.True(s.Active(1000 + PadSwitch.StaleMs - 1));
        Assert.False(s.Active(1000 + PadSwitch.StaleMs));   // no tick for half a second: the pad is FFXIV's again
        s.Set(true, 2000);
        s.Off();
        Assert.False(s.Active(2000));
    }

    [Fact]
    public void AHookThatCouldNotBeInstalledLeavesTheGameUntouched()
    {
        var gate = new FakeGate { Installed = false, Problem = "signature not found" };
        var sw = new PadSwitch();
        gate.Switch = sw;
        using var controller = new PadCaptureController(gate, sw);
        Assert.False(controller.Tick(Playing));
        Assert.False(controller.Capture.Suppress);
        Assert.False(sw.Active(0));
        Assert.False(controller.Installed);
        Assert.Equal("signature not found", controller.Problem);
    }

    [Fact]
    public void AHookThatFaultsWhileCapturingReleases()
    {
        var gate = new FakeGate();
        var sw = new PadSwitch();
        gate.Switch = sw;
        using var controller = new PadCaptureController(gate, sw);
        Assert.True(controller.Tick(Playing));
        gate.Installed = false;
        Assert.True(controller.Tick(Playing with { NowMs = 16 }));
        Assert.Equal(PadRelease.HookUnavailable, controller.Capture.LastRelease);
        Assert.False(sw.Active(16));
    }

    [Fact]
    public void DisposingReleasesThePadThenRemovesTheHookExactlyOnce()
    {
        var gate = new FakeGate();
        var sw = new PadSwitch();
        gate.Switch = sw;
        var controller = new PadCaptureController(gate, sw);
        controller.Tick(Playing);
        Assert.True(sw.Active(0));
        controller.Dispose();
        controller.Dispose();
        Assert.Equal(1, gate.Disposed);
        Assert.False(gate.SuppressedWhenDisposed);   // the pad was already back before the hook came off
        Assert.False(sw.Active(0));
        Assert.False(controller.Tick(Playing));      // a late tick after unload does nothing
        Assert.False(sw.Active(0));
    }

    [Fact]
    public void ThePluginDisposesThePadServiceAndTheServiceDisposesTheHook()
    {
        // The plugin assembly cannot be loaded outside the game, so the chain is audited in source.
        var root = typeof(PadCaptureTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        var plugin = File.ReadAllText(Path.Combine(root, "src/XivArcade.Plugin/Plugin.cs"));
        var service = File.ReadAllText(Path.Combine(root, "src/XivArcade.Plugin/PadCaptureService.cs"));
        var hook = File.ReadAllText(Path.Combine(root, "src/XivArcade.Plugin/PadHook.cs"));
        Assert.Contains("pad.Dispose();", plugin[plugin.IndexOf("private void DisposeCore()", StringComparison.Ordinal)..]);
        Assert.Contains("controller.Dispose();", service[service.IndexOf("public void Dispose()", StringComparison.Ordinal)..]);
        var dispose = hook[hook.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        Assert.Contains("hook?.Disable();", dispose);
        Assert.Contains("hook?.Dispose();", dispose);
        Assert.Contains("hook?.Dispose();", hook[hook.IndexOf("catch (Exception ex)", StringComparison.Ordinal)..]); // a half-made hook is not left behind
    }

    [Fact]
    public void ATickAllocatesNothing()
    {
        var c = new PadCapture();
        var sw = new PadSwitch();
        for (var i = 0; i < 500; i++)
            c.Tick(Playing with { NowMs = i });
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            c.Tick(Playing with { NowMs = i, PanelFocused = i % 7 != 0, ChordHeld = i % 3 == 0 });
            sw.Set(c.Suppress, i);
            sw.Active(i);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
