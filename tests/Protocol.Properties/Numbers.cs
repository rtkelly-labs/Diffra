using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CsCheck;

namespace Diffra.Protocol.Properties;

/// <summary>
/// The reference model for a JSON number: an exact value, digits times ten to the exponent, held
/// without rounding or an exponent limit. Two numbers are equal exactly when their records are.
/// </summary>
internal sealed partial record ExactNumber(bool Negative, string Digits, BigInteger Exponent)
{
    public static readonly ExactNumber Zero = new(false, "0", BigInteger.Zero);

    /// <summary>The exact value of a JSON number token.</summary>
    public static ExactNumber Parse(string token)
    {
        var match = TokenPattern().Match(token);
        if (!match.Success)
        {
            throw new FormatException($"'{token}' is not a JSON number.");
        }

        var fraction = match.Groups["fraction"].Value;
        var exponent = match.Groups["exponent"].Success
            ? BigInteger.Parse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : BigInteger.Zero;
        return Of(match.Groups["sign"].Length > 0, match.Groups["integer"].Value + fraction, exponent - fraction.Length);
    }

    /// <summary>The one record for a value: no leading or trailing zeros, and a single zero.</summary>
    public static ExactNumber Of(bool negative, string digits, BigInteger exponent)
    {
        var significant = digits.TrimStart('0');
        if (significant.Length == 0)
        {
            return Zero;
        }

        var trimmed = significant.TrimEnd('0');
        return new ExactNumber(negative, trimmed, exponent + (significant.Length - trimmed.Length));
    }

    [GeneratedRegex(@"\A(?<sign>-?)(?<integer>0|[1-9][0-9]*)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z")]
    private static partial Regex TokenPattern();
}

internal static class Numbers
{
    // Margins inside each limit, so that the model never depends on where exactly a limit falls.
    // They are wider than a token is long, and no wider: the exponents just inside them are drawn
    // (see Edges), so that exact comparison is checked close to each limit.
    private const int Margin = 100;
    public static readonly BigInteger SafeForDeepEquals = int.MaxValue - Margin;
    public static readonly BigInteger SafeForComparator = (long.MaxValue / 2) - Margin;

    private static readonly BigInteger[] Edges =
    [
        308, 324, 400, 9999, 10000, 1_000_000,
        int.MaxValue - (2 * Margin), (long.MaxValue / 2) - (2 * Margin),
        int.MaxValue, (BigInteger)int.MaxValue + 1, -(BigInteger)int.MaxValue - 1, -(BigInteger)int.MaxValue - 2,
        long.MaxValue / 2, (long.MaxValue / 2) + 1, long.MinValue / 2, (long.MinValue / 2) - 1,
        long.MaxValue, (BigInteger)long.MaxValue + 1, long.MinValue, (BigInteger)long.MinValue - 1,
        BigInteger.Pow(10, 30), -BigInteger.Pow(10, 30),
    ];

    private static readonly Gen<string> Digits = Gen.Frequency(
        (1, Gen.Const("0")),
        (5, Gen.Select(Gen.OneOfConst('1', '2', '9'), Gen.OneOfConst('0', '1', '9').Array[0, 3], (first, rest) => first + new string(rest))),
        (1, Gen.Select(Gen.Char['1', '9'], Gen.Char['0', '9'].Array[17, 40], (first, rest) => first + new string(rest))));

    private static readonly Gen<BigInteger> Exponent = Gen.Frequency(
        (3, Gen.Const(BigInteger.Zero)),
        (4, Gen.Int[-6, 6].Select(value => (BigInteger)value)),
        (2, Gen.Select(Gen.OneOfConst(Edges), Gen.Bool, Gen.Int[-3, 3], (edge, negate, offset) => (negate ? -edge : edge) + offset)));

    /// <summary>A value, as the sign, digits and exponent it was drawn with rather than its one record.</summary>
    public static readonly Gen<(bool Negative, string Digits, BigInteger Exponent)> Value = Gen.Select(Gen.Bool, Digits, Exponent);

