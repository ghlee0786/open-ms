using System.Text.Json.Nodes;
using Sim.Core.Environment;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Validation;

namespace Sim.Core.Tests;

/// <summary>
/// 원자료(메인 validation/public, PRD FX-08) 기반 수정·추가 모델 검증: Young 원문 계수, US1976 지오퍼텐셜·고고도 층,
/// 지구 모델(WGS-84 J2·자전), 감쇠 축별 기준길이, 낙하산 겉보기 질량, VRS v2, 판정 체계(보정·점 등급·외삽).
/// </summary>
public class SourceDataValidationTests
{
    private static string Root => Path.Combine(TestUtil.RepoRoot, "validation", "public");

    // ---------------- Young (SAND97-2426) ----------------

    [Fact(DisplayName = "FX-02 Young 원문 계수: Thompson 시험 1(5.522 lb, 1.5625 in, 203 fps, S 11.14 원자료 역산) → 0.95 ft")]
    public void YoungMatchesSourceDerivedS()
    {
        Assert.Equal(0.3, Penetration.CoefLow);
        Assert.Equal(0.00178, Penetration.CoefHigh);
        double aIn2 = Math.PI / 4 * Math.Pow(0.1302 * 12, 2);
        double dFt = Penetration.YoungImperial(5.522, aIn2, 203, 11.14, 0.56);
        Assert.InRange(dFt, 0.94, 0.96); // 측정 0.95 ft
        // 저속식(시험 2: 185 fps, S 14.29) → 1.04 ft
        Assert.InRange(Penetration.YoungImperial(5.522, aIn2, 185, 14.29, 0.56), 1.02, 1.06);
    }

    // ---------------- 대기 ----------------

    [Fact(DisplayName = "US1976: 지오퍼텐셜 변환 시 표값(11 km 기하 0.3648), 32·47 km 층 경계 연속, 기본(근사)은 기존 값 유지")]
    public void AtmosphereGeopotentialAndUpperLayers()
    {
        var geo = new Atmosphere { Geopotential = true };
        Assert.Equal(0.36480, geo.At(11000).Density, 3);
        Assert.Equal(0.3639, new Atmosphere().At(11000).Density, 3);
        foreach (double hb in new[] { 20000.0, 32000.0, 47000.0, 51000.0 })
        {
            var (t1, p1) = Atmosphere.Standard(hb - 1e-6);
            var (t2, p2) = Atmosphere.Standard(hb + 1e-6);
            Assert.True(Math.Abs(t1 - t2) < 1e-6 && Math.Abs(p1 / p2 - 1) < 1e-5, $"{hb}: {t1}/{t2} {p1}/{p2}");
        }
        Assert.Equal(270.65, Atmosphere.Standard(48000).T, 6);
        Assert.True(Math.Abs(geo.At(40000).Density / 3.9957e-3 - 1) < 5e-3, $"{geo.At(40000).Density}"); // US1976 표 40 km
    }

    // ---------------- 지구 모델 ----------------

    [Fact(DisplayName = "FR-DYN-05 WGS-84 J2 + 원심: 적도 9.7803, 극 9.8322 m/s² (정규중력 대비 < 3e-4); 역제곱 = μ/R²")]
    public void EarthGravity()
    {
        var eq = new EarthModel { Gravity = "wgs84-j2", Rotation = true, LatitudeDeg = 0 }.GravityAt(0, 9.80665);
        Assert.Equal(9.7803253, eq.Z, 3);
        Assert.True(Math.Abs(eq.X) < 1e-9);
        var pole = new EarthModel { Gravity = "wgs84-j2", Rotation = true, LatitudeDeg = 90 }.GravityAt(0, 9.80665);
        Assert.True(Math.Abs(pole.Norm - 9.8321849) < 3e-4, $"{pole.Norm}");
        var r = new EarthModel { Gravity = "inverse-square", RoundRadius = 6371007.181 }.GravityAt(1000, 0);
        Assert.Equal(EarthModel.Mu / Math.Pow(6372007.181, 2), r.Z, 9);
        Assert.Equal(new Vec3(0, 0, 9.80665), new EarthModel().GravityAt(5000, 9.80665));
    }

