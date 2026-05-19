namespace Heliosoph.DatumV.Manifest.Insights.Rules;

/// <summary>
/// Detects image columns with quality issues: tiny images (below 32px), huge images
/// (above 4096px), or undecodable files.
/// </summary>
internal sealed class ImageQualityRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (feature is not ImageFeatureManifest image)
            {
                continue;
            }

            if (image.ValidCount == 0)
            {
                continue;
            }

            long total = image.ValidCount;

            double tinyRatio = (double)image.TinyImageCount / total;
            double hugeRatio = (double)image.HugeImageCount / total;
            double undecodableRatio = (double)image.UndecodableCount / total;

            if (tinyRatio > thresholds.TinyImageMinRatio)
            {
                yield return EmitTinyImages(image, tinyRatio, thresholds);
            }

            if (hugeRatio > thresholds.HugeImageMinRatio)
            {
                yield return EmitHugeImages(image, hugeRatio, thresholds);
            }

            if (undecodableRatio > thresholds.UndecodableImageMinRatio)
            {
                yield return EmitUndecodableImages(image, undecodableRatio, thresholds);
            }
        }
    }

    private static RawFinding EmitTinyImages(ImageFeatureManifest image, double tinyRatio, InsightThresholds thresholds)
    {
        double confidence = Math.Min(1.0, 0.7 + (tinyRatio * 3.0));

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(image.Name, "tinyImageCount", image.TinyImageCount)
            .Add(image.Name, "tinyImageRatio", tinyRatio)
            .Add(image.Name, "minWidth", image.MinWidth)
            .Add(image.Name, "minHeight", image.MinHeight)
            .Add(image.Name, "validCount", image.ValidCount);

        return new RawFinding(
            InsightKind.TinyImages,
            InsightCategory.ImageQuality,
            InsightSeverity.Warning,
            confidence,
            InsightScope.Feature,
            $"Column '{image.Name}' contains {image.TinyImageCount:N0} images ({tinyRatio:P1}) with width or height below 32px (min: {image.MinWidth}×{image.MinHeight}).",
            "Tiny images contain too few pixels for meaningful feature extraction. Upscaling them introduces artifacts without adding information.",
            $"Filter out images below minimum resolution, or investigate why they are present.",
            Rationale: null,
            Alternatives: ["Set a minimum resolution threshold and drop affected rows."],
            [image.Name],
            [new InsightAction(
                ActionKind.Filter,
                Column: null,
                Expression: $"[{image.Name}].Width >= 32 AND [{image.Name}].Height >= 32",
                Alias: null,
                Lossy: true,
                Reversible: false,
                BundleIdentifier: null)],
            ConflictGroup: null,
            evidence.Build());
    }

    private static RawFinding EmitHugeImages(ImageFeatureManifest image, double hugeRatio, InsightThresholds thresholds)
    {
        double confidence = Math.Min(1.0, 0.7 + (hugeRatio * 3.0));

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(image.Name, "hugeImageCount", image.HugeImageCount)
            .Add(image.Name, "hugeImageRatio", hugeRatio)
            .Add(image.Name, "maxWidth", image.MaxWidth)
            .Add(image.Name, "maxHeight", image.MaxHeight)
            .Add(image.Name, "validCount", image.ValidCount);

        return new RawFinding(
            InsightKind.HugeImages,
            InsightCategory.ImageQuality,
            InsightSeverity.Warning,
            confidence,
            InsightScope.Feature,
            $"Column '{image.Name}' contains {image.HugeImageCount:N0} images ({hugeRatio:P1}) with width or height above 4096px (max: {image.MaxWidth}×{image.MaxHeight}).",
            "Huge images consume excessive GPU memory during training and may need to be resized anyway. Processing time scales quadratically with resolution.",
            $"Resize images in '{image.Name}' to a maximum dimension before training.",
            Rationale: null,
            Alternatives: ["Use progressive resizing during training."],
            [image.Name],
            [],
            ConflictGroup: null,
            evidence.Build());
    }

    private static RawFinding EmitUndecodableImages(ImageFeatureManifest image, double undecodableRatio, InsightThresholds thresholds)
    {
        double confidence = Math.Min(1.0, 0.8 + (undecodableRatio * 2.0));

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(image.Name, "undecodableCount", image.UndecodableCount)
            .Add(image.Name, "undecodableRatio", undecodableRatio)
            .Add(image.Name, "validCount", image.ValidCount);

        return new RawFinding(
            InsightKind.UndecodableImages,
            InsightCategory.ImageQuality,
            InsightSeverity.Critical,
            confidence,
            InsightScope.Feature,
            $"Column '{image.Name}' contains {image.UndecodableCount:N0} images ({undecodableRatio:P1}) that could not be decoded.",
            "Undecodable images will cause runtime failures during training or require error handling that skips rows, silently reducing the effective dataset size.",
            $"Filter out undecodable images from '{image.Name}' and investigate the source of corruption.",
            Rationale: null,
            Alternatives: null,
            [image.Name],
            [new InsightAction(
                ActionKind.Filter,
                Column: null,
                Expression: $"[{image.Name}].IsDecodable = 1",
                Alias: null,
                Lossy: true,
                Reversible: false,
                BundleIdentifier: null)],
            ConflictGroup: null,
            evidence.Build());
    }
}
