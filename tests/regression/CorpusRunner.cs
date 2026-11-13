using System.IO.Compression;
using System.Text.Json;

namespace Diffra.Regression;

public sealed class RunSummary
{
    public int TotalCases { get; set; }
    public int PassedCases { get; set; }
    public List<(string CaseId, string Reason)> DeclaredBreaks { get; } = new();
    public List<(string CaseId, string File, string? Pointer, string Message)> Failures { get; } = new();
    public bool BackwardReadsPassed { get; set; } = true;
}

public static class CorpusRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void RunCandidate(
        string corpusDir,
        string tool,
        RunSummary summary,
        bool quick = false,
        bool allowPath = false)
    {
        var manifestPath = Path.Combine(corpusDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<RegressionManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest at {manifestPath}");

        Console.WriteLine($"Running corpus {manifest.Version} ({corpusDir}) with candidate tool: {tool}");
        if (manifest.Provenance != null)
        {
            Console.WriteLine($"  [Corpus Provenance: {manifest.Provenance.Asset} ({manifest.Provenance.Tool} {manifest.Provenance.Version}) SHA256: {manifest.Provenance.Sha256}]");
        }
        if (quick)
        {
            Console.WriteLine("  [Quick mode active: skipping old-vs-new and slow directory materialization checks]");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "diffra-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (var testCase in manifest.Cases)
            {
                // In quick mode, skip slow presentation / bundle import directory generation
                if (quick && (testCase.Command is "present" || testCase.Id is "bundle-import"))
                {
                    Console.WriteLine($"SKIP {testCase.Id} (quick mode)");
                    continue;
                }

                summary.TotalCases++;
                var casePassed = true;
                var caseDeclaredBreak = false;

                // Prepare execution args
                var resolvedArgs = new List<string>();
                foreach (var token in testCase.Args)
                {
                    if (token.StartsWith("inputs/", StringComparison.Ordinal) ||
                        token.StartsWith("expected/", StringComparison.Ordinal))
                    {
                        resolvedArgs.Add(Path.Combine(corpusDir, token));
                    }
                    else if (token.StartsWith("output/", StringComparison.Ordinal))
                    {
                        var relOut = token["output/".Length..];
                        var fullOut = Path.Combine(tempRoot, relOut);
                        var dir = Path.GetDirectoryName(fullOut);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        resolvedArgs.Add(fullOut);
                    }
                    else
                    {
                        resolvedArgs.Add(token);
                    }
                }

                var (exitCode, stderr) = ToolExecutor.Run(tool, resolvedArgs, corpusDir, allowPath: allowPath);

                if (exitCode != testCase.ExpectedExitCode)
                {
                    var msg = $"Exit code mismatch: expected {testCase.ExpectedExitCode}, got {exitCode}. Stderr: {stderr.Trim()}";
                    Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                    summary.Failures.Add((testCase.Id, "exitCode", null, msg));
                    continue;
                }

                // If non-zero exit code was expected and matched, no output comparison required
                if (testCase.ExpectedExitCode != 0 && testCase.ExpectedOutputs.Count == 0)
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS {testCase.Id}");
                    continue;
                }

                // Compare expected outputs
                foreach (var exp in testCase.ExpectedOutputs)
                {
                    var expectedFile = Path.Combine(corpusDir, exp.Path);
                    var actualFile = Path.Combine(tempRoot, exp.OutputArg.StartsWith("output/", StringComparison.Ordinal)
                        ? exp.OutputArg["output/".Length..]
                        : exp.OutputArg);

                    if (exp.IsDirectory)
                    {
                        if (!Directory.Exists(actualFile))
                        {
                            var msg = $"Output directory was not created: {actualFile}";
                            Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                            casePassed = false;
                            continue;
                        }

                        if (!CompareDirectories(expectedFile, actualFile, summary, testCase.Id))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (exp.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(actualFile))
                        {
                            var msg = $"Zip output file was not created: {actualFile}";
                            Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                            casePassed = false;
                            continue;
                        }

                        if (!CompareZipFiles(expectedFile, actualFile, testCase.Id, summary))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (!File.Exists(actualFile))
                    {
                        var msg = $"Output file was not created: {actualFile}";
                        Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                        summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                        casePassed = false;
                        continue;
                    }

                    var actualBytes = File.ReadAllBytes(actualFile);
                    var expectedBytes = File.ReadAllBytes(expectedFile);

                    // Strictly assert Delta ID existence, canonical self-consistency, invariant environment, and determinism
                    if (testCase.Command == "diff" && exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryVerifyDeltaDocument(actualBytes, requireInvariantEnvironment: true, out var deltaErr))
                        {
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {deltaErr}");
                            summary.Failures.Add((testCase.Id, exp.Path, "/id", deltaErr!));
                            casePassed = false;
                            continue;
                        }

                        // Local single-version determinism test (repeat run produces byte-identical output)
                        var tempRepeat = Path.Combine(Path.GetTempPath(), "diffra-regression-det-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            Directory.CreateDirectory(tempRepeat);
                            var repeatArgs = ResolveArgs(testCase.Args, corpusDir, tempRepeat);
                            var (detCode, detErr) = ToolExecutor.Run(tool, repeatArgs, corpusDir, allowPath: allowPath);
                            if (detCode != testCase.ExpectedExitCode)
                            {
                                var msg = $"Determinism test failed: repeat run returned exit code {detCode}. Stderr: {detErr.Trim()}";
                                Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                                summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                                casePassed = false;
                                continue;
                            }

                            var repeatRel = exp.OutputArg.StartsWith("output/", StringComparison.Ordinal)
                                ? exp.OutputArg["output/".Length..]
                                : exp.OutputArg;
                            var repeatFile = Path.Combine(tempRepeat, repeatRel);

                            if (!File.Exists(repeatFile))
                            {
                                var msg = $"Determinism test failed: repeat run did not produce Delta output {repeatFile}.";
                                Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                                summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                                casePassed = false;
                                continue;
                            }

                            var repeatBytes = File.ReadAllBytes(repeatFile);
                            if (!repeatBytes.AsSpan().SequenceEqual(actualBytes))
                            {
                                var msg = "Delta ID / content determinism failure: Delta document differed between two consecutive invocations of candidate tool. Delta IDs must be byte-identical within a single tool version.";
                                Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                                summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                                casePassed = false;
                                continue;
                            }
                        }
                        finally
                        {
                            try { Directory.Delete(tempRepeat, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                    }

                    // Strictly assert Evidence ID existence and self-ID verification
                    if (testCase.Command == "collect" && exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryVerifyEvidenceDocument(actualBytes, out var evErr))
                        {
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {evErr}");
                            summary.Failures.Add((testCase.Id, exp.Path, "/id", evErr!));
                            casePassed = false;
                            continue;
                        }
                    }

                    // Byte-for-byte exact match
                    if (actualBytes.AsSpan().SequenceEqual(expectedBytes))
                    {
                        continue;
                    }

                    // Structured comparison using explicit allow-list
                    if (exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var actDoc = JsonDocument.Parse(actualBytes);
                            using var expDoc = JsonDocument.Parse(expectedBytes);

                            var documentKind = GetDocumentKind(testCase.Command, expDoc.RootElement, actDoc.RootElement);
                            var differences = JsonDiff.FindAllDifferences(expDoc.RootElement, actDoc.RootElement);

                            var disallowed = new List<string>();
                            var allowed = new List<(string Pointer, string Reason)>();

                            foreach (var diffPointer in differences)
                            {
                                if (DeclaredBreakAllowList.TryMatch(documentKind, diffPointer, expDoc.RootElement, actDoc.RootElement, out var reason))
                                {
                                    allowed.Add((diffPointer, reason));
                                }
                                else
                                {
                                    disallowed.Add(diffPointer);
                                }
                            }

                            if (disallowed.Count > 0)
                            {
                                foreach (var dis in disallowed)
                                {
                                    var msg = $"Disallowed difference at JSON pointer '{dis}'.";
                                    Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                                    summary.Failures.Add((testCase.Id, exp.Path, dis, msg));
                                }
                                casePassed = false;
                                continue;
                            }

                            if (allowed.Count > 0)
                            {
                                var distinctReasons = allowed.Select(a => a.Reason).Distinct().ToList();
                                foreach (var reason in distinctReasons)
                                {
                                    Console.WriteLine($"[DECLARED BREAK] {testCase.Id} ({exp.Path}): {reason}");
                                    summary.DeclaredBreaks.Add((testCase.Id, reason));
                                }
                                caseDeclaredBreak = true;
                                continue;
                            }
                        }
                        catch (JsonException ex)
                        {
                            var msg = $"JSON parse error during comparison: {ex.Message}";
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                            casePassed = false;
                            continue;
                        }
                    }

                    // Non-JSON file mismatch
                    var (diffLine, diffCol) = FindFirstDifferenceLine(expectedBytes, actualBytes);
                    var textMsg = $"Byte mismatch at line {diffLine}, column {diffCol}.";
                    Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {textMsg}");
                    summary.Failures.Add((testCase.Id, exp.Path, null, textMsg));
                    casePassed = false;
                }

                if (!casePassed)
                {
                    // Already logged as failure
                }
                else if (caseDeclaredBreak)
                {
                    // Declared break counted separately
                }
                else
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS {testCase.Id}");
                }
            }

            // Backward reads check
            RunBackwardReads(corpusDir, tool, summary, quick: quick, allowPath: allowPath);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    public static void RunComparison(
        string corpusDir,
        string oldTool,
        string newTool,
        RunSummary summary,
        bool allowPath = false)
    {
        var manifestPath = Path.Combine(corpusDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<RegressionManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest at {manifestPath}");

        Console.WriteLine($"Comparing old tool ({oldTool}) vs new tool ({newTool}) on corpus {manifest.Version}");
        if (manifest.Provenance != null)
        {
            Console.WriteLine($"  [Corpus Provenance: {manifest.Provenance.Asset} ({manifest.Provenance.Tool} {manifest.Provenance.Version}) SHA256: {manifest.Provenance.Sha256}]");
        }

        var tempOld = Path.Combine(Path.GetTempPath(), "diffra-cmp-old-" + Guid.NewGuid().ToString("N"));
        var tempNew = Path.Combine(Path.GetTempPath(), "diffra-cmp-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempOld);
        Directory.CreateDirectory(tempNew);

        try
        {
            foreach (var testCase in manifest.Cases)
            {
                summary.TotalCases++;
                var casePassed = true;
                var caseDeclaredBreak = false;

                var oldArgs = ResolveArgs(testCase.Args, corpusDir, tempOld);
                var newArgs = ResolveArgs(testCase.Args, corpusDir, tempNew);

                var (oldCode, oldErr) = ToolExecutor.Run(oldTool, oldArgs, corpusDir, allowPath: allowPath);
                var (newCode, newErr) = ToolExecutor.Run(newTool, newArgs, corpusDir, allowPath: allowPath);

                if (oldCode != newCode)
                {
                    var msg = $"Exit code discrepancy: old returned {oldCode}, new returned {newCode}. Old stderr: {oldErr.Trim()} | New stderr: {newErr.Trim()}";
                    Console.WriteLine($"FAIL [CMP] {testCase.Id}: {msg}");
                    summary.Failures.Add((testCase.Id, "exitCode", null, msg));
                    continue;
                }

                if (testCase.ExpectedExitCode != 0 && testCase.ExpectedOutputs.Count == 0)
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS [CMP] {testCase.Id}");
                    continue;
                }

                foreach (var exp in testCase.ExpectedOutputs)
                {
                    var relOut = exp.OutputArg.StartsWith("output/", StringComparison.Ordinal)
                        ? exp.OutputArg["output/".Length..]
                        : exp.OutputArg;
                    var oldOut = Path.Combine(tempOld, relOut);
                    var newOut = Path.Combine(tempNew, relOut);

                    if (exp.IsDirectory)
                    {
                        if (!Directory.Exists(oldOut) || !Directory.Exists(newOut))
                        {
                            var msg = $"Output directory missing for old ({Directory.Exists(oldOut)}) or new ({Directory.Exists(newOut)}) at {relOut}";
                            Console.WriteLine($"FAIL [CMP] {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, relOut, null, msg));
                            casePassed = false;
                            continue;
                        }

                        if (!CompareDirectories(oldOut, newOut, summary, testCase.Id))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (exp.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(oldOut) || !File.Exists(newOut))
                        {
                            var msg = $"Zip output missing for old or new tool at {relOut}";
                            Console.WriteLine($"FAIL [CMP] {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, relOut, null, msg));
                            casePassed = false;
                            continue;
                        }

                        if (!CompareZipFiles(oldOut, newOut, testCase.Id, summary))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (!File.Exists(oldOut) || !File.Exists(newOut))
                    {
                        var msg = $"Output file missing for old or new tool at {relOut}";
                        Console.WriteLine($"FAIL [CMP] {testCase.Id}: {msg}");
                        summary.Failures.Add((testCase.Id, relOut, null, msg));
                        casePassed = false;
                        continue;
                    }

                    var oldBytes = File.ReadAllBytes(oldOut);
                    var newBytes = File.ReadAllBytes(newOut);

                    // Strictly assert Delta ID and exact environment on new tool
                    if (testCase.Command == "diff" && exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryVerifyDeltaDocument(newBytes, requireInvariantEnvironment: true, out var deltaErr))
                        {
                            Console.WriteLine($"FAIL [CMP] {testCase.Id} ({relOut}): {deltaErr}");
                            summary.Failures.Add((testCase.Id, relOut, "/id", deltaErr!));
                            casePassed = false;
                            continue;
                        }
                    }

                    // Strictly assert Evidence self-ID on new tool
                    if (testCase.Command == "collect" && exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryVerifyEvidenceDocument(newBytes, out var evErr))
                        {
                            Console.WriteLine($"FAIL [CMP] {testCase.Id} ({relOut}): {evErr}");
                            summary.Failures.Add((testCase.Id, relOut, "/id", evErr!));
                            casePassed = false;
                            continue;
                        }
                    }

                    if (oldBytes.AsSpan().SequenceEqual(newBytes))
                    {
                        continue;
                    }

                    if (exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        using var oldDoc = JsonDocument.Parse(oldBytes);
                        using var newDoc = JsonDocument.Parse(newBytes);

                        var documentKind = GetDocumentKind(testCase.Command, oldDoc.RootElement, newDoc.RootElement);
                        var differences = JsonDiff.FindAllDifferences(oldDoc.RootElement, newDoc.RootElement);

                        var disallowed = new List<string>();
                        var allowed = new List<(string Pointer, string Reason)>();

                        foreach (var diffPointer in differences)
                        {
                            if (DeclaredBreakAllowList.TryMatch(documentKind, diffPointer, oldDoc.RootElement, newDoc.RootElement, out var reason))
                            {
                                allowed.Add((diffPointer, reason));
                            }
                            else
                            {
                                disallowed.Add(diffPointer);
                            }
                        }

                        if (disallowed.Count > 0)
                        {
                            foreach (var dis in disallowed)
                            {
                                var msg = $"Disallowed difference between old and new at JSON pointer '{dis}'.";
                                Console.WriteLine($"FAIL [CMP] {testCase.Id} ({relOut}): {msg}");
                                summary.Failures.Add((testCase.Id, relOut, dis, msg));
                            }
                            casePassed = false;
                            continue;
                        }

                        if (allowed.Count > 0)
                        {
                            var distinctReasons = allowed.Select(a => a.Reason).Distinct().ToList();
                            foreach (var reason in distinctReasons)
                            {
                                Console.WriteLine($"[DECLARED BREAK] [CMP] {testCase.Id} ({relOut}): {reason}");
                                summary.DeclaredBreaks.Add((testCase.Id, reason));
                            }
                            caseDeclaredBreak = true;
                            continue;
                        }
                    }

                    var (diffLine, diffCol) = FindFirstDifferenceLine(oldBytes, newBytes);
                    var textMsg = $"Byte mismatch between old and new at line {diffLine}, column {diffCol}.";
                    Console.WriteLine($"FAIL [CMP] {testCase.Id} ({exp.Path}): {textMsg}");
                    summary.Failures.Add((testCase.Id, exp.Path, null, textMsg));
                    casePassed = false;
                }

                if (!casePassed)
                {
                    // Failed
                }
                else if (caseDeclaredBreak)
                {
                    // Declared break
                }
                else
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS [CMP] {testCase.Id}");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempOld, true);
                Directory.Delete(tempNew, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore cleanup failure
            }
        }
    }

    public static void RunBackwardReads(
        string corpusDir,
        string tool,
        RunSummary summary,
        bool quick = false,
        bool allowPath = false)
    {
        Console.WriteLine($"Verifying backward reads across corpus documents in {corpusDir} via {tool}...");
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-regression-backward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var baselineRef = Path.Combine(corpusDir, "inputs", "baseline-reference.json");
        var baselineEv = Path.Combine(corpusDir, "inputs", "evidence", "baseline.json");
        var validDelta = Path.Combine(corpusDir, "expected", "delta-regressed.json");
        var validAssessment = Path.Combine(corpusDir, "expected", "eval-warn.json");

        var initialFailures = summary.Failures.Count;

        try
        {
            var files = Directory.EnumerateFiles(corpusDir, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(corpusDir, file).Replace('\\', '/');

                if (rel.StartsWith("output/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (quick)
                    {
                        continue;
                    }

                    var storeDir = Path.Combine(tempDir, "store-" + Guid.NewGuid().ToString("N"));
                    var (code, err) = ToolExecutor.Run(tool, ["bundle", "import", "--bundle", file, "--store", storeDir], allowPath: allowPath);
                    if (code != 0)
                    {
                        var msg = $"Backward read bundle import failed on {rel}: {err.Trim()}";
                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                        summary.Failures.Add(("backward-reads", rel, null, msg));
                    }
                }
                else if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(file);
                        using var doc = JsonDocument.Parse(bytes);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                            !doc.RootElement.TryGetProperty("schema", out var schemaProp) ||
                            schemaProp.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var schema = schemaProp.GetString();
                        switch (schema)
                        {
                            case "https://diffra.dev/schemas/evidence-v1.schema.json":
                                {
                                    if (!TryVerifyEvidenceDocument(bytes, out var evErr))
                                    {
                                        Console.WriteLine($"FAIL backward-reads ({rel}): {evErr}");
                                        summary.Failures.Add(("backward-reads", rel, "/id", evErr!));
                                        break;
                                    }

                                    var evId = doc.RootElement.GetProperty("id").GetString()!;
                                    var commit = doc.RootElement.GetProperty("subject").GetProperty("commit").GetString()!;
                                    var tempRefPath = Path.Combine(tempDir, "ref-" + Guid.NewGuid().ToString("N") + ".json");
                                    var unsignedRefObj = new
                                    {
                                        schema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
                                        role = "merge-base",
                                        status = "trusted",
                                        evidenceId = evId,
                                        subjectCommit = commit,
                                        manifestDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
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
                                            decisionId = "demonstration"
                                        }
                                    };
                                    var unsignedRefBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedRefObj);
                                    var realRefId = CanonicalHashHelper.ComputeDocumentId("baseline-reference", unsignedRefBytes);
                                    var finalRefObj = new
                                    {
                                        schema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
                                        id = realRefId,
                                        role = "merge-base",
                                        status = "trusted",
                                        evidenceId = evId,
                                        subjectCommit = commit,
                                        manifestDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
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
                                            decisionId = "demonstration"
                                        }
                                    };
                                    File.WriteAllText(tempRefPath, JsonSerializer.Serialize(finalRefObj) + "\n");

                                    var outDelta = Path.Combine(tempDir, "diff-" + Guid.NewGuid().ToString("N") + ".json");
                                    var (code, err) = ToolExecutor.Run(tool, [
                                        "diff",
                                    "--baseline-ref", tempRefPath,
                                    "--baseline-evidence", file,
                                    "--evidence", file,
                                    "-o", outDelta
                                    ], allowPath: allowPath);

                                    if (code != 0)
                                    {
                                        var msg = $"Candidate failed to read Evidence document {rel}: {err.Trim()}";
                                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                                        summary.Failures.Add(("backward-reads", rel, null, msg));
                                    }
                                    break;
                                }

                            case "https://diffra.dev/schemas/delta-v1.schema.json":
                                {
                                    if (!TryVerifyDeltaDocument(bytes, requireInvariantEnvironment: false, out var deltaErr))
                                    {
                                        Console.WriteLine($"FAIL backward-reads ({rel}): {deltaErr}");
                                        summary.Failures.Add(("backward-reads", rel, "/id", deltaErr!));
                                        break;
                                    }

                                    var outReport = Path.Combine(tempDir, "report-" + Guid.NewGuid().ToString("N") + ".json");
                                    var (code, err) = ToolExecutor.Run(tool, [
                                        "report", file,
                                    "--format", "json",
                                    "-o", outReport
                                    ], allowPath: allowPath);

                                    if (code != 0)
                                    {
                                        var msg = $"Candidate failed to read Delta document {rel}: {err.Trim()}";
                                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                                        summary.Failures.Add(("backward-reads", rel, null, msg));
                                    }
                                    break;
                                }

                            case "https://diffra.dev/schemas/assessment-v1.schema.json":
                                {
                                    string? matchingDelta = null;
                                    if (doc.RootElement.TryGetProperty("deltaId", out var deltaIdProp) && deltaIdProp.ValueKind == JsonValueKind.String)
                                    {
                                        var deltaId = deltaIdProp.GetString();
                                        foreach (var candFile in files)
                                        {
                                            if (candFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                            {
                                                try
                                                {
                                                    using var candDoc = JsonDocument.Parse(File.ReadAllBytes(candFile));
                                                    if (candDoc.RootElement.TryGetProperty("schema", out var s) &&
                                                        s.GetString() == "https://diffra.dev/schemas/delta-v1.schema.json" &&
                                                        candDoc.RootElement.TryGetProperty("id", out var idProp) &&
                                                        idProp.GetString() == deltaId)
                                                    {
                                                        matchingDelta = candFile;
                                                        break;
                                                    }
                                                }
                                                catch (JsonException)
                                                {
                                                    // Ignore invalid candidate JSON
                                                }
                                            }
                                        }
                                    }

                                    if (matchingDelta != null)
                                    {
                                        var outReport = Path.Combine(tempDir, "report-" + Guid.NewGuid().ToString("N") + ".json");
                                        var (code, err) = ToolExecutor.Run(tool, [
                                            "report", matchingDelta,
                                        "--assessment", file,
                                        "--format", "json",
                                        "-o", outReport
                                        ], allowPath: allowPath);

                                        if (code != 0)
                                        {
                                            var msg = $"Candidate failed to read Assessment document {rel}: {err.Trim()}";
                                            Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                                            summary.Failures.Add(("backward-reads", rel, null, msg));
                                        }
                                    }
                                    break;
                                }

                            case "https://diffra.dev/schemas/baseline-reference-v1.schema.json":
                                {
                                    if (File.Exists(baselineEv))
                                    {
                                        var outDelta = Path.Combine(tempDir, "diff-" + Guid.NewGuid().ToString("N") + ".json");
                                        var (code, err) = ToolExecutor.Run(tool, [
                                            "diff",
                                        "--baseline-ref", file,
                                        "--baseline-evidence", baselineEv,
                                        "--evidence", baselineEv,
                                        "-o", outDelta
                                        ], allowPath: allowPath);

                                        if (code != 0)
                                        {
                                            var msg = $"Candidate failed to read BaselineReference document {rel}: {err.Trim()}";
                                            Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                                            summary.Failures.Add(("backward-reads", rel, null, msg));
                                        }
                                    }
                                    break;
                                }

                            case "https://diffra.dev/schemas/manifest-v1.schema.json":
                                {
                                    if (File.Exists(validDelta) && File.Exists(validAssessment) && File.Exists(baselineRef) && !quick)
                                    {
                                        var outBundle = Path.Combine(tempDir, "bundle-" + Guid.NewGuid().ToString("N") + ".zip");
                                        var (code, err) = ToolExecutor.Run(tool, [
                                            "bundle", "export",
                                        "--manifest", file,
                                        "--delta", validDelta,
                                        "--assessment", validAssessment,
                                        "--baseline-ref", baselineRef,
                                        "-o", outBundle
                                        ], allowPath: allowPath);

                                        if (code != 0)
                                        {
                                            var msg = $"Candidate failed to read Manifest document {rel}: {err.Trim()}";
                                            Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                                            summary.Failures.Add(("backward-reads", rel, null, msg));
                                        }
                                    }
                                    break;
                                }
                        }
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Backward read validation exception on {rel}: {ex.Message}";
                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                        summary.Failures.Add(("backward-reads", rel, null, msg));
                    }
                }
            }

            if (summary.Failures.Count == initialFailures)
            {
                Console.WriteLine("PASS backward reads: all Evidence, Delta, Assessment, Manifest, Baseline Reference, and Bundles accepted.");
                summary.BackwardReadsPassed = true;
            }
            else
            {
                summary.BackwardReadsPassed = false;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Best effort
            }
        }
    }

    public static void RunSelfTests()
    {
        Console.WriteLine("Running regression runner self-tests...");

        // 1. Candidate that changes comparator.id AND corrupts changes must fail
        {
            var expectedJson = """
            {
              "schema": "https://diffra.dev/schemas/delta-v1.schema.json",
              "id": "sha256:dummy",
              "status": "changed",
              "comparator": { "id": "diffra.structured-numeric", "version": "1.0.0" },
              "changes": [ { "path": "/complexity", "kind": "modified" } ],
              "provenance": { "environment": { "runtime": "10.0.14" } }
            }
            """;
            var corruptCandidateJson = """
            {
              "schema": "https://diffra.dev/schemas/delta-v1.schema.json",
              "id": "sha256:dummy",
              "status": "changed",
              "comparator": { "id": "diffra.corrupt-comparator", "version": "1.0.0" },
              "changes": [ { "path": "/corrupted", "kind": "corrupted" } ],
              "provenance": { "environment": { "runtime": "dotnet" } }
            }
            """;

            using var expDoc = JsonDocument.Parse(expectedJson);
            using var actDoc = JsonDocument.Parse(corruptCandidateJson);

            var diffs = JsonDiff.FindAllDifferences(expDoc.RootElement, actDoc.RootElement);
            var disallowed = new List<string>();
            foreach (var d in diffs)
            {
                if (!DeclaredBreakAllowList.TryMatch("delta", d, expDoc.RootElement, actDoc.RootElement, out _))
                {
                    disallowed.Add(d);
                }
            }

            if (!disallowed.Contains("/comparator/id") || !disallowed.Any(p => p.StartsWith("/changes", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Self-test failed: Candidate with changed comparator.id AND corrupt changes must be rejected by allow-list.");
            }
        }

        // 2. Candidate Delta with extra keys in provenance.environment must fail
        {
            var extraEnvJson = """
            {
              "environment": { "runtime": "dotnet", "platform": "linux" }
            }
            """;
            using var doc = JsonDocument.Parse(extraEnvJson);
            if (DeclaredBreakAllowList.IsExactInvariantDeltaEnvironment(doc.RootElement.GetProperty("environment")))
            {
                throw new InvalidOperationException("Self-test failed: Delta environment with extra key 'platform' must not be valid.");
            }
        }

        // 3. Candidate Delta missing id or self-inconsistent ID must fail
        {
            var missingIdJson = """
            {
              "schema": "https://diffra.dev/schemas/delta-v1.schema.json",
              "status": "changed",
              "provenance": { "environment": { "runtime": "dotnet" } }
            }
            """u8.ToArray();

            if (TryVerifyDeltaDocument(missingIdJson, requireInvariantEnvironment: true, out var missingErr))
            {
                throw new InvalidOperationException("Self-test failed: Delta document missing 'id' was incorrectly accepted.");
            }
            if (string.IsNullOrEmpty(missingErr) || !missingErr.Contains("missing a valid string 'id'"))
            {
                throw new InvalidOperationException($"Self-test failed: Expected missing id error, got '{missingErr}'.");
            }

            var wrongIdJson = """
            {
              "schema": "https://diffra.dev/schemas/delta-v1.schema.json",
              "id": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
              "status": "changed",
              "provenance": { "environment": { "runtime": "dotnet" } }
            }
            """u8.ToArray();

            if (TryVerifyDeltaDocument(wrongIdJson, requireInvariantEnvironment: true, out var wrongIdErr))
            {
                throw new InvalidOperationException("Self-test failed: Delta document with wrong id was incorrectly accepted.");
            }
            if (string.IsNullOrEmpty(wrongIdErr) || !wrongIdErr.Contains("Delta ID self-inconsistent"))
            {
                throw new InvalidOperationException($"Self-test failed: Expected self-inconsistent id error, got '{wrongIdErr}'.");
            }
        }

        // 4. Evidence ID drift must fail
        {
            var driftedEvJson = """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "id": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
              "status": "produced",
              "typeId": "org.diffra.test",
              "typeVersion": "1.0",
              "typeResolution": "recognized",
              "subject": { "kind": "git-repository", "id": "test", "commit": "1111111111111111111111111111111111111111" },
              "producer": { "id": "test", "version": "1.0", "configDigest": "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a" },
              "environment": { "platform": "test", "runtime": "10.0", "tools": [] },
              "provenance": { "source": "git", "artifactDigests": [] },
              "payloadDigest": "sha256:d8c05165eee4d2d71332e0014fd0a04a41c9b0eba88db0c081d63b340b347c0e",
              "payload": { "test": 1 },
              "artifactRefs": []
            }
            """u8.ToArray();

            if (TryVerifyEvidenceDocument(driftedEvJson, out var driftErr))
            {
                throw new InvalidOperationException("Self-test failed: Drifted Evidence ID was incorrectly accepted.");
            }
            if (string.IsNullOrEmpty(driftErr) || !driftErr.Contains("Evidence ID drift detected"))
            {
                throw new InvalidOperationException($"Self-test failed: Expected drift error, got '{driftErr}'.");
            }
        }

        // 5. Evidence tool entries: only the dotnet-sdk version may vary; an added key, a changed
        //    tool id, or a changed tools array length must fail.
        {
            const string expectedEvidenceJson = """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "environment": { "platform": "a", "runtime": "b", "tools": [ { "id": "dotnet-sdk", "version": "10.0.100" } ] }
            }
            """;

            var versionOnly = DisallowedEvidenceDifferences(expectedEvidenceJson, """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "environment": { "platform": "a", "runtime": "b", "tools": [ { "id": "dotnet-sdk", "version": "10.0.200" } ] }
            }
            """);
            if (versionOnly.Count != 0)
            {
                throw new InvalidOperationException($"Self-test failed: An Evidence dotnet-sdk version change must be allow-listed, but '{string.Join(", ", versionOnly)}' was rejected.");
            }

            var addedKey = DisallowedEvidenceDifferences(expectedEvidenceJson, """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "environment": { "platform": "a", "runtime": "b", "tools": [ { "id": "dotnet-sdk", "version": "10.0.200", "path": "/x" } ] }
            }
            """);
            if (!addedKey.Contains("/environment/tools/0/path") || !addedKey.Contains("/environment/tools/0/version"))
            {
                throw new InvalidOperationException("Self-test failed: An added key inside an Evidence tool entry must be rejected, together with the version change in that entry.");
            }

            var changedToolId = DisallowedEvidenceDifferences(expectedEvidenceJson, """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "environment": { "platform": "a", "runtime": "b", "tools": [ { "id": "other-sdk", "version": "10.0.200" } ] }
            }
            """);
            if (!changedToolId.Contains("/environment/tools/0/id") || !changedToolId.Contains("/environment/tools/0/version"))
            {
                throw new InvalidOperationException("Self-test failed: A changed Evidence tool id must be rejected.");
            }

            var addedTool = DisallowedEvidenceDifferences(expectedEvidenceJson, """
            {
              "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
              "environment": { "platform": "a", "runtime": "b", "tools": [ { "id": "dotnet-sdk", "version": "10.0.200" }, { "id": "extra", "version": "1" } ] }
            }
            """);
            if (!addedTool.Contains("/environment/tools/[length:1!=2]") || !addedTool.Contains("/environment/tools/0/version"))
            {
                throw new InvalidOperationException("Self-test failed: A changed Evidence tools array length must be rejected.");
            }
        }

        Console.WriteLine("PASS regression runner self-tests (all 5 security and allow-list assertions verified).");
    }

    private static List<string> DisallowedEvidenceDifferences(string expectedJson, string actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        return JsonDiff.FindAllDifferences(expected.RootElement, actual.RootElement)
            .Where(d => !DeclaredBreakAllowList.TryMatch("evidence", d, expected.RootElement, actual.RootElement, out _))
            .ToList();
    }

    private static bool TryVerifyDeltaDocument(byte[] bytes, bool requireInvariantEnvironment, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("id", out var idProp) ||
                idProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idProp.GetString()))
            {
                error = "Candidate Delta is missing a valid string 'id' property.";
                return false;
            }

            var actualId = idProp.GetString()!;
            var expectedId = CanonicalHashHelper.ComputeDocumentId("delta", bytes);
            if (!string.Equals(actualId, expectedId, StringComparison.Ordinal))
            {
                error = $"Delta ID self-inconsistent: expected {expectedId}, got {actualId}.";
                return false;
            }

            if (requireInvariantEnvironment)
            {
                if (!doc.RootElement.TryGetProperty("provenance", out var prov) ||
                    !prov.TryGetProperty("environment", out var env) ||
                    !DeclaredBreakAllowList.IsExactInvariantDeltaEnvironment(env))
                {
                    error = "Candidate Delta environment must equal {\"runtime\":\"dotnet\"} exactly with no extra or changed keys.";
                    return false;
                }
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Delta document is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryVerifyEvidenceDocument(byte[] bytes, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("id", out var idProp) ||
                idProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idProp.GetString()))
            {
                error = "Evidence document is missing a valid string 'id' property.";
                return false;
            }

            var actualId = idProp.GetString()!;
            var expectedId = CanonicalHashHelper.ComputeEvidenceId(bytes);
            if (!string.Equals(actualId, expectedId, StringComparison.Ordinal))
            {
                error = $"Evidence ID drift detected: recomputed ID '{expectedId}' != stored ID '{actualId}'.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Evidence document is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string GetDocumentKind(string command, JsonElement element1, JsonElement element2)
    {
        string? schema = null;
        if (element1.TryGetProperty("schema", out var s1) && s1.ValueKind == JsonValueKind.String)
        {
            schema = s1.GetString();
        }
        else if (element2.TryGetProperty("schema", out var s2) && s2.ValueKind == JsonValueKind.String)
        {
            schema = s2.GetString();
        }

        return schema switch
        {
            "https://diffra.dev/schemas/delta-v1.schema.json" => "delta",
            "https://diffra.dev/schemas/evidence-v1.schema.json" => "evidence",
            "https://diffra.dev/schemas/assessment-v1.schema.json" => "assessment",
            "https://diffra.dev/schemas/manifest-v1.schema.json" => "manifest",
            "https://diffra.dev/schemas/baseline-reference-v1.schema.json" => "baseline-reference",
            _ => command switch
            {
                "diff" => "delta",
                "collect" => "evidence",
                "eval" => "assessment",
                _ => "unknown"
            }
        };
    }

    private static bool CompareDirectories(string expectedDir, string actualDir, RunSummary summary, string caseId)
    {
        if (!Directory.Exists(expectedDir))
        {
            var msg = $"Expected directory does not exist: {expectedDir}";
            Console.WriteLine($"FAIL {caseId}: {msg}");
            summary.Failures.Add((caseId, expectedDir, null, msg));
            return false;
        }

        // Case-sensitive ordinal inventory comparison
        var expFiles = Directory.EnumerateFiles(expectedDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expectedDir, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var actFiles = Directory.EnumerateFiles(actualDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actualDir, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missingInAct = expFiles.Except(actFiles, StringComparer.Ordinal).ToList();
        var extraInAct = actFiles.Except(expFiles, StringComparer.Ordinal).ToList();

        if (missingInAct.Count > 0 || extraInAct.Count > 0)
        {
            var msg = $"Directory inventory mismatch in {caseId}. Missing: [{string.Join(", ", missingInAct)}], Extra: [{string.Join(", ", extraInAct)}]";
            Console.WriteLine($"FAIL {caseId}: {msg}");
            summary.Failures.Add((caseId, expectedDir, null, msg));
            return false;
        }

        foreach (var relFile in expFiles)
        {
            var expPath = Path.Combine(expectedDir, relFile);
            var actPath = Path.Combine(actualDir, relFile);

            var expBytes = File.ReadAllBytes(expPath);
            var actBytes = File.ReadAllBytes(actPath);

            if (!expBytes.AsSpan().SequenceEqual(actBytes))
            {
                if (relFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var expDoc = JsonDocument.Parse(expBytes);
                    using var actDoc = JsonDocument.Parse(actBytes);
                    var pointer = JsonDiff.FindFirstDifference(expDoc.RootElement, actDoc.RootElement);
                    var msg = $"File content mismatch at '{relFile}', pointer '{pointer}'.";
                    Console.WriteLine($"FAIL {caseId}: {msg}");
                    summary.Failures.Add((caseId, relFile, pointer, msg));
                }
                else
                {
                    var (diffLine, diffCol) = FindFirstDifferenceLine(expBytes, actBytes);
                    var msg = $"File content mismatch at '{relFile}' (line {diffLine}, col {diffCol}).";
                    Console.WriteLine($"FAIL {caseId}: {msg}");
                    summary.Failures.Add((caseId, relFile, null, msg));
                }
                return false;
            }
        }

        return true;
    }

    private static bool CompareZipFiles(string expectedZip, string actualZip, string caseId, RunSummary summary)
    {
        using var expArchive = ZipFile.OpenRead(expectedZip);
        using var actArchive = ZipFile.OpenRead(actualZip);

        // Case-sensitive ordinal dictionary of entries
        var expEntries = expArchive.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var actEntries = actArchive.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);

        if (expEntries.Count != actEntries.Count)
        {
            var msg = $"Zip entry count mismatch: expected {expEntries.Count}, got {actEntries.Count}.";
            Console.WriteLine($"FAIL {caseId}: {msg}");
            summary.Failures.Add((caseId, expectedZip, null, msg));
            return false;
        }

        foreach (var (name, expEntry) in expEntries)
        {
            if (!actEntries.TryGetValue(name, out var actEntry))
            {
                var msg = $"Missing zip entry: {name}";
                Console.WriteLine($"FAIL {caseId}: {msg}");
                summary.Failures.Add((caseId, expectedZip, null, msg));
                return false;
            }

            using var expStream = expEntry.Open();
            using var actStream = actEntry.Open();
            using var expMs = new MemoryStream();
            using var actMs = new MemoryStream();
            expStream.CopyTo(expMs);
            actStream.CopyTo(actMs);

            if (!expMs.ToArray().SequenceEqual(actMs.ToArray()))
            {
                var msg = $"Zip entry content mismatch for '{name}'.";
                Console.WriteLine($"FAIL {caseId}: {msg}");
                summary.Failures.Add((caseId, expectedZip, null, msg));
                return false;
            }
        }

        return true;
    }

    private static (int Line, int Column) FindFirstDifferenceLine(byte[] expected, byte[] actual)
    {
        var minLen = Math.Min(expected.Length, actual.Length);
        var line = 1;
        var col = 1;

        for (var i = 0; i < minLen; i++)
        {
            if (expected[i] != actual[i])
            {
                return (line, col);
            }

            if (expected[i] == (byte)'\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }

        return (line, col);
    }

    private static List<string> ResolveArgs(IReadOnlyList<string> args, string corpusDir, string tempOutDir)
    {
        var resolved = new List<string>();
        foreach (var token in args)
        {
            if (token.StartsWith("inputs/", StringComparison.Ordinal) ||
                token.StartsWith("expected/", StringComparison.Ordinal))
            {
                resolved.Add(Path.Combine(corpusDir, token));
            }
            else if (token.StartsWith("output/", StringComparison.Ordinal))
            {
                var relOut = token["output/".Length..];
                var fullOut = Path.Combine(tempOutDir, relOut);
                var dir = Path.GetDirectoryName(fullOut);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                resolved.Add(fullOut);
            }
            else
            {
                resolved.Add(token);
            }
        }
        return resolved;
    }
}
