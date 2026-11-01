using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Airflow;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>GPU 가상 풍동(앱 쪽): 좌표 · IPC 형식 · 프레임 해석 · 클라이언트(가짜 서버) · 재생 시퀀스 어댑터 · OpenFOAM 비교값.</summary>
public class AirflowTests
{
    [Fact]
    public void Body_to_tunnel_rotation_matches_python_and_windtunnel()
    {
        // tools/airflow/tests test_shared_vector_with_csharp 와 같은 값
        var r = AirflowFrames.BodyToTunnel(30, 10);
        var v = r * new Vec3(1, 2, 3);
        Assert.Equal(2.67737652, v.X, 6); Assert.Equal(1.55875951, v.Y, 6); Assert.Equal(2.09807621, v.Z, 6);
        foreach (var (a, b) in new[] { (0.0, 0.0), (45.0, 0.0), (90.0, 15.0), (-30.0, -40.0), (170.0, 5.0) })
        {
            var d = AirflowFrames.BodyToTunnel(a, b) * AirflowFrames.TravelDirection(a, b);
            Assert.Equal(1, d.X, 12); Assert.Equal(0, d.Y, 12); Assert.Equal(0, d.Z, 12);
        }
        var p = new Vec3(0.3, -0.2, 0.7);
        var w = WindTunnel.ToTunnel(p, 37, Vec3.Zero);
        var q = AirflowFrames.BodyToTunnel(37, 0) * p;
        Assert.True((w - q).Norm < 1e-12);
    }

    private static Dictionary<string, AirflowArray> FrameArrays() => new()
    {
        ["slice_speed"] = new("f4", [3, 2], F4: [1, 2, 3, float.NaN, 5, 6]),
        ["slice_vort"] = new("f4", [3, 2], F4: [0, 0.5f, -1, float.NaN, 0, 0]),
        ["particles"] = new("f4", [2, 4], F4: [0, 0, 0, 1, -1, 0.1f, 0, 0.8f]),
        ["lines"] = new("f4", [3, 4], F4: [1, 0, 0, 1, 0, 0, 0, 1.1f, -1, 0, 0, 1.2f]),
        ["line_counts"] = new("i4", [1], I4: [3]),
    };

    private static JsonObject FrameHeader() => new()
    {
        ["type"] = "frame", ["backend"] = "XLB", ["step"] = 1200, ["frame"] = 7, ["alpha"] = 45.0, ["beta"] = -5.0, ["speed"] = 168.0,
        ["re"] = 615.0, ["reMax"] = 615.0, ["reClamped"] = true, ["mach"] = 0.5, ["settling"] = false,
        ["coeffs"] = new JsonObject { ["CD"] = 7.5, ["CL"] = -5.1, ["CY"] = 0.2 }, ["coeffsMean"] = new JsonObject(),
        ["slice"] = new JsonObject { ["plane"] = "xz", ["origin"] = new JsonArray(1.0, 0.0, -2.0), ["axisA"] = new JsonArray(-6.0, 0.0, 0.0), ["axisB"] = new JsonArray(0.0, 0.0, 4.0) },
    };

