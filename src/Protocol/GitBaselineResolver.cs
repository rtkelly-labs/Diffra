using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Diffra.Protocol;

public sealed record ResolvedBaseline(
    string ReferenceId,
    string EvidenceId,
    string ManifestDigest,
    string ResolvedCommit,
    string SourceRef,
    string Role,
    string Status,
    byte[] BaselineReferenceBytes);

/// <summary>Resolves Git merge-base baselines and enforces immutable commit pinning.</summary>
public static class GitBaselineResolver
{
    public const string BaselineReferenceSchema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json";
    private static readonly Regex CommitShaPattern = new("^[a-f0-9]{40}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves the Git merge-base between a target branch/ref and a candidate commit,
    /// rejecting mutable branch labels and constructing a verified BaselineReference.
    /// </summary>
    public static async Task<ResolvedBaseline> ResolveAsync(
        string repoRoot,
        string sourceRef,
        string headCommit,
        string evidenceId,
        string manifestDigest,
        string role = "merge-base",
        string status = "trusted",
        string sourceStore = "local",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDigest);

        if (!CommitShaPattern.IsMatch(headCommit))
        {
            throw new ArgumentException($"Head commit '{headCommit}' must be a full 40-character lowercase hexadecimal SHA.", nameof(headCommit));
        }

        // 1. Resolve merge-base commit via git subprocess
        var resolvedCommit = await RunGitMergeBaseAsync(repoRoot, sourceRef, headCommit, cancellationToken).ConfigureAwait(false);
        if (!CommitShaPattern.IsMatch(resolvedCommit))
        {
            throw new InvalidOperationException($"Git resolved merge-base '{resolvedCommit}' is not a valid 40-character commit SHA.");
        }

        // 2. Build verified BaselineReference document
        var resolverNode = new JsonObject
        {
            ["id"] = "diffra.git-merge-base-resolver",
            ["version"] = "1.0",
            ["sourceRef"] = sourceRef,
            ["resolvedCommit"] = resolvedCommit,
        };

        var trustNode = new JsonObject
        {
            ["issuer"] = "diffra.git-resolver",
            ["decisionId"] = $"merge-base:{resolvedCommit[..12]}",
        };

        var referenceObject = new JsonObject
        {
            ["schema"] = BaselineReferenceSchema,
            ["role"] = role,
            ["status"] = status,
            ["evidenceId"] = evidenceId,
            ["subjectCommit"] = resolvedCommit,
            ["manifestDigest"] = manifestDigest,
            ["resolver"] = resolverNode,
            ["sourceStore"] = sourceStore,
            ["trust"] = trustNode,
        };

        var canonicalBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(referenceObject.ToJsonString()));
        var unsignedBytes = CanonicalJson.CanonicalizeWithoutRootProperty(canonicalBytes, "id");

        var domainFramed = Encoding.ASCII.GetBytes("diffra/v1\0baseline-reference\0").Concat(unsignedBytes).ToArray();
        var refId = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(domainFramed)).ToLowerInvariant();

        referenceObject["id"] = refId;
        var finalJsonBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(referenceObject.ToJsonString()));

        return new ResolvedBaseline(
            ReferenceId: refId,
            EvidenceId: evidenceId,
            ManifestDigest: manifestDigest,
            ResolvedCommit: resolvedCommit,
            SourceRef: sourceRef,
            Role: role,
            Status: status,
            BaselineReferenceBytes: finalJsonBytes);
    }

    private static async Task<string> RunGitMergeBaseAsync(
        string repoRoot,
        string sourceRef,
        string headCommit,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("merge-base");
        startInfo.ArgumentList.Add(sourceRef);
        startInfo.ArgumentList.Add(headCommit);

        using var proc = new Process { StartInfo = startInfo };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to execute 'git merge-base': {ex.Message}", ex);
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git merge-base failed ({proc.ExitCode}): {stderr.Trim()}");
        }

        return stdout.Trim();
    }
}
