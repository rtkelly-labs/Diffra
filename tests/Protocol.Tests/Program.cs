using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;
using Diffra.Protocol.Tests;

var tests = new (string Name, Action Run)[]
{
    ("canonicalizes object order and strings", CanonicalizesObjectsAndStrings),
    ("sorts object names by UTF-16 ordinal order", SortsUtf16Ordinal),
    ("preserves arrays and canonicalizes integers", CanonicalizesArraysAndIntegers),
    ("rejects duplicate object names", RejectsDuplicateNames),
    ("rejects fractional and exponent number tokens", RejectsNonIntegerNumbers),
    ("rejects unsafe integers", RejectsUnsafeIntegers),
    ("rejects invalid UTF-8 and byte-order marks", RejectsInvalidUtf8),
    ("rejects oversized and deeply nested inputs", RejectsBounds),
    ("hashes payload canonical bytes", HashesPayload),
    ("hashes evidence projection without its id", HashesEvidenceProjection),
    ("verifies evidence payload digest and identity", VerifiesEvidence),
    ("rejects evidence with a stale payload digest or id", RejectsStaleEvidence),
    ("rejects unsafe artifact reference paths", RejectsUnsafeArtifactPaths),
    ("rejects malformed artifact references", RejectsInvalidArtifactReferences),
    ("rejects unrecognized evidence fields", RejectsUnknownEvidenceFields),
};

foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
}

Console.WriteLine($"Passed {tests.Length} protocol identity checks.");
ComparisonTests.RunAll();
ReportingTests.RunAll();
CollectTests.RunAll();
DiffEvalTests.RunAll();
SampleFixtureTests.RunAll();

static void CanonicalizesObjectsAndStrings()
{
    var actual = CanonicalJson.Canonicalize(UTF8(" { \"b\": \"<\\n\", \"a\": \"é\" } "));
    Equal("{\"a\":\"é\",\"b\":\"<\\n\"}", Encoding.UTF8.GetString(actual));
}

static void SortsUtf16Ordinal()
{
    var actual = CanonicalJson.Canonicalize(UTF8("{\"\\uE000\":2,\"😀\":1}"));
    Equal("{\"😀\":1,\"\":2}", Encoding.UTF8.GetString(actual));
}

static void CanonicalizesArraysAndIntegers()
{
    var actual = CanonicalJson.Canonicalize(UTF8("[ -0, 9007199254740991, -9007199254740991, true, null ]"));
    Equal("[0,9007199254740991,-9007199254740991,true,null]", Encoding.UTF8.GetString(actual));
}

static void RejectsDuplicateNames() => Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("{\"x\":1,\"x\":2}")));

static void RejectsNonIntegerNumbers()
{
    Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("1.0")));
    Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("1e0")));
}

static void RejectsUnsafeIntegers()
{
    Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("9007199254740992")));
    Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("-9007199254740992")));
}

static void RejectsInvalidUtf8()
{
    Throws<JsonException>(() => CanonicalJson.Canonicalize([0x22, 0xC3, 0x28, 0x22]));
    Throws<JsonException>(() => CanonicalJson.Canonicalize([0xEF, 0xBB, 0xBF, 0x7B, 0x7D]));
    Throws<JsonException>(() => CanonicalJson.Canonicalize(UTF8("\"\\uD800\"")));
}

static void RejectsBounds()
{
    Throws<JsonException>(() => CanonicalJson.Canonicalize(new byte[CanonicalJson.MaximumDocumentBytes + 1]));
    var deep = UTF8(new string('[', CanonicalJson.MaximumDepth + 1) + "0" + new string(']', CanonicalJson.MaximumDepth + 1));
    Throws<JsonException>(() => CanonicalJson.Canonicalize(deep));
}

static void HashesPayload()
{
    Equal(
        "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
        EvidenceIdentity.ComputePayloadDigest(UTF8("{ }")));
    Equal(
        EvidenceIdentity.ComputePayloadDigest(UTF8("{\"a\":1,\"b\":2}")),
        EvidenceIdentity.ComputePayloadDigest(UTF8("{\"b\":2,\"a\":1}")));
}

