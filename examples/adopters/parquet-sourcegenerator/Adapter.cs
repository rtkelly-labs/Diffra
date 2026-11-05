using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Diffra.Protocol;

namespace Diffra.Examples.Adopters;

/// <summary>
/// Adapts Parquet.SourceGenerator derived golden outputs (*.g.cs, *.api.txt)
/// into canonical Diffra Evidence documents and a unified Manifest.
/// </summary>
public static class ParquetSourceGeneratorAdapter
{
    private static readonly Regex MethodSignatureRegex = new(@"^(?:static\s+)?(?:readonly\s+)?(?:this\s+)?([^\s\(\)]+)\s*(?:\((.*)\))?(?:\s*->\s*(.+))?$", RegexOptions.CultureInvariant);

    public sealed record AdapterOutput(
        byte[] ApiEvidenceBytes,
        string ApiEvidenceId,
        byte[] SourceEvidenceBytes,
        string SourceEvidenceId,
        byte[] ComplexityEvidenceBytes,
        string ComplexityEvidenceId,
        byte[] ManifestBytes,
        string ManifestId);

    public static AdapterOutput Adapt(
        string modelName,
        string apiTxtContent,
        string sourceCodeContent,
        string commit,
        string subjectId = "repo:rtkelly-labs/Parquet.SourceGenerator")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiTxtContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCodeContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        // 1. Build API Surface Evidence
        var apiMembers = ParseApiTxt(apiTxtContent);
        var apiPayload = new ApiSurfacePayload(
            Scope: $"model:{modelName}",
            Methodology: "roslyn-symbol-catalogue",
            Members: apiMembers);

