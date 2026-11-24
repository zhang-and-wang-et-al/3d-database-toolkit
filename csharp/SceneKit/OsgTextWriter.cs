using System.Globalization;
using System.Text;

namespace SceneKit;

/// <summary>Serialises statement trees (and generated content) as .osgt or legacy .osg text.</summary>
public sealed class TextOut
{
    public readonly string Dialect;
    public TextWriter Sink;
    public readonly Dictionary<Obj, object?> Seen = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<string> usedNames = new();
    public int NextId = 1;
    public Dictionary<Obj, int>? UseCount;                 // legacy: how often each object is written
    public readonly Dictionary<Obj, int> Calls = new(ReferenceEqualityComparer.Instance);
    static readonly string[] Indents = Enumerable.Range(0, 64).Select(i => new string(' ', 2 * i)).ToArray();
    readonly bool osgt;

    public TextOut(string dialect, TextWriter sink)
    {
        Dialect = dialect; Sink = sink; osgt = dialect == "osgt";
    }

    public void Line(int depth, string text)
    {
        Sink.Write(depth < Indents.Length ? Indents[depth] : new string(' ', 2 * depth));
        Sink.Write(text);
        Sink.Write('\n');
    }

    void Indent(int depth) => Sink.Write(depth < Indents.Length ? Indents[depth] : new string(' ', 2 * depth));

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Fmt(Stmt s, int i) => s.IsQuoted(i) ? Quote(s.Toks[i]) : s.Toks[i];

    public static string Head(Stmt s)
    {
        if (s.Toks.Length == 1) return Fmt(s, 0);
        var sb = new StringBuilder();
        for (int i = 0; i < s.Toks.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(Fmt(s, i)); }
        return sb.ToString();
    }

    public void WriteStmts(List<Stmt> stmts, int depth)
    {
        foreach (var s in stmts) WriteStmt(s, depth);
    }

    public void WriteStmt(Stmt s, int depth)
    {
        string head = Head(s);
        switch (s.Block)
        {
            case null:
                Line(depth, osgt ? head + " " : head);
                break;
            case Num num:
                Line(depth, (head + " {").Trim());
                if (num.I != null) WriteNumbers(depth + 1, num.I, num.PerLine, osgt ? " " : "");
                else WriteNumbers(depth + 1, num.D!, num.PerLine, false, osgt ? " " : "");
                Line(depth, "}");
                break;
            case Obj o:
                WriteObj(s, o, depth);
                break;
            case List<Stmt> list:
                Line(depth, (head + " {").Trim());
                WriteStmts(list, depth + 1);
                Line(depth, "}");
                break;
        }
    }

    public void WriteObj(Stmt head, Obj obj, int depth, Action<int>? body = null) =>
        WriteObj(head.Toks.Length == 0 ? "" : Head(head), obj, depth, body);

    /// <summary>Write an object; <paramref name="body"/> overrides its statements.</summary>
    public void WriteObj(string headText, Obj obj, int depth, Action<int>? body = null)
    {
        bool first = !Seen.ContainsKey(obj);
        Calls[obj] = Calls.TryGetValue(obj, out var c) ? c + 1 : 1;
        if (osgt)
        {
            if (first) Seen[obj] = NextId++;
            int uid = (int)Seen[obj]!;
            if (obj.Style == ObjStyle.Image)
            {
                Line(depth, headText + " {");
                Line(depth + 1, $"ClassName {obj.Cls} ");
                Line(depth + 1, $"UniqueID {uid} ");
            }
            else
            {
                Line(depth, obj.Cls + " {");
                Line(depth + 1, $"UniqueID {uid} ");
            }
            if (first)
            {
                if (body != null) body(depth + 1);
                else WriteStmts(obj.Stmts, depth + 1);
            }
            Line(depth, "}");
            return;
        }
        // legacy .osg
        if (!first)
        {
            Line(depth, $"Use {(Seen[obj] as string) ?? "None"}");
            return;
        }
        bool shared = UseCount == null || (UseCount.TryGetValue(obj, out var uc) ? uc : 0) > 1;
        string? name = null;
        if (shared)
        {
            name = !string.IsNullOrEmpty(obj.Uid) && !usedNames.Contains(obj.Uid!) ? obj.Uid! : $"{ShortCls(obj.Cls)}_{NextId}";
            NextId++;
            Seen[obj] = name;
            usedNames.Add(name);
        }
        else Seen[obj] = null;
        Line(depth, obj.Cls + " {");
        if (name != null) Line(depth + 1, $"UniqueID {name}");
        if (body != null) body(depth + 1);
        else WriteStmts(obj.Stmts, depth + 1);
        Line(depth, "}");
    }

    static string ShortCls(string cls)
    {
        int i = cls.LastIndexOf("::", StringComparison.Ordinal);
        return i < 0 ? cls : cls[(i + 2)..];
    }

    // ------------------------------------------------------------------ numbers

    enum NumMode { Int, Short, F32 }

