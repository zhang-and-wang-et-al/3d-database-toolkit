using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace SceneKit;

/// <summary>A block that only contains numbers (vertex data, indices, matrices). Either doubles or ints.</summary>
public sealed class Num
{
    public double[]? D;
    public int[]? I;
    public int PerLine;
    public Num(double[] d, int perLine) { D = d; PerLine = perLine; }
    public Num(int[] i, int perLine) { I = i; PerLine = perLine; }
    public int Length => D?.Length ?? I?.Length ?? 0;
    public double this[int k] => D != null ? D[k] : I![k];
    public double[] AsDoubles() => D ?? I!.Select(x => (double)x).ToArray();
}

/// <summary>One statement: tokens plus an optional block (null, List&lt;Stmt&gt;, Num or Obj).</summary>
public sealed class Stmt
{
    public string[] Toks;
    public bool[]? Quoted;      // which tokens were quoted strings (null = none)
    public object? Block;

    public Stmt(string[] toks, bool[]? quoted = null, object? block = null) { Toks = toks; Quoted = quoted; Block = block; }

    public string Key => Toks.Length > 0 ? Toks[0] : "";
    public bool IsQuoted(int i) => Quoted != null && i < Quoted.Length && Quoted[i];

    public void SetToken(int i, string value, bool quoted)
    {
        Toks[i] = value;
        if (quoted || Quoted != null)
        {
            Quoted ??= new bool[Toks.Length];
            Quoted[i] = quoted;
        }
    }

    public override string ToString() => $"<Stmt {string.Join(" ", Toks.Take(6))}{(Block == null ? "" : " {" + Block.GetType().Name + "}")}>";
}

public enum ObjStyle { Osgt, Image, Osg }

/// <summary>An object with identity. A shared object is the same Obj instance in several places.</summary>
public sealed class Obj
{
    public string Cls;
    public string? Uid;
    public List<Stmt> Stmts;
    public ObjStyle Style;

    public Obj(string cls, string? uid, List<Stmt> stmts, ObjStyle style) { Cls = cls; Uid = uid; Stmts = stmts; Style = style; }

    public Stmt? Get(string key)
    {
        foreach (var s in Stmts) if (s.Toks.Length > 0 && s.Toks[0] == key) return s;
        return null;
    }

    public string? Value(string key, string? dflt = null, int index = 1)
    {
        var s = Get(key);
        if (s == null || s.Toks.Length <= index) return dflt;
        return s.Toks[index];
    }

    public override string ToString() => $"<Obj {Cls} #{Uid}>";
}

public sealed class Document
{
    public string Dialect;
    public List<Stmt> Stmts;
    public List<string> Header;

    public Document(string dialect, List<Stmt> stmts, List<string> header) { Dialect = dialect; Stmts = stmts; Header = header; }

    public int Version
    {
        get
        {
            foreach (var line in Header)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "#Version")
                    return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            }
            return 0;
        }
    }

    public List<Obj> RootObjects() => Stmts.Where(s => s.Block is Obj).Select(s => (Obj)s.Block!).ToList();
}

