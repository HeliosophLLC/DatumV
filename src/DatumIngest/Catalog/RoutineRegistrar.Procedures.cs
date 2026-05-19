using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

internal sealed partial class RoutineRegistrar
{
    // ───────────────────── Procedures ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE PROCEDURE</c> statement. The procedural body is
    /// validated against the registry (every embedded <c>udf.X</c> call
    /// must resolve) and the verbatim source text is captured so
    /// <c>system_procedures.source_text</c> can show the user's exact
    /// formatting and a round-trip through <see cref="CatalogStore"/>
    /// reparses the same SQL.
    /// </summary>
    public void ApplyCreateProcedure(CreateProcedureStatement create, string? sourceText)
    {
        ValidateDefaultsContiguous(create.Parameters, $"CREATE PROCEDURE {create.Name}");

        // Same DDL-capable schema rules as ApplyCreateFunction: explicit
        // qualification wins; unqualified picks the first DDL-capable
        // schema on the session search_path.
        QualifiedName qn = Resolver().ResolveForCreate(create.SchemaName, create.Name);

        try
        {
            ValidateProcedureBody(create.Body);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE PROCEDURE {qn}: {ex.Message}", ex);
        }

        // Arity / kind gate over the body's reachable function calls. Runs
        // after the udf-inliner walker (above) so a bad inner-call surfaces
        // at CREATE PROCEDURE rather than at the first CALL. Wraps the
        // routine name onto the error so it's immediately attributable.
        ProceduralBodyArityGate.Enforce(
            [create.Body], create.Parameters, _functions, $"procedure {qn}");

        // When the source text isn't available (e.g. registered via the AST-only
        // BatchExecutor path), store a placeholder so the procedure can still run
        // and persist. The display in system_procedures.source_text will show this
        // synthetic text rather than the user's original formatting.
        string text = sourceText ?? $"CREATE PROCEDURE {qn}";

        ProcedureDescriptor descriptor = new(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            Body: create.Body,
            SourceText: text);

        if (create.IfNotExists && _procedures.TryGet(qn, out _))
        {
            return;
        }

        // Capture pre-state for the event below — Created vs Altered turns
        // on whether the key already had a descriptor.
        _procedures.TryGet(qn, out ProcedureDescriptor? before);

        _procedures.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels, _views);

