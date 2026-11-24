using SceneKit;
using Xunit;

public class SceneTests
{
    const string Head = "#Ascii Scene \n#Version 161 \n#Generator OpenSceneGraph 3.6.5 \n\n";

    static string Quad(int uid, double x) => $@"osg::Geode {{
  UniqueID {uid}
  Drawables 1 {{
    osg::Geometry {{
      UniqueID {uid + 1}
      PrimitiveSetList 1 {{
        osg::DrawElementsUShort {{
          UniqueID {uid + 2}
          Mode TRIANGLES
          vector 6 {{
            0 1 2 0 2 3
          }}
        }}
      }}
      VertexArray TRUE {{
        osg::Vec3Array {{
          UniqueID {uid + 3}
          Binding BIND_PER_VERTEX
          vector 4 {{
            {x} 0 0
            {x + 1} 0 0
            {x + 1} 1 0
            {x} 1 0
          }}
        }}
      }}
    }}
  }}
}}";

    static string Write(string text)
    {
        var p = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}.osgt");
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void ReadAnalyzeAndRoundTrip()
    {
        var text = Head + $"osg::Group {{\n  UniqueID 1\n  Name \"root\"\n  Children 3 {{\n{Quad(10, 0)}\n{Quad(20, 2)}\n    osg::Geode {{\n      UniqueID 10\n    }}\n  }}\n}}\n";
        var p = Write(text);
        try
        {
            var sc = SceneIO.Load(p);
            Assert.Equal("root", sc.Root!.Name);
            Assert.Equal(3, sc.Root.Children.Count);
            Assert.Same(sc.Root.Children[0], sc.Root.Children[2]);   // shared geode
            var rep = Analyzer.Analyze(sc);
            Assert.Equal(6, rep.Total.Tris);                          // 2 + 2 (shared, drawn twice) + 2
            Assert.Equal(2, rep.Instances[sc.Root.Children[0]]);
            Assert.Equal(2, rep.Geoms.Count);
            Assert.Equal(rep.Geoms.Sum(g => g.Bytes), rep.Total.GeoBytes);   // shared memory counted once
            var outp = p + ".out.osgt";
            SceneIO.Save(sc, outp);
            var back = SceneIO.Load(outp);
            Assert.Same(back.Root!.Children[0], back.Root.Children[2]);
            Assert.Equal(6, Analyzer.Analyze(back).Total.Tris);
            File.Delete(outp);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ModifiedGeometryIsRegenerated()
    {
        var p = Write(Head + $"osg::Group {{\n  UniqueID 1\n  Children 1 {{\n{Quad(10, 0)}\n  }}\n}}\n");
        try
        {
            var sc = SceneIO.Load(p);
            var geo = sc.Root!.Children[0].Children[0];
            geo.Geom!.Prims[0] = new Primitive("TRIANGLES", "elements", indices: new[] { 0, 1, 2 }, indexType: "DrawElementsUShort");
            geo.Mark("geometry");
            var outp = p + ".out.osgt";
            SceneIO.Save(sc, outp);
            var back = SceneIO.Load(outp);
            Assert.Equal(1, Analyzer.Analyze(back).Total.Tris);
            File.Delete(outp);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void FlatSceneAnalysisIsFast()
    {
        var sb = new System.Text.StringBuilder(Head + "osg::Group {\n  UniqueID 1\n  Children 5000 {\n");
        for (int i = 0; i < 5000; i++) sb.Append(Quad(10 + i * 10, i)).Append('\n');
        sb.Append("  }\n}\n");
        var p = Write(sb.ToString());
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rep = Analyzer.Analyze(SceneIO.Load(p));
            Assert.Equal(10000, rep.Total.Tris);
            Assert.True(sw.Elapsed.TotalSeconds < 20);
        }
        finally { File.Delete(p); }
    }
}
