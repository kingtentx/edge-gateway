using System.Globalization;
using System.Numerics;

namespace EdgeGateway.Service;

// Decimal coefficient arithmetic avoids converting UInt64/Int64 (or a scaled decimal) to lossy double.
// Round-trip float text is bounded; malformed driver strings cannot allocate an unbounded BigInteger power.
internal readonly record struct ExactNumericValue(BigInteger Coefficient, int Exponent)
{
    public ExactNumericValue Abs() => new(BigInteger.Abs(Coefficient), Exponent);
    public static ExactNumericValue Max(ExactNumericValue a, ExactNumericValue b) => Compare(a, b) >= 0 ? a : b;
    public static int Compare(ExactNumericValue a, ExactNumericValue b)
    {
        var exponent = Math.Min(a.Exponent, b.Exponent);
        return (a.Coefficient * BigInteger.Pow(10, a.Exponent - exponent)).CompareTo(b.Coefficient * BigInteger.Pow(10, b.Exponent - exponent));
    }
    public static ExactNumericValue Distance(ExactNumericValue a, ExactNumericValue b)
    {
        var exponent = Math.Min(a.Exponent, b.Exponent);
        return new(BigInteger.Abs(a.Coefficient * BigInteger.Pow(10, a.Exponent - exponent)
            - b.Coefficient * BigInteger.Pow(10, b.Exponent - exponent)), exponent);
    }
    public static bool TryParse(object value, out ExactNumericValue number)
    {
        number = default;
        var text = value switch
        {
            double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
            float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024) return false;
        text = text.Trim();
        var index = text.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (index >= 0)
        {
            if (!int.TryParse(text[(index + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is < -1024 or > 1024) return false;
            text = text[..index];
        }
        var dot = text.IndexOf('.');
        if (dot >= 0) { exponent -= text.Length - dot - 1; text = text.Remove(dot, 1); }
        if (exponent is < -2048 or > 1024 || !BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var coefficient)) return false;
        number = new(coefficient, exponent);
        return true;
    }
}