    /// <summary>A value in one of its spellings.</summary>
    public static readonly Gen<string> Token = Gen.Select(Value, Gen.Int, (value, seed) => Spell(value, new Random(seed)));

    /// <summary>
    /// Two tokens that are often equal in value: the second respells the first, changes it a little,
    /// or is unrelated.
    /// </summary>
    public static readonly Gen<(string First, string Second)> Pair = Gen.Select(Value, Value, Gen.Int, (value, other, seed) =>
    {
        var random = new Random(seed);
        return (Spell(value, random), Spell(Vary(value, other, random), random));
    });

    /// <summary>The exponent a token states, or zero when it states none.</summary>
    public static BigInteger WrittenExponent(string token)
    {
        var index = token.AsSpan().IndexOfAny('e', 'E');
        return index < 0
            ? BigInteger.Zero
            : BigInteger.Parse(token.AsSpan(index + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>The value itself, a neighbour of it, or <paramref name="other"/>.</summary>
    public static (bool Negative, string Digits, BigInteger Exponent) Vary(
        (bool Negative, string Digits, BigInteger Exponent) value,
        (bool Negative, string Digits, BigInteger Exponent) other,
        Random random) => random.Next(12) switch
        {
            < 5 => value,
            5 => (value.Negative, value.Digits, value.Exponent + 1),
            6 => (value.Negative, value.Digits, value.Exponent - 1),
            7 => (!value.Negative, value.Digits, value.Exponent),
            8 => (value.Negative, value.Digits + "1", value.Exponent - 1),

            // The same exponent once 32 or 64 bits wrap round: equal to a comparison that overflows.
            9 => (value.Negative, value.Digits, value.Exponent + (value.Exponent.Sign > 0 ? -Wrap32 : Wrap32)),
            10 => (value.Negative, value.Digits, value.Exponent + (value.Exponent.Sign > 0 ? -Wrap64 : Wrap64)),
            _ => other,
        };

    private static readonly BigInteger Wrap32 = BigInteger.Pow(2, 32);
    private static readonly BigInteger Wrap64 = BigInteger.Pow(2, 64);

    /// <summary>
    /// Writes a value as a valid JSON number in one of its many spellings: trailing zeros, a decimal
    /// point anywhere, an exponent in either case with an optional plus sign and leading zeros.
    /// </summary>
    public static string Spell((bool Negative, string Digits, BigInteger Exponent) value, Random random)
    {
        var digits = value.Digits.TrimStart('0');
        var exponent = value.Exponent;
        var zero = digits.Length == 0;
        if (!zero)
        {
            var padding = random.Next(4) == 0 ? random.Next(1, 4) : 0;
            digits += new string('0', padding);
            exponent -= padding;
        }

        var fractionLength = random.Next(3) == 0 ? 0 : random.Next(0, digits.Length + 3);
        if (zero)
        {
            digits = new string('0', Math.Max(1, fractionLength));
        }

        var builder = new StringBuilder();
        if (value.Negative)
        {
            builder.Append('-');
        }

        if (fractionLength == 0)
        {
            builder.Append(digits);
        }
        else if (fractionLength < digits.Length)
        {
            builder.Append(digits, 0, digits.Length - fractionLength).Append('.').Append(digits, digits.Length - fractionLength, fractionLength);
        }
        else
        {
            builder.Append("0.").Append('0', fractionLength - digits.Length).Append(digits);
        }

        var written = exponent + fractionLength;
        if (!written.IsZero || random.Next(3) == 0)
        {
            builder.Append(random.Next(2) == 0 ? 'e' : 'E');
            var minus = written.Sign < 0 || (written.IsZero && random.Next(3) == 0);
            builder.Append(minus ? "-" : random.Next(3) == 0 ? "+" : string.Empty);
            builder.Append('0', random.Next(5) == 0 ? random.Next(1, 3) : 0);
            builder.Append(BigInteger.Abs(written).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
