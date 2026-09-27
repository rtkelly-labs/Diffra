using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Computes stable v1 payload digests and evidence identities.</summary>
public static class EvidenceIdentity
{
    private static readonly byte[] EvidenceDomain = Encoding.ASCII.GetBytes("diffra/v1\0evidence\0");
    private static readonly HashSet<string> EvidenceProjectionFields = new(StringComparer.Ordinal)
    {
        "schema", "typeId", "typeVersion", "typeResolution", "status", "subject", "producer",
        "environment", "provenance", "payloadDigest", "payload", "artifactRefs",
    };

    /// <summary>Returns the SHA-256 digest of the canonical payload JSON.</summary>
    public static string ComputePayloadDigest(ReadOnlySpan<byte> payloadJson)
    {
        var canonicalPayload = CanonicalJson.Canonicalize(payloadJson);
        return FormatDigest(SHA256.HashData(canonicalPayload));
    }

    /// <summary>
    /// Computes an evidence ID from an identity projection. Its root <c>id</c> member is excluded
    /// from the hash. Callers provide only fields selected by the evidence type contract as
    /// identity-relevant; the evidence schema URI identifies the document kind.
    /// </summary>
    public static string ComputeId(ReadOnlySpan<byte> identityProjectionJson)
    {
        var canonicalProjection = CanonicalJson.CanonicalizeWithoutRootProperty(identityProjectionJson, "id");
        var hashInput = new byte[EvidenceDomain.Length + canonicalProjection.Length];
        EvidenceDomain.CopyTo(hashInput, 0);
        canonicalProjection.CopyTo(hashInput, EvidenceDomain.Length);
        return FormatDigest(SHA256.HashData(hashInput));
    }

