using DatumIngest.Catalog;
using DatumIngest.Web.Llm;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/llm")]
public sealed class LlmController : ControllerBase
{
    private readonly TableCatalog _catalog;

    public LlmController(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    // GET /api/llm/available — the LLMs the chat surface can pick from
    // right now. Empty list = "no LLMs installed", the Settings picker
    // collapses to its disabled / CTA state. Each entry carries a
    // FitsInBudget flag so the picker can dim entries the VRAM probe
    // says won't load on this host.
    [HttpGet("available")]
    public IReadOnlyList<InstalledLlm> Available()
    {
        if (_catalog.Models is null) return Array.Empty<InstalledLlm>();
        return ModelSelector.ListInstalled(_catalog.Models);
    }
}
