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
    /// </summary>
    public static ImportResult Import(string bundlePath, ContentAddressedStore store)
    {
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException($"Bundle file not found: {bundlePath}", bundlePath);
        }

        using var archive = ZipFile.OpenRead(bundlePath);

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

        byte[] manifestBytes;
        using (var stream = manifestEntry.Open())
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            manifestBytes = ms.ToArray();
        }

        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var root = manifestDoc.RootElement;
        var manifestId = root.GetProperty("id").GetString()!;
        var artifactsProp = root.GetProperty("artifacts");

        // Map declared digests
        var declaredArtifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var art in artifactsProp.EnumerateArray())
        {
            var relPath = NormalizeEntryPath(art.GetProperty("path").GetString()!);
            var digest = art.GetProperty("digest").GetString()!;
            declaredArtifacts[relPath] = digest;
            declaredArtifacts[digest["sha256:".Length..]] = digest;
        }

        var importedDigests = new List<string>();
        int docCount = 0;
        int artifactCount = 0;

        // Ingest documents
        var manifestDigest = store.Put(manifestBytes);
        importedDigests.Add(manifestDigest);
        docCount++;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            if (entry.FullName.StartsWith("documents/", StringComparison.Ordinal) && !entry.FullName.Equals("documents/manifest.json", StringComparison.Ordinal))
            {
                byte[] docBytes;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    docBytes = ms.ToArray();
                }
                var d = store.Put(docBytes);
                importedDigests.Add(d);
                docCount++;
            }
            else if (entry.FullName.StartsWith("assets/sha256/", StringComparison.Ordinal))
            {
                var hex = entry.FullName["assets/sha256/".Length..];
                var expectedDigest = "sha256:" + hex;
                byte[] artBytes;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    artBytes = ms.ToArray();
                }

                var d = store.Put(artBytes, expectedDigest);
                importedDigests.Add(d);
                artifactCount++;
            }
            else if (entry.FullName.StartsWith("artifacts/", StringComparison.Ordinal))
            {
                var relPath = entry.FullName["artifacts/".Length..];
                if (declaredArtifacts.TryGetValue(relPath, out var expectedDigest))
                {
                    byte[] artBytes;
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        artBytes = ms.ToArray();
                    }
                    var d = store.Put(artBytes, expectedDigest);
                    importedDigests.Add(d);
                    artifactCount++;
                }
            }
        }

        return new ImportResult(manifestId, docCount, artifactCount, importedDigests);
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
