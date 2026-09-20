using XivArcade.Core.Arcade;

namespace XivArcade.Core.Tests;

public sealed class CoverLayoutTests
{
    [Theory]
    [InlineData(0f, 1)]
    [InlineData(150f, 1)]
    [InlineData(309f, 1)]
    [InlineData(310f, 2)]
    [InlineData(790f, 5)]
    public void ColumnsFillTheWidthAndNeverDropBelowOne(float available, int expected)
        => Assert.Equal(expected, CoverLayout.Columns(available, 150, 10));

    [Fact]
    public void AWideBoxIsFittedByWidthAndStandsOnTheFloor()
    {
        var r = CoverLayout.AspectFit(CoverLayout.Style("snes").Aspect, new CoverRect(10, 20, 140, 180));
        Assert.Equal(140, r.W, 3);
        Assert.Equal(100, r.H, 3);
        Assert.Equal(10, r.X, 3);
        Assert.Equal(200, r.Y + r.H, 3);
    }

    [Fact]
    public void ATallBoxIsFittedByHeightAndCentred()
    {
        var r = CoverLayout.AspectFit(500, 1000, new CoverRect(0, 0, 140, 180));
        Assert.Equal(180, r.H, 3);
        Assert.Equal(90, r.W, 3);
        Assert.Equal(25, r.X, 3);
    }

    [Fact]
    public void NothingIsEverStretched()
    {
        foreach (var system in new[] { "nes", "snes", "n64", "gb", "gba", "nds", "psx", "ps2", "psp", "genesis", "unknown" })
        {
            var aspect = CoverLayout.Style(system).Aspect;
            var r = CoverLayout.AspectFit(aspect, new CoverRect(0, 0, 150, 170));
            Assert.Equal(aspect, r.W / r.H, 3);
            Assert.True(r.W <= 150.001f && r.H <= 170.001f);
        }

        Assert.Equal(1f, CoverLayout.AspectFit(0, 0, new CoverRect(0, 0, 50, 80)).W / 50f, 3);
    }

    [Fact]
    public void ConsolesKeepTheirOwnBoxShapes()
    {
        Assert.True(CoverLayout.Style("snes").Aspect > 1.2f);
        Assert.True(CoverLayout.Style("n64").Aspect > 1.2f);
        Assert.Equal(1f, CoverLayout.Style("psx").Aspect, 2);
        Assert.Equal(1f, CoverLayout.Style("gba").Aspect, 2);
        Assert.True(CoverLayout.Style("ps2").Aspect < 0.8f);
    }

    [Fact]
    public void AShelfPlaceholderTakesTheShapeOfItsFirstPlatform()
    {
        Assert.Equal("snes", CoverLayout.SystemForPlatforms("SNES · PS1 · GBA"));
        Assert.Equal("nes", CoverLayout.SystemForPlatforms("Famicom"));
        Assert.Equal("psx", CoverLayout.SystemForPlatforms("PS1"));
        Assert.Equal("", CoverLayout.SystemForPlatforms("Dreamcast"));
        Assert.Equal("GAME", CoverLayout.Style("").Mark);
    }

    [Fact]
    public void FocusGrowsAboutTheCentreAndIsEased()
    {
        var r = new CoverRect(100, 100, 100, 200);
        Assert.Equal(r, CoverLayout.Focus(r, 0));
        var full = CoverLayout.Focus(r, 1);
        Assert.Equal(100 * (1 + CoverLayout.FocusGrow), full.W, 3);
        Assert.Equal(r.Centre, full.Centre);
        Assert.Equal(0.5f, CoverLayout.Ease(0.5f), 3);
        Assert.True(CoverLayout.Ease(0.1f) < 0.1f && CoverLayout.Ease(0.9f) > 0.9f);
        Assert.Equal(1f, CoverLayout.Ease(7f));
        Assert.Equal(CoverLayout.RestBrightness, CoverLayout.Brightness(0), 3);
        Assert.Equal(1f, CoverLayout.Brightness(1), 3);
    }

    [Fact]
    public void ApproachIsFrameRateIndependentAndSettles()
    {
        var one = CoverLayout.Approach(0, 1, 0.1f, 12);
        var two = CoverLayout.Approach(CoverLayout.Approach(0, 1, 0.05f, 12), 1, 0.05f, 12);
        Assert.Equal(one, two, 3);
        var v = 0f;
        for (var i = 0; i < 120; i++)
            v = CoverLayout.Approach(v, 1, 1 / 60f, 12);
        Assert.Equal(1f, v);
        Assert.Equal(0.4f, CoverLayout.Approach(0.4f, 1, 0, 12));
    }

