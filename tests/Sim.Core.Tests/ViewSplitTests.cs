using Sim.App.Rendering;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>
/// 3D 뷰 1·2·4 분할: 배치 수학, GPU 버퍼 장부(메시 VBO 는 분할 수와 무관하게 물체마다 1벌 — 화면마다 늘어나는 것은 색 버퍼뿐),
/// 분할 화면 카메라 복사, 색 모드 표.
/// </summary>
public class ViewSplitTests
{
    [Theory]
    [InlineData(SplitLayout.Single, 1, 1, 1)]
    [InlineData(SplitLayout.Dual, 2, 1, 2)]
    [InlineData(SplitLayout.Quad, 4, 2, 2)]
    public void Layout_counts_and_grid(SplitLayout l, int panes, int rows, int cols)
    {
        Assert.Equal(panes, ViewSplit.PaneCount(l));
        Assert.Equal((rows, cols), ViewSplit.Grid(l));
        Assert.Equal(l, ViewSplit.FromCount(panes));
    }

    [Theory]
    [InlineData(SplitLayout.Single, 1201, 701)]
    [InlineData(SplitLayout.Dual, 1201, 701)]
    [InlineData(SplitLayout.Quad, 1201, 701)]
    [InlineData(SplitLayout.Quad, 3, 3)]
    public void Pane_rects_tile_the_view_without_gaps_or_overlap(SplitLayout l, int w, int h)
    {
        int n = ViewSplit.PaneCount(l);
        double area = 0;
        for (int p = 0; p < n; p++)
        {
            var (x, y, rw, rh) = ViewSplit.Rect(l, p, w, h);
            Assert.True(rw >= 1 && rh >= 1);
            Assert.True(x >= 0 && y >= 0 && x + rw <= w && y + rh <= h);
            area += rw * rh;
            // 사각형 가운데 점은 그 화면으로 판정
            Assert.Equal(p, ViewSplit.PaneAt(l, w, h, x + rw / 2, y + rh / 2));
        }
        Assert.Equal((double)w * h, area);
        // 화면 밖 좌표는 가장 가까운 칸
        Assert.Equal(0, ViewSplit.PaneAt(l, w, h, -50, -50));
        Assert.Equal(n - 1, ViewSplit.PaneAt(l, w, h, w + 50, h + 50));
    }

    [Fact]
    public void Quad_order_is_row_major()
    {
        Assert.Equal((0.0, 0.0, 600.0, 350.0), ViewSplit.Rect(SplitLayout.Quad, 0, 1200, 700));
        Assert.Equal((600.0, 0.0, 600.0, 350.0), ViewSplit.Rect(SplitLayout.Quad, 1, 1200, 700));
        Assert.Equal((0.0, 350.0, 600.0, 350.0), ViewSplit.Rect(SplitLayout.Quad, 2, 1200, 700));
        Assert.Equal((600.0, 0.0, 600.0, 700.0), ViewSplit.Rect(SplitLayout.Dual, 1, 1200, 700));
    }

    /// <summary>가짜 GPU — 할당한 버퍼 id 와 크기(바이트)를 기록.</summary>
    private sealed class FakeGpu
    {
        public readonly Dictionary<int, long> Live = new();
        public int Next = 1;
        public readonly int[] VertexCount;
        public FakeGpu(int[] vertexCount) => VertexCount = vertexCount;
        public long MeshBytes(SurfaceBufferCache<(int Id, bool Mesh, int Count)> c) => c.MeshBuffers.Sum(m => Live[m.Id]);
    }

    private static (SurfaceBufferCache<(int Id, bool Mesh, int Count)> Cache, FakeGpu Gpu) NewCache(int bodies, int verts = 3000)
    {
        var gpu = new FakeGpu(Enumerable.Repeat(verts, bodies).ToArray());
        var cache = new SurfaceBufferCache<(int Id, bool Mesh, int Count)>(
            body => { int id = gpu.Next++; gpu.Live[id] = gpu.VertexCount[body] * 6L * 4; return (id, true, gpu.VertexCount[body]); },
            (_, _, mesh) => { int id = gpu.Next++; gpu.Live[id] = mesh.Count * 3L * 4; return (id, false, mesh.Count); },
            b => Assert.True(gpu.Live.Remove(b.Id), "같은 버퍼를 두 번 해제"));
        return (cache, gpu);
    }