/// <summary>Parser for .osgt (serializer format) and legacy .osg text, working directly on the file bytes.</summary>
public static unsafe class OsgParser
{
    public static Document ParseFile(string path, string? dialect = null, Action<string>? progress = null)
    {
        long n = new FileInfo(path).Length;
        if (n == 0) return ParseBytes(Array.Empty<byte>(), dialect, progress);
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            p += view.PointerOffset;
            return new Parser(p, n, progress).Run(dialect);
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    public static Document ParseText(string text, string? dialect = null) => ParseBytes(Encoding.UTF8.GetBytes(text), dialect, null);

    public static Document ParseBytes(byte[] data, string? dialect = null, Action<string>? progress = null)
    {
        fixed (byte* p = data) return new Parser(p, data.Length, progress).Run(dialect);
    }

    /// <summary>'osgt' when the text starts with '#Ascii' (after whitespace / BOM), else 'osg'.</summary>
    public static string SniffDialect(byte* p, long n)
    {
        long i = 0;
        if (n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF) i = 3;
        while (i < n && IsSpace(p[i])) i++;
        ReadOnlySpan<byte> asc = "#Ascii"u8;
        if (n - i < asc.Length) return "osg";
        for (int k = 0; k < asc.Length; k++) if (p[i + k] != asc[k]) return "osg";
        return "osgt";
    }

    public static string SniffDialect(string text)
    {
        var b = Encoding.UTF8.GetBytes(text.Length > 256 ? text[..256] : text);
        fixed (byte* p = b) return SniffDialect(p, b.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSpace(byte c) => c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n' || c == 0x0B || c == 0x0C || (c >= 0x1C && c <= 0x1F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsSpaceNoNl(byte c) => c != (byte)'\n' && IsSpace(c);

    sealed class Parser
    {
        readonly byte* p;
        readonly long n;
        readonly Action<string>? progress;
        readonly StringPool pool = new();
        readonly List<string> toks = new();
        readonly List<bool> quoted = new();
        bool anyQuoted;
        double[] numBuf = new double[1024];

        public Parser(byte* p, long n, Action<string>? progress) { this.p = p; this.n = n; this.progress = progress; }

        public Document Run(string? dialect)
        {
            dialect ??= SniffDialect(p, n);
            var header = new List<string>();
            long pos = 0;
            if (n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF) pos = 3;
            if (dialect == "osgt")
            {
                while (true)
                {
                    long i = pos;
                    while (i < n && (p[i] == ' ' || p[i] == '\t' || p[i] == '\r')) i++;
                    if (i < n && p[i] == '#')
                    {
                        long e = i;
                        while (e < n && p[e] != '\n') e++;
                        header.Add(Utf8(i, e).Trim());
                        pos = e < n ? e + 1 : e;
                        continue;
                    }
                    if (i < n && p[i] == '\n') { pos = i + 1; continue; }
                    break;
                }
            }
            var root = new List<Stmt>();
            var stack = new List<List<Stmt>> { root };
            bool legacy = dialect == "osg";
            Stmt? cur = null;
            long step = Math.Max(n / 50, 1 << 20), nextReport = progress != null ? step : long.MaxValue;
            while (pos < n)
            {
                if (pos >= nextReport)
                {
                    progress!($"parsing ... {pos * 100 / n} % ({pos / 1e6:N0} of {n / 1e6:N0} MB)");
                    nextReport = pos + step;
                }
                byte c = p[pos];
                if (c == '\n') { Finish(ref cur); pos++; continue; }
                if (IsSpace(c)) { pos++; continue; }
                if (c == '{')
                {
                    pos++;
                    var lst = stack[^1];
                    Stmt target;
                    if (cur == null)
                    {
                        if (lst.Count > 0 && lst[^1].Block == null) target = lst[^1];   // '{' on its own line (legacy .osg)
                        else { target = new Stmt(Array.Empty<string>()); lst.Add(target); }
                    }
                    else { Finish(ref cur, keepRef: true); target = cur!; }
                    cur = null;
                    var num = NumericBlock(pos, out long after);
                    if (num != null) { target.Block = num; pos = after; }
                    else
                    {
                        var b = new List<Stmt>();
                        target.Block = b;
                        stack.Add(b);
                    }
                    continue;
                }
                if (c == '}')
                {
                    pos++;
                    Finish(ref cur);
                    if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                string tok;
                bool q = false;
                if (c == '"')
                {
                    long e = pos + 1;
                    bool closed = false;
                    while (e < n)
                    {
                        if (p[e] == '\\') { e += 2; continue; }
                        if (p[e] == '"') { closed = true; break; }
                        e++;
                    }
                    if (!closed) { pos++; continue; }   // a lone quote matches nothing (skipped)
                    tok = Unescape(Utf8(pos + 1, e));
                    q = true;
                    pos = e + 1;
                }
                else
                {
                    long e = pos;
                    while (e < n) { byte b = p[e]; if (b == '{' || b == '}' || b == '"' || IsSpace(b)) break; e++; }
                    if (legacy && c == '/' && e - pos >= 2 && p[pos + 1] == '/')
                    {
                        while (pos < n && p[pos] != '\n') pos++;   // comment up to the end of the line
                        continue;
                    }
                    tok = Word(pos, e);
                    pos = e;
                }
                if (cur == null)
                {
                    cur = new Stmt(Array.Empty<string>());
                    stack[^1].Add(cur);
                }
                toks.Add(tok);
                quoted.Add(q);
                anyQuoted |= q;
            }
            Finish(ref cur);
            var doc = new Document(dialect, root, header);
            ResolveObjects(doc);
            return doc;
        }

        void Finish(ref Stmt? cur, bool keepRef = false)
        {
            if (cur != null)
            {
                cur.Toks = toks.ToArray();
                if (anyQuoted) cur.Quoted = quoted.ToArray();
            }
            toks.Clear(); quoted.Clear(); anyQuoted = false;
            if (!keepRef) cur = null;
        }

        string Utf8(long a, long b) => b <= a ? "" : Encoding.UTF8.GetString(p + a, checked((int)(b - a)));

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>A token; keywords, class names, enums and short tokens are kept once.</summary>
        string Word(long a, long b)
        {
            int len = (int)(b - a);
            byte f = p[a];
            bool alpha = (f >= 'a' && f <= 'z') || (f >= 'A' && f <= 'Z') || f >= 0x80;
            if ((alpha || len <= 2) && len <= 64) return pool.Get(p + a, len);
            return Utf8(a, b);
        }

        static bool NumStart(byte c) => (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.';

        /// <summary>If the block starting right after '{' at <paramref name="start"/> holds only numbers:
        /// the numbers and the position after the closing brace.</summary>
        Num? NumericBlock(long start, out long after)
        {
            after = start;
            long i = start;
            while (i < n && IsSpace(p[i])) i++;
            if (i >= n) return null;
            byte c = p[i];
            if (!NumStart(c) && !StartsWith(i, "nan"u8) && !StartsWith(i, "inf"u8)) return null;
            long end = i;
            while (end < n && p[end] != '}') end++;
            if (end >= n) return null;
            for (long k = start; k < end; k++) if (p[k] == '{' || p[k] == '"') return null;
            int count = 0;
            long q = start;
            int perLine = 0;
            bool firstLineDone = false;
            int lineCount = 0;
            while (true)
            {
                while (q < end && IsSpace(p[q]))
                {
                    if (p[q] == '\n' && lineCount > 0 && !firstLineDone) { perLine = lineCount; firstLineDone = true; }
                    q++;
                }
                if (q >= end) break;
                long e = q;
                while (e < end && !IsSpace(p[e])) e++;
                if (!FastFloat.TryParse(p + q, (int)(e - q), out double v)) return null;
                if (count == numBuf.Length) Array.Resize(ref numBuf, numBuf.Length * 2);
                numBuf[count++] = v;
                if (!firstLineDone) lineCount++;
                q = e;
            }
            if (count == 0) return null;
            if (!firstLineDone) perLine = lineCount;
            after = end + 1;
            // integral blocks (indices, lengths, counts) are kept as ints: half the memory, written the same way
            bool integral = true;
            for (int k = 0; k < count; k++)
            {
                double v = numBuf[k];
                if (!(v >= -2147483648.0 && v <= 2147483647.0) || v != Math.Floor(v)) { integral = false; break; }
            }
            Num num;
            if (integral)
            {
                var ints = new int[count];
                for (int k = 0; k < count; k++) ints[k] = (int)numBuf[k];
                num = new Num(ints, perLine);
            }
            else
            {
                var data = new double[count];
                Array.Copy(numBuf, data, count);
                num = new Num(data, perLine);
            }
            if (numBuf.Length > 1 << 16) numBuf = new double[1024];
            return num;
        }

        bool StartsWith(long i, ReadOnlySpan<byte> s)
        {
            if (n - i < s.Length) return false;
            for (int k = 0; k < s.Length; k++) if (p[i + k] != s[k]) return false;
            return true;
        }
    }

    /// <summary>Turn object blocks into Obj instances and resolve shared references (document order).</summary>
    static void ResolveObjects(Document doc)
    {
        var table = new Dictionary<string, Obj>();
        bool osgt = doc.Dialect == "osgt";
        var work = new Stack<(List<Stmt> lst, int i)>();
        work.Push((doc.Stmts, 0));
        while (work.Count > 0)
        {
            var (cur, i) = work.Pop();
            while (i < cur.Count)
            {
                var s = cur[i];
                i++;
                if (s.Block is not List<Stmt> b)
                {
                    if (osgt || !(s.Toks.Length == 2 && s.Toks[0] == "Use")) continue;
                    if (table.TryGetValue(s.Toks[1], out var refObj))
                    {
                        s.Toks = new[] { refObj.Cls };
                        s.Quoted = null;
                        s.Block = refObj;
                    }
                    continue;
                }
                if (osgt)
                {
                    if (s.Toks.Length == 1 && s.Toks[0].Contains("::") && b.Count > 0 && b[0].Toks.Length > 1 && b[0].Toks[0] == "UniqueID")
                    {
                        string uid = b[0].Toks[1];
                        if (!table.TryGetValue(uid, out var obj))
                        {
                            obj = new Obj(s.Toks[0], uid, b.GetRange(1, b.Count - 1), ObjStyle.Osgt);
                            table[uid] = obj;
                            s.Block = obj;
                            work.Push((cur, i));
                            work.Push((obj.Stmts, 0));
                            goto nextFrame;
                        }
                        s.Block = obj;
                        continue;
                    }
                    if (b.Count >= 2 && b[0].Toks.Length > 1 && b[0].Toks[0] == "ClassName" && b[1].Toks.Length > 1 && b[1].Toks[0] == "UniqueID")
                    {
                        string uid = b[1].Toks[1];
                        if (!table.TryGetValue(uid, out var obj))
                        {
                            obj = new Obj(b[0].Toks[1], uid, b.GetRange(2, b.Count - 2), ObjStyle.Image);
                            table[uid] = obj;
                            s.Block = obj;
                            work.Push((cur, i));
                            work.Push((obj.Stmts, 0));
                            goto nextFrame;
                        }
                        s.Block = obj;
                        continue;
                    }
                }
                else if (s.Toks.Length == 1)
                {
                    string? uid = null;
                    var rest = b;
                    for (int k = 0; k < b.Count; k++)
                    {
                        var sub = b[k];
                        if (sub.Toks.Length > 1 && sub.Toks[0] == "UniqueID" && sub.Block == null)
                        {
                            uid = sub.Toks[1];
                            rest = new List<Stmt>(b.Count - 1);
                            rest.AddRange(b.GetRange(0, k));
                            rest.AddRange(b.GetRange(k + 1, b.Count - k - 1));
                            break;
                        }
                    }
                    var obj = new Obj(s.Toks[0], uid, rest, ObjStyle.Osg);
                    if (uid != null) table[uid] = obj;
                    s.Block = obj;
                    work.Push((cur, i));
                    work.Push((obj.Stmts, 0));
                    goto nextFrame;
                }
                work.Push((cur, i));
                work.Push((b, 0));
                goto nextFrame;
            }
            nextFrame:;
        }
    }
}

/// <summary>Keeps one string instance per distinct short token (keyed by its UTF-8 bytes).</summary>
unsafe sealed class StringPool
{
    string?[] keys = new string?[4096];
    int count;

    public string Get(byte* s, int len)
    {
        uint h = 2166136261;
        bool ascii = true;
        for (int i = 0; i < len; i++) { h = (h ^ s[i]) * 16777619; if (s[i] >= 0x80) ascii = false; }
        if (!ascii) return Encoding.UTF8.GetString(s, len);
        int mask = keys.Length - 1;
        int slot = (int)(h & (uint)mask);
        while (true)
        {
            var k = keys[slot];
            if (k == null) break;
            if (k.Length == len)
            {
                bool eq = true;
                for (int i = 0; i < len; i++) if (k[i] != s[i]) { eq = false; break; }
                if (eq) return k;
            }
            slot = (slot + 1) & mask;
        }
        var str = Encoding.ASCII.GetString(s, len);
        if (count * 2 >= keys.Length) { Grow(); return Get(s, len); }
        keys[slot] = str;
        count++;
        return str;
    }

    void Grow()
    {
        var old = keys;
        keys = new string?[old.Length * 2];
        count = 0;
        int mask = keys.Length - 1;
        foreach (var k in old)
        {
            if (k == null) continue;
            uint h = 2166136261;
            foreach (char ch in k) h = (h ^ (byte)ch) * 16777619;
            int slot = (int)(h & (uint)mask);
            while (keys[slot] != null) slot = (slot + 1) & mask;
            keys[slot] = k;
            count++;
        }
    }
}

/// <summary>strtod-like float parsing of one token: correctly rounded, fast path for short decimals.</summary>
public static unsafe class FastFloat
{
    static readonly double[] Pow10 = Enumerable.Range(0, 23).Select(i => Math.Pow(10, i)).ToArray();

    public static bool TryParse(byte* s, int len, out double v)
    {
        v = 0;
        if (len <= 0) return false;
        int i = 0;
        bool neg = false;
        if (s[0] == '-' || s[0] == '+') { neg = s[0] == '-'; i++; }
        // nan / inf / infinity
        if (i < len && ((s[i] | 0x20) == 'n' || (s[i] | 0x20) == 'i'))
        {
            var rest = Encoding.ASCII.GetString(s + i, len - i).ToLowerInvariant();
            if (rest == "nan") { v = double.NaN; return true; }
            if (rest == "inf" || rest == "infinity") { v = neg ? double.NegativeInfinity : double.PositiveInfinity; return true; }
            return false;
        }
        ulong mant = 0;
        int digits = 0, exp10 = 0;
        bool anyDigit = false, overflow = false;
        while (i < len && s[i] >= '0' && s[i] <= '9')
        {
            anyDigit = true;
            if (digits < 19) { mant = mant * 10 + (ulong)(s[i] - '0'); if (mant != 0) digits++; }
            else { overflow = true; exp10++; }
            i++;
        }
        if (i < len && s[i] == '.')
        {
            i++;
            while (i < len && s[i] >= '0' && s[i] <= '9')
            {
                anyDigit = true;
                if (digits < 19) { mant = mant * 10 + (ulong)(s[i] - '0'); if (mant != 0) digits++; exp10--; }
                else overflow = true;
                i++;
            }
        }
        if (!anyDigit) return false;
        if (i < len && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            bool eneg = false;
            if (i < len && (s[i] == '-' || s[i] == '+')) { eneg = s[i] == '-'; i++; }
            if (i >= len || s[i] < '0' || s[i] > '9') return false;
            int e = 0;
            while (i < len && s[i] >= '0' && s[i] <= '9') { if (e < 100000) e = e * 10 + (s[i] - '0'); i++; }
            exp10 += eneg ? -e : e;
        }
        if (i != len) return false;
        // Clinger's fast path: exact when the mantissa fits in 53 bits and |exponent| <= 22
        if (!overflow && mant < (1UL << 53) && exp10 >= -22 && exp10 <= 22)
        {
            double d = mant;
            d = exp10 < 0 ? d / Pow10[-exp10] : d * Pow10[exp10];
            v = neg ? -d : d;
            return true;
        }
        var str = Encoding.ASCII.GetString(s, len);
        return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
