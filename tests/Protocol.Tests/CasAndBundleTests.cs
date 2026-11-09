using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>Unit and CLI integration checks for ContentAddressedStore and EvidenceBundle.</summary>
public static class CasAndBundleTests
{
    public static void RunAll()
    {
        CasStoresAndVerifiesAtomicObjects();
        CasDetectsCorruptedStoredObject();
        CasRejectsInvalidDigestFormats();
        BundleExportsAndImportsReproducibly();
        BundleRejectsZipSlipAttack();
        BundleRejectsTamperedArtifactBytes();
        CliBundleExportAndImportEndToEnd();
        Console.WriteLine("PASS content-addressed store and portable evidence bundles");
    }

    private static void CasStoresAndVerifiesAtomicObjects()
    {
        using var temp = new ScratchDirectory();
        var cas = new ContentAddressedStore(temp.DirectoryPath);

        var data = Encoding.UTF8.GetBytes("hello diffra cas");
        var digest = cas.Put(data);

        Equal("sha256:03b8bbd27f46976ec064b0b78cb6d633da6031d97f9aaf1362be97922da9e2f2", digest, "CAS digest computed incorrectly.");
        True(cas.Contains(digest), "CAS should contain stored object.");

        var retrieved = cas.Get(digest);
        Equal("hello diffra cas", Encoding.UTF8.GetString(retrieved), "Retrieved data should match original.");

        // Idempotent put
        var secondDigest = cas.Put(data, digest);
        Equal(digest, secondDigest, "Idempotent put should succeed and return same digest.");
    }

    private static void CasDetectsCorruptedStoredObject()
    {
        using var temp = new ScratchDirectory();
        var cas = new ContentAddressedStore(temp.DirectoryPath);

        var data = Encoding.UTF8.GetBytes("tamper proof test");
        var digest = cas.Put(data);

        // Tamper with the file on disk
        var filePath = cas.GetObjectPath(digest);
        File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("tampered bytes here"));

