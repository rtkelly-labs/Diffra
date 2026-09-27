using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Computes and verifies IDs for non-Evidence protocol documents.</summary>
public static class DocumentIdentity
{
    /// <summary>Hashes all root fields except <c>id</c> with the document kind's v1 domain.</summary>
    public static string ComputeId(string kind, ReadOnlySpan<byte> documentJson)
    {
        var schema = SchemaFor(kind);
        using var document = CanonicalJson.Parse(documentJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schema", out var schemaValue) ||
            schemaValue.ValueKind != JsonValueKind.String ||
            !string.Equals(schemaValue.GetString(), schema, StringComparison.Ordinal))
        {
            throw new JsonException($"Expected the Diffra {kind} v1 schema.");
        }

        var projection = CanonicalJson.CanonicalizeWithoutRootProperty(documentJson, "id");
        var domain = Encoding.ASCII.GetBytes($"diffra/v1\0{kind}\0");
        var input = new byte[domain.Length + projection.Length];
        domain.CopyTo(input, 0);
        projection.CopyTo(input, domain.Length);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(input))}";
    }

    /// <summary>Rejects a document whose ID differs from its v1 canonical identity.</summary>
    public static void Verify(string kind, ReadOnlySpan<byte> documentJson)
    {
        using var document = CanonicalJson.Parse(documentJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("id", out var idValue) ||
            idValue.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Diffra {kind} is missing a string id.");
        }

        var expected = ComputeId(kind, documentJson);
        if (!string.Equals(idValue.GetString(), expected, StringComparison.Ordinal))
        {
            throw new JsonException($"Diffra {kind} id does not match its canonical content.");
        }
    }

    private static string SchemaFor(string kind) => kind switch
    {
        "baseline-reference" => "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
        "delta" => "https://diffra.dev/schemas/delta-v1.schema.json",
        "assessment" => "https://diffra.dev/schemas/assessment-v1.schema.json",
        "manifest" => "https://diffra.dev/schemas/manifest-v1.schema.json",
        _ => throw new ArgumentException($"Unsupported Diffra document kind '{kind}'.", nameof(kind)),
    };
}
