using Heliosoph.DatumV.LanguageServer;
using Heliosoph.DatumV.Web.Dtos.Lsp;
using Heliosoph.DatumV.Web.Lsp;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// SQL language-intelligence endpoints. Each route translates a JSON
/// request into a method on <see cref="LanguageService"/> against the
/// live, event-synced manifest held by <see cref="LanguageManifestService"/>.
/// No per-request manifest refresh — the manifest is kept current by
/// catalog-event subscriptions wired up at service construction.
/// </summary>
[ApiController]
[Route("api/lang")]
public sealed class LanguageController(LanguageManifestService manifest) : ControllerBase
{
    [HttpPost("complete")]
    public CompletionItem[] Complete([FromBody] LangPositionRequest req)
        => manifest.Service.GetCompletions(req.Sql, req.Offset);

    [HttpPost("hover")]
    public HoverResult? Hover([FromBody] LangPositionRequest req)
        => manifest.Service.GetHover(req.Sql, req.Offset);

    [HttpPost("signature")]
    public SignatureHelp? Signature([FromBody] LangPositionRequest req)
        => manifest.Service.GetSignatureHelp(req.Sql, req.Offset);

    [HttpPost("diagnose")]
    public Diagnostic[] Diagnose([FromBody] LangSqlRequest req)
        => manifest.Service.GetDiagnostics(req.Sql);

    [HttpGet("grammar")]
    public IActionResult Grammar()
    {
        // Bundles the Monarch grammar with the editor themes that pair
        // with it. The client passes `grammar` to
        // `monaco.languages.setMonarchTokensProvider('sql', …)` and the
        // light/dark theme bodies to `monaco.editor.defineTheme(…)`.
        // Both shapes are static (independent of the catalog), so no
        // dependence on the manifest service.
        return new JsonResult(new
        {
            grammar = MonarchGrammarFactory.Build(manifest.RegisteredFunctionNames),
            themes = MonarchEditorThemeFactory.BuildThemes(),
        });
    }
}
