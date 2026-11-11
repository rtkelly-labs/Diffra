using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Diffra.RootFile;

/// <summary>A node of the parsed root file, with the 1-based position where it starts.</summary>
internal abstract record YamlNode(long Line, long Column);

/// <summary>A scalar. <paramref name="Plain"/> is false for a quoted or block scalar.</summary>
internal sealed record YamlScalar(long Line, long Column, string Value, bool Plain) : YamlNode(Line, Column)
{
    /// <summary>A plain <c>null</c>, <c>Null</c>, <c>NULL</c>, <c>~</c> or empty scalar. A quoted one is text.</summary>
    public bool IsNull => Plain && Value is "" or "~" or "null" or "Null" or "NULL";
}

internal sealed record YamlSequence(long Line, long Column, IReadOnlyList<YamlNode> Items) : YamlNode(Line, Column);

internal sealed record YamlMapping(long Line, long Column, IReadOnlyList<KeyValuePair<YamlScalar, YamlNode>> Entries)
    : YamlNode(Line, Column)
{
    public YamlNode? Get(string key)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key.Value, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// Builds a <see cref="YamlNode"/> tree from the YAML event stream. The root file uses a small part
/// of YAML: one document, no anchors, aliases or tags, text keys, and no duplicate keys. Anything
/// else is rejected rather than interpreted.
/// </summary>
internal static class YamlTree
{
    private const int MaxDepth = 64;

    public static YamlNode? Parse(string text, List<RootFileProblem> problems)
    {
        var parser = new Parser(new StringReader(text));
        try
        {
            Expect<StreamStart>(Next(parser));
            var first = Next(parser);
            if (first is StreamEnd)
            {
                problems.Add(new RootFileProblem(1, 1, "The root file is empty."));
                return null;
            }

            Expect<DocumentStart>(first);
            var root = ReadNode(parser, Next(parser), 0);
            Expect<DocumentEnd>(Next(parser));
            var last = Next(parser);
            if (last is DocumentStart)
            {
                problems.Add(At(last, "The root file holds more than one YAML document."));
                return null;
            }

            Expect<StreamEnd>(last);
            return root;
        }
        catch (YamlException e)
        {
            problems.Add(new RootFileProblem(e.Start.Line, e.Start.Column, e.Message));
            return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // YamlDotNet reports some malformed input, such as an empty or invalid tag, this way.
            problems.Add(new RootFileProblem(1, 1, $"The root file is not valid YAML: {e.Message}"));
            return null;
        }
    }

    private static YamlNode ReadNode(Parser parser, ParsingEvent current, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Reject(current, $"The root file nests deeper than {MaxDepth} levels.");
        }

        if (current is AnchorAlias)
        {
            throw Reject(current, "Aliases are not allowed in the root file.");
        }

        if (current is NodeEvent node)
        {
            if (!node.Anchor.IsEmpty)
            {
                throw Reject(current, "Anchors are not allowed in the root file.");
            }

            if (!node.Tag.IsEmpty)
            {
                throw Reject(current, "Tags are not allowed in the root file.");
            }
        }

        switch (current)
        {
            case Scalar scalar:
                return new YamlScalar(current.Start.Line, current.Start.Column, scalar.Value, scalar.Style == ScalarStyle.Plain);

            case SequenceStart:
            {
                var items = new List<YamlNode>();
                for (var next = Next(parser); next is not SequenceEnd; next = Next(parser))
                {
                    items.Add(ReadNode(parser, next, depth + 1));
                }

                return new YamlSequence(current.Start.Line, current.Start.Column, items);
            }

            case MappingStart:
            {
                var entries = new List<KeyValuePair<YamlScalar, YamlNode>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var next = Next(parser); next is not MappingEnd; next = Next(parser))
                {
                    if (ReadNode(parser, next, depth + 1) is not YamlScalar key)
                    {
                        throw Reject(next, "A key in the root file must be text.");
                    }

                    if (!seen.Add(key.Value))
                    {
                        throw Reject(next, $"The key '{key.Value}' appears twice in the same mapping.");
                    }

                    entries.Add(new KeyValuePair<YamlScalar, YamlNode>(key, ReadNode(parser, Next(parser), depth + 1)));
                }

                return new YamlMapping(current.Start.Line, current.Start.Column, entries);
            }

            default:
                throw Reject(current, $"Unexpected YAML event {current.GetType().Name}.");
        }
    }

    private static ParsingEvent Next(Parser parser)
    {
        if (!parser.MoveNext() || parser.Current is null)
        {
            throw new YamlException("The YAML stream ended unexpectedly.");
        }

        return parser.Current;
    }

    private static void Expect<T>(ParsingEvent current)
        where T : ParsingEvent
    {
        if (current is not T)
        {
            throw Reject(current, $"Expected {typeof(T).Name} but found {current.GetType().Name}.");
        }
    }

    private static YamlException Reject(ParsingEvent current, string message) =>
        new(current.Start, current.End, message);

    private static RootFileProblem At(ParsingEvent current, string message) =>
        new(current.Start.Line, current.Start.Column, message);
}