    [Fact]
    public void ThumbnailsKeepTheirShapeAndNeverGrow()
    {
        Assert.Equal((512, 256), CoverLayout.Thumbnail(2048, 1024, 512));
        Assert.Equal((256, 512), CoverLayout.Thumbnail(1000, 2000, 512));
        Assert.Equal((300, 200), CoverLayout.Thumbnail(300, 200, 512));
        Assert.Equal((1, 1), CoverLayout.Thumbnail(0, 10, 512));
    }

    [Fact]
    public void TheGridStacksShelvesUnderHeadersAndTheArrowsCrossThem()
    {
        var grid = new CoverGrid();
        grid.Build([5, 0, 2], 3 * 160, 150, 170, 10, 30);
        Assert.Equal(3, grid.Columns);
        Assert.Equal(7, grid.Cells.Count);
        Assert.Equal([0, 5, 5], grid.Firsts);
        Assert.Equal(30, grid.Cells[0].Y);
        Assert.Equal(30 + 180, grid.Cells[3].Y);
        Assert.True(grid.Headers[2] > grid.Headers[1] && grid.Cells[5].Y == grid.Headers[2] + 30);
        Assert.Equal(grid.Cells[6].Y + 180, grid.Height);

        Assert.Equal(1, grid.Move(0, 1, 0));
        Assert.Equal(0, grid.Move(0, -1, 0));
        Assert.Equal(6, grid.Move(6, 1, 0));
        Assert.Equal(4, grid.Move(1, 0, 1));
        Assert.Equal(4, grid.Move(2, 0, 1));   // no cover below: the nearest one in that row
        Assert.Equal(6, grid.Move(4, 0, 1));   // down out of one shelf into the next
        Assert.Equal(3, grid.Move(5, 0, -1));
        Assert.Equal(6, grid.Move(6, 0, 1));   // nowhere to go
        Assert.Equal(5, grid.NextSection(0, 1)); // the empty shelf is skipped
        Assert.Equal(0, grid.NextSection(5, 1));
        Assert.Equal(5, grid.NextSection(0, -1));
        Assert.Equal(0, new CoverGrid().Move(3, 0, 1));
    }

    [Fact]
    public void ScrollingCentresTheCoverWithoutLeavingTheContent()
    {
        Assert.Equal(0, CoverGrid.ScrollTo(new CoverRect(0, 0, 100, 100), 0, 400, 2000));
        Assert.Equal(850, CoverGrid.ScrollTo(new CoverRect(0, 900, 100, 100), 100, 400, 2000));
        Assert.Equal(1600, CoverGrid.ScrollTo(new CoverRect(0, 1900, 100, 100), 0, 400, 2000));
    }
}

public sealed class CoverTextTests
{
    // a monospace stand-in for ImGui: every character is 0.6 of the font size wide
    private static float Measure(string s) => s.Length * 0.6f;

    [Fact]
    public void ASeriesNameBecomesTheKicker()
    {
        Assert.Equal(("FINAL FANTASY", "VI"), CoverText.Split("Final Fantasy VI"));
        Assert.Equal(("FINAL FANTASY", "Tactics Advance"), CoverText.Split("Final Fantasy Tactics Advance"));
        Assert.Equal(("", "Final Fantasy"), CoverText.Split("Final Fantasy"));
        Assert.Equal(("", "Chrono Trigger"), CoverText.Split("Chrono Trigger"));
        Assert.Equal(("", "Final Fantasylike"), CoverText.Split("Final Fantasylike"));
    }

    [Fact]
    public void AShortTitleGetsTheLargestSize()
    {
        var t = CoverText.Fit("Final Fantasy VI", 100, 80, 40, 10, Measure);
        Assert.Equal(40, t.Size);
        Assert.Equal(["VI"], t.Lines);
    }

    [Fact]
    public void ALongTitleShrinksUntilEveryLineFits()
    {
        var t = CoverText.Fit("Chrono Trigger and the Extraordinarily Long Subtitle", 100, 60, 40, 8, Measure);
        Assert.InRange(t.Size, 8, 39.9f);
        Assert.All(t.Lines, l => Assert.True(Measure(l) * t.Size <= 100.001f, l));
        Assert.True(t.Lines.Count * t.Size * 1.12f <= 60.001f);
        Assert.DoesNotContain(t.Lines, l => l.EndsWith('…'));
        Assert.Equal("Chrono Trigger and the Extraordinarily Long Subtitle", string.Join(' ', t.Lines));
    }

