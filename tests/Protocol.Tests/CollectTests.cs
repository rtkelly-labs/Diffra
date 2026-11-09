using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>End-to-end checks for Evidence collection through the built CLI.</summary>
public static class CollectTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static void RunAll()
    {
        CollectsDeterministicEvidence();
        RejectsInvalidPayload();
        RejectsMissingArguments();
        RefusesToOverwritePayload();
    }

    private static void CollectsDeterministicEvidence()
    {
        WithTemporaryDirectory(directory =>
        {
            var payloadOne = Path.Combine(directory, "payload-one.json");
            var payloadTwo = Path.Combine(directory, "payload-two.json");
            var outputOne = Path.Combine(directory, "evidence-one.json");
            var outputTwo = Path.Combine(directory, "evidence-two.json");
            File.WriteAllText(payloadOne, "{ \"b\": 2, \"a\": 1 }", new UTF8Encoding(false));
            File.WriteAllText(payloadTwo, "{\"a\":1,\"b\":2}", new UTF8Encoding(false));

            var first = RunCollect(payloadOne, outputOne);
            Assert(first.ExitCode == 0, $"Expected collection to succeed. {first.Error}");
            var second = RunCollect(payloadTwo, outputTwo);
            Assert(second.ExitCode == 0, $"Expected repeated collection to succeed. {second.Error}");

            var bytesOne = File.ReadAllBytes(outputOne);
            var bytesTwo = File.ReadAllBytes(outputTwo);
            Assert(bytesOne.SequenceEqual(bytesTwo), "Canonical-equivalent payloads should produce identical Evidence bytes.");
            EvidenceIdentity.Verify(bytesOne);

            using var document = JsonDocument.Parse(bytesOne);
            var root = document.RootElement;
            Assert(root.GetProperty("schema").GetString() == "https://diffra.dev/schemas/evidence-v1.schema.json", "Unexpected Evidence schema.");
            Assert(root.GetProperty("status").GetString() == "produced", "Expected produced status.");
            Assert(root.GetProperty("typeResolution").GetString() == "recognized", "Expected recognized type resolution.");
            Assert(root.GetProperty("id").GetString()!.StartsWith("sha256:", StringComparison.Ordinal), "Expected SHA-256 Evidence ID.");
            Assert(root.GetProperty("payload").GetRawText() == "{\"a\":1,\"b\":2}", "Expected canonical payload in output.");
            Assert(root.GetProperty("producer").GetProperty("configDigest").GetString()
                == "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", "Expected fixed empty-config digest.");
            Assert(root.GetProperty("artifactRefs").GetArrayLength() == 0, "Expected no artifact references in the initial collector.");
        });
    }

    private static void RejectsInvalidPayload()
    {
        WithTemporaryDirectory(directory =>
        {
            var payload = Path.Combine(directory, "payload.json");
            var output = Path.Combine(directory, "evidence.json");
            File.WriteAllText(payload, "{\"value\":1.5}", new UTF8Encoding(false));
            var result = RunCollect(payload, output);
            Assert(result.ExitCode != 0, "Fractional JSON payload values must be rejected.");
            Assert(!File.Exists(output), "Invalid payload must not create Evidence output.");
        });
    }

    private static void RejectsMissingArguments()
    {
        var result = RunCli("collect", "--payload", "missing.json");
        Assert(result.ExitCode != 0, "Missing required collect options must fail.");
    }

    private static void RefusesToOverwritePayload()
    {
        WithTemporaryDirectory(directory =>
        {
            var payload = Path.Combine(directory, "payload.json");
            File.WriteAllText(payload, "{\"value\":1}", new UTF8Encoding(false));
            var original = File.ReadAllBytes(payload);
            var result = RunCollect(payload, payload);
            Assert(result.ExitCode != 0, "The output path must not overwrite the payload.");
            Assert(original.SequenceEqual(File.ReadAllBytes(payload)), "Refusing overwrite must preserve the payload bytes.");
        });
    }

    private static (int ExitCode, string Error) RunCollect(string payload, string output) => RunCli(
        "collect",
        "--payload", payload,
        "--subject-id", "repo:example/project",
        "--commit", Commit,
        "--type-id", "org.example.value",
        "--type-version", "1.0.0",
        "--producer-id", "org.example.collector",
        "--producer-version", "1.0.0",
        "-o", output);

    private static (int ExitCode, string Error) RunCli(params string[] arguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var cliAssembly = Path.Combine(repositoryRoot, "src", "Cli", "bin", "Release", "net10.0", "Diffra.Cli.dll");
        if (!File.Exists(cliAssembly))
        {
            throw new FileNotFoundException("Build Diffra.slnx before running CollectTests.", cliAssembly);
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Diffra CLI process.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find Diffra.slnx above the test assembly.");
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"diffra-collect-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
