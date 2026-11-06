using System.Buffers;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Builds deterministic, verified v1 Evidence document bytes.</summary>
public static class EvidenceDocumentBuilder
{
    public const string EvidenceSchema = "https://diffra.dev/schemas/evidence-v1.schema.json";
    public const string EmptyConfigDigest = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";

    public static (string Id, string PayloadDigest, byte[] Bytes) Build(
        string typeId,
        string typeVersion,
        string subjectId,
        string commit,
        string producerId,
        string producerVersion,
        byte[] canonicalPayloadBytes,
        string platform,
        string runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        ArgumentNullException.ThrowIfNull(canonicalPayloadBytes);

        var payloadDigest = EvidenceIdentity.ComputePayloadDigest(canonicalPayloadBytes);

        var unsignedBytes = BuildJson(
            typeId, typeVersion, subjectId, commit, producerId, producerVersion,
            canonicalPayloadBytes, payloadDigest, platform, runtime, id: null);

        var id = EvidenceIdentity.ComputeId(unsignedBytes);

        var signedBytes = BuildJson(
            typeId, typeVersion, subjectId, commit, producerId, producerVersion,
            canonicalPayloadBytes, payloadDigest, platform, runtime, id);

        EvidenceIdentity.Verify(signedBytes);

        return (id, payloadDigest, signedBytes);
    }

    private static byte[] BuildJson(
        string typeId,
        string typeVersion,
        string subjectId,
        string commit,
        string producerId,
        string producerVersion,
        byte[] canonicalPayloadBytes,
        string payloadDigest,
        string platform,
        string runtime,
        string? id)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", EvidenceSchema);
            if (id is not null)
            {
                writer.WriteString("id", id);
            }

            writer.WriteString("status", "produced");
            writer.WriteString("typeId", typeId);
            writer.WriteString("typeVersion", typeVersion);
            writer.WriteString("typeResolution", "recognized");

            writer.WriteStartObject("subject");
            writer.WriteString("kind", "git-repository");
            writer.WriteString("id", subjectId);
            writer.WriteString("commit", commit);
            writer.WriteEndObject();

            writer.WriteStartObject("producer");
            writer.WriteString("id", producerId);
            writer.WriteString("version", producerVersion);
            writer.WriteString("configDigest", EmptyConfigDigest);
            writer.WriteEndObject();

            writer.WriteStartObject("environment");
            writer.WriteString("platform", platform);
            writer.WriteString("runtime", runtime);
            writer.WriteStartArray("tools");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("provenance");
            writer.WriteString("source", $"git:{commit}");
            writer.WriteStartArray("artifactDigests");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteString("payloadDigest", payloadDigest);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(canonicalPayloadBytes, skipInputValidation: false);
            writer.WriteStartArray("artifactRefs");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        var result = new byte[output.WrittenCount + 1];
        output.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }
}