    [Fact]
    public void Protocol_roundtrip_and_frame_parsing()
    {
        var bytes = AirflowProtocol.Encode(FrameHeader(), FrameArrays());
        var m = AirflowProtocol.Decode(bytes);
        Assert.Equal("frame", m.Type);
        var f = AirflowFrame.FromMessage(m);
        Assert.Equal(1200, f.Step); Assert.Equal(45, f.Alpha); Assert.Equal(-5, f.Beta); Assert.True(f.ReClamped);
        Assert.Equal(7.5, f.Coeffs!.Value.CD); Assert.Null(f.CoeffsMean);
        Assert.NotNull(f.Slice);
        Assert.Equal((3, 2), (f.Slice!.NA, f.Slice.NB));
        Assert.Equal(6, f.Slice.At(f.Slice.Speed, 2, 1));
        Assert.True(float.IsNaN(f.Slice.At(f.Slice.Speed, 1, 1)));
        Assert.Equal(new Vec3(-6, 0, 0), f.Slice.AxisA);
        Assert.Equal(2, f.ParticleCount);
        var lines = f.Streamlines().ToList();
        Assert.Single(lines);
        Assert.Equal(3, lines[0].P.Length); Assert.Equal(1.2, lines[0].S[2], 6);
        // 잘린 메시지 · 본문 밖 배열은 거부
        Assert.Throws<InvalidDataException>(() => AirflowProtocol.Decode(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void Decodes_python_layout_message()
    {
        // protocol.py pack 과 같은 배치: 머리 JSON(구분자 공백 없음) + f4 배열 2 개(little-endian)
        string json = """{"type":"frame","step":5,"arrays":[{"name":"particles","dtype":"f4","shape":[1,4],"offset":0,"nbytes":16},{"name":"slice_speed","dtype":"f4","shape":[1,1],"offset":16,"nbytes":4}]}""";
        var hb = Encoding.UTF8.GetBytes(json);
        var payload = new List<byte>();
        foreach (var v in new float[] { 1.5f, -2, 3, 0.25f, 9 }) payload.AddRange(BitConverter.GetBytes(v));
        var msg = new List<byte>();
        msg.AddRange(BitConverter.GetBytes((uint)hb.Length)); msg.AddRange(BitConverter.GetBytes((uint)payload.Count));
        msg.AddRange(hb); msg.AddRange(payload);
        var m = AirflowProtocol.Decode(msg.ToArray());
        Assert.Equal([1.5f, -2, 3, 0.25f], m.Arrays["particles"].F4!);
        var f = AirflowFrame.FromMessage(m);
        Assert.Equal(5, f.Step); Assert.Null(f.Slice);   // slice 머리가 없으면 단면 없음
        Assert.Empty(f.Lines);
    }

    [Fact]
    public void Inconsistent_line_counts_are_dropped()
    {
        var a = FrameArrays();
        a["line_counts"] = new("i4", [1], I4: [5]);
        var f = AirflowFrame.FromMessage(AirflowProtocol.Decode(AirflowProtocol.Encode(FrameHeader(), a)));
        Assert.Empty(f.LineCounts); Assert.Empty(f.Streamlines());
    }

    [Fact]
    public async Task Client_talks_to_fake_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var gotConfig = new TaskCompletionSource<JsonObject>();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            await AirflowProtocol.WriteAsync(s, new JsonObject { ["type"] = "hello", ["backend"] = "fake" });
            var cfg = await AirflowProtocol.ReadAsync(s);
            gotConfig.SetResult(cfg!.Header);
            await AirflowProtocol.WriteAsync(s, new JsonObject { ["type"] = "status", ["phase"] = "build", ["message"] = "격자" });
            await AirflowProtocol.WriteAsync(s, FrameHeader(), FrameArrays());
            await AirflowProtocol.WriteAsync(s, new JsonObject { ["type"] = "error", ["message"] = "발산", ["diverged"] = true, ["newRe"] = 369.0 });
            var quit = await AirflowProtocol.ReadAsync(s);
            Assert.Equal("quit", quit!.Type);
        });
        using var client = new AirflowClient();
        var frame = new TaskCompletionSource<AirflowFrame>();
        var err = new TaskCompletionSource<(string, bool, double?)>();
        var statuses = new List<string>();
        client.FrameReceived += f => frame.TrySetResult(f);
        client.Error += (m, d, r) => err.TrySetResult((m, d, r));
        client.Status += (p, m) => { lock (statuses) statuses.Add(p); };
        await client.ConnectAsync(port);
        await client.SendConfigAsync(new AirflowSettings { Mesh = "x.stl", Alpha = 30, Resolution = "medium" });
        var cfgSeen = await gotConfig.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("config", cfgSeen["cmd"]!.GetValue<string>());
        Assert.Equal(30, cfgSeen["alpha"]!.GetValue<double>());
        Assert.Equal("medium", cfgSeen["resolution"]!.GetValue<string>());
        var f = await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(45, f.Alpha);
        var (msg, diverged, newRe) = await err.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(diverged); Assert.Equal(369, newRe); Assert.Equal("발산", msg);
        Assert.Equal("fake", client.Hello!["backend"]!.GetValue<string>());
        lock (statuses) Assert.Contains("build", statuses);
        client.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
    }

