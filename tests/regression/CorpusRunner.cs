using System.IO.Compression;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Regression;

public sealed class RunSummary
{
    public int TotalCases { get; set; }
    public int PassedCases { get; set; }
    public List<(string CaseId, string Reason)> DeclaredBreaks { get; } = [];
    public List<(string CaseId, string File, string? Pointer, string Message)> Failures { get; } = [];
    public bool BackwardReadsPassed { get; set; }
}

public static class CorpusRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void RunCandidate(string corpusDir, string candidateTool, RunSummary summary)
    {
        var manifestPath = Path.Combine(corpusDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<RegressionManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.json");

        Console.WriteLine($"Running corpus {manifest.Version} ({corpusDir}) with candidate tool: {candidateTool}");
        var tempRoot = Path.Combine(Path.GetTempPath(), "diffra-regression-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (var testCase in manifest.Cases)
            {
                summary.TotalCases++;
                var caseRunDir = Path.Combine(tempRoot, testCase.Id);
                Directory.CreateDirectory(caseRunDir);

                foreach (var inputFile in testCase.InputFiles)
                {
                    var fullPath = Path.Combine(corpusDir, inputFile);
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        throw new FileNotFoundException($"Declared input '{inputFile}' for case '{testCase.Id}' not found at '{fullPath}'.");
                    }
                }

                var resolvedArgs = new List<string>();
                foreach (var arg in testCase.Args)
                {
                    if (arg.StartsWith("inputs/", StringComparison.Ordinal) || arg.StartsWith("expected/", StringComparison.Ordinal))
                    {
                        resolvedArgs.Add(Path.GetFullPath(Path.Combine(corpusDir, arg)));
                    }
                    else if (arg.StartsWith("output/", StringComparison.Ordinal))
                    {
                        var outPath = Path.Combine(caseRunDir, arg);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        resolvedArgs.Add(outPath);
                    }
                    else
                    {
                        resolvedArgs.Add(arg);
                    }
                }

                var (exitCode, _, stderr) = ToolExecutor.Run(candidateTool, resolvedArgs, caseRunDir);
                if (exitCode != testCase.ExpectedExitCode)
                {
                    var msg = $"Exit code mismatch: expected {testCase.ExpectedExitCode}, got {exitCode}. Stderr: {stderr}";
                    Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                    summary.Failures.Add((testCase.Id, "process", null, msg));
                    continue;
                }

                var casePassed = true;
                foreach (var exp in testCase.ExpectedOutputs)
                {
                    var expectedFile = Path.Combine(corpusDir, exp.Path);
                    var actualFile = Path.Combine(caseRunDir, exp.OutputArg);

                    if (exp.IsDirectory)
                    {
                        if (!CompareDirectories(expectedFile, actualFile, testCase.Id, summary))
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

                    if (exp.NormalizeEnvironment)
                    {
                        try
                        {
                            EvidenceIdentity.Verify(actualBytes);
                        }
                        catch (Exception ex)
                        {
                            var msg = $"Candidate evidence failed identity verification: {ex.Message}";
                            Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                            casePassed = false;
                            continue;
                        }

                        var normActual = DocumentNormalizer.NormalizeCollectJson(actualBytes);
                        var normExpected = DocumentNormalizer.NormalizeCollectJson(expectedBytes);

                        if (!normActual.AsSpan().SequenceEqual(normExpected))
                        {
                            using var actDoc = JsonDocument.Parse(normActual);
                            using var expDoc = JsonDocument.Parse(normExpected);
                            var pointer = JsonDiff.FindFirstDifference(expDoc.RootElement, actDoc.RootElement);
                            var msg = $"Differing JSON at pointer '{pointer}' after environment normalization.";
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, pointer, msg));
                            casePassed = false;
                        }
                        continue;
                    }

                    // Delta, Assessment, Report outputs must match byte for byte
                    if (actualBytes.AsSpan().SequenceEqual(expectedBytes))
                    {
                        continue;
                    }

                    // Check for declared break if JSON
                    if (exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var actDoc = JsonDocument.Parse(actualBytes);
                            using var expDoc = JsonDocument.Parse(expectedBytes);

                            if (TryGetDeclaredBreak(expDoc.RootElement, actDoc.RootElement, out var breakReason))
                            {
                                Console.WriteLine($"[DECLARED BREAK] {testCase.Id} ({exp.Path}): {breakReason}");
                                summary.DeclaredBreaks.Add((testCase.Id, breakReason));
                                continue;
                            }

                            // If diff command output (Delta document), verify if difference is only runtime environment / id
                            if (testCase.Command == "diff" ||
                                (actDoc.RootElement.TryGetProperty("schema", out var actSchema) &&
                                 actSchema.GetString() == "https://diffra.dev/schemas/delta-v1.schema.json"))
                            {
                                var normAct = DocumentNormalizer.NormalizeDeltaJson(actualBytes);
                                var normExp = DocumentNormalizer.NormalizeDeltaJson(expectedBytes);
                                if (normAct.AsSpan().SequenceEqual(normExp))
                                {
                                    // Delta semantically matches once host .NET runtime version difference is normalized
                                    continue;
                                }

                                using var normActDoc = JsonDocument.Parse(normAct);
                                using var normExpDoc = JsonDocument.Parse(normExp);
                                var normPointer = JsonDiff.FindFirstDifference(normExpDoc.RootElement, normActDoc.RootElement);
                                var normMsg = $"Differing JSON at pointer '{normPointer}' after delta environment normalization.";
                                Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {normMsg}");
                                summary.Failures.Add((testCase.Id, exp.Path, normPointer, normMsg));
                                casePassed = false;
                                continue;
                            }

                            var pointer = JsonDiff.FindFirstDifference(expDoc.RootElement, actDoc.RootElement);
                            var msg = $"Differing JSON at pointer '{pointer}'.";
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, pointer, msg));
                            casePassed = false;
                            continue;
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

                if (casePassed)
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS {testCase.Id}");
                }
            }

            // Backward reads check
            RunBackwardReads(corpusDir, summary);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // ignore temp cleanup error
            }
        }
    }

    public static void RunComparison(string corpusDir, string oldTool, string newTool, RunSummary summary)
    {
        var manifestPath = Path.Combine(corpusDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<RegressionManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.json");

        Console.WriteLine($"Comparing old tool ({oldTool}) vs new tool ({newTool}) on corpus {manifest.Version}");
        var tempRoot = Path.Combine(Path.GetTempPath(), "diffra-compare-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (var testCase in manifest.Cases)
            {
                summary.TotalCases++;
                var oldCaseDir = Path.Combine(tempRoot, testCase.Id + "-old");
                var newCaseDir = Path.Combine(tempRoot, testCase.Id + "-new");
                Directory.CreateDirectory(oldCaseDir);
                Directory.CreateDirectory(newCaseDir);

                List<string> ResolveArgs(string runDir)
                {
                    var list = new List<string>();
                    foreach (var arg in testCase.Args)
                    {
                        if (arg.StartsWith("inputs/", StringComparison.Ordinal) || arg.StartsWith("expected/", StringComparison.Ordinal))
                        {
                            list.Add(Path.GetFullPath(Path.Combine(corpusDir, arg)));
                        }
                        else if (arg.StartsWith("output/", StringComparison.Ordinal))
                        {
                            var outPath = Path.Combine(runDir, arg);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            list.Add(outPath);
                        }
                        else
                        {
                            list.Add(arg);
                        }
                    }
                    return list;
                }

                var (oldCode, _, _) = ToolExecutor.Run(oldTool, ResolveArgs(oldCaseDir), oldCaseDir);
                var (newCode, _, newErr) = ToolExecutor.Run(newTool, ResolveArgs(newCaseDir), newCaseDir);

                if (oldCode != newCode)
                {
                    var msg = $"Exit code difference between old ({oldCode}) and new ({newCode}). Stderr new: {newErr}";
                    Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                    summary.Failures.Add((testCase.Id, "process", null, msg));
                    continue;
                }

                var casePassed = true;
                foreach (var exp in testCase.ExpectedOutputs)
                {
                    var oldFile = Path.Combine(oldCaseDir, exp.OutputArg);
                    var newFile = Path.Combine(newCaseDir, exp.OutputArg);

                    if (exp.IsDirectory)
                    {
                        if (!CompareDirectories(oldFile, newFile, testCase.Id, summary))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (exp.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(oldFile) || !File.Exists(newFile))
                        {
                            var msg = $"Zip output missing. Old exists: {File.Exists(oldFile)}, New exists: {File.Exists(newFile)}";
                            Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                            casePassed = false;
                            continue;
                        }

                        if (!CompareZipFiles(oldFile, newFile, testCase.Id, summary))
                        {
                            casePassed = false;
                        }
                        continue;
                    }

                    if (!File.Exists(oldFile) || !File.Exists(newFile))
                    {
                        var msg = $"Output missing. Old exists: {File.Exists(oldFile)}, New exists: {File.Exists(newFile)}";
                        Console.WriteLine($"FAIL {testCase.Id}: {msg}");
                        summary.Failures.Add((testCase.Id, exp.Path, null, msg));
                        casePassed = false;
                        continue;
                    }

                    var oldBytes = File.ReadAllBytes(oldFile);
                    var newBytes = File.ReadAllBytes(newFile);

                    if (exp.NormalizeEnvironment)
                    {
                        var normOld = DocumentNormalizer.NormalizeCollectJson(oldBytes);
                        var normNew = DocumentNormalizer.NormalizeCollectJson(newBytes);
                        if (!normOld.AsSpan().SequenceEqual(normNew))
                        {
                            using var expDoc = JsonDocument.Parse(normOld);
                            using var actDoc = JsonDocument.Parse(normNew);
                            var pointer = JsonDiff.FindFirstDifference(expDoc.RootElement, actDoc.RootElement);
                            var msg = $"Differing JSON at pointer '{pointer}' after environment normalization.";
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                            summary.Failures.Add((testCase.Id, exp.Path, pointer, msg));
                            casePassed = false;
                        }
                        continue;
                    }

                    if (oldBytes.AsSpan().SequenceEqual(newBytes))
                    {
                        continue;
                    }

                    if (exp.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        using var oldDoc = JsonDocument.Parse(oldBytes);
                        using var newDoc = JsonDocument.Parse(newBytes);

                        if (TryGetDeclaredBreak(oldDoc.RootElement, newDoc.RootElement, out var breakReason))
                        {
                            Console.WriteLine($"[DECLARED BREAK] {testCase.Id} ({exp.Path}): {breakReason}");
                            summary.DeclaredBreaks.Add((testCase.Id, breakReason));
                            continue;
                        }

                        // If diff command output (Delta document), verify if difference is only runtime environment / id
                        if (testCase.Command == "diff" ||
                            (newDoc.RootElement.TryGetProperty("schema", out var actSchema) &&
                             actSchema.GetString() == "https://diffra.dev/schemas/delta-v1.schema.json"))
                        {
                            var normOld = DocumentNormalizer.NormalizeDeltaJson(oldBytes);
                            var normNew = DocumentNormalizer.NormalizeDeltaJson(newBytes);
                            if (normOld.AsSpan().SequenceEqual(normNew))
                            {
                                continue;
                            }

                            using var normOldDoc = JsonDocument.Parse(normOld);
                            using var normNewDoc = JsonDocument.Parse(normNew);
                            var normPointer = JsonDiff.FindFirstDifference(normOldDoc.RootElement, normNewDoc.RootElement);
                            var normMsg = $"Differing JSON at pointer '{normPointer}' after delta environment normalization.";
                            Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {normMsg}");
                            summary.Failures.Add((testCase.Id, exp.Path, normPointer, normMsg));
                            casePassed = false;
                            continue;
                        }

                        var pointer = JsonDiff.FindFirstDifference(oldDoc.RootElement, newDoc.RootElement);
                        var msg = $"Differing JSON at pointer '{pointer}'.";
                        Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {msg}");
                        summary.Failures.Add((testCase.Id, exp.Path, pointer, msg));
                        casePassed = false;
                        continue;
                    }

                    var (diffLine, diffCol) = FindFirstDifferenceLine(oldBytes, newBytes);
                    var textMsg = $"Byte mismatch at line {diffLine}, column {diffCol}.";
                    Console.WriteLine($"FAIL {testCase.Id} ({exp.Path}): {textMsg}");
                    summary.Failures.Add((testCase.Id, exp.Path, null, textMsg));
                    casePassed = false;
                }

                if (casePassed)
                {
                    summary.PassedCases++;
                    Console.WriteLine($"PASS {testCase.Id}");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // ignore cleanup error
            }
        }
    }

    public static void RunBackwardReads(string corpusDir, RunSummary summary)
    {
        Console.WriteLine($"Verifying backward reads across corpus documents in {corpusDir}...");
        var tempCas = Path.Combine(Path.GetTempPath(), "diffra-cas-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempCas);

        try
        {
            var store = new ContentAddressedStore(tempCas);
            var files = Directory.EnumerateFiles(corpusDir, "*.*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(file);
                    try
                    {
                        using var doc = JsonDocument.Parse(bytes);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                            !doc.RootElement.TryGetProperty("schema", out var schemaProp))
                        {
                            continue;
                        }

                        var schema = schemaProp.GetString();
                        switch (schema)
                        {
                            case "https://diffra.dev/schemas/evidence-v1.schema.json":
                                EvidenceIdentity.Verify(bytes);
                                break;
                            case "https://diffra.dev/schemas/delta-v1.schema.json":
                                DocumentIdentity.Verify("delta", bytes);
                                break;
                            case "https://diffra.dev/schemas/assessment-v1.schema.json":
                                DocumentIdentity.Verify("assessment", bytes);
                                break;
                            case "https://diffra.dev/schemas/manifest-v1.schema.json":
                                DocumentIdentity.Verify("manifest", bytes);
                                break;
                            case "https://diffra.dev/schemas/baseline-reference-v1.schema.json":
                                DocumentIdentity.Verify("baseline-reference", bytes);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        var rel = Path.GetRelativePath(corpusDir, file);
                        var msg = $"Backward read validation failed on {rel}: {ex.Message}";
                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                        summary.Failures.Add(("backward-reads", rel, null, msg));
                    }
                }
                else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var result = EvidenceBundle.Import(file, store);
                        if (result.DocumentsCount == 0)
                        {
                            throw new InvalidOperationException("Bundle imported zero documents.");
                        }
                    }
                    catch (Exception ex)
                    {
                        var rel = Path.GetRelativePath(corpusDir, file);
                        var msg = $"Backward bundle import failed on {rel}: {ex.Message}";
                        Console.WriteLine($"FAIL backward-reads ({rel}): {msg}");
                        summary.Failures.Add(("backward-reads", rel, null, msg));
                    }
                }
            }

            if (summary.Failures.All(f => f.CaseId != "backward-reads"))
            {
                summary.BackwardReadsPassed = true;
                Console.WriteLine("PASS backward reads: all Evidence, Delta, Assessment and bundles accepted.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempCas, true);
            }
            catch
            {
                // ignore cleanup error
            }
        }
    }

    private static bool CompareDirectories(string expectedDir, string actualDir, string caseId, RunSummary summary)
    {
        if (!Directory.Exists(actualDir))
        {
            var msg = $"Expected directory not found: {actualDir}";
            Console.WriteLine($"FAIL {caseId}: {msg}");
            summary.Failures.Add((caseId, actualDir, null, msg));
            return false;
        }

        var passed = true;
        var expectedFiles = Directory.EnumerateFiles(expectedDir, "*.*", SearchOption.AllDirectories).ToList();
        foreach (var expFile in expectedFiles)
        {
            var relPath = Path.GetRelativePath(expectedDir, expFile);
            var actFile = Path.Combine(actualDir, relPath);

            if (!File.Exists(actFile))
            {
                var msg = $"Missing output file in directory: {relPath}";
                Console.WriteLine($"FAIL {caseId} ({relPath}): {msg}");
                summary.Failures.Add((caseId, relPath, null, msg));
                passed = false;
                continue;
            }

            var expBytes = File.ReadAllBytes(expFile);
            var actBytes = File.ReadAllBytes(actFile);
            if (!expBytes.AsSpan().SequenceEqual(actBytes))
            {
                var msg = $"File content mismatch in materialized directory: {relPath}";
                Console.WriteLine($"FAIL {caseId} ({relPath}): {msg}");
                summary.Failures.Add((caseId, relPath, null, msg));
                passed = false;
            }
        }

        return passed;
    }

    private static bool CompareZipFiles(string expectedZip, string actualZip, string caseId, RunSummary summary)
    {
        using var expArchive = ZipFile.OpenRead(expectedZip);
        using var actArchive = ZipFile.OpenRead(actualZip);

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

    private static bool TryGetDeclaredBreak(JsonElement expected, JsonElement actual, out string reason)
    {
        reason = "";

        if (expected.TryGetProperty("comparator", out var expComp) &&
            actual.TryGetProperty("comparator", out var actComp) &&
            expComp.TryGetProperty("version", out var expCompVer) &&
            actComp.TryGetProperty("version", out var actCompVer))
        {
            var expV = expCompVer.GetString();
            var actV = actCompVer.GetString();
            if (!string.Equals(expV, actV, StringComparison.Ordinal))
            {
                reason = $"Comparator version changed from '{expV}' to '{actV}'";
                return true;
            }
        }

        if (expected.TryGetProperty("provenance", out var expProv) &&
            actual.TryGetProperty("provenance", out var actProv) &&
            expProv.TryGetProperty("version", out var expProvVer) &&
            actProv.TryGetProperty("version", out var actProvVer))
        {
            var expV = expProvVer.GetString();
            var actV = actProvVer.GetString();
            if (!string.Equals(expV, actV, StringComparison.Ordinal))
            {
                reason = $"Evaluator version changed from '{expV}' to '{actV}'";
                return true;
            }
        }

        return false;
    }

    private static (int Line, int Column) FindFirstDifferenceLine(byte[] expected, byte[] actual)
    {
        var min = Math.Min(expected.Length, actual.Length);
        int line = 1, col = 1;
        for (var i = 0; i < min; i++)
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
}
