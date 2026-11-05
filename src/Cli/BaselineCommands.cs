using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Cli;

internal static class BaselineCommands
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        var subcommand = args[0];
        var subArgs = args[1..];

        return subcommand switch
        {
            "verify" => RunVerify(subArgs),
            "resolve" => RunResolve(subArgs),
            "update" => RunUpdate(subArgs),
            _ => HandleUnknownSubcommand(subcommand)
        };
    }

    private static int RunVerify(string[] args)
    {
        try
        {
            var options = ParseArguments(args,
                ("--evidence", true),
                ("--verification", true),
                ("--head-commit", true),
                ("--policy-id", true),
                ("--policy-version", true),
                ("--rules-digest", true),
                ("-o", false),
                ("--issuer", false));

            var evidenceBytes = ReadBounded(options["--evidence"]);
            var verificationBytes = ReadBounded(options["--verification"]);
            var headCommit = options["--head-commit"];
            var policyId = options["--policy-id"];
            var policyVersion = options["--policy-version"];
            var rulesDigest = options["--rules-digest"];
            var issuer = options.GetValueOrDefault("--issuer", "diffra-maintainer");
            var outputPath = options.GetValueOrDefault("-o");

            var approval = PromotionManager.VerifyAndApprove(
                evidenceBytes,
                verificationBytes,
                headCommit,
                policyId,
                policyVersion,
                rulesDigest,
                issuer);

            var json = approval.ToJsonObject().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            if (outputPath != null)
            {
                WriteAtomically(outputPath, json);
                Console.WriteLine($"Baseline verification passed. Approval record saved to '{outputPath}'.");
            }
            else
            {
                Console.WriteLine(json);
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintVerifyUsage();
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Verification failed: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during baseline verify: {ex.Message}");
            return 1;
        }
    }

    private static int RunResolve(string[] args)
    {
        try
        {
            var options = ParseArguments(args,
                ("--repo", true),
                ("--head", true),
                ("--base", true),
                ("--evidence-id", true),
                ("--manifest-digest", true),
                ("-o", false),
                ("--role", false));

            var repo = options["--repo"];
            var head = options["--head"];
            var baseRef = options["--base"];
            var evidenceId = options["--evidence-id"];
            var manifestDigest = options["--manifest-digest"];
            var role = options.GetValueOrDefault("--role", "merge-base");
            var outputPath = options.GetValueOrDefault("-o");

            var resolved = GitBaselineResolver.ResolveAsync(
                repo,
                baseRef,
                head,
                evidenceId,
                manifestDigest,
                role: role).GetAwaiter().GetResult();

            var json = Encoding.UTF8.GetString(resolved.BaselineReferenceBytes);

            if (outputPath != null)
            {
                WriteAtomically(outputPath, json);
                Console.WriteLine($"Baseline resolved commit '{resolved.ResolvedCommit}'. Written to '{outputPath}'.");
            }
            else
            {
                Console.WriteLine(json);
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintResolveUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during baseline resolve: {ex.Message}");
            return 1;
        }
    }

    private static int RunUpdate(string[] args)
    {
        try
        {
            var options = ParseArguments(args,
                ("--mode", true),
                ("--candidate", true),
                ("--baseline", true),
                ("-o", false));

            var mode = options["--mode"];
            if (mode is not ("missing" or "changed" or "all"))
            {
                throw new ArgumentException($"Invalid update mode '{mode}'. Expected 'missing', 'changed', or 'all'.");
            }

            var candidatePath = options["--candidate"];
            var baselinePath = options["--baseline"];
            var outputPath = options.GetValueOrDefault("-o", baselinePath);

            var candidateBytes = ReadBounded(candidatePath);
            EvidenceIdentity.Verify(candidateBytes);

            bool baselineExists = File.Exists(baselinePath);
            bool shouldUpdate = mode switch
            {
                "all" => true,
                "missing" => !baselineExists,
                "changed" => !baselineExists || HasContentChanged(baselinePath, candidateBytes),
                _ => false
            };

            if (!shouldUpdate)
            {
                Console.WriteLine($"Baseline '{baselinePath}' is up to date (mode: {mode}). No changes made.");
                return 0;
            }

            WriteAtomically(outputPath, Encoding.UTF8.GetString(candidateBytes));
            Console.WriteLine($"Updated baseline '{outputPath}' from candidate '{candidatePath}' (mode: {mode}).");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUpdateUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during baseline update: {ex.Message}");
            return 1;
        }
    }

    private static bool HasContentChanged(string baselinePath, byte[] candidateBytes)
    {
        var baselineBytes = File.ReadAllBytes(baselinePath);
        return !candidateBytes.AsSpan().SequenceEqual(baselineBytes);
    }

    private static int HandleUnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown baseline subcommand '{subcommand}'.");
        PrintUsage();
        return 2;
    }

    private static Dictionary<string, string> ParseArguments(string[] args, params (string Name, bool Required)[] rules)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-'))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    result[arg] = args[++i];
                }
                else
                {
                    result[arg] = "true";
                }
            }
        }

        foreach (var (name, req) in rules)
        {
            if (req && !result.ContainsKey(name))
            {
                throw new ArgumentException($"Missing required argument '{name}'.");
            }
        }

        return result;
    }

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"File not found: {path}");
        if (info.Length > CanonicalJson.MaximumDocumentBytes) throw new InvalidOperationException($"File exceeds maximum document limit: {path}");
        return File.ReadAllBytes(path);
    }

    private static void WriteAtomically(string outputPath, string contents)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: diffra baseline <verify|resolve|update> [options]");
        Console.Error.WriteLine("  verify   --evidence <run1.json> --verification <run2.json> --head-commit <sha> --policy-id <id> --policy-version <v> --rules-digest <sha256> [-o <approval.json>]");
        Console.Error.WriteLine("  resolve  --repo <path> --head <sha> --base <ref> --evidence-id <id> --manifest-digest <sha256> [-o <baseline-ref.json>]");
        Console.Error.WriteLine("  update   --mode missing|changed|all --candidate <cand.json> --baseline <base.json> [-o <target.json>]");
    }

    private static void PrintVerifyUsage() =>
        Console.Error.WriteLine("Usage: diffra baseline verify --evidence <run1.json> --verification <run2.json> --head-commit <sha> --policy-id <id> --policy-version <v> --rules-digest <sha256> [-o <approval.json>]");

    private static void PrintResolveUsage() =>
        Console.Error.WriteLine("Usage: diffra baseline resolve --repo <path> --head <sha> --base <ref> --evidence-id <id> --manifest-digest <sha256> [-o <baseline-ref.json>]");

    private static void PrintUpdateUsage() =>
        Console.Error.WriteLine("Usage: diffra baseline update --mode missing|changed|all --candidate <cand.json> --baseline <base.json> [-o <target.json>]");
}