    [Fact]
    public void Environment_reports_missing_python_with_setup_hint()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"af_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "tools", "airflow"));
        File.WriteAllText(Path.Combine(dir, "tools", "airflow", "server.py"), "");
        try
        {
            var prev = System.Environment.GetEnvironmentVariable("MNS_AIRFLOW_PYTHON");
            System.Environment.SetEnvironmentVariable("MNS_AIRFLOW_PYTHON", null);
            try
            {
                Assert.Null(AirflowEnvironment.Find(dir, out var why));
                Assert.Contains("requirements.txt", why);
            }
            finally { System.Environment.SetEnvironmentVariable("MNS_AIRFLOW_PYTHON", prev); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Stl_writer_roundtrips_through_loader()
    {
        var m = TriMesh.Box(new Vec3(1, 2, 3), new Vec3(0.5, 0, 0));
        var p = Path.Combine(Path.GetTempPath(), $"af_{Guid.NewGuid():N}.stl");
        try
        {
            AirflowEnvironment.WriteStl(m, p);
            var back = TriMesh.LoadStl(p);
            Assert.Equal(m.TriangleCount, back.TriangleCount);
            for (int i = 0; i < m.Vertices.Count; i++) Assert.True((m.Vertices[i] - back.Vertices[i]).Norm < 1e-6);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Flow_sequence_adapter_discovers_and_reads_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"af_{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "results", "cfd", "transient", "container_a45");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var h = FrameHeader();
                h.Remove("alpha");
                h["step"] = i;
                File.WriteAllBytes(Path.Combine(dir, $"f{i}.mnsf"), AirflowProtocol.Encode(h, FrameArrays()));
            }
            File.WriteAllText(Path.Combine(dir, "sequence.json"), """
                {"schema":"mns.flow-sequence/1","name":"container_a45","solver":"OpenFOAM v2512","method":"pimpleFoam URANS","alpha":45,"speed":168,"mach":0.5,
                 "frames":[{"t":0.002,"file":"f1.mnsf"},{"t":0.0,"file":"f0.mnsf"},{"t":0.004,"file":"f2.mnsf"}]}
                """);
            // 형식이 다른 sequence.json 은 이유와 함께 건너뜀
            var bad = Path.Combine(root, "config", "aero_db", "x");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "sequence.json"), """{"schema":"other"}""");
            var reasons = new List<string>();
            var seqs = FlowSequence.Discover(root, reasons);
            var s = Assert.Single(seqs);
            Assert.Single(reasons);
            Assert.Equal(3, s.Frames.Count);
            Assert.Equal(0.0, s.Frames[0].T);
            var f = s.ReadFrame(1);
            Assert.Equal(45, f.Alpha);            // 프레임에 없으면 시퀀스 값
            Assert.Equal(0.002, f.TimePhys);
            Assert.Equal(1, f.Step);              // f1.mnsf
            Assert.NotNull(f.Slice);
            Assert.Equal(0, s.IndexAt(0.001));
            Assert.Equal(0, s.IndexAt(0.0041));   // 반복 재생: 끝(0.004)을 넘으면 처음부터
            Assert.Equal(1, s.IndexAt(0.003));
            Assert.Equal(1, s.IndexAt(0.0039));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Flow_sequence_discovery_is_empty_without_data()
    {
        var root = Path.Combine(Path.GetTempPath(), $"af_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { Assert.Empty(FlowSequence.Discover(root)); }
        finally { Directory.Delete(root, true); }
    }

    [DesignFact]
    public void OpenFoam_reference_folds_angles()
    {
        Assert.Equal((45.0, 1), OpenFoamReference.Fold(45));
        Assert.Equal((45.0, -1), OpenFoamReference.Fold(-45));
        Assert.Equal((45.0, -1), OpenFoamReference.Fold(135));
        Assert.Equal((30.0, 1), OpenFoamReference.Fold(-150));
        var r = OpenFoamReference.Load(TestUtil.DesignFile("config/aero_db", "container_*_flow.json"));
        var a45 = r.At(45)!.Value;
        Assert.Equal(3.24061, a45.CD, 4); Assert.Equal(-2.02338, a45.CL, 4);
        Assert.Contains("M0.50", a45.Label);
        Assert.Equal(2.02338, r.At(-45)!.Value.CL, 4);
        Assert.Equal(2.02338, r.At(135)!.Value.CL, 4);
        var mid = r.At(52.5)!.Value;   // 45–60 사이 선형 보간
        Assert.Equal((3.24061 + 4.14904) / 2, mid.CD, 4);
        // 옆미끄럼만(α 0, β 90) → 전체 받음각 90°
        Assert.Equal(4.14214, r.At(0, 90)!.Value.CD, 4);
    }
}