    /// <summary>Numbers as lines of <paramref name="perLine"/> values (Python format_numbers).</summary>
    public void WriteNumbers(int depth, ReadOnlySpan<double> a, int perLine, bool dbl, string suffix)
    {
        if (perLine <= 0) perLine = a.Length > 4 ? 4 : Math.Max(a.Length, 1);
        if (a.Length == 0) return;
        var mode = NumMode.Int;
        foreach (var v in a)
            if (!double.IsFinite(v) || v != Math.Round(v) || Math.Abs(v) >= 9007199254740992.0) { mode = NumMode.Short; break; }
        if (mode != NumMode.Int) mode = dbl || a.Length <= 64 ? NumMode.Short : NumMode.F32;
        for (int i = 0; i < a.Length; i += perLine)
        {
            Indent(depth);
            int e = Math.Min(i + perLine, a.Length);
            for (int k = i; k < e; k++)
            {
                if (k > i) Sink.Write(' ');
                double v = a[k];
                switch (mode)
                {
                    case NumMode.Int: WriteInt(v); break;
                    case NumMode.Short: Sink.Write(NumFmt.ShortFloat(v)); break;
                    default: WriteG9((float)v); break;
                }
            }
            Sink.Write(suffix);
            Sink.Write('\n');
        }
    }

    public void WriteNumbers(int depth, ReadOnlySpan<int> a, int perLine, string suffix)
    {
        if (perLine <= 0) perLine = a.Length > 4 ? 4 : Math.Max(a.Length, 1);
        Span<char> buf = stackalloc char[16];
        for (int i = 0; i < a.Length; i += perLine)
        {
            Indent(depth);
            int e = Math.Min(i + perLine, a.Length);
            for (int k = i; k < e; k++)
            {
                if (k > i) Sink.Write(' ');
                a[k].TryFormat(buf, out int w, default, CultureInfo.InvariantCulture);
                Sink.Write(buf[..w]);
            }
            Sink.Write(suffix);
            Sink.Write('\n');
        }
    }

    void WriteInt(double v)
    {
        Span<char> buf = stackalloc char[24];
        ((long)v).TryFormat(buf, out int w, default, CultureInfo.InvariantCulture);
        Sink.Write(buf[..w]);
    }

    /// <summary>"%.9g" of a float32 value without allocations (the hot path when saving big scenes).</summary>
    void WriteG9(float f)
    {
        double d = f;
        if (!double.IsFinite(d) || d == 0) { Sink.Write(PyFormat.G(d, 9)); return; }
        Span<char> e = stackalloc char[32];
        Span<char> r = stackalloc char[40];
        double a = Math.Abs(d);
        // a tie at the 10th digit is only possible when the shortest repr has exactly 10 digits ending in 5
        if (a.TryFormat(r, out int rn, "R", CultureInfo.InvariantCulture) && TieCandidate(r[..rn]))
        {
            Sink.Write(PyFormat.G(d, 9));
            return;
        }
        a.TryFormat(e, out int en, "E8", CultureInfo.InvariantCulture);   // d.dddddddddE+xxx, correctly rounded
        Span<char> digits = stackalloc char[9];
        digits[0] = e[0];
        for (int i = 0; i < 8; i++) digits[i + 1] = e[2 + i];
        int ep = 10;   // 'E'
        int x = int.Parse(e[(ep + 1)..en], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        Span<char> o = stackalloc char[40];
        int n = 0;
        if (d < 0) o[n++] = '-';
        if (x < 9 && x >= -4)
        {
            int point = x + 1;
            int last = 8;
            while (last > 0 && digits[last] == '0' && last >= point) last--;   // trailing zeros after the point
            if (point <= 0)
            {
                o[n++] = '0'; o[n++] = '.';
                for (int i = 0; i < -point; i++) o[n++] = '0';
                for (int i = 0; i <= last; i++) o[n++] = digits[i];
            }
            else
            {
                for (int i = 0; i < point; i++) o[n++] = digits[i];
                if (last >= point)
                {
                    o[n++] = '.';
                    for (int i = point; i <= last; i++) o[n++] = digits[i];
                }
            }
        }
        else
        {
            int last = 8;
            while (last > 0 && digits[last] == '0') last--;
            o[n++] = digits[0];
            if (last > 0) { o[n++] = '.'; for (int i = 1; i <= last; i++) o[n++] = digits[i]; }
            o[n++] = 'e';
            o[n++] = x < 0 ? '-' : '+';
            int ax = Math.Abs(x);
            if (ax < 10) o[n++] = '0';
            ax.TryFormat(o[n..], out int w, default, CultureInfo.InvariantCulture);
            n += w;
        }
        Sink.Write(o[..n]);
    }

    static bool TieCandidate(ReadOnlySpan<char> r)
    {
        int digits = 0;
        char lastDigit = '0';
        bool started = false;
        int trailingZeros = 0;
        foreach (char c in r)
        {
            if (c == 'E' || c == 'e') break;
            if (c < '0' || c > '9') continue;
            if (!started && c == '0') continue;
            started = true;
            digits++;
            if (c == '0') trailingZeros++; else { trailingZeros = 0; lastDigit = c; }
        }
        return digits - trailingZeros == 10 && lastDigit == '5';
    }
}