static void HashesEvidenceProjection()
{
    var projection = UTF8("{\"schema\":\"https://diffra.dev/schemas/evidence-v1.schema.json\",\"payloadDigest\":\"sha256:x\",\"payload\":{\"value\":1},\"id\":\"ignored\"}");
    var sameProjection = UTF8("{\"id\":\"another\",\"payloadDigest\":\"sha256:x\",\"payload\":{\"value\":1},\"schema\":\"https://diffra.dev/schemas/evidence-v1.schema.json\"}");
    var canonicalWithoutId = UTF8("{\"payload\":{\"value\":1},\"payloadDigest\":\"sha256:x\",\"schema\":\"https://diffra.dev/schemas/evidence-v1.schema.json\"}");
    var framed = Encoding.ASCII.GetBytes("diffra/v1\0evidence\0").Concat(canonicalWithoutId).ToArray();
    var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(framed)).ToLowerInvariant();

    Equal(expected, EvidenceIdentity.ComputeId(projection));
    Equal(expected, EvidenceIdentity.ComputeId(sameProjection));
    Throws<JsonException>(() => EvidenceIdentity.ComputeId(UTF8("[]")));
}

static void VerifiesEvidence()
{
    var evidence = BuildEvidence("[{\"id\":\"source\",\"digest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",\"mediaType\":\"application/json\",\"sizeBytes\":2,\"path\":\"artifacts/source.json\"}]");
    EvidenceIdentity.Verify(UTF8(evidence));
}

static void RejectsStaleEvidence()
{
    var evidence = BuildEvidence("[]");
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(evidence.Replace("\"value\":1", "\"value\":2", StringComparison.Ordinal))));

    var idStart = evidence.IndexOf("\"id\":\"", StringComparison.Ordinal) + 6;
    var idEnd = evidence.IndexOf('"', idStart);
    var staleId = "sha256:" + new string('0', 64);
    var withStaleId = evidence[..idStart] + staleId + evidence[idEnd..];
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(withStaleId)));
}

static void RejectsUnsafeArtifactPaths()
{
    foreach (var path in new[] { "../outside.json", "/absolute.json", "folder\\source.json", "C:\\source.json" })
    {
        var refs = $"[{{\"id\":\"source\",\"digest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",\"mediaType\":\"application/json\",\"sizeBytes\":2,\"path\":\"{path.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}]";
        Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(BuildEvidence(refs))));
    }
}

static void RejectsUnknownEvidenceFields()
{
    var evidence = BuildEvidence("[]");
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(evidence[..^1] + ",\"verdict\":\"pass\"}")));
    var withUnknownProducerField = evidence.Replace(
        "\"version\":\"1.0.0\",\"configDigest\"",
        "\"version\":\"1.0.0\",\"verdict\":\"pass\",\"configDigest\"",
        StringComparison.Ordinal);
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(withUnknownProducerField)));
}

static void RejectsInvalidArtifactReferences()
{
    const string goodRef = "{\"id\":\"source\",\"digest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",\"mediaType\":\"application/json\",\"sizeBytes\":2}";
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(BuildEvidence($"[{goodRef},{goodRef}]"))));
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(BuildEvidence("[{\"id\":\"source\",\"digest\":\"bad\",\"mediaType\":\"application/json\",\"sizeBytes\":2}]"))));
    Throws<JsonException>(() => EvidenceIdentity.Verify(UTF8(BuildEvidence("[{\"id\":\"source\",\"digest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",\"mediaType\":\"application/json\",\"sizeBytes\":-1}]"))));
}

static string BuildEvidence(string artifactRefs)
{
    const string payload = "{\"value\":1}";
    var payloadDigest = EvidenceIdentity.ComputePayloadDigest(UTF8(payload));
    using var refsDocument = JsonDocument.Parse(artifactRefs);
    var referencedDigests = refsDocument.RootElement.EnumerateArray()
        .Select(item => item.GetProperty("digest").GetString()!)
        .Distinct(StringComparer.Ordinal);
    var artifactDigests = string.Join(',', referencedDigests.Select(digest => $"\"{digest}\""));
    var projection = $"{{\"schema\":\"https://diffra.dev/schemas/evidence-v1.schema.json\",\"typeId\":\"org.example.value\",\"typeVersion\":\"1.0.0\",\"typeResolution\":\"recognized\",\"status\":\"produced\",\"subject\":{{\"kind\":\"git-repository\",\"id\":\"repo:example/project\",\"commit\":\"abcdef\"}},\"producer\":{{\"id\":\"org.example.producer\",\"version\":\"1.0.0\",\"configDigest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"}},\"environment\":{{\"platform\":\"test\",\"runtime\":\"net10.0\",\"tools\":[]}},\"provenance\":{{\"source\":\"test\",\"artifactDigests\":[{artifactDigests}]}},\"payloadDigest\":\"{payloadDigest}\",\"payload\":{payload},\"artifactRefs\":{artifactRefs}}}";
    var id = EvidenceIdentity.ComputeId(UTF8(projection));
    return projection[..^1] + $",\"id\":\"{id}\"}}";
}

static byte[] UTF8(string value) => Encoding.UTF8.GetBytes(value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
}