    [Fact(DisplayName = "지구 자전: 적도 30 s 자유낙하 동쪽 편향 v_E = Ω·g·t², 관성 각속도 0 물체의 롤 = −Ω·t")]
    public void EarthRotationCoriolisAndAttitude()
    {
        var w = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.01,"tEnd":30}, "environment":{"vacuum":true,"earth":{"gravity":"constant","rotation":true,"latitude":0}},
          "bodies":[{"name":"b","mass":1,"inertia":[1,1,1],"aero":"none","position":[0,0,-20000]}] }
        """);
        w.Run();
        var b = w.GetBody("b");
        double expect = EarthModel.Omega * 9.80665 * 30 * 30;
        Assert.True(Math.Abs(b.V.Y / expect - 1) < 5e-3, $"{b.V.Y} vs {expect}");
        Assert.Equal(-EarthModel.Omega * 30, b.Q.ToEuler().X, 6);
        Assert.Equal(0, b.W.Norm, 12); // W 는 관성 각속도
    }

    // ---------------- 공력 감쇠 ----------------

    [Fact(DisplayName = "FR-AER-03 감쇠 축별 기준길이: M_i = ¼ρVS·l_i²·C_i·ω_i (p·b, q·c̄, r·b)")]
    public void DampingLengthsPerAxis()
    {
        var a = new AeroModel { Cd = 0, Area = 0.02, Damping = new Vec3(-1, -2, -3), DampingLengths = new Vec3(0.1, 0.2, 0.1) };
        var (_, m) = a.Compute(new Vec3(30, 0, 0), new Vec3(1, 1, 1), 1.0, Vec3.Zero);
        double k = 0.25 * 1.0 * 30 * 0.02;
        Assert.Equal(k * 0.01 * -1, m.X, 12);
        Assert.Equal(k * 0.04 * -2, m.Y, 12);
        Assert.Equal(k * 0.01 * -3, m.Z, 12);
    }

    // ---------------- 낙하산 겉보기 질량 ----------------

    private static double InfiniteMassPeak(double k0, double n)
    {
        var w = TestUtil.Build($$"""
        { "schema":"mns.scenario/1", "sim":{"dt":0.0005,"tEnd":{{n * 10 / 40 * 1.3 + 0.1}}}, "environment":{"constantDensity":1.2,"gravity":0},
          "bodies":[{"name":"b","mass":1e7,"inertia":[1e7,1e7,1e7],"aero":"none","position":[0,0,-1000],"velocity":[40,0,0],
            "devices":[{"type":"parachute","name":"c","d0":10,"cd0":0.5,"attach":[0,0,0],"lineStretchTime":0,"fillConstant":{{n}},"fillExponent":2,"apparentMassK0":{{k0}}}]}],
          "events":[{"name":"d","when":{"type":"time","t":0},"do":[{"type":"deploy","device":"b.c"}]}] }
        """);
        w.Run();
        return ((Parachute)w.GetBody("b").Devices[0]).MaxForce / (0.5 * 1.2 * 40 * 40 * 0.5 * Math.PI / 4 * 100);
    }

    [Fact(DisplayName = "FR-PAR-02 겉보기 질량 유입 항(Cruz 식 6): 무한 질량·j=2 에서 Cx = 1 + 2·k0/(n·CD0), k0=0 이면 1")]
    public void ApparentMassOpeningFactor()
    {
        Assert.InRange(InfiniteMassPeak(0, 8), 0.995, 1.005);
        double cx = InfiniteMassPeak(1.2, 8);
        Assert.Equal(1 + 2 * 1.2 / (8 * 0.5), cx, 2);
    }

    // ---------------- 로터 ----------------

    [Fact(DisplayName = "FR-UAV-02 v2 RotorModel: 정지 1, 보정 스크립트(파이썬)와 같은 값(TE10x7), x 증가 시 윈드밀 회복")]
    public void RotorVrsModel()
    {
        Assert.Equal(1, RotorModel.DescentThrustRatio(0, 0.08, 0.1), 12);
        double[] j = [-0.136, -0.176, -0.223, -0.262, -0.285];
        double[] py = [0.92039, 0.88593, 0.85884, 0.85786, 0.86954];
        for (int k = 0; k < j.Length; k++) Assert.Equal(py[k], RotorModel.CtRatioAtJ(j[k], 0.0811, 0.1047), 3);
        Assert.True(RotorModel.DescentThrustRatio(3.0, 0.08, 0.1) > 1.5);
        Assert.True(RotorModel.StaticCt(0.1, 5000, 7000) > 0.1 && RotorModel.StaticCt(0.1, 5000, 3000) < 0.1);
    }

    [Fact(DisplayName = "FR-UAV-02 Multirotor vrsModel uiuc-v2: 하강 중 추력 계수가 RotorModel 값")]
    public void MultirotorUsesV2()
    {
        var w = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.002,"tEnd":3},
          "bodies":[{"name":"d","mass":2,"inertia":[0.02,0.02,0.04],"aero":"none","position":[0,0,-100],"velocity":[0,0,4],
            "devices":[{"type":"multirotor","name":"m","maxThrust":60,"vrsModel":"uiuc-v2","propSolidity":0.09,"propCt0":0.1}]}],
          "events":[{"name":"a","when":{"type":"time","t":0},"do":[{"type":"activate","device":"d.m"},{"type":"set","device":"d.m","mode":"hold","targetAgl":100}]}] }
        """);
        for (int k = 0; k < 50; k++) w.Step();
        var m = (Multirotor)w.GetBody("d").Devices[0];
        Assert.True(m.VrsFactor < 0.999 || m.VrsFactor > 1.001, $"VrsFactor {m.VrsFactor}");
        Assert.InRange(m.VrsFactor, 0.5, 2.5);
    }

    // ---------------- 판정 체계 ----------------

    private static string TempCase(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_sd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "c"));
        File.WriteAllText(Path.Combine(dir, "c", "case.json"), json);
        return dir;
    }

    [Fact(DisplayName = "VV-50 보정/검증 분리: 그룹별 계수 이분법 보정 → 보정점 정확 재현, 같은 그룹 검증점은 그 계수로 예측")]
    public void CalibrationThenPrediction()
    {
        var dir = TempCase("""
        { "schema":"mns.validation-case/1", "id":"c", "grade":"A", "kind":"young-penetration", "tolerance":{"relative":0.3},
          "calibration":{"parameter":"soilS","lo":0.5,"hi":200},
          "inputs":{"nose":"flat","diameter_m":0.04},
          "points":[{"label":"cal","group":"T","role":"calibration","inputs":{"mass_kg":2.5,"speed_mps":60},"measured":0.3},
                    {"label":"val","group":"T","inputs":{"mass_kg":3.3,"speed_mps":60},"measured":0.4}] }
        """);
        try
        {
            var r = PublicValidation.RunAll(dir).Single();
            Assert.Equal(0.3, r.Points[0].Predicted, 6);
            Assert.Equal("calibration", r.Points[0].Role);
            double s = 0.3 / Penetration.Young(2.5, Math.PI / 4 * 0.0016, 60, 1, 0.56).Depth;
            Assert.Equal(Penetration.Young(3.3, Math.PI / 4 * 0.0016, 60, s, 0.56).Depth, r.Points[1].Predicted, 6);
            Assert.Contains(r.Notes, n => n.StartsWith("보정: soilS[T]"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact(DisplayName = "VV-50/VV-32 점 등급 B 불합격 = 경고, 적용 범위 밖 점만 있으면 외삽(합격/불합격 없음)")]
    public void PointGradeAndEnvelope()
    {
        var dir = TempCase("""
        { "schema":"mns.validation-case/1", "id":"c", "grade":"A", "kind":"young-penetration", "tolerance":{"relative":0.1},
          "inputs":{"nose":"flat","diameter_m":0.04,"soilS":10},
          "points":[{"label":"ok","inputs":{"mass_kg":3,"speed_mps":60},"measured":1.0,"grade":"B"}] }
        """);
        var dir2 = TempCase("""
        { "schema":"mns.validation-case/1", "id":"c", "grade":"A", "kind":"young-penetration", "tolerance":{"relative":0.1},
          "inputs":{"nose":"flat","diameter_m":0.04,"soilS":10},
          "points":[{"label":"light","inputs":{"mass_kg":1.0,"speed_mps":60},"measured":1.0}] }
        """);
        try
        {
            Assert.Equal(CaseStatus.Warn, PublicValidation.RunAll(dir).Single().Status);
            var r2 = PublicValidation.RunAll(dir2).Single();
            Assert.Equal(CaseStatus.OutOfEnvelope, r2.Status);
            Assert.False(r2.Points[0].Judged);
            Assert.Contains("외삽", PublicValidation.CaseMarkdown(r2));
        }
        finally { try { Directory.Delete(dir, true); Directory.Delete(dir2, true); } catch { } }
    }

    // ---------------- 저장소 변환 사례(원자료) ----------------

    [Fact(DisplayName = "FX-08 원자료 변환 사례: NASA check-case 1~8·Cruz 개방하중·UIUC VRS 통과, Young 보정 S = 원자료 역산 S")]
    public void SourceDataCases()
    {
        var results = PublicValidation.RunAll(Root).ToDictionary(r => r.Id);
        Assert.Equal(CaseStatus.Pass, results["nasa_6dof_checkcases"].Status);
        Assert.True(results["nasa_6dof_checkcases"].Points.Count >= 60);
        Assert.Equal(CaseStatus.Pass, results["parachute_dgb_opening_cruz2003"].Status);
        Assert.Equal(CaseStatus.Pass, results["rotor_vrs_uiuc"].Status);
        Assert.Equal(CaseStatus.OutOfEnvelope, results["parachute_dgb_flight_tmx1451"].Status);
        Assert.Equal(CaseStatus.OutOfEnvelope, results["sphere_free_fall_bacon_reid1924"].Status);
        var young = results["penetration_young_thompson1969"];
        Assert.NotEqual(CaseStatus.Fail, young.Status);
        // 원자료가 Young 식으로 역산한 S(측정 아님)와 같은 S 로 보정되어야 한다(계수·단위 변환 일치)
        foreach (var (target, s) in new[] { ("T2", 11.14), ("T3", 14.29), ("T6", 23.9), ("T7", 10.02), ("T8", 16.06) })
        {
            var note = young.Notes.Single(n => n.StartsWith($"보정: soilS[{target}]"));
            double v = double.Parse(note.Split('=')[1].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(v / s - 1) < 0.01, $"{target}: {v} vs {s}");
        }
        foreach (var r in results.Values.Where(r => J0(r.Directory) is { } o && o["origin"] is not null))
            Assert.NotEqual(CaseStatus.Unsupported, r.Status);
    }

    private static JsonObject? J0(string dir) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "case.json"))) as JsonObject;
}
