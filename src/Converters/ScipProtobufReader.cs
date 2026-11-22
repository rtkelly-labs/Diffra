using System.Text;

namespace Diffra.Converters;

/// <summary>Decoded SCIP <c>Index</c> subset needed for architecture dependency folding (<c>scip.proto</c>).</summary>
internal sealed record ScipIndex(
    string ToolName,
    string? ToolVersion,
    IReadOnlyList<ScipDocument> Documents);

internal sealed record ScipDocument(
    string RelativePath,
    string Language,
    IReadOnlyList<ScipOccurrence> Occurrences,
    IReadOnlyList<ScipSymbolInfo> Symbols);

internal sealed record ScipOccurrence(
    string Symbol,
    int SymbolRoles,
    int? OneBasedLine);

internal sealed record ScipSymbolInfo(
    string Symbol,
    IReadOnlyList<ScipRelationship> Relationships);

internal sealed record ScipRelationship(
    string Symbol,
    bool IsReference,
    bool IsImplementation,
    bool IsTypeDefinition,
    bool IsDefinition);

/// <summary>
/// Zero-dependency Protobuf wire decoder for <c>scip.proto</c> (sourcegraph/scip).
/// Implements exact wire types (varint=0, fixed64=1, length-delimited=2, fixed32=5) with hard
/// nesting and length bounds so untrusted <c>.scip</c> files cannot allocate or recurse unboundedly.
/// </summary>
internal static class ScipProtobufReader
{
    public const int MaxNestingDepth = 32;

    // SymbolRole bitset from scip.proto lines 524-546.
    public const int RoleDefinition = 0x1;
    public const int RoleImport = 0x2;
    public const int RoleWriteAccess = 0x4;
    public const int RoleReadAccess = 0x8;
    public const int RoleGenerated = 0x10;
    public const int RoleTest = 0x20;
    public const int RoleForwardDefinition = 0x40;