    /// <summary>
    /// Verifies the canonical payload digest and evidence ID for a complete v1 Evidence document.
    /// </summary>
    /// <remarks>
    /// This verifies the envelope references, including relative artifact paths, but cannot verify
    /// artifact bytes because they are not supplied. Import verifies each artifact's exact-byte digest.
    /// </remarks>
    /// <exception cref="JsonException">The document, payload digest, identity, or artifact path is invalid.</exception>
    public static void Verify(ReadOnlySpan<byte> evidenceDocumentJson)
    {
        using var document = CanonicalJson.Parse(evidenceDocumentJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An Evidence document must be a JSON object.");
        }

        ValidateEnvelopeShape(root);

        var payload = RequiredProperty(root, "payload");
        var payloadDigest = RequiredString(root, "payloadDigest");
        var expectedPayloadDigest = ComputePayloadDigest(Encoding.UTF8.GetBytes(payload.GetRawText()));
        if (!string.Equals(payloadDigest, expectedPayloadDigest, StringComparison.Ordinal))
        {
            throw new JsonException("Evidence payloadDigest does not match the canonical payload.");
        }

        var artifactDigests = ValidateArtifactRefs(RequiredProperty(root, "artifactRefs"));
        ValidateProvenanceArtifactDigests(RequiredProperty(root, "provenance"), artifactDigests);

        foreach (var field in EvidenceProjectionFields)
        {
            _ = RequiredProperty(root, field);
        }

        var id = RequiredString(root, "id");
        var projection = CanonicalJson.CanonicalizeRootProjection(evidenceDocumentJson, EvidenceProjectionFields);
        var expectedId = ComputeId(projection);
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            throw new JsonException("Evidence id does not match its identity projection.");
        }
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            throw new JsonException($"Evidence document is missing required property '{name}'.");
        }

        return property;
    }

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        EnsureOnlyProperties(root, "schema", "id", "status", "typeId", "typeVersion", "typeResolution",
            "subject", "producer", "environment", "provenance", "payloadDigest", "payload", "artifactRefs", "message");
        if (RequiredString(root, "schema") != "https://diffra.dev/schemas/evidence-v1.schema.json")
        {
            throw new JsonException("Evidence schema URI is unsupported.");
        }

        if (RequiredString(root, "status") is not ("produced" or "unavailable" or "invalid" or "inconclusive") ||
            RequiredString(root, "typeResolution") is not ("recognized" or "opaque") ||
            string.IsNullOrWhiteSpace(RequiredString(root, "typeId")) ||
            string.IsNullOrWhiteSpace(RequiredString(root, "typeVersion")))
        {
            throw new JsonException("Evidence status, type resolution, or type identity is invalid.");
        }

        var subject = RequiredProperty(root, "subject");
        EnsureOnlyProperties(subject, "kind", "id", "commit", "repository");
        RequireNonemptyString(subject, "kind");
        RequireNonemptyString(subject, "id");
        RequireNonemptyString(subject, "commit");

        var producer = RequiredProperty(root, "producer");
        EnsureOnlyProperties(producer, "id", "version", "configDigest");
        RequireNonemptyString(producer, "id");
        RequireNonemptyString(producer, "version");
        if (!IsDigest(RequiredString(producer, "configDigest")))
        {
            throw new JsonException("Producer configDigest is invalid.");
        }

        var environment = RequiredProperty(root, "environment");
        EnsureOnlyProperties(environment, "platform", "runtime", "tools");
        RequireNonemptyString(environment, "platform");
        RequireNonemptyString(environment, "runtime");
        var tools = RequiredProperty(environment, "tools");
        if (tools.ValueKind != JsonValueKind.Array) throw new JsonException("Evidence tools must be an array.");
        foreach (var tool in tools.EnumerateArray())
        {
            EnsureOnlyProperties(tool, "id", "version");
            RequireNonemptyString(tool, "id");
            RequireNonemptyString(tool, "version");
        }

        var provenance = RequiredProperty(root, "provenance");
        EnsureOnlyProperties(provenance, "source", "artifactDigests");
        RequireNonemptyString(provenance, "source");
        if (root.TryGetProperty("message", out var message) && message.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Evidence message must be a string.");
        }
    }

    private static void EnsureOnlyProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object in the Evidence document.");
        }

        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw new JsonException($"Unknown Evidence property '{property.Name}'.");
            }
        }
    }

    private static void RequireNonemptyString(JsonElement element, string name)
    {
        if (string.IsNullOrWhiteSpace(RequiredString(element, name)))
        {
            throw new JsonException($"Evidence property '{name}' must be nonempty.");
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var property = RequiredProperty(element, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Evidence property '{name}' must be a string.");
        }

        return property.GetString()!;
    }

    private static HashSet<string> ValidateArtifactRefs(JsonElement artifactRefs)
    {
        if (artifactRefs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Evidence artifactRefs must be an array.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifactRef in artifactRefs.EnumerateArray())
        {
            if (artifactRef.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each Evidence artifact reference must be an object.");
            }
            EnsureOnlyProperties(artifactRef, "id", "digest", "mediaType", "sizeBytes", "path");

            var id = RequiredString(artifactRef, "id");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
            {
                throw new JsonException("Evidence artifact reference IDs must be non-empty and unique.");
            }

            var digest = RequiredString(artifactRef, "digest");
            if (!IsDigest(digest))
            {
                throw new JsonException($"Artifact reference '{id}' has an invalid SHA-256 digest.");
            }

            digests.Add(digest);
            RequireNonemptyString(artifactRef, "mediaType");
            var size = RequiredProperty(artifactRef, "sizeBytes");
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var sizeBytes) || sizeBytes < 0)
            {
                throw new JsonException($"Artifact reference '{id}' has an invalid sizeBytes value.");
            }

            if (!artifactRef.TryGetProperty("path", out var pathElement))
            {
                continue;
            }

            if (pathElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("An artifact reference path must be a string.");
            }

            var path = pathElement.GetString()!;
            var segments = path.Split('/');
            if (path.Length == 0
                || path[0] == '/'
                || path.Contains('\\')
                || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
                || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            {
                throw new JsonException($"Artifact path must be a normalized relative path without traversal: {path}");
            }
        }

        return digests;
    }

    private static void ValidateProvenanceArtifactDigests(JsonElement provenance, HashSet<string> artifactDigests)
    {
        var values = RequiredProperty(provenance, "artifactDigests");
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Evidence provenance artifactDigests must be an array.");
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !IsDigest(value.GetString()!) || !declared.Add(value.GetString()!))
            {
                throw new JsonException("Evidence provenance artifactDigests must contain unique SHA-256 digests.");
            }
        }

        if (!declared.SetEquals(artifactDigests))
        {
            throw new JsonException("Evidence provenance artifactDigests must match the referenced artifact digests.");
        }
    }

    private static bool IsDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var digit in digest.AsSpan(7))
        {
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatDigest(ReadOnlySpan<byte> hash) => $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
}
