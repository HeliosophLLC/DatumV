namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Test-side container for the root-scope variable bindings captured at
/// the end of a procedural batch. Built off
/// <see cref="Heliosoph.DatumV.Execution.VariableScopeSnapshot.Capture"/>
/// so tests read assertions as <c>result.FinalBindings["x"]</c>.
/// </summary>
internal sealed record BatchSnapshot(IReadOnlyDictionary<string, object?> FinalBindings);