    /// <summary>GlView 가 프레임마다 하는 것과 같은 순서: 장면 업로드(물체마다 메시) → 화면마다 색칠된 물체의 색 버퍼.</summary>
    private static void RenderFrame(SurfaceBufferCache<(int Id, bool Mesh, int Count)> c, int bodies, int panes, int version, Func<int, int, bool> colored)
    {
        for (int i = 0; i < bodies; i++) c.Mesh(i);
        for (int p = 0; p < panes; p++)
            for (int i = 0; i < bodies; i++)
            {
                if (!colored(p, i)) continue;
                var (buf, stale) = c.Color(p, i, version);
                Assert.False(buf.Mesh);
                if (stale) c.MarkUploaded(p, i, version);
            }
    }

    [Fact]
    public void Mesh_buffers_do_not_grow_with_split_count()
    {
        const int bodies = 21;
        var (cache, gpu) = NewCache(bodies);
        var perLayout = new List<(int Panes, int Mesh, long MeshBytes, int Color)>();
        int version = 0;
        foreach (var l in new[] { SplitLayout.Single, SplitLayout.Dual, SplitLayout.Quad, SplitLayout.Dual, SplitLayout.Single, SplitLayout.Quad })
        {
            int n = ViewSplit.PaneCount(l);
            cache.TrimPanes(n);
            // 0번 화면 = 물체 색(색 버퍼 없음), 나머지 화면은 물체 9개를 색칠
            RenderFrame(cache, bodies, n, ++version, (p, i) => p > 0 && i < 9);
            RenderFrame(cache, bodies, n, version, (p, i) => p > 0 && i < 9); // 다음 프레임(같은 색) — 새 버퍼 없음
            perLayout.Add((n, cache.MeshBufferCount, gpu.MeshBytes(cache), cache.ColorBufferCount));
        }
        foreach (var (n, mesh, bytes, color) in perLayout)
        {
            Assert.Equal(bodies, mesh);
            Assert.Equal(bodies * 3000L * 6 * 4, bytes);
            Assert.Equal((n - 1) * 9, color);
        }
        // 분할을 여러 번 바꿔도 메시는 처음 한 번만 올렸다(복제·재업로드 없음)
        Assert.Equal(bodies, cache.MeshCreations);
        // 살아 있는 GPU 버퍼 = 메시 21 + 마지막 4분할의 색 27 — 해제 누락 없음
        Assert.Equal(bodies + 27, gpu.Live.Count);
        cache.Clear();
        Assert.Empty(gpu.Live);
    }

    [Fact]
    public void Color_buffer_is_per_pane_and_reuploads_only_on_version_change()
    {
        var (cache, _) = NewCache(2);
        var a = cache.Color(0, 1, 5);
        var b = cache.Color(1, 1, 5);
        Assert.True(a.Stale && b.Stale);
        Assert.NotEqual(a.Buf.Id, b.Buf.Id);
        cache.MarkUploaded(0, 1, 5);
        Assert.False(cache.Color(0, 1, 5).Stale);
        Assert.True(cache.Color(1, 1, 5).Stale); // 다른 화면의 색은 따로
        Assert.True(cache.Color(0, 1, 6).Stale);
        Assert.Equal(1, cache.MeshBufferCount); // 두 화면 색 버퍼가 같은 메시 버퍼 하나를 참조
        Assert.Equal(2, cache.ColorBufferCount);
    }

    [Fact]
    public void Pane_camera_copy_reproduces_view()
    {
        var a = new ViewCamera();
        a.SetViewport(600, 350);
        a.LookAt(new Vec3(120, -40, -300), 25);
        a.SetAngles(0.4, 0.7);
        a.PanPixels(30, -12);
        var b = new ViewCamera();
        b.CopyFrom(a);
        Assert.Equal(a.Eye, b.Eye);
        Assert.Equal(a.Focus, b.Focus);
        Assert.Equal(a.Forward, b.Forward);
        Assert.Equal(a.Distance, b.Distance);
        // 복사 뒤에는 따로 움직인다(동기 해제)
        b.Orbit(0.3, 0);
        Assert.NotEqual(a.Forward, b.Forward);
    }

    [Fact]
    public void Every_color_mode_has_one_table_row_and_parses_back()
    {
        foreach (var m in Enum.GetValues<ColorMode>())
        {
            var info = Assert.Single(ColorModeInfo.All, x => x.Mode == m);
            Assert.Equal(m, ColorModeInfo.Parse(info.Key));
            Assert.False(string.IsNullOrWhiteSpace(info.Label));
        }
        Assert.Equal(ColorModeInfo.All.Length, ColorModeInfo.All.Select(x => x.Key).Distinct().Count());
        Assert.Equal(ColorMode.Off, ColorModeInfo.Parse("없는모드"));
    }
}
