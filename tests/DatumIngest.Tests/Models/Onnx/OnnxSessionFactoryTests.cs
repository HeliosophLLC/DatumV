namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using Microsoft.ML.OnnxRuntime;

/// <summary>
/// Diagnostic test that confirms whether CUDA actually loaded after
/// switching to the GPU ONNX Runtime package. If CUDA is present,
/// ONNX models run ~10× faster than on CPU; if it silently fell back,
/// every generation is CPU-bound and feels slow even though the
/// pipeline is correct. This test is the canary.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class OnnxSessionFactoryTests : ServiceTestBase
{
    /// <summary>
    /// Creates any small ONNX session, then checks the factory's
    /// availability cache. Self-skips when no model file is present
    /// (we need an actual ONNX file to trigger session creation).
    /// </summary>
    [Fact]
    public void CudaProbe_ReportsAvailability()
    {
        // Use MobileNetV2 — smallest registered ONNX file, fastest to load.
        string modelPath = Path.Combine(
            ModelCatalog.DefaultModelDirectory, BuiltinModels.MobileNetV2DefaultFilename);
        if (!File.Exists(modelPath)) return;

        // Trigger one session creation through the factory.
        using InferenceSession session = OnnxSessionFactory.Create(modelPath);

        // Print result so it shows up in test output.
        bool available = OnnxSessionFactory.IsCudaLikelyAvailable;
        Console.WriteLine($"CUDA available: {available}");

        // No assertion — this is purely diagnostic. The test passes either
        // way; the *output* tells you whether CUDA is loaded.
    }

    /// <summary>
    /// Diagnostic: try the CUDA EP directly, capturing the actual exception
    /// message. Tells us whether CUDA is missing entirely, has a version
    /// mismatch, or is failing for some other reason. Run this when the
    /// availability probe says <c>False</c> and you want to know why.
    /// </summary>
    [Fact]
    public void CudaProbe_DirectAttempt_PrintsErrorIfAny()
    {
        try
        {
            using SessionOptions options = new();
            options.AppendExecutionProvider_CUDA(0);
            Console.WriteLine("CUDA EP added successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CUDA EP failed: {ex.GetType().Name}");
            Console.WriteLine($"  Message: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Diagnostic: list which CUDA-related DLLs are actually present in the
    /// runtime native binaries directory. Helps identify whether ONNX Runtime
    /// has the providers it needs and whether LLamaSharp's CUDA backend left
    /// something useful behind.
    /// </summary>
    [Fact]
    public void RuntimeDllsList_PrintsAvailableNativeBinaries()
    {
        string baseDir = AppContext.BaseDirectory;
        string nativeDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
        Console.WriteLine($"Looking for native DLLs in: {nativeDir}");

        if (!Directory.Exists(nativeDir))
        {
            Console.WriteLine("  (directory does not exist)");
            return;
        }

        foreach (string file in Directory.EnumerateFiles(nativeDir, "*.dll"))
        {
            FileInfo info = new(file);
            Console.WriteLine($"  {info.Name} ({info.Length:N0} bytes)");
        }
    }
}
