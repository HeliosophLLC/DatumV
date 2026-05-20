using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

internal sealed partial class RoutineRegistrar
{
    // ───────────────────── Views ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE VIEW</c> statement. Resolves the target schema
    /// the same way <see cref="ApplyCreateFunction"/> does, builds the
    /// descriptor, and registers it. The body's SELECT statement is stored
    /// as-is — expansion against the live catalog happens at plan time
    /// when a query references the view from a FROM clause.
    /// </summary>
    public void ApplyCreateView(CreateViewStatement create, string? sourceText = null)
    {
        QualifiedName qn = Resolver().ResolveForCreate(create.SchemaName, create.Name);

        if (create.IfNotExists && _views.TryGet(qn, out _))
        {
            return;
        }

        // A view shouldn't shadow an existing table at the same qualified
        // name — the planner's source resolution would expand the view
        // first, hiding the table without any obvious signal. Block it at
        // registration so the conflict surfaces immediately.
        if (_catalog.TryGetTable(qn, out _))
        {
            throw new InvalidOperationException(
                $"CREATE VIEW {qn}: a table with that qualified name already exists.");
        }

        _views.TryGet(qn, out ViewDescriptor? before);

        string text = sourceText ?? $"CREATE VIEW {qn}";
        ViewDescriptor descriptor = new(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Body: create.Body,
            SourceText: text);

        _views.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels, _views);

        if (before is null)
        {
            _catalog.Events.Raise(new ViewCreatedEvent(qn, descriptor, sourceText));
        }
        else
        {
            _catalog.Events.Raise(new ViewAlteredEvent(qn, before, descriptor, sourceText));
        }
    }

    /// <summary>
    /// Applies a <c>DROP VIEW</c> statement. Throws when the named view
    /// isn't registered unless the statement carries <c>IF EXISTS</c>.
    /// </summary>
    public void ApplyDropView(DropViewStatement drop, string? sourceText = null)
    {
        if (!_views.TryResolve(drop.SchemaName, drop.Name, _catalog.SearchPath, out ViewDescriptor? view))
        {
            if (drop.IfExists) return;
            string label = drop.SchemaName is null ? drop.Name : $"{drop.SchemaName}.{drop.Name}";
            throw new InvalidOperationException(
                $"View '{label}' is not registered. Use DROP VIEW IF EXISTS to make this a no-op.");
        }

        _views.Unregister(view.QualifiedName);
        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels, _views);

        _catalog.Events.Raise(new ViewDroppedEvent(view.QualifiedName, view, sourceText));
    }
}