        Throws<InvalidOperationException>(() => cas.Get(digest), "Corrupted object must throw on Get.");
    }

    private static void CasRejectsInvalidDigestFormats()
    {
        using var temp = new ScratchDirectory();
        var cas = new ContentAddressedStore(temp.DirectoryPath);

        Throws<ArgumentException>(() => cas.Get("bad-digest"), "Bad digest must be rejected.");
        Throws<ArgumentException>(() => cas.Get("sha256:short"), "Short digest must be rejected.");
        Throws<ArgumentException>(() => cas.Get("md5:00000000000000000000000000000000"), "Non-sha256 digest must be rejected.");
    }

    private static void BundleExportsAndImportsReproducibly()
    {
        using var temp = new ScratchDirectory();
        var manifestPath = temp.FilePath("manifest.json");
        var artifactPath = temp.FilePath("metric.json");
        var artifactBytes = Encoding.UTF8.GetBytes("{\"cpu\": 42}");
        File.WriteAllBytes(artifactPath, artifactBytes);
        var artifactDigest = ContentAddressedStore.ComputeDigest(artifactBytes);

        var manifestContent = $$"""
        {
          "schema": "https://diffra.dev/schemas/manifest-v1.schema.json",
          "id": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "subject": { "kind": "git-repository", "id": "test/repo", "commit": "12345678" },
          "groups": [{
            "id": "g1", "name": "Quality", "role": "quality", "required": true,
            "artifactIds": ["art1"], "evidenceIds": [], "groups": []
          }],
          "artifacts": [{
            "id": "art1",
            "digest": "{{artifactDigest}}",
            "mediaType": "application/json",
            "sizeBytes": {{artifactBytes.Length}},
            "path": "metric.json"
          }],
          "evidenceIds": [],
          "provenance": {
            "producerId": "test", "producerVersion": "1.0",
            "environment": { "os": "linux" }, "source": "test"
          }
        }
        """;
        File.WriteAllText(manifestPath, manifestContent);

        var bundleZip = temp.FilePath("bundle.zip");
        EvidenceBundle.Export(new EvidenceBundle.ExportOptions(
            manifestPath,
            bundleZip,
            ArtifactsDirectory: temp.DirectoryPath));

        True(File.Exists(bundleZip), "Export should create the bundle zip file.");

        var casDir = temp.FilePath("cas-store");
        var cas = new ContentAddressedStore(casDir);
        var result = EvidenceBundle.Import(bundleZip, cas);

        Equal("sha256:1111111111111111111111111111111111111111111111111111111111111111", result.ManifestId, "Imported manifest ID should match.");
        True(cas.Contains(artifactDigest), "CAS should contain imported artifact.");
        Equal(artifactBytes, cas.Get(artifactDigest), "Imported artifact bytes should match exactly.");
    }

    private static void BundleRejectsZipSlipAttack()
    {
        using var temp = new ScratchDirectory();
        var zipPath = temp.FilePath("malicious.zip");

        // Manually build a zip with a traversal entry
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../../etc/passwd");
            using var s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes("malicious"));
        }

        var cas = new ContentAddressedStore(temp.FilePath("cas"));
        Throws<InvalidOperationException>(() => EvidenceBundle.Import(zipPath, cas), "Zip slip traversal must be rejected.");
    }

    private static void BundleRejectsTamperedArtifactBytes()
    {
        using var temp = new ScratchDirectory();
        var manifestPath = temp.FilePath("manifest.json");
        var artifactPath = temp.FilePath("data.txt");
        var artifactBytes = Encoding.UTF8.GetBytes("original data");
        File.WriteAllBytes(artifactPath, artifactBytes);
        var originalDigest = ContentAddressedStore.ComputeDigest(artifactBytes);

        var manifestContent = $$"""
        {
          "schema": "https://diffra.dev/schemas/manifest-v1.schema.json",
          "id": "sha256:2222222222222222222222222222222222222222222222222222222222222222",
          "subject": { "kind": "git-repository", "id": "test/repo", "commit": "12345678" },
          "groups": [],
          "artifacts": [{
            "id": "data",
            "digest": "{{originalDigest}}",
            "mediaType": "text/plain",
            "sizeBytes": {{artifactBytes.Length}},
            "path": "data.txt"
          }],
          "evidenceIds": [],
          "provenance": {
            "producerId": "test", "producerVersion": "1.0",
            "environment": { "os": "linux" }, "source": "test"
          }
        }
        """;
        File.WriteAllText(manifestPath, manifestContent);

        var bundleZip = temp.FilePath("bundle.zip");
        EvidenceBundle.Export(new EvidenceBundle.ExportOptions(manifestPath, bundleZip, ArtifactsDirectory: temp.DirectoryPath));

        // Now tamper with the zip: change artifact content in place
        var tamperedZip = temp.FilePath("tampered.zip");
        using (var inArchive = ZipFile.OpenRead(bundleZip))
        using (var outArchive = ZipFile.Open(tamperedZip, ZipArchiveMode.Create))
        {
            foreach (var entry in inArchive.Entries)
            {
                var newEntry = outArchive.CreateEntry(entry.FullName);
                using var outStream = newEntry.Open();
                if (entry.FullName.Contains("data.txt"))
                {
                    outStream.Write(Encoding.UTF8.GetBytes("tampered data!"));
                }
                else
                {
                    using var inStream = entry.Open();
                    inStream.CopyTo(outStream);
                }
            }
        }

        var cas = new ContentAddressedStore(temp.FilePath("cas"));
        Throws<InvalidOperationException>(() => EvidenceBundle.Import(tamperedZip, cas), "Tampered artifact bytes in bundle must be rejected.");
    }

    private static void CliBundleExportAndImportEndToEnd()
    {
        using var temp = new ScratchDirectory();
        var manifestPath = temp.FilePath("manifest.json");
        var artifactPath = temp.FilePath("payload.json");
        var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
        File.WriteAllBytes(artifactPath, bytes);
        var digest = ContentAddressedStore.ComputeDigest(bytes);

        var manifestContent = $$"""
        {
          "schema": "https://diffra.dev/schemas/manifest-v1.schema.json",
          "id": "sha256:3333333333333333333333333333333333333333333333333333333333333333",
          "subject": { "kind": "git-repository", "id": "test/repo", "commit": "12345678" },
          "groups": [],
          "artifacts": [{
            "id": "art1",
            "digest": "{{digest}}",
            "mediaType": "application/json",
            "sizeBytes": {{bytes.Length}},
            "path": "payload.json"
          }],
          "evidenceIds": [],
          "provenance": {
            "producerId": "test", "producerVersion": "1.0",
            "environment": { "os": "linux" }, "source": "test"
          }
        }
        """;
        File.WriteAllText(manifestPath, manifestContent);

        var bundleZip = temp.FilePath("bundle.zip");
        var casDir = temp.FilePath("cas-dir");

        var exportRes = RunCli("bundle", "export", "--manifest", manifestPath, "--artifacts-dir", temp.DirectoryPath, "-o", bundleZip);
        Equal(0, exportRes.ExitCode, $"diffra bundle export failed: {exportRes.StandardError}");
        True(File.Exists(bundleZip), "Exported bundle zip must exist.");

        var importRes = RunCli("bundle", "import", "--bundle", bundleZip, "--store", casDir);
        Equal(0, importRes.ExitCode, $"diffra bundle import failed: {importRes.StandardError}");

        var cas = new ContentAddressedStore(casDir);
        True(cas.Contains(digest), "Imported CAS store must contain the artifact digest.");
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunCli(string command, params string[] args)
    {
        var repoRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
        var cli = Path.Combine(repoRoot, "src", "Cli", "bin", configuration, "net10.0", "Diffra.Cli.dll");
        if (!File.Exists(cli)) throw new FileNotFoundException("Build the CLI before running CasAndBundleTests.", cli);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add(command);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate Diffra.slnx.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void Equal(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message} Byte sequence mismatch.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{message} Expected {typeof(TException).Name} but caught {ex.GetType().Name}: {ex.Message}");
        }

        throw new InvalidOperationException($"{message} Expected exception {typeof(TException).Name} was not thrown.");
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"diffra-cas-test-{Guid.NewGuid():N}");
        public string FilePath(string name) => Path.Combine(DirectoryPath, name);

        public ScratchDirectory() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore */ }
        }
    }
}