    public static ScipIndex Parse(ReadOnlySpan<byte> bytes)
    {
        var toolName = "scip";
        string? toolVersion = null;
        var documents = new List<ScipDocument>();

        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2) // Index.metadata
            {
                (toolName, toolVersion) = ReadMetadata(reader.ReadBytes());
            }
            else if (field == 2 && wireType == 2) // Index.documents
            {
                documents.Add(ReadDocument(reader.ReadBytes()));
            }
            else
            {
                reader.SkipField(wireType);
            }
        }

        return new ScipIndex(toolName, toolVersion, documents);
    }

    /// <summary>Decodes a SCIP <c>SymbolRole</c> bitset into canonical SCIP enum names.</summary>
    public static IReadOnlyList<string> DecodeRoles(int bitset)
    {
        var roles = new List<string>();
        if ((bitset & RoleDefinition) != 0) roles.Add("Definition");
        if ((bitset & RoleImport) != 0) roles.Add("Import");
        if ((bitset & RoleWriteAccess) != 0) roles.Add("WriteAccess");
        if ((bitset & RoleReadAccess) != 0) roles.Add("ReadAccess");
        if ((bitset & RoleGenerated) != 0) roles.Add("Generated");
        if ((bitset & RoleTest) != 0) roles.Add("Test");
        if ((bitset & RoleForwardDefinition) != 0) roles.Add("ForwardDefinition");
        return roles;
    }

    private static (string ToolName, string? ToolVersion) ReadMetadata(ReadOnlySpan<byte> bytes)
    {
        var toolName = "scip";
        string? toolVersion = null;
        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 2 && wireType == 2) // Metadata.tool_info
            {
                (toolName, toolVersion) = ReadToolInfo(reader.ReadBytes());
            }
            else
            {
                reader.SkipField(wireType);
            }
        }

        return (toolName, toolVersion);
    }

    private static (string Name, string? Version) ReadToolInfo(ReadOnlySpan<byte> bytes)
    {
        var name = "scip";
        string? version = null;
        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == 2)
            {
                var read = reader.ReadString();
                if (!string.IsNullOrWhiteSpace(read))
                {
                    name = read;
                }
            }
            else if (field == 2 && wireType == 2)
            {
                version = reader.ReadString();
            }
            else
            {
                reader.SkipField(wireType);
            }
        }

        return (name, version);
    }

    private static ScipDocument ReadDocument(ReadOnlySpan<byte> bytes)
    {
        var relativePath = string.Empty;
        var language = "UnspecifiedLanguage";
        var occurrences = new List<ScipOccurrence>();
        var symbols = new List<ScipSymbolInfo>();

        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (1, 2): // Document.relative_path
                    relativePath = reader.ReadString();
                    break;
                case (2, 2): // Document.occurrences
                    occurrences.Add(ReadOccurrence(reader.ReadBytes()));
                    break;
                case (3, 2): // Document.symbols
                    symbols.Add(ReadSymbolInfo(reader.ReadBytes()));
                    break;
                case (4, 2): // Document.language
                    var lang = reader.ReadString();
                    if (!string.IsNullOrWhiteSpace(lang))
                    {
                        language = lang;
                    }

                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new ScipDocument(relativePath, language, occurrences, symbols);
    }

    private static ScipOccurrence ReadOccurrence(ReadOnlySpan<byte> bytes)
    {
        var symbol = string.Empty;
        var roles = 0;
        int? legacyZeroBasedLine = null;
        int? typedZeroBasedLine = null;

        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (1, 2): // packed repeated int32 range (deprecated)
                    var packed = new WireReader(reader.ReadBytes());
                    if (!packed.IsAtEnd)
                    {
                        legacyZeroBasedLine = (int)packed.ReadVarint();
                    }

                    break;
                case (1, 0): // unpacked repeated int32 range (first element is startLine)
                    var val = (int)reader.ReadVarint();
                    legacyZeroBasedLine ??= val;
                    break;
                case (2, 2): // Occurrence.symbol
                    symbol = reader.ReadString();
                    break;
                case (3, 0): // Occurrence.symbol_roles
                    roles = (int)reader.ReadVarint();
                    break;
                case (8, 2): // Occurrence.single_line_range (field 1 = line)
                    typedZeroBasedLine = ReadFirstVarintField(reader.ReadBytes(), targetField: 1);
                    break;
                case (9, 2): // Occurrence.multi_line_range (field 1 = start_line)
                    typedZeroBasedLine = ReadFirstVarintField(reader.ReadBytes(), targetField: 1);
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        var zeroBased = typedZeroBasedLine ?? legacyZeroBasedLine;
        int? oneBased = zeroBased is >= 0 ? zeroBased.Value + 1 : null;
        return new ScipOccurrence(symbol, roles, oneBased);
    }

    private static int? ReadFirstVarintField(ReadOnlySpan<byte> bytes, int targetField)
    {
        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == targetField && wireType == 0)
            {
                return (int)reader.ReadVarint();
            }

            reader.SkipField(wireType);
        }

        return 0; // proto3 default for int32 line = 0
    }

    private static ScipSymbolInfo ReadSymbolInfo(ReadOnlySpan<byte> bytes)
    {
        var symbol = string.Empty;
        var relationships = new List<ScipRelationship>();
        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (1, 2): // SymbolInformation.symbol
                    symbol = reader.ReadString();
                    break;
                case (4, 2): // SymbolInformation.relationships
                    relationships.Add(ReadRelationship(reader.ReadBytes()));
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new ScipSymbolInfo(symbol, relationships);
    }

    private static ScipRelationship ReadRelationship(ReadOnlySpan<byte> bytes)
    {
        var symbol = string.Empty;
        var isRef = false;
        var isImpl = false;
        var isTypeDef = false;
        var isDef = false;
        var reader = new WireReader(bytes);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (1, 2): symbol = reader.ReadString(); break;
                case (2, 0): isRef = reader.ReadVarint() != 0; break;
                case (3, 0): isImpl = reader.ReadVarint() != 0; break;
                case (4, 0): isTypeDef = reader.ReadVarint() != 0; break;
                case (5, 0): isDef = reader.ReadVarint() != 0; break;
                default: reader.SkipField(wireType); break;
            }
        }

        return new ScipRelationship(symbol, isRef, isImpl, isTypeDef, isDef);
    }

    private ref struct WireReader(ReadOnlySpan<byte> span)
    {
        private readonly ReadOnlySpan<byte> _span = span;
        private int _pos;

        public readonly bool IsAtEnd => _pos >= _span.Length;

        public (int Field, int WireType) ReadTag()
        {
            var tag = ReadVarint();
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (field <= 0)
            {
                throw new InvalidDataException($"Invalid protobuf field number {field}.");
            }

            return (field, wireType);
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (_pos >= _span.Length)
                {
                    throw new InvalidDataException("Unexpected end of protobuf stream while reading varint.");
                }

                var b = _span[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift >= 64)
                {
                    throw new InvalidDataException("Malformed protobuf varint exceeds 64 bits.");
                }
            }
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var len = ReadVarint();
            if (len > (ulong)(_span.Length - _pos))
            {
                throw new InvalidDataException($"Protobuf length-delimited field ({len} bytes) exceeds remaining input.");
            }

            var slice = _span.Slice(_pos, (int)len);
            _pos += (int)len;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = ReadVarint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    _ = ReadBytes();
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
            }
        }

        private void Advance(int count)
        {
            if (count > _span.Length - _pos)
            {
                throw new InvalidDataException("Unexpected end of protobuf stream.");
            }

            _pos += count;
        }
    }
}
