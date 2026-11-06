using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>
/// Packages and unpacks portable evidence bundles with strict Zip-Slip protection, bounded streaming, and digest validation.
/// </summary>
public static class EvidenceBundle
{
    public const long MaximumBundleEntryBytes = 500 * 1024 * 1024; // 500 MB bounded single entry

    public sealed record ExportOptions(
        string ManifestPath,
        string OutputPath,
        string? StorePath = null,
        string? ArtifactsDirectory = null,
        string? DeltaPath = null,
        string? AssessmentPath = null,
        string? BaselineReferencePath = null);

    public sealed record ImportResult(
        string ManifestId,
        int DocumentsCount,
        int ArtifactsCount,
        IReadOnlyList<string> ImportedDigests);

    /// <summary>
    /// Exports a self-contained portable evidence bundle zip file.
    /// </summary>
    public static void Export(ExportOptions options)
    {
        if (!File.Exists(options.ManifestPath))
        {
            throw new FileNotFoundException($"Manifest not found: {options.ManifestPath}", options.ManifestPath);
        }

        var manifestBytes = File.ReadAllBytes(options.ManifestPath);
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var root = manifestDoc.RootElement;
        if (!root.TryGetProperty("id", out var idProp) || !root.TryGetProperty("artifacts", out var artifactsProp))
        {
            throw new InvalidOperationException("Invalid manifest document: missing 'id' or 'artifacts'.");
        }

        var fullOutput = Path.GetFullPath(options.OutputPath);
        var outputDir = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(outputDir);

        var tempZip = Path.Combine(outputDir, $".bundle-{Guid.NewGuid():N}.tmp.zip");
        ContentAddressedStore? cas = options.StorePath is not null && Directory.Exists(options.StorePath)
            ? new ContentAddressedStore(options.StorePath)
            : null;

        try
        {
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // 1. Root manifest
                WriteZipEntry(archive, "manifest.json", manifestBytes);

                // 2. Documents
                WriteZipEntry(archive, "documents/manifest.json", manifestBytes);
                if (options.DeltaPath is not null && File.Exists(options.DeltaPath))
                {
                    WriteZipEntry(archive, "documents/delta.json", File.ReadAllBytes(options.DeltaPath));
                }
                if (options.AssessmentPath is not null && File.Exists(options.AssessmentPath))
                {
                    WriteZipEntry(archive, "documents/assessment.json", File.ReadAllBytes(options.AssessmentPath));
                }
                if (options.BaselineReferencePath is not null && File.Exists(options.BaselineReferencePath))
                {
                    WriteZipEntry(archive, "documents/baseline-reference.json", File.ReadAllBytes(options.BaselineReferencePath));
                }

                // 3. Artifacts
                foreach (var artifact in artifactsProp.EnumerateArray())
                {
                    var digest = artifact.GetProperty("digest").GetString()!;
                    var artifactPath = artifact.GetProperty("path").GetString()!;

                    byte[]? artifactBytes = null;
                    if (cas is not null && cas.Contains(digest))
                    {
                        artifactBytes = cas.Get(digest);
                    }
                    else if (options.ArtifactsDirectory is not null)
                    {
                        var candidatePath = Path.Combine(options.ArtifactsDirectory, artifactPath);
                        if (File.Exists(candidatePath))
                        {
                            artifactBytes = File.ReadAllBytes(candidatePath);
                        }
                    }

                    if (artifactBytes is null)
                    {
                        throw new FileNotFoundException($"Required artifact '{artifactPath}' with digest '{digest}' could not be located in CAS or artifacts directory.");
                    }

                    // Verify digest before adding
                    var actualDigest = ContentAddressedStore.ComputeDigest(artifactBytes);
                    if (!string.Equals(actualDigest, digest, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Artifact '{artifactPath}' digest mismatch. Expected '{digest}', computed '{actualDigest}'.");
                    }

                    var cleanPath = NormalizeEntryPath(artifactPath);
                    WriteZipEntry(archive, $"artifacts/{cleanPath}", artifactBytes);
                    WriteZipEntry(archive, $"assets/sha256/{digest["sha256:".Length..]}", artifactBytes);
                }
            }

            File.Move(tempZip, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Imports a portable bundle into a destination ContentAddressedStore after verifying every entry's digest and safety.
    /// Every entry is verified before anything is written, so a rejected bundle leaves the store unchanged.
    /// Malformed bundles are reported as <see cref="InvalidOperationException"/> or <see cref="JsonException"/>.
    /// </summary>
    public static ImportResult Import(string bundlePath, ContentAddressedStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException($"Bundle file not found: {bundlePath}", bundlePath);
        }

        try
        {
            using var archive = ZipFile.OpenRead(bundlePath);
            return ImportArchive(archive, store);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException($"Bundle is not a readable zip archive: {exception.Message}", exception);
        }
    }

    private static ImportResult ImportArchive(ZipArchive archive, ContentAddressedStore store)
    {
        // Security check: validate all entry names first (Zip Slip prevention)
        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);
            if (entry.Length > MaximumBundleEntryBytes)
            {
                throw new InvalidOperationException($"Bundle entry '{entry.FullName}' exceeds maximum allowable size of {MaximumBundleEntryBytes} bytes.");
            }
        }

        var manifestEntry = archive.GetEntry("manifest.json") ?? archive.GetEntry("documents/manifest.json")
            ?? throw new InvalidOperationException("Bundle is missing required manifest.json.");

        var manifestBytes = ReadEntry(manifestEntry);
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var root = manifestDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Bundle manifest must be a JSON object.");
        }

