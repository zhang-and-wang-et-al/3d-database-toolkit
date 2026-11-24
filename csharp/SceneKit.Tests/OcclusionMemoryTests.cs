using SceneKit;
using Xunit;

[CollectionDefinition("serial", DisableParallelization = true)]
public class SerialCollection { }

/// <summary>Review #7: one depth buffer per worker, not one per scheduled task. Runs alone so the allocations are ours.</summary>
[Collection("serial")]
public class OcclusionMemoryTests
{
    [Fact]
    public void DepthBuffersStayWithinTheBudget()
    {
        var a = OcclusionTests.Box(-2, -2, -2, 2, 2, 2);
        var b = OcclusionTests.Box(-1, -1, -1, 1, 1, 1);
        const int R = 4096;
        long buffer = (long)R * R * 4;
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        Occlusion.Visible(new[] { new Occlusion.Item(a.V, a.T), new Occlusion.Item(b.V, b.T) }, new Occlusion.Options { Resolution = R });
        long used = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(used < (Environment.ProcessorCount + 2) * buffer, $"{used / (1 << 20)} MB allocated");
    }
}