        var canonicalApiPayload = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(apiPayload.ToJsonObject().ToJsonString()));
        var (apiId, _, apiEvidenceBytes) = EvidenceDocumentBuilder.Build(
            typeId: "diffra.api-surface.v1",
            typeVersion: "1.0",
            subjectId: subjectId,
            commit: commit,
            producerId: "diffra.adopter.parquet-sourcegenerator",
            producerVersion: "1.0",
            canonicalPayloadBytes: canonicalApiPayload,
            platform: Environment.OSVersion.Platform.ToString(),
            runtime: Environment.Version.ToString());

        // 2. Build Source Text Evidence
        var sourceBytes = Encoding.UTF8.GetBytes(sourceCodeContent);
        var sourceDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var lineCount = sourceCodeContent.Split('\n').Length;
        var charCount = sourceCodeContent.Length;

        var sourcePayload = new SourceTextPayload(
            Scope: $"artifacts/golden/{modelName}ParquetExtensions.g.cs",
            Language: "csharp",
            LineCount: lineCount,
            CharacterCount: charCount,
            ContentDigest: sourceDigest,
            RawSourcePath: $"artifacts/golden/{modelName}ParquetExtensions.g.cs");

        var canonicalSourcePayload = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(sourcePayload.ToJsonObject().ToJsonString()));
        var (sourceId, _, sourceEvidenceBytes) = EvidenceDocumentBuilder.Build(
            typeId: "diffra.source-text.v1",
            typeVersion: "1.0",
            subjectId: subjectId,
            commit: commit,
            producerId: "diffra.adopter.parquet-sourcegenerator",
            producerVersion: "1.0",
            canonicalPayloadBytes: canonicalSourcePayload,
            platform: Environment.OSVersion.Platform.ToString(),
            runtime: Environment.Version.ToString());

        // 3. Build Complexity Evidence
        var methods = EstimateMethodComplexities(modelName, sourceCodeContent);
        var complexityPayload = ComplexityPayload.Compute(
            scope: $"model:{modelName}",
            methodology: "roslyn-cyclomatic-complexity",
            methods: methods,
            outlierThreshold: 15);

        var canonicalComplexityPayload = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(complexityPayload.ToJsonObject().ToJsonString()));
        var (complexityId, _, complexityEvidenceBytes) = EvidenceDocumentBuilder.Build(
            typeId: "diffra.complexity.v1",
            typeVersion: "1.0",
            subjectId: subjectId,
            commit: commit,
            producerId: "diffra.adopter.parquet-sourcegenerator",
            producerVersion: "1.0",
            canonicalPayloadBytes: canonicalComplexityPayload,
            platform: Environment.OSVersion.Platform.ToString(),
            runtime: Environment.Version.ToString());

        // 4. Build Manifest Document
        var apiArtifactDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiTxtContent))).ToLowerInvariant();
        var manifestDoc = new JsonObject
        {
            ["schema"] = "https://diffra.dev/schemas/manifest-v1.schema.json",
            ["subject"] = new JsonObject
            {
                ["kind"] = "git-repository",
                ["id"] = subjectId,
                ["commit"] = commit,
                ["repository"] = "https://github.com/rtkelly-labs/Parquet.SourceGenerator",
            },
            ["groups"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "group:golden-code-generation",
                    ["name"] = $"{modelName} Golden Code Generation",
                    ["role"] = "generated-source",
                    ["required"] = true,
                    ["baselineRole"] = "merge-base",
                    ["retention"] = "long-term",
                    ["gate"] = "golden-codegen-regression",
                    ["artifactIds"] = new JsonArray { "artifact:source-code", "artifact:api-catalogue" },
                    ["evidenceIds"] = new JsonArray { sourceId, apiId, complexityId },
                    ["groups"] = new JsonArray(),
                }
            },
            ["artifacts"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "artifact:source-code",
                    ["digest"] = sourceDigest,
                    ["mediaType"] = "text/x-csharp",
                    ["sizeBytes"] = sourceBytes.Length,
                    ["path"] = $"artifacts/golden/{modelName}ParquetExtensions.g.cs",
                    ["producerId"] = "Parquet.SourceGenerator.Emitter",
                },
                new JsonObject
                {
                    ["id"] = "artifact:api-catalogue",
                    ["digest"] = apiArtifactDigest,
                    ["mediaType"] = "text/plain",
                    ["sizeBytes"] = Encoding.UTF8.GetByteCount(apiTxtContent),
                    ["path"] = $"artifacts/golden/{modelName}ParquetExtensions.api.txt",
                    ["producerId"] = "Parquet.SourceGenerator.ApiExporter",
                }
            },
            ["evidenceIds"] = new JsonArray { sourceId, apiId, complexityId },
            ["provenance"] = new JsonObject
            {
                ["producerId"] = "diffra.adopter.parquet-sourcegenerator",
                ["producerVersion"] = "1.0",
                ["environment"] = new JsonObject
                {
                    ["platform"] = Environment.OSVersion.Platform.ToString(),
                    ["runtime"] = Environment.Version.ToString(),
                },
                ["source"] = $"git:{commit}",
            }
        };

        var unsignedManifest = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(manifestDoc.ToJsonString()));
        var manifestId = "sha256:" + Convert.ToHexString(SHA256.HashData(unsignedManifest)).ToLowerInvariant();
        manifestDoc["id"] = manifestId;
        var canonicalSignedManifest = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(manifestDoc.ToJsonString()));

        return new AdapterOutput(
            ApiEvidenceBytes: apiEvidenceBytes,
            ApiEvidenceId: apiId,
            SourceEvidenceBytes: sourceEvidenceBytes,
            SourceEvidenceId: sourceId,
            ComplexityEvidenceBytes: complexityEvidenceBytes,
            ComplexityEvidenceId: complexityId,
            ManifestBytes: canonicalSignedManifest,
            ManifestId: manifestId);
    }

    public static IReadOnlyList<ApiSurfaceMember> ParseApiTxt(string content)
    {
        var members = new List<ApiSurfaceMember>();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            bool isStatic = line.StartsWith("static ", StringComparison.Ordinal);
            string processedLine = isStatic ? line[7..].Trim() : line;

            string kind = "method";
            if (!processedLine.Contains('(') && !processedLine.Contains("->"))
            {
                kind = "type";
            }
            else if (processedLine.Contains(".get ->") || processedLine.Contains(".set ->"))
            {
                kind = "property";
            }

            string? returnType = null;
            var arrowIdx = processedLine.IndexOf("->", StringComparison.Ordinal);
            string memberSig = processedLine;
            if (arrowIdx >= 0)
            {
                memberSig = processedLine[..arrowIdx].Trim();
                returnType = processedLine[(arrowIdx + 2)..].Trim();
            }

            string id = memberSig;
            var parenIdx = memberSig.IndexOf('(', StringComparison.Ordinal);
            List<string>? parameters = null;
            if (parenIdx >= 0)
            {
                var closeParen = memberSig.LastIndexOf(')');
                if (closeParen > parenIdx)
                {
                    var paramString = memberSig.Substring(parenIdx + 1, closeParen - parenIdx - 1);
                    parameters = paramString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }

            bool isAsync = returnType != null && (returnType.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) || returnType.StartsWith("System.Collections.Generic.IAsyncEnumerable", StringComparison.Ordinal));

            members.Add(new ApiSurfaceMember(
                Id: id,
                Kind: kind,
                Signature: line,
                ReturnType: returnType,
                Parameters: parameters,
                IsStatic: isStatic,
                IsAsync: isAsync));
        }

        return members;
    }

    public static IReadOnlyList<MethodComplexity> EstimateMethodComplexities(string modelName, string sourceCode)
    {
        var methods = new List<MethodComplexity>();
        var lines = sourceCode.Split('\n');
        string currentMethod = "ClassInitialization";
        int currentComplexity = 1;
        int currentLines = 0;
        var seenCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        void AddCurrent()
        {
            if (currentLines <= 0) return;
            var baseName = $"{modelName}.{currentMethod}";
            if (seenCounts.TryGetValue(baseName, out var count))
            {
                seenCounts[baseName] = count + 1;
                methods.Add(new MethodComplexity($"{baseName}#{count + 1}", currentComplexity, currentLines));
            }
            else
            {
                seenCounts[baseName] = 1;
                methods.Add(new MethodComplexity(baseName, currentComplexity, currentLines));
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("public static", StringComparison.Ordinal) ||
                trimmed.StartsWith("private static", StringComparison.Ordinal) ||
                trimmed.StartsWith("internal static", StringComparison.Ordinal))
            {
                var parenIdx = trimmed.IndexOf('(', StringComparison.Ordinal);
                if (parenIdx > 0)
                {
                    var beforeParen = trimmed[..parenIdx].Trim();
                    if (!beforeParen.Contains('='))
                    {
                        var parts = beforeParen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            var candidateName = parts[^1].Trim();
                            if (candidateName.Length > 0 && (char.IsLetter(candidateName[0]) || candidateName[0] == '_'))
                            {
                                AddCurrent();
                                currentMethod = candidateName;
                                currentComplexity = 1;
                                currentLines = 0;
                            }
                        }
                    }
                }
            }

            currentLines++;

            // Count branching constructs for cyclomatic complexity
            if (trimmed.StartsWith("if ", StringComparison.Ordinal) || trimmed.StartsWith("if(", StringComparison.Ordinal) ||
                trimmed.StartsWith("else if", StringComparison.Ordinal) ||
                trimmed.StartsWith("case ", StringComparison.Ordinal) ||
                trimmed.StartsWith("for ", StringComparison.Ordinal) || trimmed.StartsWith("for(", StringComparison.Ordinal) ||
                trimmed.StartsWith("foreach ", StringComparison.Ordinal) || trimmed.StartsWith("foreach(", StringComparison.Ordinal) ||
                trimmed.StartsWith("while ", StringComparison.Ordinal) || trimmed.StartsWith("while(", StringComparison.Ordinal) ||
                trimmed.StartsWith("catch ", StringComparison.Ordinal) || trimmed.StartsWith("catch(", StringComparison.Ordinal) ||
                trimmed.Contains("??") || trimmed.Contains("&&") || trimmed.Contains("||"))
            {
                currentComplexity++;
            }
        }

        AddCurrent();

        return methods;
    }
}
