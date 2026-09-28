using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>
/// Prepares a self-contained, verified presentation directory layout ready for local inspection or offline serving.
/// </summary>
public static class PresentationBundle
{
    public sealed record MaterializeOptions(
        string OutputDirectory,
        string DeltaPath,
        string? AssessmentPath = null,
        string? BaselineReferencePath = null,
        string? ManifestPath = null,
        string? DeliveryIndexPath = null,
        string? ArtifactsDirectory = null,
        string? StorePath = null);

    public sealed record Result(
        string OutputDirectory,
        string IndexHtmlPath,
        int DocumentsCount,
        int AssetsCount);

    /// <summary>
    /// Materializes a complete review-bundle presentation folder atomically.
    /// </summary>
    public static Result Materialize(MaterializeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeltaPath);

        if (!File.Exists(options.DeltaPath))
        {
            throw new FileNotFoundException($"Delta document not found: {options.DeltaPath}", options.DeltaPath);
        }

        var fullOutput = Path.GetFullPath(options.OutputDirectory);
        var parentDir = Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(parentDir);

        var tempDir = Path.Combine(parentDir, $".bundle-{Guid.NewGuid():N}.tmp");
        var docsDir = Path.Combine(tempDir, "documents");
        var assetsDir = Path.Combine(tempDir, "assets", "sha256");

        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(assetsDir);

        ContentAddressedStore? cas = options.StorePath is not null && Directory.Exists(options.StorePath)
            ? new ContentAddressedStore(options.StorePath)
            : null;

        try
        {
            var deltaBytes = File.ReadAllBytes(options.DeltaPath);
            DocumentIdentity.Verify("delta", deltaBytes);
            File.WriteAllBytes(Path.Combine(docsDir, "delta.json"), deltaBytes);
            var docsCount = 1;

            byte[]? assessmentBytes = null;
            if (options.AssessmentPath is not null && File.Exists(options.AssessmentPath))
            {
                assessmentBytes = File.ReadAllBytes(options.AssessmentPath);
                DocumentIdentity.Verify("assessment", assessmentBytes);
                File.WriteAllBytes(Path.Combine(docsDir, "assessment.json"), assessmentBytes);
                docsCount++;
            }

            byte[]? baselineRefBytes = null;
            if (options.BaselineReferencePath is not null && File.Exists(options.BaselineReferencePath))
            {
                baselineRefBytes = File.ReadAllBytes(options.BaselineReferencePath);
                DocumentIdentity.Verify("baseline-reference", baselineRefBytes);
                File.WriteAllBytes(Path.Combine(docsDir, "baseline-reference.json"), baselineRefBytes);
                docsCount++;
            }

            byte[]? manifestBytes = null;
            if (options.ManifestPath is not null && File.Exists(options.ManifestPath))
            {
                manifestBytes = File.ReadAllBytes(options.ManifestPath);
                File.WriteAllBytes(Path.Combine(docsDir, "manifest.json"), manifestBytes);
                docsCount++;
            }

            // Render HTML report
            var html = ReportRenderer.Render(deltaBytes, assessmentBytes, baselineRefBytes, ReportFormat.Html);
            var indexHtmlPath = Path.Combine(tempDir, "index.html");
            File.WriteAllText(indexHtmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var inventoryItems = new List<DeliveryIndex.InventoryItem>();
            var assetsCount = 0;

            // Materialize referenced artifacts if manifest is present
            if (manifestBytes is not null)
            {
                using var manifestDoc = JsonDocument.Parse(manifestBytes);
                var root = manifestDoc.RootElement;
                if (root.TryGetProperty("artifacts", out var artifactsProp))
                {
                    foreach (var art in artifactsProp.EnumerateArray())
                    {
                        var id = art.GetProperty("id").GetString()!;
                        var digest = art.GetProperty("digest").GetString()!;
                        var mediaType = art.GetProperty("mediaType").GetString()!;
                        var sizeBytes = art.GetProperty("sizeBytes").GetInt64();
                        var relPath = art.GetProperty("path").GetString()!;

                        byte[]? artBytes = null;
                        if (cas is not null && cas.Contains(digest))
                        {
                            artBytes = cas.Get(digest);
                        }
                        else if (options.ArtifactsDirectory is not null)
                        {
                            var candidate = Path.Combine(options.ArtifactsDirectory, relPath);
                            if (File.Exists(candidate))
                            {
                                artBytes = File.ReadAllBytes(candidate);
                            }
                        }

                        if (artBytes is not null)
                        {
                            var actualDigest = ContentAddressedStore.ComputeDigest(artBytes);
                            if (!string.Equals(actualDigest, digest, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException($"Artifact '{id}' digest mismatch.");
                            }

                            var hex = digest["sha256:".Length..];
                            var destAsset = Path.Combine(assetsDir, hex);
                            File.WriteAllBytes(destAsset, artBytes);
                            assetsCount++;

                            inventoryItems.Add(new DeliveryIndex.InventoryItem(
                                id,
                                Path.GetFileName(relPath),
                                digest,
                                $"assets/sha256/{hex}",
                                mediaType,
                                sizeBytes));
                        }
                    }
                }
            }

            // Write inventory.json
            var inventoryJson = DeliveryIndex.CreateInventoryJson(inventoryItems);
            File.WriteAllText(Path.Combine(tempDir, "inventory.json"), inventoryJson, new UTF8Encoding(false));

            // Write delivery index if present or generate from inventory
            if (options.DeliveryIndexPath is not null && File.Exists(options.DeliveryIndexPath))
            {
                var delivBytes = File.ReadAllBytes(options.DeliveryIndexPath);
                DeliveryIndex.Verify(delivBytes);
                File.WriteAllBytes(Path.Combine(tempDir, "diffra-delivery-index.json"), delivBytes);
            }
            else if (inventoryItems.Count > 0)
            {
                var assets = inventoryItems.Select(i => new DeliveryIndex.AssetEntry(i.Id, i.Digest, i.MediaType, i.SizeBytes, i.RelativePath)).ToList();
                var generatedIndex = DeliveryIndex.Create("local/repository", "v0.1.0", new string('0', 40), assets);
                File.WriteAllText(Path.Combine(tempDir, "diffra-delivery-index.json"), generatedIndex, new UTF8Encoding(false));
            }

            // Atomic move to outputDirectory
            if (Directory.Exists(fullOutput))
            {
                Directory.Delete(fullOutput, recursive: true);
            }
            Directory.Move(tempDir, fullOutput);

            return new Result(fullOutput, Path.Combine(fullOutput, "index.html"), docsCount, assetsCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
