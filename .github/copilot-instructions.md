# Copilot Instructions — DatumIngest

## Code style

- **No abbreviations.** Use full, descriptive names everywhere. `Declaration` not `Decl`, `Parameter` not `Param`, `Configuration` not `Config`.
- **No `var` keyword.** Always use explicit types. The target-typed `new()` form is preferred on the right-hand side.
  - Bad: `var builder = new StringBuilder();`
  - Good: `StringBuilder builder = new();`
- **No DTO.** This is a domain model, not a data transfer layer. Types represent meaningful domain concepts with behavior, not property bags.

## Documentation

- **XML documentation** on all public and internal classes, methods, and properties. No exceptions.
- **Comments explain *why*, never *what*.** If a comment restates what the code already says, delete it. Comments exist to capture intent, constraints, trade-offs, or non-obvious reasoning.
- Check the markdown files (README.md, /docs/*.md) for any relevant documentation updates when making changes to features, benchmarks, or design. When in doubt if the docs need updating, always ask.

## Design

- **Small, focused objects.** Prefer many small classes/modules collaborating over one large class with many responsibilities. Each type should encapsulate a single piece of functionality. If a class is growing beyond ~100 lines, look for extraction opportunities.
- **Composition over inheritance.** Assemble behavior from small building blocks rather than deep class hierarchies.

## Testing

- **Test-first for bug fixes.** When fixing a bug, write a failing test that reproduces the issue *before* writing the fix. The test proves the bug exists and confirms the fix works.
- **All tests must pass.** Never leave the test suite in a broken state. Run all tests after every change and fix any regressions before proceeding.
- **Tests are first-class code.** Apply the same naming, documentation, and style rules to test code.

## Benchmarks

- **Update README after every benchmark run.** After running benchmarks (`dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks`), read the updated result files from `BenchmarkDotNet.Artifacts/results/*-report-github.md` and update the **Benchmarks → Results** section in `README.md` with the new numbers. Include Mean, Error, StdDev, and Allocated columns for each suite (Parsing, Providers, Execution, Statistics, Output). Update the environment header block and the analysis commentary beneath each table if the numbers have changed materially.

## Project context

- .NET 10, C# 14. Target framework `net10.0`.
- High-performance ML ETL library — performance and memory efficiency matter.
- ParquetSharp for Parquet I/O, PureHDF for HDF5, SkiaSharp for image transforms.
- xUnit for tests, BenchmarkDotNet for benchmarks.
