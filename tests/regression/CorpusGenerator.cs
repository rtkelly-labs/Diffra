using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Regression;

public static class CorpusGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Generate(string corpusRoot, string version, string toolExecutable, string? repoRoot = null)
    {
        if (toolExecutable.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            toolExecutable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Refusing to generate corpus with in-tree source or csproj. Must use a verified release package or archive executable.");
        }

        if (repoRoot != null && Path.GetFullPath(toolExecutable).StartsWith(Path.Combine(repoRoot, "src"), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Refusing to generate corpus with in-tree tool path. Must use an installed release binary.");
        }

        var corpusDir = Path.Combine(corpusRoot, version);
        var inputsDir = Path.Combine(corpusDir, "inputs");
        var payloadsDir = Path.Combine(inputsDir, "payloads");
        var rulesDir = Path.Combine(inputsDir, "rules");
        var evidenceDir = Path.Combine(inputsDir, "evidence");
        var deltasDir = Path.Combine(inputsDir, "deltas");
        var assessmentsDir = Path.Combine(inputsDir, "assessments");
        var expectedDir = Path.Combine(corpusDir, "expected");

        if (Directory.Exists(expectedDir))
        {
            Directory.Delete(expectedDir, true);
        }

        Directory.CreateDirectory(payloadsDir);
        Directory.CreateDirectory(rulesDir);
        Directory.CreateDirectory(evidenceDir);
        Directory.CreateDirectory(deltasDir);
        Directory.CreateDirectory(assessmentsDir);
        Directory.CreateDirectory(expectedDir);

        // 1. Payloads
        WritePayloads(payloadsDir);

        // 2. Rules
        WriteRules(rulesDir);

        // 3. Produce committed Evidence inputs using released tool, sanitized to remove machine host details
        ProduceEvidenceInputs(payloadsDir, evidenceDir, toolExecutable);

        // 4. Produce committed baseline-reference.json and manifest.json
        ProduceReferenceAndManifest(evidenceDir, inputsDir);

        // 5. Run cases and produce expected outputs using released tool
        var cases = RunAndRecordExpected(corpusDir, inputsDir, expectedDir, toolExecutable);

        // 6. Write manifest.json with release provenance
        var reportedVersion = ToolExecutor.InspectToolVersion(toolExecutable);
        if (reportedVersion != "unknown" &&
            !string.Equals(reportedVersion, version, StringComparison.OrdinalIgnoreCase) &&
            !reportedVersion.StartsWith(version + "+", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tool version mismatch: running '{toolExecutable}' reported version '{reportedVersion}', but requested corpus version is '{version}'.");
        }

        var assetName = $"Diffra.Cli.{version}.nupkg";
        string sha256;

        if (string.Equals(version, "0.0.1-alpha.1", StringComparison.OrdinalIgnoreCase))
        {
            sha256 = "b6212a455da046549e01ab595f169d60ad840d64cd54c6e62ac6910526d1ab04";
        }
        else
        {
            string? pkgPath = null;
            if (repoRoot != null)
            {
                var candidatePkg = Path.Combine(repoRoot, "artifacts", "package", assetName);
                if (File.Exists(candidatePkg))
                {
                    pkgPath = candidatePkg;
                }
                else
                {
                    var pinnedPkg = Path.Combine(repoRoot, "temp", "pinned-tool", assetName);
                    if (File.Exists(pinnedPkg))
                    {
                        pkgPath = pinnedPkg;
                    }
                }
            }

            if (pkgPath != null && File.Exists(pkgPath))
            {
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pkgPath)));
            }
            else if (File.Exists(toolExecutable))
            {
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(toolExecutable)));
                assetName = Path.GetFileName(toolExecutable);
            }
            else
            {
                throw new InvalidOperationException($"Unable to derive provenance SHA-256 for tool version '{version}'. Package or binary not found.");
            }
        }

        var provenance = new CorpusProvenance(
            Asset: assetName,
            Sha256: sha256,
            Tool: "diffra",
            Version: version);

        var manifest = new RegressionManifest(version, cases, provenance);
        var manifestPath = Path.Combine(corpusDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
        Console.WriteLine($"Corpus generated successfully for {version} at {corpusDir} with {cases.Count} cases.");
    }

    private static void WritePayloads(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "standard.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "5", "unit": "cyclomatic-complexity" },
            "methodCount": { "value": "2", "unit": "methods" },
            "coverage": { "value": "85.5", "unit": "percent" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "empty.json"), """
        {
          "scope": "regression/test",
          "methodology": "sample-metrics-v1",
          "metrics": {}
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "large-values.json"), """
        {
          "scope": "regression/test",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "largeInteger": 9007199254740991,
            "largeDecimal": { "value": "12345678901234567890.987654321", "unit": "score" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "unicode-keys.json"), """
        {
          "scope": "regression/test",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "méthode/coût": { "value": "15", "unit": "cycles" },
            "déjà_vu": 42
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "negative-zero.json"), """
        {
          "scope": "regression/test",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "negativeValue": { "value": "-100", "unit": "points" },
            "zeroFloat": { "value": "0.0", "unit": "ms" },
            "zeroValue": { "value": "0", "unit": "count" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "baseline.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" },
            "latency": { "value": "100", "unit": "ms" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-unchanged.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" },
            "latency": { "value": "100", "unit": "ms" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-improved.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "8", "unit": "cyclomatic-complexity" },
            "latency": { "value": "80", "unit": "ms" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-regressed.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "12", "unit": "cyclomatic-complexity" },
            "latency": { "value": "160", "unit": "ms" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-added.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" },
            "latency": { "value": "100", "unit": "ms" },
            "newMetric": { "value": "25", "unit": "count" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-removed.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" },
            "latency": { "value": "100", "unit": "ms" },
            "zeroBaseline": { "value": "0", "unit": "errors" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-incompatible.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v2-incompatible",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" }
          }
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "candidate-zero-eval.json"), """
        {
          "scope": "example/project",
          "methodology": "sample-metrics-v1",
          "metrics": {
            "complexity": { "value": "10", "unit": "cyclomatic-complexity" },
            "latency": { "value": "100", "unit": "ms" },
            "removedMetric": { "value": "5", "unit": "count" },
            "zeroBaseline": { "value": "5", "unit": "errors" }
          }
        }
        """ + "\n");
    }

    private static void WriteRules(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "pass.json"), """
        {
          "id": "org.example.budget",
          "version": "1.0.0",
          "warnAboveAbsoluteIncrease": "100",
          "failAboveAbsoluteIncrease": "200"
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "warn.json"), """
        {
          "id": "org.example.budget",
          "version": "1.0.0",
          "warnAboveAbsoluteIncrease": "50",
          "failAboveAbsoluteIncrease": "100"
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "fail.json"), """
        {
          "id": "org.example.budget",
          "version": "1.0.0",
          "warnAboveAbsoluteIncrease": "10",
          "failAboveAbsoluteIncrease": "30"
        }
        """ + "\n");

        File.WriteAllText(Path.Combine(dir, "relative-zero.json"), """
        {
          "id": "org.example.budget",
          "version": "1.0.0",
          "warnAboveRelativeIncrease": "0.10",
          "failAboveRelativeIncrease": "0.50"
        }
        """ + "\n");
    }

    private static void ProduceEvidenceInputs(string payloadsDir, string evidenceDir, string tool)
    {
        var evidenceMap = new (string Payload, string Output, string Commit, string TypeId)[]
        {
            ("baseline.json", "baseline.json", "1111111111111111111111111111111111111111", "org.example.metrics"),
            ("candidate-unchanged.json", "candidate-unchanged.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-improved.json", "candidate-improved.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-regressed.json", "candidate-regressed.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-added.json", "candidate-added.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-removed.json", "candidate-removed.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-incompatible.json", "candidate-incompatible.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
            ("candidate-zero-eval.json", "candidate-zero-eval.json", "2222222222222222222222222222222222222222", "org.example.metrics"),
        };

        foreach (var (payloadFile, outputFile, commit, typeId) in evidenceMap)
        {
            var payloadPath = Path.Combine(payloadsDir, payloadFile);
            var outputPath = Path.Combine(evidenceDir, outputFile);
            var args = new[]
            {
                "collect",
                "--payload", payloadPath,
                "--subject-id", "example/project",
                "--commit", commit,
                "--type-id", typeId,
                "--type-version", "1.0",
                "--producer-id", "org.diffra.regression",
                "--producer-version", "1.0",
                "-o", outputPath
            };

            var (code, err) = ToolExecutor.Run(tool, args);
            if (code != 0)
            {
                throw new InvalidOperationException($"Failed to collect evidence for {payloadFile}: {err}");
            }

            // Strip local host fingerprint from committed input evidence
            SanitizeEvidenceHostDetails(outputPath);
        }
    }

    private static void SanitizeEvidenceHostDetails(string evidencePath)
    {
        var jsonText = File.ReadAllText(evidencePath);
        if (JsonNode.Parse(jsonText) is JsonObject node)
        {
            var env = new JsonObject
            {
                ["platform"] = "generic",
                ["runtime"] = "10.0",
                ["tools"] = new JsonArray()
            };
            node["environment"] = env;
            node.Remove("id");

            var unsignedBytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            var id = CanonicalHashHelper.ComputeEvidenceId(unsignedBytes);

            var finalObj = new JsonObject
            {
                ["schema"] = node["schema"]?.DeepClone(),
                ["id"] = id
            };
            foreach (var kvp in node)
            {
                if (kvp.Key != "schema" && kvp.Key != "id")
                {
                    finalObj[kvp.Key] = kvp.Value?.DeepClone();
                }
            }

            File.WriteAllText(evidencePath, finalObj.ToJsonString(JsonOptions) + "\n");
        }
    }

    private static void ProduceReferenceAndManifest(string evidenceDir, string inputsDir)
    {
        var baselineEvidenceBytes = File.ReadAllBytes(Path.Combine(evidenceDir, "baseline.json"));
        using var baselineDoc = JsonDocument.Parse(baselineEvidenceBytes);
        var evidenceId = baselineDoc.RootElement.GetProperty("id").GetString()!;
        var commit = baselineDoc.RootElement.GetProperty("subject").GetProperty("commit").GetString()!;

        // 1. Baseline Reference
        var referenceObj = new
        {
            schema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
            role = "merge-base",
            status = "trusted",
            evidenceId,
            subjectCommit = commit,
            manifestDigest = "sha256:6d560cfbd938993f08de299e278b1610dbdb19404b32579dd125c931edda12fd",
            resolver = new
            {
                id = "org.diffra.regression-resolver",
                version = "1.0",
                resolvedCommit = commit,
                sourceRef = "local-fixture"
            },
            sourceStore = "local-fixture",
            trust = new
            {
                issuer = "fixture-only",
                decisionId = "demonstration",
                decisionDigest = "sha256:2b9cc4a9db471dbe900f09fbe469af30efdd8a740f938e325120903fb274339d"
            }
        };

        var referenceBytes = JsonSerializer.SerializeToUtf8Bytes(referenceObj);
        var referenceId = CanonicalHashHelper.ComputeDocumentId("baseline-reference", referenceBytes);

        var finalReferenceObj = new
        {
            schema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
            id = referenceId,
            role = "merge-base",
            status = "trusted",
            evidenceId,
            subjectCommit = commit,
            manifestDigest = "sha256:6d560cfbd938993f08de299e278b1610dbdb19404b32579dd125c931edda12fd",
            resolver = new
            {
                id = "org.diffra.regression-resolver",
                version = "1.0",
                resolvedCommit = commit,
                sourceRef = "local-fixture"
            },
            sourceStore = "local-fixture",
            trust = new
            {
                issuer = "fixture-only",
                decisionId = "demonstration",
                decisionDigest = "sha256:2b9cc4a9db471dbe900f09fbe469af30efdd8a740f938e325120903fb274339d"
            }
        };

        File.WriteAllText(
            Path.Combine(inputsDir, "baseline-reference.json"),
            JsonSerializer.Serialize(finalReferenceObj, JsonOptions) + "\n");

        // 2. Manifest for bundle export
        var manifestObj = new
        {
            schema = "https://diffra.dev/schemas/manifest-v1.schema.json",
            subject = new
            {
                kind = "git-repository",
                id = "example/project",
                commit
            },
            groups = new[]
            {
                new
                {
                    id = "quality",
                    name = "Quality",
                    role = "quality",
                    required = true,
                    baselineRole = "merge-base",
                    retention = "fixture",
                    gate = "required",
                    artifactIds = Array.Empty<string>(),
                    evidenceIds = new[] { evidenceId },
                    groups = Array.Empty<object>()
                }
            },
            artifacts = Array.Empty<object>(),
            evidenceIds = new[] { evidenceId },
            provenance = new
            {
                producerId = "org.diffra.regression",
                producerVersion = "1.0",
                environment = new Dictionary<string, string>
                {
                    ["platform"] = "Linux",
                    ["runtime"] = ".NET 10.0.12"
                },
                source = $"git:{commit}"
            }
        };

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifestObj);
        var manifestId = CanonicalHashHelper.ComputeDocumentId("manifest", manifestBytes);

        var finalManifestObj = new
        {
            schema = "https://diffra.dev/schemas/manifest-v1.schema.json",
            id = manifestId,
            subject = new
            {
                kind = "git-repository",
                id = "example/project",
                commit
            },
            groups = new[]
            {
                new
                {
                    id = "quality",
                    name = "Quality",
                    role = "quality",
                    required = true,
                    baselineRole = "merge-base",
                    retention = "fixture",
                    gate = "required",
                    artifactIds = Array.Empty<string>(),
                    evidenceIds = new[] { evidenceId },
                    groups = Array.Empty<object>()
                }
            },
            artifacts = Array.Empty<object>(),
            evidenceIds = new[] { evidenceId },
            provenance = new
            {
                producerId = "org.diffra.regression",
                producerVersion = "1.0",
                environment = new Dictionary<string, string>
                {
                    ["platform"] = "Linux",
                    ["runtime"] = ".NET 10.0.12"
                },
                source = $"git:{commit}"
            }
        };

        File.WriteAllText(
            Path.Combine(inputsDir, "manifest.json"),
            JsonSerializer.Serialize(finalManifestObj, JsonOptions) + "\n");
    }

    private static List<RegressionCase> RunAndRecordExpected(
        string corpusDir,
        string inputsDir,
        string expectedDir,
        string tool)
    {
        var cases = new List<RegressionCase>();

        void RunTool(string[] args, int expectedExitCode, string caseDesc)
        {
            var (exitCode, err) = ToolExecutor.Run(tool, args, corpusDir);
            if (exitCode != expectedExitCode)
            {
                throw new InvalidOperationException($"Case '{caseDesc}' failed during corpus generation: expected exit code {expectedExitCode}, got {exitCode}: {err}");
            }
        }

        // --- COLLECT CASES ---
        var collectCases = new (string Id, string PayloadFile, string OutputFile)[]
        {
            ("collect-standard", "payloads/standard.json", "evidence-standard.json"),
            ("collect-empty-metrics", "payloads/empty.json", "evidence-empty.json"),
            ("collect-large-values", "payloads/large-values.json", "evidence-large-values.json"),
            ("collect-unicode-keys", "payloads/unicode-keys.json", "evidence-unicode-keys.json"),
            ("collect-negative-zero", "payloads/negative-zero.json", "evidence-negative-zero.json")
        };

        foreach (var (id, payloadFile, outputFile) in collectCases)
        {
            var inputRel = $"inputs/{payloadFile}";
            var outputRel = $"expected/{outputFile}";
            var runOutputRel = $"output/{outputFile}";

            var expectedFullPath = Path.Combine(corpusDir, outputRel);
            var args = new[]
            {
                "collect",
                "--payload", Path.Combine(corpusDir, inputRel),
                "--subject-id", "regression/corpus",
                "--commit", "1111111111111111111111111111111111111111",
                "--type-id", "org.diffra.metrics",
                "--type-version", "1.0",
                "--producer-id", "org.diffra.test",
                "--producer-version", "1.0",
                "-o", expectedFullPath
            };

            RunTool(args, 0, id);

            cases.Add(new RegressionCase(
                id,
                "collect",
                [
                    "collect",
                    "--payload", inputRel,
                    "--subject-id", "regression/corpus",
                    "--commit", "1111111111111111111111111111111111111111",
                    "--type-id", "org.diffra.metrics",
                    "--type-version", "1.0",
                    "--producer-id", "org.diffra.test",
                    "--producer-version", "1.0",
                    "-o", runOutputRel
                ],
                [inputRel],
                [new ExpectedOutput(outputRel, runOutputRel, NormalizeEnvironment: true)],
                0));
        }

        // --- DIFF CASES ---
        var diffCases = new (string Id, string CandidateEvidence, string OutputFile)[]
        {
            ("diff-unchanged", "evidence/candidate-unchanged.json", "delta-unchanged.json"),
            ("diff-improved", "evidence/candidate-improved.json", "delta-improved.json"),
            ("diff-regressed", "evidence/candidate-regressed.json", "delta-regressed.json"),
            ("diff-added", "evidence/candidate-added.json", "delta-added.json"),
            ("diff-removed", "evidence/candidate-removed.json", "delta-removed.json"),
            ("diff-incompatible", "evidence/candidate-incompatible.json", "delta-incompatible.json"),
            ("diff-zero-eval", "evidence/candidate-zero-eval.json", "delta-zero-eval.json")
        };

        foreach (var (id, candFile, outputFile) in diffCases)
        {
            var baseRefRel = "inputs/baseline-reference.json";
            var baseEvRel = "inputs/evidence/baseline.json";
            var candRel = $"inputs/{candFile}";
            var outputRel = $"expected/{outputFile}";
            var runOutputRel = $"output/{outputFile}";

            var args = new[]
            {
                "diff",
                "--baseline-ref", Path.Combine(corpusDir, baseRefRel),
                "--baseline-evidence", Path.Combine(corpusDir, baseEvRel),
                "--evidence", Path.Combine(corpusDir, candRel),
                "-o", Path.Combine(corpusDir, outputRel)
            };

            RunTool(args, 0, id);

            cases.Add(new RegressionCase(
                id,
                "diff",
                [
                    "diff",
                    "--baseline-ref", baseRefRel,
                    "--baseline-evidence", baseEvRel,
                    "--evidence", candRel,
                    "-o", runOutputRel
                ],
                [baseRefRel, baseEvRel, candRel],
                [new ExpectedOutput(outputRel, runOutputRel)],
                0));
        }

        // Copy key deltas into inputs/deltas for eval and report inputs
        File.Copy(Path.Combine(expectedDir, "delta-improved.json"), Path.Combine(inputsDir, "deltas/delta-improved.json"), true);
        File.Copy(Path.Combine(expectedDir, "delta-regressed.json"), Path.Combine(inputsDir, "deltas/delta-regressed.json"), true);
        File.Copy(Path.Combine(expectedDir, "delta-incompatible.json"), Path.Combine(inputsDir, "deltas/delta-incompatible.json"), true);
        File.Copy(Path.Combine(expectedDir, "delta-zero-eval.json"), Path.Combine(inputsDir, "deltas/delta-zero-eval.json"), true);

        // --- EVAL CASES ---
        var evalCases = new (string Id, string DeltaFile, string RulesFile, string OutputFile, int ExitCode)[]
        {
            ("eval-pass", "deltas/delta-improved.json", "rules/pass.json", "eval-pass.json", 0),
            ("eval-warn", "deltas/delta-regressed.json", "rules/warn.json", "eval-warn.json", 0),
            ("eval-fail", "deltas/delta-regressed.json", "rules/fail.json", "eval-fail.json", 3),
            ("eval-needs-review", "deltas/delta-zero-eval.json", "rules/relative-zero.json", "eval-needs-review.json", 3),
            ("eval-inconclusive", "deltas/delta-incompatible.json", "rules/pass.json", "eval-inconclusive.json", 3)
        };

        foreach (var (id, deltaFile, rulesFile, outputFile, exitCode) in evalCases)
        {
            var deltaRel = $"inputs/{deltaFile}";
            var rulesRel = $"inputs/{rulesFile}";
            var outputRel = $"expected/{outputFile}";
            var runOutputRel = $"output/{outputFile}";

            var args = new[]
            {
                "eval",
                "--rules", Path.Combine(corpusDir, rulesRel),
                "--delta", Path.Combine(corpusDir, deltaRel),
                "-o", Path.Combine(corpusDir, outputRel)
            };

            RunTool(args, exitCode, id);

            cases.Add(new RegressionCase(
                id,
                "eval",
                [
                    "eval",
                    "--rules", rulesRel,
                    "--delta", deltaRel,
                    "-o", runOutputRel
                ],
                [rulesRel, deltaRel],
                [new ExpectedOutput(outputRel, runOutputRel)],
                exitCode));
        }

        // Copy assessment to inputs/assessments
        File.Copy(Path.Combine(expectedDir, "eval-warn.json"), Path.Combine(inputsDir, "assessments/assessment-warn.json"), true);

        // --- REPORT CASES ---
        var reportCases = new (string Id, string Format, string OutputFile)[]
        {
            ("report-html", "html", "report.html"),
            ("report-json", "json", "report.json"),
            ("report-markdown", "markdown", "report.md")
        };

        foreach (var (id, format, outputFile) in reportCases)
        {
            var deltaRel = "inputs/deltas/delta-regressed.json";
            var assessRel = "inputs/assessments/assessment-warn.json";
            var baseRefRel = "inputs/baseline-reference.json";
            var outputRel = $"expected/{outputFile}";
            var runOutputRel = $"output/{outputFile}";

            var args = new[]
            {
                "report",
                Path.Combine(corpusDir, deltaRel),
                "--assessment", Path.Combine(corpusDir, assessRel),
                "--baseline-ref", Path.Combine(corpusDir, baseRefRel),
                "--format", format,
                "-o", Path.Combine(corpusDir, outputRel)
            };

            RunTool(args, 0, id);

            cases.Add(new RegressionCase(
                id,
                "report",
                [
                    "report",
                    deltaRel,
                    "--assessment", assessRel,
                    "--baseline-ref", baseRefRel,
                    "--format", format,
                    "-o", runOutputRel
                ],
                [deltaRel, assessRel, baseRefRel],
                [new ExpectedOutput(outputRel, runOutputRel)],
                0));
        }

        // --- BUNDLE EXPORT CASE ---
        {
            var manifestRel = "inputs/manifest.json";
            var deltaRel = "inputs/deltas/delta-regressed.json";
            var assessRel = "inputs/assessments/assessment-warn.json";
            var baseRefRel = "inputs/baseline-reference.json";
            var outputRel = "expected/bundle.zip";
            var runOutputRel = "output/bundle.zip";

            var args = new[]
            {
                "bundle",
                "export",
                "--manifest", Path.Combine(corpusDir, manifestRel),
                "--delta", Path.Combine(corpusDir, deltaRel),
                "--assessment", Path.Combine(corpusDir, assessRel),
                "--baseline-ref", Path.Combine(corpusDir, baseRefRel),
                "-o", Path.Combine(corpusDir, outputRel)
            };

            RunTool(args, 0, "bundle-export");

            cases.Add(new RegressionCase(
                "bundle-export",
                "bundle",
                [
                    "bundle",
                    "export",
                    "--manifest", manifestRel,
                    "--delta", deltaRel,
                    "--assessment", assessRel,
                    "--baseline-ref", baseRefRel,
                    "-o", runOutputRel
                ],
                [manifestRel, deltaRel, assessRel, baseRefRel],
                [new ExpectedOutput(outputRel, runOutputRel)],
                0));
        }

        // --- BUNDLE IMPORT CASE ---
        {
            var bundleRel = "expected/bundle.zip";
            var storeRel = "expected/imported-cas";
            var runStoreRel = "output/imported-cas";
            Directory.CreateDirectory(Path.Combine(corpusDir, storeRel));

            var args = new[]
            {
                "bundle",
                "import",
                "--bundle", Path.Combine(corpusDir, bundleRel),
                "--store", Path.Combine(corpusDir, storeRel)
            };

            RunTool(args, 0, "bundle-import");

            cases.Add(new RegressionCase(
                "bundle-import",
                "bundle",
                [
                    "bundle",
                    "import",
                    "--bundle", bundleRel,
                    "--store", runStoreRel
                ],
                [bundleRel],
                [new ExpectedOutput(storeRel, runStoreRel, IsDirectory: true)],
                0));
        }

        // --- PRESENT BUNDLE CASE ---
        {
            var manifestRel = "inputs/manifest.json";
            var deltaRel = "inputs/deltas/delta-regressed.json";
            var assessRel = "inputs/assessments/assessment-warn.json";
            var baseRefRel = "inputs/baseline-reference.json";
            var outputRel = "expected/presentation";
            var runOutputRel = "output/presentation";

            var args = new[]
            {
                "present",
                "bundle",
                "--delta", Path.Combine(corpusDir, deltaRel),
                "--assessment", Path.Combine(corpusDir, assessRel),
                "--baseline-ref", Path.Combine(corpusDir, baseRefRel),
                "--manifest", Path.Combine(corpusDir, manifestRel),
                "-o", Path.Combine(corpusDir, outputRel)
            };

            RunTool(args, 0, "present-bundle");

            cases.Add(new RegressionCase(
                "present-bundle",
                "present",
                [
                    "present",
                    "bundle",
                    "--delta", deltaRel,
                    "--assessment", assessRel,
                    "--baseline-ref", baseRefRel,
                    "--manifest", manifestRel,
                    "-o", runOutputRel
                ],
                [deltaRel, assessRel, baseRefRel, manifestRel],
                [new ExpectedOutput(outputRel, runOutputRel, IsDirectory: true)],
                0));
        }

        return cases;
    }
}