        if (before is null)
        {
            _catalog.Events.Raise(new ProcedureCreatedEvent(qn, descriptor, sourceText));
        }
        else
        {
            _catalog.Events.Raise(new ProcedureAlteredEvent(qn, before, descriptor, sourceText));
        }
    }

    /// <summary>
    /// Applies a <c>DROP PROCEDURE</c> statement. Throws when the named
    /// procedure isn't registered unless the statement carries <c>IF EXISTS</c>.
    /// </summary>
    public void ApplyDropProcedure(DropProcedureStatement drop, string? sourceText = null)
    {
        if (!_procedures.TryResolve(drop.SchemaName, drop.Name, _catalog.SearchPath, out ProcedureDescriptor? proc))
        {
            if (drop.IfExists) return;
            string label = drop.SchemaName is null ? drop.Name : $"{drop.SchemaName}.{drop.Name}";
            throw new InvalidOperationException(
                $"Procedure '{label}' is not registered. " +
                "Use DROP PROCEDURE IF EXISTS to make this a no-op.");
        }

        _procedures.Unregister(proc.QualifiedName);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels, _views);

        _catalog.Events.Raise(new ProcedureDroppedEvent(proc.QualifiedName, proc, sourceText));
    }

    /// <summary>
    /// Walks every expression in a procedure body's statement tree and
    /// runs the UDF inliner against it, so unresolved <c>udf.X(...)</c>
    /// references surface at <c>CREATE PROCEDURE</c> time rather than at
    /// the first <c>CALL</c>. Doesn't substitute parameters — those are
    /// resolved at runtime when the procedure is invoked.
    /// </summary>
    private void ValidateProcedureBody(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement child in block.Statements) ValidateProcedureBody(child);
                break;
            case IfStatement ifs:
                _ = UdfInliner.Inline(ifs.Predicate, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(ifs.Then);
                if (ifs.Else is not null) ValidateProcedureBody(ifs.Else);
                break;
            case WhileStatement loop:
                _ = UdfInliner.Inline(loop.Predicate, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(loop.Body);
                break;
            case ForCounterStatement forC:
                _ = UdfInliner.Inline(forC.Start, _udfs, _catalog.SearchPath);
                _ = UdfInliner.Inline(forC.End, _udfs, _catalog.SearchPath);
                if (forC.Step is not null) _ = UdfInliner.Inline(forC.Step, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(forC.Body);
                break;
            case ForInStatement forIn:
                _ = UdfInliner.Inline(forIn.Source, _udfs, _catalog.SearchPath);
                ValidateProcedureBody(forIn.Body);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null) _ = UdfInliner.Inline(decl.Initializer, _udfs, _catalog.SearchPath);
                break;
            case SetStatement set:
                _ = UdfInliner.Inline(set.Value, _udfs, _catalog.SearchPath);
                break;
            case QueryStatement q:
                _ = UdfInliner.Inline(q.Query, _udfs, _catalog.SearchPath);
                break;
            case CallStatement call:
                _ = UdfInliner.Inline(call.Call, _udfs, _catalog.SearchPath);
                break;
            case BreakStatement:
            case ContinueStatement:
                // No expressions to validate; legality (must sit inside a
                // loop) is enforced at invocation time by the executor.
                break;
            // Nested routine DDL inside a procedure body is rejected here so
            // the user sees the error at CREATE PROCEDURE rather than at the
            // first CALL. Nested DML and table DDL are intentionally allowed
            // — procedures should be able to mutate data and shape temp
            // tables.
            case CreateFunctionStatement createFn:
                throw new InvalidOperationException(
                    $"Nested CREATE FUNCTION '{createFn.Name}' is not allowed inside a " +
                    "procedure body. Define UDFs at the top level before the procedure.");
            case CreateProcedureStatement createProc:
                throw new InvalidOperationException(
                    $"Nested CREATE PROCEDURE '{createProc.Name}' is not allowed inside a " +
                    "procedure body.");
            case DropFunctionStatement dropFn:
                throw new InvalidOperationException(
                    $"Nested DROP FUNCTION '{dropFn.Name}' is not allowed inside a procedure body.");
            case DropProcedureStatement dropProc:
                throw new InvalidOperationException(
                    $"Nested DROP PROCEDURE '{dropProc.Name}' is not allowed inside a procedure body.");
            default:
                break;
        }
    }

    /// <summary>
    /// Reads the on-disk size of the model bundle and applies the same
    /// 1.2× multiplier the C# builtin path uses to size resident VRAM.
    /// Returns 0 (forces the admission manager to treat the load as
    /// unknown-size) when the file is missing or unreadable rather than
    /// throwing — registration is past the point where we know the file
    /// existed at <c>CREATE MODEL</c> time, so a stat failure here is a
    /// rare race we'd rather log around than abort.
    /// </summary>
    private static long EstimateFileSizeBytes(string? resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath)) return 0;
        try
        {
            FileInfo info = new(resolvedPath);
            if (!info.Exists) return 0;
            // 1.2× — matches the builtin ModelCatalogEntry estimates and the
            // residency manager's own default multiplier.
            return (long)(info.Length * 1.2);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Registration-time pre-flight: evaluates each parameter's default
    /// expression once against an empty scope and runs the canonicalised
    /// <c>CHECK</c> against it, so an out-of-range default (e.g. a typo'd
    /// <c>0.25</c> that should have been <c>0.025</c>) surfaces as a
    /// CREATE-time error instead of waiting for the first call site that
    /// happens to omit the override. Skips parameters without a default
    /// (nothing to evaluate) or without a check (nothing to enforce).
    /// </summary>
    /// <remarks>
    /// Parameter defaults in real catalog SQL are constant expressions —
    /// <c>CAST(0.25 AS Float32)</c>, literals, simple arithmetic — so the
    /// evaluator never blocks. The evaluator is built against the same
    /// <see cref="FunctionRegistry"/> the body uses, so a default that
    /// invokes a registered function (rare, but legal) still resolves.
    /// </remarks>
    private async ValueTask ValidateDefaultsAgainstChecksAsync(
        IReadOnlyList<UdfParameter> parameters,
        string contextLabel,
        Heliosoph.DatumV.Execution.ExecutionContext context,
        CancellationToken cancellationToken)
    {
        Arena scratch = new();
        VariableScope checkScope = new(context.Accountant);
        // Scope-bound evaluator so CustomCheck expressions resolve the
        // parameter name to the just-evaluated default value (and any
        // earlier parameter to its evaluated default). The check scope and
        // scratch arena are isolated to this validation pass; everything
        // else (accountant, types, video registry) is borrowed from the
        // caller's context.
        using Execution.ExecutionContext checkContext = context.Derive(
            store: scratch,
            variableScope: checkScope,
            variableStore: scratch);
        ExpressionEvaluator evaluator = checkContext.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, scratch);

        for (int i = 0; i < parameters.Count; i++)
        {
            UdfParameter p = parameters[i];
            if (p.Default is null) continue;

            ValueRef defaultValue;
            try
            {
                defaultValue = await evaluator.EvaluateAsValueRefAsync(p.Default, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't pretend a broken default is a constraint failure — surface
                // the underlying evaluation error with the registration context
                // wrapped around it so the caller knows which CREATE site to fix.
                throw new InvalidOperationException(
                    $"{contextLabel}: default expression for parameter '@{p.Name}' failed to evaluate at registration time. {ex.Message}",
                    ex);
            }

            // Declare into scope so subsequent parameters' CustomChecks can
            // reference this one — mirrors the runtime BindParametersAsync
            // ordering. Cheap; the scratch arena + accountant are torn down
            // with this method.
            checkScope.Declare(p.Name, defaultValue);

            if (p.Check is null) continue;
            ParameterCheck typed = ParameterCheckWalker.Canonicalise(p.Check, p.Name);

            if (typed is CustomCheck cc)
            {
                if (defaultValue.IsNull) continue;
                bool ok = await evaluator.EvaluateAsBooleanAsync(cc.Expr, frame, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    throw new FunctionArgumentException(
                        contextLabel,
                        $"default value for parameter '@{p.Name}' violates CHECK constraint.");
                }
                continue;
            }

            string? error = typed.Validate(defaultValue);
            if (error is not null)
            {
                throw new FunctionArgumentException(
                    contextLabel,
                    $"default value for parameter '@{p.Name}' violates CHECK: {error}");
            }
        }
    }

    /// <summary>
    /// Lifts the four UI-facing fields on a <see cref="UdfParameter"/>
    /// (<c>Check</c>, <c>Step</c>, <c>Unit</c>, <c>Description</c>) into a
    /// <see cref="ParameterMetadata"/> record, canonicalising the raw
    /// CHECK expression through <see cref="ParameterCheckWalker"/> so the
    /// catalog surfaces a typed constraint shape. Returns <see langword="null"/>
    /// when no field is set — keeps the registered <see cref="ParameterSpec"/>
    /// clean for parameters without any declared hints.
    /// </summary>
    private static ParameterMetadata? BuildParameterMetadata(UdfParameter p)
    {
        if (p.Check is null && p.Step is null && p.Unit is null && p.Description is null)
        {
            return null;
        }
        ParameterCheck? check = p.Check is null
            ? null
            : ParameterCheckWalker.Canonicalise(p.Check, p.Name);
        return new ParameterMetadata(
            Check: check,
            Step: p.Step,
            Unit: p.Unit,
            Description: p.Description);
    }
}
