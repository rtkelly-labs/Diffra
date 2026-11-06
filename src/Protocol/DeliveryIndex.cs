using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>
/// Represents an immutable, content-addressed delivery index for transport and presentation.
/// </summary>
public static class DeliveryIndex
{
    public const string SchemaUri = "https://diffra.dev/schemas/delivery-index-v1.schema.json";

    public sealed record AssetEntry(
        string Id,
        string Digest,
        string MediaType,
        long SizeBytes,
        string? Path = null,
        string? SourceUrl = null);

    public sealed record Document(
        string Schema,
        string Id,
        string Repository,
        string ReleaseTag,
        string Commit,
        IReadOnlyList<AssetEntry> Assets);

    public sealed record InventoryItem(
        string Id,
        string Name,
        string Digest,
        string RelativePath,
        string MediaType,
        long SizeBytes);

    /// <summary>
    /// Creates a verified Delivery Index JSON string with canonical SHA-256 identity.
    /// </summary>
    public static string Create(string repository, string releaseTag, string commit, IEnumerable<AssetEntry> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var sortedAssets = assets.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();

        var assetsArray = new JsonArray();
        foreach (var asset in sortedAssets)
        {
            var node = new JsonObject
            {
                ["digest"] = asset.Digest,
                ["id"] = asset.Id,
                ["mediaType"] = asset.MediaType,
                ["sizeBytes"] = asset.SizeBytes
            };
            if (asset.Path is not null) node["path"] = asset.Path;
            if (asset.SourceUrl is not null) node["sourceUrl"] = asset.SourceUrl;
            assetsArray.Add(node);
        }

        var unsigned = new JsonObject
        {
            ["assets"] = assetsArray,
            ["commit"] = commit,
            ["releaseTag"] = releaseTag,
            ["repository"] = repository,
            ["schema"] = SchemaUri
        };

        var unsignedBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(unsigned.ToJsonString()));
        var framed = Encoding.ASCII.GetBytes("diffra/v1\0delivery-index\0").Concat(unsignedBytes).ToArray();
        var id = "sha256:" + Convert.ToHexString(SHA256.HashData(framed)).ToLowerInvariant();

        unsigned["id"] = id;
        var finalBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(unsigned.ToJsonString()));
        return Encoding.UTF8.GetString(finalBytes);
    }

    /// <summary>
    /// Validates a Delivery Index document and asserts that its ID matches the canonical payload hash.
    /// </summary>
    public static Document Verify(ReadOnlySpan<byte> utf8Json)
    {
        using var json = CanonicalJson.Parse(utf8Json);
        var root = json.RootElement;

        if (!root.TryGetProperty("schema", out var s) || s.GetString() != SchemaUri)
        {
            throw new JsonException($"Expected schema '{SchemaUri}'.");
        }

        var id = root.GetProperty("id").GetString()!;
        var repo = root.GetProperty("repository").GetString()!;
        var tag = root.GetProperty("releaseTag").GetString()!;
        var commit = root.GetProperty("commit").GetString()!;

        // Check canonical ID
        var node = JsonNode.Parse(utf8Json)?.AsObject() ?? throw new JsonException("Invalid delivery index JSON.");
        node.Remove("id");
        var unsignedBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var framed = Encoding.ASCII.GetBytes("diffra/v1\0delivery-index\0").Concat(unsignedBytes).ToArray();
        var expectedId = "sha256:" + Convert.ToHexString(SHA256.HashData(framed)).ToLowerInvariant();

        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Delivery index ID mismatch. Expected '{expectedId}', got '{id}'.");
        }

        var assets = new List<AssetEntry>();
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            assets.Add(new AssetEntry(
                item.GetProperty("id").GetString()!,
                item.GetProperty("digest").GetString()!,
                item.GetProperty("mediaType").GetString()!,
                item.GetProperty("sizeBytes").GetInt64(),
                item.TryGetProperty("path", out var p) ? p.GetString() : null,
                item.TryGetProperty("sourceUrl", out var u) ? u.GetString() : null));
        }

        return new Document(SchemaUri, id, repo, tag, commit, assets);
    }

    /// <summary>
    /// Generates the inventory mapping human artifact IDs to digest-addressed relative files.
    /// </summary>
    public static string CreateInventoryJson(IEnumerable<InventoryItem> items)
    {
        var sorted = items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        var array = new JsonArray();
        foreach (var item in sorted)
        {
            array.Add(new JsonObject
            {
                ["digest"] = item.Digest,
                ["id"] = item.Id,
                ["mediaType"] = item.MediaType,
                ["name"] = item.Name,
                ["relativePath"] = item.RelativePath,
                ["sizeBytes"] = item.SizeBytes
            });
        }

        var canonicalBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(array.ToJsonString()));
        return Encoding.UTF8.GetString(canonicalBytes);
    }
}
