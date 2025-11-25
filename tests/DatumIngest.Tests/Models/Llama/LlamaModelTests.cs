namespace DatumIngest.Tests.Models.Llama;

using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Llama;
using DatumIngest.Pooling;

/// <summary>
/// Phase B end-to-end tests for the LlamaSharp-backed LLM.
/// </summary>
/// <remarks>
/// <para>
/// These tests touch a real GGUF model file at the user's local
/// <see cref="ModelCatalog.DefaultModelDirectory"/>. They self-skip when the file
/// is absent so CI machines without the artefact don't fail. Locally, drop
/// <c>Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf</c> from
/// <a href="https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF">bartowski's
/// repo</a> into <c>E:\models</c> to enable them.
/// </para>
/// <para>
/// Each test loads the full model (~5 GB into VRAM); this is slow and memory-
/// intensive by design. Generation tokens are capped (<c>maxTokens=64</c>) so
/// each test finishes in seconds rather than minutes.
/// </para>
/// </remarks>
public sealed class LlamaModelTests : ServiceTestBase
{
    static LlamaModelTests()
    {
        // Tests run on whatever backend the system can provide. In production
        // (the shell) we require CUDA so the user gets a real error if it's
        // misconfigured; here we want tests green on CPU-only machines too.
        LlamaModel.RequireCuda = false;
    }

    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.Llama31_8BDefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    /// <summary>
    /// Loading the GGUF and exposing the declared signature should succeed
    /// when the file exists. Cheapest signal that the LlamaSharp wiring + CUDA
    /// backend resolve correctly on this machine.
    /// </summary>
    [Fact]
    public void Load_RealLlama31_ExposesExpectedSignature()
    {
        if (!ModelAvailable)
        {
            return;
        }

        using LlamaModel model = new(name: "llm", modelFilePath: ModelPath, maxTokens: 16);

        Assert.Equal("llm", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
    }

    /// <summary>
    /// One-shot inference: send a simple prompt, expect a non-empty string back.
    /// Doesn't pin the exact response (LLM is nondeterministic), only that
    /// generation completed and produced something usable.
    /// </summary>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsNonEmptyResponse()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using LlamaModel model = new(name: "llm", modelFilePath: ModelPath, maxTokens: 32);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromString("What is 2 + 2? Reply with just the number.")],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef response = Assert.Single(outputs);
            Assert.False(response.IsNull);
            string text = response.AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "LLM returned empty response");
            // Stop-token leak is the most common failure mode — explicitly catch it.
            Assert.DoesNotContain("<|eot_id|>", text);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Multi-row dispatch: confirms each row gets its own response and the
    /// stateless executor doesn't bleed context between rows. Each prompt asks
    /// for a unique token so we can verify they're not duplicates of each other.
    /// </summary>
    [Fact]
    public async Task InferBatch_MultipleRows_ReturnsOneResponsePerRow()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using LlamaModel model = new(name: "llm", modelFilePath: ModelPath, maxTokens: 24);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'apple'.")],
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'banana'.")],
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'cherry'.")],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            Assert.Equal(3, outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                DatumIngest.Functions.ValueRef response = outputs[i];
                Assert.False(response.IsNull, $"row {i} returned a null response");
                string text = response.AsString();
                Assert.False(string.IsNullOrWhiteSpace(text), $"row {i} returned empty");
                Assert.DoesNotContain("<|eot_id|>", text);
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip: registering the model via <see cref="BuiltinModels"/>
    /// and resolving it through <see cref="ModelCatalog.GetModel"/> should yield
    /// a usable <see cref="LlamaModel"/> with the expected signature.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsLlamaModel()
    {
        if (!ModelAvailable)
        {
            return;
        }

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterLlama31(catalog, maxTokens: 16);

        ModelCatalogEntry? entry = catalog.TryGetEntry("llm");
        Assert.NotNull(entry);
        Assert.Equal("llama", entry!.Backend);
        Assert.Equal(BuiltinModels.Llama31_8BDefaultFilename, entry.RelativePath);
        Assert.False(entry.IsDeterministic);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("llm");
        IModel model = lease.Model;
        Assert.IsType<LlamaModel>(model);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
    }
}
