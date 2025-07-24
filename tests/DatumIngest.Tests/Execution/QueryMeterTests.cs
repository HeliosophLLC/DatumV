using DatumIngest.Execution;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="QueryMeter"/>, verifying accumulation,
/// budget enforcement, and thread-safety semantics.
/// </summary>
public class QueryMeterTests
{
    /// <summary>
    /// A newly created meter with no calls should report zero Query Units.
    /// </summary>
    [Fact]
    public void FunctionQueryUnits_Initially_Zero()
    {
        QueryMeter meter = new();

        Assert.Equal(0, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// Add increments the accumulated Query Units by the specified cost.
    /// </summary>
    [Fact]
    public void Add_AccumulatesCost()
    {
        QueryMeter meter = new();

        meter.Add(3);
        meter.Add(5);

        Assert.Equal(8, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// Adding a cost of zero does not change the accumulated total.
    /// </summary>
    [Fact]
    public void Add_ZeroCost_NoChange()
    {
        QueryMeter meter = new();
        meter.Add(10);

        meter.Add(0);

        Assert.Equal(10, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// When no budget is set, IsBudgetExceeded is always false regardless of accumulation.
    /// </summary>
    [Fact]
    public void IsBudgetExceeded_NoBudget_AlwaysFalse()
    {
        QueryMeter meter = new();

        meter.Add(1_000_000);

        Assert.False(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// When a budget is set and the accumulated cost is within bounds, IsBudgetExceeded is false.
    /// </summary>
    [Fact]
    public void IsBudgetExceeded_WithinBudget_False()
    {
        QueryMeter meter = new(budget: 100);

        meter.Add(50);
        meter.Add(50);

        Assert.False(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// When a budget is set and the accumulated cost exceeds it, IsBudgetExceeded is true.
    /// </summary>
    [Fact]
    public void IsBudgetExceeded_OverBudget_True()
    {
        QueryMeter meter = new(budget: 10);

        meter.Add(5);
        meter.Add(6);

        Assert.True(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// Budget at exactly the limit is not exceeded (boundary condition).
    /// </summary>
    [Fact]
    public void IsBudgetExceeded_ExactlyAtBudget_False()
    {
        QueryMeter meter = new(budget: 10);

        meter.Add(10);

        Assert.False(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// Budget property reflects the value passed to the constructor.
    /// </summary>
    [Fact]
    public void Budget_ReflectsConstructorArgument()
    {
        QueryMeter withBudget = new(budget: 42);
        QueryMeter withoutBudget = new();

        Assert.Equal(42, withBudget.Budget);
        Assert.Null(withoutBudget.Budget);
    }

    /// <summary>
    /// Concurrent adds from multiple threads produce a correct total, verifying thread safety.
    /// </summary>
    [Fact]
    public async Task Add_ConcurrentAccess_AccumulatesCorrectly()
    {
        QueryMeter meter = new();
        int threadCount = 8;
        int addsPerThread = 10_000;

        Task[] tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < addsPerThread; j++)
                {
                    meter.Add(1);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(threadCount * addsPerThread, meter.FunctionQueryUnits);
    }
}
