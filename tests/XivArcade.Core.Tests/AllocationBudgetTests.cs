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
}
