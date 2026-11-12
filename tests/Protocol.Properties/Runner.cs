using System.Diagnostics;
using System.Globalization;
using CsCheck;

namespace Diffra.Protocol.Properties;

/// <summary>A group of properties, found by type and run in name order.</summary>
internal interface IPropertySuite
{
    void Register(Runner runner);
}

/// <summary>
/// Runs each property over inputs that depend only on the root seed, the property's name and the
/// iteration number, so the same seed checks the same inputs on every run and on any number of
/// threads. CsCheck's own seed fixes only the first case of a sample, so it is used here only to
/// shrink a failure that has already been found.
/// </summary>
internal sealed class Runner(long iterations, ulong seed, string? filter)
{
    private const long ShrinkIterations = 2000;

    private readonly List<Func<string>> notes = [];

    public List<PropertyResult> Results { get; } = [];

    /// <summary>The non-vacuity notes, read once every property has run.</summary>
    public IEnumerable<string> Notes => notes.Select(note => note());

    /// <summary>Adds a line to the report that says how often the interesting cases were reached.</summary>
    public void Note(Func<string> note) => notes.Add(note);

    /// <summary>
    /// Checks one property. <paramref name="divisor"/> divides the iteration count for a property
    /// whose single case is expensive.
    /// </summary>
    public void Check<T>(string name, Gen<T> gen, Action<T> assert, Func<T, string> print, int divisor = 1)
    {
        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var count = Math.Max(1, iterations / divisor);
        var stream = StreamOf(name);
        var watch = Stopwatch.StartNew();
        var gate = new Lock();
        var failedAt = long.MaxValue;
        Exception? failure = null;
        Parallel.For(0, count, index =>
        {
            // Every case below the lowest failure still runs, so the failure reported is always the first.
            if (index > Volatile.Read(ref failedAt))
            {
                return;
            }

            try
            {
                assert(gen.Generate(CaseGenerator(stream, index), null, out _));
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    if (index < failedAt)
                    {
                        failedAt = index;
                        failure = exception;
                    }
                }
            }
        });

        if (failure is null)
        {
            Results.Add(new PropertyResult(name, null, watch.Elapsed, count));
            Console.WriteLine($"PASS {name}");
            return;
        }

        var message = Limit(Describe(gen, assert, print, stream, failedAt, failure));
        Results.Add(new PropertyResult(name, message, watch.Elapsed, count));
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(message);
    }

    private string Describe<T>(Gen<T> gen, Action<T> assert, Func<T, string> print, uint stream, long index, Exception failure)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Root seed {seed}, case {index}: {failure.Message}");
        try
        {
            text += "\n  input: " + Limit(print(gen.Generate(CaseGenerator(stream, index), null, out _)));

            // The failing case is replayed first, then CsCheck looks for a smaller one.
            gen.Sample(assert, seed: CaseGenerator(stream, index).ToString(), iter: ShrinkIterations, print: print);
            return text + "\n(CsCheck did not reproduce the failure, so it was not shrunk.)";
        }
        catch (Exception shrunk)
        {
            return text + "\nShrunk by CsCheck:\n" + shrunk.Message;
        }
    }

    private PCG CaseGenerator(uint stream, long index) => new(stream, Mix(seed + (ulong)index));

    private static string Limit(string text) => text.Length > 4000 ? text[..4000] + "..." : text;

    // FNV-1a, because string.GetHashCode differs from one process to the next.
    private static uint StreamOf(string name)
    {
        var hash = 2166136261;
        foreach (var character in name)
        {
            hash = unchecked((hash ^ character) * 16777619);
        }

        return hash;
    }

    // SplitMix64's finalizer, so that neighbouring case numbers give unrelated generators.
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
            return value ^ (value >> 31);
        }
    }
}
