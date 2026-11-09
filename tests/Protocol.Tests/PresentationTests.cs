using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>Unit and CLI integration checks for DeliveryIndex, PresentationBundle, and local loopback serving.</summary>
public static class PresentationTests
{
    public static void RunAll()
    {
        DeliveryIndexVerifiesCanonicalIdentity();
        InventoryMapsAssetsToDigestPaths();
        PresentationBundleMaterializesCompleteLayout();
        CliPresentBundleAndServeWithCsp();
        Console.WriteLine("PASS artifact presentation, design-system report, and local review server");
    }

    private static void DeliveryIndexVerifiesCanonicalIdentity()
    {
        var assets = new[]
        {
            new DeliveryIndex.AssetEntry("metrics-json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "application/json", 84, "artifacts/metrics.json")
        };

        var indexJson = DeliveryIndex.Create("owner/project", "v1.0.0", new string('0', 40), assets);
        var doc = DeliveryIndex.Verify(Encoding.UTF8.GetBytes(indexJson));

        Equal("owner/project", doc.Repository, "Delivery index repository mismatch.");
        Equal("v1.0.0", doc.ReleaseTag, "Delivery index release tag mismatch.");
        Equal(1, doc.Assets.Count, "Delivery index asset count mismatch.");

        // Tamper with commit to ensure verification fails
        var tampered = indexJson.Replace(new string('0', 40), new string('1', 40), StringComparison.Ordinal);
        Throws<InvalidOperationException>(() => DeliveryIndex.Verify(Encoding.UTF8.GetBytes(tampered)), "Tampered delivery index must fail verification.");
    }

    private static void InventoryMapsAssetsToDigestPaths()
    {
        var items = new[]
        {
            new DeliveryIndex.InventoryItem("art-b", "b.png", "sha256:2222222222222222222222222222222222222222222222222222222222222222", "assets/sha256/2222222222222222222222222222222222222222222222222222222222222222", "image/png", 100),
            new DeliveryIndex.InventoryItem("art-a", "a.json", "sha256:1111111111111111111111111111111111111111111111111111111111111111", "assets/sha256/1111111111111111111111111111111111111111111111111111111111111111", "application/json", 50)
        };

        var inventoryJson = DeliveryIndex.CreateInventoryJson(items);
        using var parsed = JsonDocument.Parse(inventoryJson);
        var array = parsed.RootElement.EnumerateArray().ToList();

        Equal(2, array.Count, "Inventory count should be 2.");
        Equal("art-a", array[0].GetProperty("id").GetString(), "Inventory should be sorted by ID.");
        Equal("art-b", array[1].GetProperty("id").GetString(), "Inventory should be sorted by ID.");
    }

    private static void PresentationBundleMaterializesCompleteLayout()
    {
        using var scratch = new ScratchDirectory();
        var repoRoot = FindRepositoryRoot();
        var sampleDir = Path.Combine(repoRoot, "examples", "sample-project", "workflow");

        var deltaPath = Path.Combine(sampleDir, "delta.json");
        var assessmentPath = Path.Combine(sampleDir, "assessment.json");
        var baselineRefPath = Path.Combine(sampleDir, "baseline-reference.json");
        var manifestPath = Path.Combine(sampleDir, "baseline-manifest.json");
        var outDir = scratch.FilePath("review-bundle");

        var result = PresentationBundle.Materialize(new PresentationBundle.MaterializeOptions(
            OutputDirectory: outDir,
            DeltaPath: deltaPath,
            AssessmentPath: assessmentPath,
            BaselineReferencePath: baselineRefPath,
            ManifestPath: manifestPath,
            ArtifactsDirectory: sampleDir));

        True(File.Exists(Path.Combine(outDir, "index.html")), "index.html must exist.");
        True(File.Exists(Path.Combine(outDir, "inventory.json")), "inventory.json must exist.");
        True(File.Exists(Path.Combine(outDir, "documents", "delta.json")), "documents/delta.json must exist.");
        True(File.Exists(Path.Combine(outDir, "documents", "assessment.json")), "documents/assessment.json must exist.");
        True(File.Exists(Path.Combine(outDir, "documents", "baseline-reference.json")), "documents/baseline-reference.json must exist.");

        var html = File.ReadAllText(Path.Combine(outDir, "index.html"));
        Contains(html, "verdict-banner");
        Contains(html, "stat-grid");
        Contains(html, "Diffra comparison report");
        Contains(html, "--ds-surface-base");
    }

    private static void CliPresentBundleAndServeWithCsp()
    {
        using var scratch = new ScratchDirectory();
        var repoRoot = FindRepositoryRoot();
        var sampleDir = Path.Combine(repoRoot, "examples", "sample-project", "workflow");

        var deltaPath = Path.Combine(sampleDir, "delta.json");
        var assessmentPath = Path.Combine(sampleDir, "assessment.json");
        var baselineRefPath = Path.Combine(sampleDir, "baseline-reference.json");
        var bundleDir = scratch.FilePath("cli-bundle");

        var bundleRes = RunCli("present", "bundle",
            "--delta", deltaPath,
            "--assessment", assessmentPath,
            "--baseline-ref", baselineRefPath,
            "-o", bundleDir);

        Equal(0, bundleRes.ExitCode, $"diffra present bundle failed: {bundleRes.StandardError}");
        True(File.Exists(Path.Combine(bundleDir, "index.html")), "Bundle index.html must exist.");

        var freePort = GetFreePort();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
        var cli = Path.Combine(repoRoot, "src", "Cli", "bin", configuration, "net10.0", "Diffra.Cli.dll");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("present");
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add(bundleDir);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(freePort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var serverProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not start server process.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var baseUri = $"http://127.0.0.1:{freePort}/";

            // Wait for server to be responsive
            HttpResponseMessage? response = null;
            for (var i = 0; i < 30; i++)
            {
                try
                {
                    response = client.GetAsync(baseUri).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode) break;
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }

            True(response is not null && response.IsSuccessStatusCode, "Server did not respond with 200 OK.");
            var headers = response!.Headers;

            True(headers.Contains("Content-Security-Policy"), "Response must contain Content-Security-Policy header.");
            var csp = headers.GetValues("Content-Security-Policy").First();
            Contains(csp, "default-src 'self'");

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Contains(body, "Diffra comparison report");
            Contains(body, "verdict-banner");

            // Test Host spoofing rejection
            using var hostCheckRequest = new HttpRequestMessage(HttpMethod.Get, baseUri);
            hostCheckRequest.Headers.Host = "evil-domain.com";
            var spoofedResponse = client.SendAsync(hostCheckRequest).GetAwaiter().GetResult();
            Equal(HttpStatusCode.BadRequest, spoofedResponse.StatusCode, "Spoofed Host header must be rejected with 400 Bad Request.");
        }
        finally
        {
            try
            {
                serverProcess.Kill(entireProcessTree: true);
                serverProcess.WaitForExit(2000);
            }
            catch { /* ignore */ }
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunCli(string command, params string[] args)
    {
        var repoRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
        var cli = Path.Combine(repoRoot, "src", "Cli", "bin", configuration, "net10.0", "Diffra.Cli.dll");
        if (!File.Exists(cli)) throw new FileNotFoundException("Build the CLI before running PresentationTests.", cli);

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

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected string to contain '{expected}'.");
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
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"diffra-pres-test-{Guid.NewGuid():N}");
        public string FilePath(string name) => Path.Combine(DirectoryPath, name);

        public ScratchDirectory() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore */ }
        }
    }
}
