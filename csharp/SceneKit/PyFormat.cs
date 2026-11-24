using System.Globalization;
using System.Numerics;
using System.Text;

namespace SceneKit;

/// <summary>Number formatting that matches Python exactly (repr, %g, fixed with half-even rounding,
/// thousands separators), so files and reports are identical to the Python version.</summary>
public static class PyFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exact value of a finite double as (mantissa, binary exponent): v = m * 2^e.</summary>
    static (BigInteger m, int e, bool neg) Decompose(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        return (man, exp - 1075, neg);
    }

    /// <summary>round_half_even(|v| * 10^k) as an integer (k may be negative).</summary>
    static BigInteger ScaledRound(double v, int k)
    {
        var (m, e, _) = Decompose(v);
        BigInteger num = m, den = BigInteger.One;
        if (e >= 0) num <<= e; else den <<= -e;
        if (k >= 0) num *= BigInteger.Pow(10, k); else den *= BigInteger.Pow(10, -k);
        var q = BigInteger.DivRem(num, den, out var r);
        var twice = r * 2;
        int c = twice.CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
        return q;
    }

    static string Special(double v)
    {
        if (double.IsNaN(v)) return "nan";
        return v > 0 ? "inf" : "-inf";
    }

    /// <summary>Python f"{v:.{d}f}" (optionally with "," grouping).</summary>
    public static string Fixed(double v, int decimals, bool group = false)
    {
        if (!double.IsFinite(v)) return Special(v);
        bool neg = BitConverter.DoubleToInt64Bits(v) < 0;
        var n = ScaledRound(v, decimals);
        string digits = n.ToString(Inv);
        if (decimals > 0 && digits.Length <= decimals) digits = new string('0', decimals - digits.Length + 1) + digits;
        string ip = decimals > 0 ? digits[..^decimals] : digits;
        string fp = decimals > 0 ? digits[^decimals..] : "";
        if (group) ip = Group(ip);
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        sb.Append(ip);
        if (decimals > 0) sb.Append('.').Append(fp);
        return sb.ToString();
    }

    static string Group(string digits)
    {
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        int first = digits.Length % 3;
        if (first > 0) sb.Append(digits, 0, first);
        for (int i = first; i < digits.Length; i += 3)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(digits, i, 3);
        }
        return sb.ToString();
    }

    /// <summary>Python f"{n:,}" for integers.</summary>
    public static string Int(long n, bool group = true)
    {
        string s = Math.Abs((decimal)n).ToString(Inv);
        if (group) s = Group(s);
        return n < 0 ? "-" + s : s;
    }

    /// <summary>Python "%.{p}g" % v (C printf semantics, correctly rounded, ties to even).</summary>
    public static string G(double v, int p = 6, bool group = false)
    {
        if (!double.IsFinite(v)) return Special(v);
        if (p == 0) p = 1;
        bool neg = BitConverter.DoubleToInt64Bits(v) < 0;
        if (v == 0) return neg ? "-0" : "0";
        double a = Math.Abs(v);
        var (digits, x) = SigDigits(a, p);
        return GLayout(digits, x, p, neg, group);
    }

    /// <summary>|v| rounded to p significant digits: (exactly p digits, decimal exponent of the first).</summary>
    static (string digits, int x) SigDigits(double a, int p)
    {
        // fast: .NET formats correctly rounded, but ties go away from zero; C rounds ties to even.
        string e = a.ToString("E" + (p - 1).ToString(Inv), Inv);   // d.dddE+xxx
        int ep = e.IndexOf('E');
        string fast = e[..ep].Replace(".", "");
        int fx = int.Parse(e[(ep + 1)..], Inv);
        string r = a.ToString("R", Inv);
        int rp = r.IndexOfAny(new[] { 'E', 'e' });
        string rm = (rp < 0 ? r : r[..rp]).Replace(".", "").TrimStart('0').TrimEnd('0');
        if (rm.Length == p + 1 && rm[^1] == '5') return SigDigitsExact(a, p);   // possible tie: decide exactly
        return (fast, fx);
    }

    static (string digits, int x) SigDigitsExact(double a, int p)
    {
        int x = (int)Math.Floor(Math.Log10(a));
        BigInteger lo = BigInteger.Pow(10, p - 1), hi = BigInteger.Pow(10, p);
        while (true)
        {
            var n = ScaledRound(a, p - 1 - x);
            if (n >= hi) { x += 1; continue; }
            if (n < lo) { x -= 1; continue; }
            return (n.ToString(Inv), x);
        }
    }

    static string GLayout(string digits, int x, int p, bool neg, bool group)
    {
        string body;
        if (x < p && x >= -4)
        {
            int point = x + 1;   // digits before the decimal point
            string s;
            if (point <= 0) s = "0." + new string('0', -point) + digits;
            else if (point >= digits.Length) s = digits + new string('0', point - digits.Length);
            else s = digits[..point] + "." + digits[point..];
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            if (group)
            {
                int dot = s.IndexOf('.');
                string ip = dot < 0 ? s : s[..dot];
                s = Group(ip) + (dot < 0 ? "" : s[dot..]);
            }
            body = s;
        }
        else
        {
            string m = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "");
            if (m.Contains('.')) m = m.TrimEnd('0').TrimEnd('.');
            body = m + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", Inv);
        }
        return neg ? "-" + body : body;
    }

    /// <summary>Python repr(float): shortest round-trip digits, Python's layout.</summary>
    public static string Repr(double v)
    {
        if (!double.IsFinite(v)) return Special(v);
        if (v == 0) return BitConverter.DoubleToInt64Bits(v) < 0 ? "-0.0" : "0.0";
        bool neg = v < 0;
        string r = Math.Abs(v).ToString("R", Inv);   // shortest round-trip (".NET Core 3.0+")
        // split into digits and decimal exponent
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        int exp = 0;
        string mant = r;
        if (ePos >= 0) { exp = int.Parse(r[(ePos + 1)..], Inv); mant = r[..ePos]; }
        int dot = mant.IndexOf('.');
        string intPart = dot < 0 ? mant : mant[..dot];
        string frac = dot < 0 ? "" : mant[(dot + 1)..];
        string digits = (intPart + frac).TrimStart('0');
        int lead = (intPart + frac).Length - digits.Length;   // leading zeros removed
        // decimal point position relative to the start of 'digits'
        int decpt = intPart.Length - lead + exp;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        string body;
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0) body = "0." + new string('0', -decpt) + digits;
            else if (decpt >= digits.Length) body = digits + new string('0', decpt - digits.Length) + ".0";
            else body = digits[..decpt] + "." + digits[decpt..];
        }
        else
        {
            int x = decpt - 1;
            string m = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "");
            body = m + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", Inv);
        }
        return neg ? "-" + body : body;
    }

    /// <summary>Python str(int(v)) for integral doubles.</summary>
    public static string IntOf(double v)
    {
        if (Math.Abs(v) < 9.2e18) return ((long)v).ToString(Inv);
        return new BigInteger(v).ToString(Inv);
    }

    /// <summary>Python f"{v:.0%}" style percentages.</summary>
    public static string Percent(double v, int decimals) => Fixed(v * 100.0, decimals) + "%";
}

public static class NumFmt
{
    /// <summary>Python osgtext._short_float.</summary>
    public static string ShortFloat(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        if (v == Math.Truncate(v) && Math.Abs(v) < 1e15) return PyFormat.IntOf(v);
        string r = PyFormat.Repr(v);
        string f = PyFormat.G(v, 9);
        if (f.Length < r.Length)
        {
            double fv = double.Parse(f, System.Globalization.CultureInfo.InvariantCulture);
            if ((float)fv == (float)v && Math.Abs(fv - v) <= Math.Abs(v) * 1e-15) return f;
        }
        return r;
    }

    /// <summary>Python osgwrite._num.</summary>
    public static string Num(double v)
    {
        if (double.IsFinite(v) && v == Math.Truncate(v) && Math.Abs(v) < 1e15) return PyFormat.IntOf(v);
        return Math.Abs(v) < 1e16 ? PyFormat.Repr(v) : PyFormat.G(v, 6);
    }
}
