using System.Text.Json;
using SceneKit;
using Xunit;

public class FormatTests
{
    static List<JsonElement> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "pyformat_cases.json");
        return JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void MatchesPythonFormatting()
    {
        int n = 0;
        foreach (var c in Cases())
        {
            double v = BitConverter.Int64BitsToDouble(c.GetProperty("v").GetInt64());
            Assert.Equal(c.GetProperty("repr").GetString(), PyFormat.Repr(v));
            Assert.Equal(c.GetProperty("g9").GetString(), PyFormat.G(v, 9));
            Assert.Equal(c.GetProperty("g3").GetString(), PyFormat.G(v, 3));
            Assert.Equal(c.GetProperty("g6").GetString(), PyFormat.G(v, 6));
            Assert.Equal(c.GetProperty("f1").GetString(), PyFormat.Fixed(v, 1));
            Assert.Equal(c.GetProperty("f0g").GetString(), PyFormat.Fixed(v, 0, true));
            Assert.Equal(c.GetProperty("pct1").GetString(), PyFormat.Percent(v, 1));
            Assert.Equal(c.GetProperty("short").GetString(), NumFmt.ShortFloat(v));
            var num = c.GetProperty("num").GetString();
            if (!string.IsNullOrEmpty(num)) Assert.Equal(num, NumFmt.Num(v));
            var g9f = c.GetProperty("g9f32").GetString();
            if (!string.IsNullOrEmpty(g9f))
            {
                var sw = new StringWriter();
                var t = new TextOut("osgt", sw);
                var arr = Enumerable.Repeat((double)(float)v, 65).Append(0.5).ToArray();   // > 64 values: float32 mode
                t.WriteNumbers(0, arr, 1, false, "");
                Assert.Equal(g9f, sw.ToString().Split('\n')[0]);
            }
            n++;
        }
        Assert.True(n > 1000);
    }

    [Fact]
    public void ParsesNumbersLikeStrtod()
    {
        var doc = OsgParser.ParseText("#Ascii Scene\n#Version 161\n\nosg::Group {\n UniqueID 1\n M {\n 1 2.5 -3e2 .5\n 5. nan inf -inf\n }\n X {\n 1 2 x\n }\n}\n");
        var o = doc.RootObjects()[0];
        var m = (Num)o.Get("M")!.Block!;
        Assert.Equal(8, m.Length);
        Assert.Equal(4, m.PerLine);
        Assert.Equal(-300, m[2]);
        Assert.True(double.IsNaN(m[5]));
        Assert.IsType<List<Stmt>>(o.Get("X")!.Block);    // not all numbers: statements
    }
}
