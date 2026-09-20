using XivArcade.Core.Arcade;

namespace XivArcade.Core.Tests;

/// <summary>
/// Allocation budgets for code that runs on every keystroke in the search box. Bytes per call on one thread,
/// from the runtime's own counter; the budget is about twice today's cost, so a change that doubles the
/// garbage fails here instead of as stutter in the game.
/// </summary>
public sealed class AllocationBudgetTests
{
    private static long PerOperation(Action action, int n = 5000)
    {
        for (var i = 0; i < 500; i++)
            action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
    }

    [Fact]
    public void ScoringATitleStaysWithinItsAllocationBudget()
    {
        var bytes = PerOperation(() => ArcadeCommands.Score("Final Fantasy Tactics Advance", "ff tactics"));
        Assert.True(bytes < 4096, $"{bytes} bytes allocated per score, budget 4096");
    }

    [Fact]
    public void LayingOutTheCoverWallEveryFrameAllocatesNothing()
    {
        var grid = new CoverGrid();
        List<int> counts = [26, 120, 40, 300];
        var bytes = PerOperation(() =>
        {
            grid.Build(counts, 1180, 148, 190, 16, 34);
            var focused = CoverLayout.Focus(CoverLayout.AspectFit(1.4f, grid.Cells[7]), 0.6f);
            _ = CoverLayout.Brightness(0.6f) + focused.W + grid.Move(7, 0, 1) + CoverGrid.ScrollTo(grid.Cells[400], 0, 600, grid.Height);
        }, 2000);
        Assert.True(bytes < 64, $"{bytes} bytes allocated per frame of layout, budget 64");
    }
}
