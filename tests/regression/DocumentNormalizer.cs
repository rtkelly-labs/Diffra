using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Regression;

public static class DocumentNormalizer
{
    public static byte[] NormalizeCollectJson(byte[] jsonBytes)
    {
        var node = JsonNode.Parse(jsonBytes);
        if (node is JsonObject obj)
        {
            var env = new JsonObject
            {
                ["platform"] = "normalized",
                ["runtime"] = "normalized",
                ["tools"] = new JsonArray()
            };
            obj["environment"] = env;
            obj["id"] = "sha256:normalized";
        }

        var normalizedJson = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
        return CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(normalizedJson));
    }

    public static byte[] NormalizeDeltaJson(byte[] jsonBytes)
    {
        var node = JsonNode.Parse(jsonBytes);
        if (node is JsonObject obj)
        {
            if (obj["provenance"] is JsonObject prov)
            {
                var env = new JsonObject
                {
                    ["runtime"] = "normalized"
                };
                prov["environment"] = env;
            }
            obj["id"] = "sha256:normalized";
        }

        var normalizedJson = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
        return CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(normalizedJson));
    }
}
