namespace DatumIngest.Tests.Execution;

/// <summary>
/// Collection definition for allocation regression tests. Disables parallel execution
/// so that <see cref="System.GC.CollectionCount"/> measurements are not contaminated
/// by GC pressure from other tests running concurrently.
/// </summary>
[CollectionDefinition("Allocation", DisableParallelization = true)]
public sealed class AllocationCollection;
