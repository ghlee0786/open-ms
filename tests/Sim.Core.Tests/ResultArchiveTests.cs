using System.IO.Compression;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>결과 저장 → 불러오기 왕복 동일성(VV-61 ⑧) · 손상·오류 처리.</summary>
public class ResultArchiveTests
{
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void Save_then_load_restores_frames_events_impacts_and_summary_exactly()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("04_fusion_model_drop.json"));
        var w = doc.Build();
        w.Run();
        var summary = Results.Summary(doc, w);
        var mc = new JsonObject { ["label"] = "test", ["points"] = new JsonArray(new JsonArray(1.0, 2.0), new JsonArray(3.0, 4.0)) };
        var path = TempFile("r.mnsr");
        ResultArchive.Save(path, doc, w, summary, monteCarlo: mc);

        var r = ResultArchive.Load(path, Path.Combine(Path.GetDirectoryName(path)!, "x"));
        var lw = r.World;
        Assert.Equal(w.Bodies.Count, lw.Bodies.Count);
        Assert.Equal(w.Recorder.Frames.Count, lw.Recorder.Frames.Count);
        for (int i = 0; i < w.Recorder.Frames.Count; i++)
        {
            var a = w.Recorder.Frames[i]; var b = lw.Recorder.Frames[i];
            Assert.Equal(a.T, b.T);
            Assert.Equal(a.Bodies, b.Bodies);           // record struct 값 비교(비트 단위 double)
            Assert.Equal(a.Devices, b.Devices);
            Assert.Equal(a.Connectors, b.Connectors);
        }
        Assert.Equal(w.EventLog, lw.EventLog);
        Assert.Equal(w.Warnings, lw.Warnings);
        Assert.Equal(w.T, lw.T);
        Assert.Equal(w.StopReason, lw.StopReason);
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            Assert.Equal(w.Bodies[i].Name, lw.Bodies[i].Name);
            Assert.Equal(w.Bodies[i].Impact?.Position, lw.Bodies[i].Impact?.Position);
            Assert.Equal(w.Bodies[i].Impact?.NoseAngleDeg, lw.Bodies[i].Impact?.NoseAngleDeg);
            Assert.Equal(w.Bodies[i].MaxLoadG, lw.Bodies[i].MaxLoadG);
            // 자기완결 시나리오: 질량특성·형상 동일
            Assert.Equal(w.Bodies[i].OwnMass, lw.Bodies[i].OwnMass, 12);
            Assert.Equal(w.Bodies[i].Shape?.Kind, lw.Bodies[i].Shape?.Kind);
        }
        Assert.Equal(summary.ToJsonString(), r.Summary.ToJsonString());
        Assert.Equal(2, r.MonteCarlo!["points"]!.AsArray().Count);
        Assert.Equal(ResultArchive.Schema, r.Manifest["schema"]!.GetValue<string>());
        // 불러온 시나리오는 원본 STL·massprops 없이 다시 실행 가능하고 같은 궤적을 낸다(결정성)
        var w2 = r.Doc.Build();
        w2.Run();
        Assert.Equal(w.T, w2.T, 9);
        var la = w.Recorder.Frames[^1].Bodies; var lb = w2.Recorder.Frames[^1].Bodies;
        for (int i = 0; i < la.Length; i++) Assert.True((la[i].Cg - lb[i].Cg).Norm < 1e-6, $"재실행 위치 차 {(la[i].Cg - lb[i].Cg).Norm}");
    }

    [Fact]
    public void Split_template_with_shell_segments_and_hinges_round_trips()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("06_munition_3petal_split.json"));
        doc.Root["sim"]!["tEnd"] = 1.5;
        var w = doc.Build();
        w.Run();
        var path = TempFile("split.mnsr");
        ResultArchive.Save(path, doc, w);
        var r = ResultArchive.Load(path);
        Assert.Equal(w.Recorder.Frames.Count, r.World.Recorder.Frames.Count);
        Assert.Equal(w.Recorder.Frames[^1].Connectors, r.World.Recorder.Frames[^1].Connectors);
        // 기록 시점 루트(재생 보간의 수납 물체 고정용)는 파일에 없지만 시나리오 부모 사슬로 같은 값이 복원된다(분리 전·후 프레임 모두)
        for (int i = 0; i < w.Recorder.Frames.Count; i++)
            Assert.Equal(w.Recorder.Frames[i].Bodies.Select(b => b.Root), r.World.Recorder.Frames[i].Bodies.Select(b => b.Root));
        Assert.Contains(w.Recorder.Frames[0].Bodies, b => b.Mode == Models.BodyMode.Stowed);
        Assert.Contains(r.World.EventLog, e => e.Name == "hinge1.released");
        Assert.Equal(3, r.World.Bodies.Count(b => b.Shape?.Kind == Geometry.ShapeKind.ShellSegment));
        Assert.Equal("al7075-t6", r.World.GetBody("petal1").Structure!.Material!.Key);
    }

    [Fact]
    public void Corrupt_missing_or_foreign_files_give_clear_errors()
    {
        Assert.Throws<FileNotFoundException>(() => ResultArchive.Load(TempFile("none.mnsr")));
        var junk = TempFile("junk.mnsr");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllText(junk, "not a zip");
        var e1 = Assert.Throws<InvalidDataException>(() => ResultArchive.Load(junk));
        Assert.Contains("손상", e1.Message);
        var foreign = TempFile("foreign.mnsr");
        using (var z = ZipFile.Open(foreign, ZipArchiveMode.Create))
        {
            using var s = new StreamWriter(z.CreateEntry("result.json").Open());
            s.Write("{\"schema\":\"other/9\"}");
        }
        var e2 = Assert.Throws<InvalidDataException>(() => ResultArchive.Load(foreign));
        Assert.Contains("schema", e2.Message);
        // frames.bin 잘림
        var doc = ScenarioDoc.Load(TestUtil.Scenario("01_single_sensor_drop.json"));
        var w = doc.Build(); w.Run();
        var good = TempFile("good.mnsr");
        ResultArchive.Save(good, doc, w);
        var cut = TempFile("cut.mnsr");
        using (var src = ZipFile.OpenRead(good))
        using (var dst = ZipFile.Open(cut, ZipArchiveMode.Create))
            foreach (var e in src.Entries)
            {
                using var i = e.Open();
                using var o = dst.CreateEntry(e.FullName).Open();
                if (e.FullName == "frames.bin") { var buf = new byte[200]; int n = i.Read(buf, 0, buf.Length); o.Write(buf, 0, n); }
                else i.CopyTo(o);
            }
        var e3 = Assert.Throws<InvalidDataException>(() => ResultArchive.Load(cut));
        Assert.Contains("frames.bin", e3.Message);
    }
}