        var manifestId = RequiredString(root, "id", "manifest");
        if (!root.TryGetProperty("artifacts", out var artifactsProp) || artifactsProp.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Bundle manifest must declare an 'artifacts' array.");
        }

        // Map declared digests
        var declaredArtifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var art in artifactsProp.EnumerateArray())
        {
            if (art.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Bundle manifest artifacts must be JSON objects.");
            }

            var relPath = NormalizeEntryPath(RequiredString(art, "path", "manifest artifact"));
            var digest = RequiredString(art, "digest", "manifest artifact");
            if (!ContentAddressedStore.IsValidDigest(digest))
            {
                throw new InvalidOperationException($"Manifest artifact '{relPath}' has an invalid digest '{digest}'.");
            }

            declaredArtifacts[relPath] = digest;
            declaredArtifacts[digest["sha256:".Length..]] = digest;
        }

        // Pass 1: decide what to import, read every entry and verify every digest without touching the store.
        // Reading here as well means a corrupt entry is rejected before anything is written.
        var plan = new List<(ZipArchiveEntry Entry, string? ExpectedDigest, bool IsDocument)>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            if (entry.FullName.StartsWith("documents/", StringComparison.Ordinal) && !entry.FullName.Equals("documents/manifest.json", StringComparison.Ordinal))
            {
                plan.Add((entry, null, true));
            }
            else if (entry.FullName.StartsWith("assets/sha256/", StringComparison.Ordinal))
            {
                var expectedDigest = "sha256:" + entry.FullName["assets/sha256/".Length..];
                if (!ContentAddressedStore.IsValidDigest(expectedDigest))
                {
                    throw new InvalidOperationException($"Bundle asset entry '{entry.FullName}' is not named by a SHA-256 digest.");
                }

                plan.Add((entry, expectedDigest, false));
            }
            else if (entry.FullName.StartsWith("artifacts/", StringComparison.Ordinal) &&
                     declaredArtifacts.TryGetValue(entry.FullName["artifacts/".Length..], out var declaredDigest))
            {
                plan.Add((entry, declaredDigest, false));
            }
        }

        var staged = new List<(byte[] Bytes, string? ExpectedDigest, bool IsDocument)>(plan.Count);
        foreach (var (entry, expectedDigest, isDocument) in plan)
        {
            var bytes = ReadEntry(entry);
            staged.Add((bytes, expectedDigest, isDocument));
            if (expectedDigest is null)
            {
                continue;
            }

            var actualDigest = ContentAddressedStore.ComputeDigest(bytes);
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Payload digest mismatch for bundle entry '{entry.FullName}'. Expected '{expectedDigest}' but computed '{actualDigest}'.");
            }
        }

        // Pass 2: everything verified; write to the store.
        var importedDigests = new List<string> { store.Put(manifestBytes) };
        var docCount = 1;
        var artifactCount = 0;
        foreach (var (bytes, expectedDigest, isDocument) in staged)
        {
            importedDigests.Add(store.Put(bytes, expectedDigest));
            if (isDocument)
            {
                docCount++;
            }
            else
            {
                artifactCount++;
            }
        }

        return new ImportResult(manifestId, docCount, artifactCount, importedDigests);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string RequiredString(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw new InvalidOperationException($"Bundle {context} is missing required string property '{name}'.");
        }

        return value.GetString()!;
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    public static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Zip entry name cannot be empty.");
        }

        if (name.Contains("..", StringComparison.Ordinal) ||
            name.StartsWith('/') || name.StartsWith('\\') ||
            (name.Length >= 2 && name[1] == ':'))
        {
            throw new InvalidOperationException($"Zip entry path traversal detected in '{name}'. Entry rejected.");
        }
    }

    public static string NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        ValidateEntryName(normalized);
        return normalized;
    }
}