    [Fact]
    public void WhatCannotFitEvenAtTheSmallestSizeEndsInAnEllipsis()
    {
        var t = CoverText.Fit(string.Join(' ', Enumerable.Repeat("word", 80)), 60, 30, 20, 10, Measure);
        Assert.Equal(10, t.Size);
        Assert.EndsWith("…", t.Lines[^1]);
        Assert.All(t.Lines, l => Assert.True(Measure(l) * t.Size <= 60.001f, l));
        var single = CoverText.Fit("Supercalifragilisticexpialidocious", 60, 30, 20, 10, Measure);
        Assert.Single(single.Lines);
        Assert.True(Measure(single.Lines[0]) * single.Size <= 60.001f);
    }

    [Fact]
    public void AnEmptyTitleIsNotACrash() => Assert.Empty(CoverText.Fit("  ", 100, 50, 30, 10, Measure).Lines);
}

public sealed class CoverCacheTests
{
    private sealed class Picture(string name) : IDisposable
    {
        public string Name { get; } = name;

        public int Disposed;

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private static T? Wait<T>(CoverCache<T> cache, string key)
        where T : class, IDisposable
    {
        for (var i = 0; i < 400; i++)
        {
            if (cache.Get(key) is { } v)
                return v;
            Thread.Sleep(5);
        }

        return null;
    }

    [Fact]
    public void GetNeverWaitsForTheLoad()
    {
        var release = new TaskCompletionSource<Picture?>();
        using var cache = new CoverCache<Picture>(4, 2, (_, _) => release.Task);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(cache.Get("a"));
        Assert.Null(cache.Get("a"));
        Assert.True(sw.ElapsedMilliseconds < 500);
        release.SetResult(new Picture("a"));
        Assert.Equal("a", Wait(cache, "a")!.Name);
    }

    [Fact]
    public void TheLoadRunsOffTheCallingThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var loader = 0;
        using var cache = new CoverCache<Picture>(4, 2, (k, _) =>
        {
            loader = Environment.CurrentManagedThreadId;
            return Task.FromResult<Picture?>(new Picture(k));
        });
        Assert.NotNull(Wait(cache, "a"));
        Assert.NotEqual(caller, loader);
    }

    [Fact]
    public void TheLeastRecentlyUsedPictureIsEvictedAndDisposedOnce()
    {
        var made = new System.Collections.Concurrent.ConcurrentDictionary<string, Picture>();
        using var cache = new CoverCache<Picture>(2, 4, (k, _) => Task.FromResult<Picture?>(made[k] = new Picture(k)));
        Wait(cache, "a");
        Wait(cache, "b");
        Assert.NotNull(cache.Get("a")); // a is now the most recent
        Wait(cache, "c");
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, made["b"].Disposed);
        Assert.Equal(0, made["a"].Disposed);
        Assert.Equal(0, made["c"].Disposed);
    }

    [Fact]
    public void DisposeReleasesEveryPictureIncludingOnesStillArriving()
    {
        var late = new TaskCompletionSource<Picture?>();
        var made = new System.Collections.Concurrent.ConcurrentBag<Picture>();
        var cache = new CoverCache<Picture>(8, 8, (k, _) =>
        {
            if (k == "late")
                return late.Task;
            var p = new Picture(k);
            made.Add(p);
            return Task.FromResult<Picture?>(p);
        });
        Wait(cache, "a");
        Wait(cache, "b");
        Assert.Null(cache.Get("late"));
        cache.Dispose();
        cache.Dispose();
        var straggler = new Picture("late");
        late.SetResult(straggler);
        for (var i = 0; i < 400 && straggler.Disposed == 0; i++)
            Thread.Sleep(5);
        Assert.Equal(1, straggler.Disposed);
        Assert.All(made, p => Assert.Equal(1, p.Disposed));
        Assert.Null(cache.Get("a"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void AFailedLoadIsRememberedInsteadOfRetriedEveryFrame()
    {
        var calls = 0;
        using var cache = new CoverCache<Picture>(4, 2, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("unreadable");
        });
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(cache.Get("bad"));
            Thread.Sleep(2);
        }

        Assert.Equal(1, calls);
        cache.Invalidate("bad");
        cache.Get("bad");
        for (var i = 0; i < 200 && calls < 2; i++)
            Thread.Sleep(5);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void NoMoreLoadsRunAtOnceThanAllowed()
    {
        var gate = new TaskCompletionSource<Picture?>();
        var started = 0;
        using var cache = new CoverCache<Picture>(64, 3, (_, _) =>
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        });
        for (var i = 0; i < 20; i++)
            cache.Get("k" + i);
        Thread.Sleep(100);
        Assert.Equal(3, started);
        gate.SetResult(null);
    }
}
