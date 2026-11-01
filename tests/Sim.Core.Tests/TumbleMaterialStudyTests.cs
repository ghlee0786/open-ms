using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Environment;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>3분할 충격 → 컨테이너 텀블링 연구(TumbleStudy) · M 0.5 컨테이너 재질 연구(ContainerMaterialStudy).</summary>
public class TumbleMaterialStudyTests
{
    private static ScenarioDoc Load09()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("09_munition_3petal_longcontainer_real.json"));
        doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
        return doc;
    }

    [Theory]
    [InlineData(5, 10, 0)]
    [InlineData(14.9, 300, 0)]
    [InlineData(15, 20, 1)]
    [InlineData(89.9, 359, 1)]
    [InlineData(90, 120, 2)]
    [InlineData(170, 359.9, 2)]
    [InlineData(40, 360, 3)]
    public void Tumbling_class_thresholds(double aoa, double cum, int expected) =>
        Assert.Equal(expected, TumbleStudy.Classify(aoa, cum));

    [Fact]
    public void Drogue_attitude_class_thresholds()
    {
        Assert.Equal(0, TumbleStudy.DrogueClass(29.9));
        Assert.Equal(1, TumbleStudy.DrogueClass(30));
        Assert.Equal(2, TumbleStudy.DrogueClass(90));
        Assert.Equal(2, TumbleStudy.DrogueClass(double.NaN));
    }

    [DesignFact]
    public void Factor_application_scales_petal_dv_and_sets_tipoff_aoa_drogue()
    {
        var doc = Load09();
        var bl = TumbleStudy.ReadBaseline(doc);
        var d = TumbleStudy.Apply(doc, bl, new Dictionary<string, double>
        {
            ["dvScale"] = 2, ["petal1Factor"] = 0.5, ["tipRateDps"] = 90, ["tipDirDeg"] = 90, ["aoaDeg"] = 7, ["drogueDelay"] = 2.5,
            ["petal3DelayMs"] = 20, ["shockScale"] = 0, ["dampScale"] = 0.5,
        });
        var r = d.Root;
        var ev = r["events"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e["name"]!.GetValue<string>());
        var v1 = J.V3((JsonObject)ev["petal1_cut"]["do"]![0]!, "relVel", Vec3.Zero);
        var v2 = J.V3((JsonObject)ev["petal2_cut"]["do"]![0]!, "relVel", Vec3.Zero);
        Assert.Equal((bl.PetalVel[0] * 1.0).Norm, v1.Norm, 9);      // 2 × 0.5
        Assert.Equal((bl.PetalVel[1] * 2.0).Norm, v2.Norm, 9);
        Assert.Equal(bl.PetalT[0] + 0.020, ev["petal3_cut"]["when"]!["t"]!.GetValue<double>(), 9);
        var w = J.V3((JsonObject)ev["container_exposed"]["do"]!.AsArray().OfType<JsonObject>().First(a => a["type"]!.GetValue<string>() == "separate"), "relOmega", Vec3.Zero);
        Assert.Equal(0, w.Y, 9);
        Assert.Equal(Math.PI / 2, w.Z, 9);
        Assert.Equal(7, r["carrier"]!["pitch"]!.GetValue<double>());
        Assert.Equal(2.5, ev["lid_drogue"]["when"]!["delay"]!.GetValue<double>());
        var cont = r["bodies"]!.AsArray().OfType<JsonObject>().First(b => b["name"]!.GetValue<string>() == "container");
        Assert.Equal(bl.Damping.Y * 0.5, J.V3((JsonObject)cont["aero"]!, "damping", Vec3.Zero).Y, 9);
        // 원본 문서는 바뀌지 않는다
        Assert.Equal(bl.PetalVel[0].Norm, J.V3((JsonObject)doc.Root["events"]!.AsArray().OfType<JsonObject>().First(e => e["name"]!.GetValue<string>() == "petal1_cut")["do"]![0]!, "relVel", Vec3.Zero).Norm, 9);
    }

    [DesignFact]
    public void Nominal_run_measures_free_flight_window_and_drogue_attitude()
    {
        var doc = Load09();
        var bl = TumbleStudy.ReadBaseline(doc);
        var run = TumbleStudy.Execute(doc, bl, "nominal", new Dictionary<string, double>());
        Assert.Null(run.Error);
        Assert.InRange(run.TDrogue - run.TExposed, 0.99, 1.01);
        Assert.True(double.IsFinite(run.AoaAtDrogue) && run.AoaAtDrogue <= run.MaxAoaFree + 1e-6);
        Assert.InRange(run.MaxQ, 12000, 17000);                        // M 0.5 @ 1 km 동압 ≈ 15.7 kPa 이하
        Assert.True(run.MaxCrossQ <= run.MaxQ + 1e-6);
        Assert.Equal(TumbleStudy.Classify(run.MaxAoaFree, run.CumFree), run.Class);
    }

    [DesignFact]
    public void Zero_damping_tumbles_more_than_doubled_damping()
    {
        var doc = Load09();
        var bl = TumbleStudy.ReadBaseline(doc);
        var lo = TumbleStudy.Execute(doc, bl, "d0", new() { ["dampScale"] = 0, ["drogueDelay"] = 2 });
        var hi = TumbleStudy.Execute(doc, bl, "d2", new() { ["dampScale"] = 2, ["drogueDelay"] = 2 });
        Assert.Null(lo.Error); Assert.Null(hi.Error);
        Assert.True(lo.MaxAoaFree > hi.MaxAoaFree, $"감쇠 0: {lo.MaxAoaFree:F1}° vs ×2: {hi.MaxAoaFree:F1}°");
    }

    [DesignFact]
    public void Aero_db_swap_point_replaces_container_aero()
    {
        var db = Path.Combine(TestUtil.RepoRoot, "config", "aero_db", "container_rans_M0.50.json");
        var d = TumbleStudy.Prepare(Load09(), new TumbleStudyOptions { OutDir = "", AeroDb = db, Damping = new Vec3(-1, -3, -3) });
        var cont = d.Root["bodies"]!.AsArray().OfType<JsonObject>().First(b => b["name"]!.GetValue<string>() == "container");
        Assert.NotNull(cont["aero"]!["db"]);
        Assert.Equal(-3, J.V3((JsonObject)cont["aero"]!, "damping", Vec3.Zero).Y);
        Assert.True(TumbleStudy.ReadBaseline(d).HasDb);
    }

    [DesignFact]
    public void Tumble_study_small_run_writes_report_and_envelope()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_tumble_test_" + Guid.NewGuid().ToString("N")[..8]);
        var res = TumbleStudy.Generate(Load09(), new TumbleStudyOptions { OutDir = dir, McRuns = 12, Sweeps = false, Maps = false, Command = "test" });
        Assert.Equal(12, res.Mc.Count);
        Assert.All(res.Mc, r => Assert.Null(r.Error));
        Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        var env = J.ParseObject(File.ReadAllText(Path.Combine(dir, "envelope.json")));
        Assert.Equal("mns.tumble-envelope/1", env["schema"]!.GetValue<string>());
        Assert.True(env["maxQ_Pa"]!["mcMax"]!.GetValue<double>() > 10000);
    }

    // ---------------- 재질 연구 ----------------

    [Fact]
    public void Dynamic_pressure_at_1km_mach_half_matches_isa()
    {
        var f = ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50);
        // ISA 1 km: T 281.65 K, p 89,874.6 Pa → q = ½ γ p M² = 15,728 Pa
        Assert.Equal(281.65, f.T, 2);
        Assert.Equal(89874.6, f.P, 0);
        Assert.Equal(0.7 * 89874.6 * 0.25, f.Q, 0);
        Assert.Equal(f.Q, 0.5 * f.Rho * f.V * f.V, 6);
        // 압축성 정체점 Cp0(M 0.5) = 1.0641, 음속 Cp* = −2.1327
        Assert.Equal(1.0641, f.Cp0, 3);
        Assert.Equal(-2.1327, f.CpStar, 3);
        // 보정된 Cp 분포 적분 CD = TR R-300 CD2D
        Assert.Equal(f.Cd2D, ContainerMaterialStudy.Integrate(p => ContainerMaterialStudy.CrossflowCp(p, f.Cp0, f.CpStar, f.CpBase)).Cd, 3);
    }

    [Fact]
    public void Potential_flow_cp_integrates_to_zero_drag_and_c2_of_two()
    {
        // 퍼텐셜 원통 Cp = 1 − 4 sin²φ: CD = 0(달랑베르), c2 = 2
        var (cd, c2) = ContainerMaterialStudy.Integrate(p => 1 - 4 * Math.Sin(p) * Math.Sin(p));
        Assert.Equal(0, cd, 6);
        Assert.Equal(2, c2, 4);
        // 앞면만 정체압 1, 뒷면 0 → CD = ½∫_{−π/2}^{π/2} cosφ dφ = 1 (부호: 바람받이 압력 = 양의 항력)
        Assert.Equal(1, ContainerMaterialStudy.Integrate(p => Math.Cos(p) > 0 ? 1 : 0).Cd, 3);
        // 보정 분포는 기저압이 정체압보다 낮고 음속 하한 이상
        var f = ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50);
        Assert.InRange(f.CpBase, f.CpStar, 0);
    }

    [Fact]
    public void Sp8007_knockdowns_and_classical_buckling()
    {
        // R/t = 100: φ = 10/16 = 0.625 → γ = 1 − 0.901(1 − e^−0.625) = 1 − 0.901 × 0.4647 = 0.5813
        Assert.Equal(0.5813, ContainerMaterialStudy.GammaAxial(0.1, 0.001), 3);
        Assert.Equal(1 - 0.731 * (1 - Math.Exp(-0.625)), ContainerMaterialStudy.GammaBending(0.1, 0.001), 9);
        // 고전 축압축: 0.605 E t / R (ν 0.3)
        Assert.InRange(ContainerMaterialStudy.ClassicalAxial(70e9, 0.3, 0.1, 0.001) / (70e9 * 0.01), 0.604, 0.606);
        // 아주 긴 원통은 긴 원통식 E/(4(1−ν²))(t/R)³ 로 수렴
        // Timoshenko–Gere 단순지지 횡압: 매우 긴 원통은 n = 2, p → E/(4(1−ν²))·(t/R)³ (Bresse 링)
        var (pl, nl) = ContainerMaterialStudy.ExternalPressureTimoshenko(200e9, 0.3, 0.1, 0.001, 1000);
        Assert.Equal(2, nl);
        Assert.InRange(pl / (200e9 / (4 * 0.91) * 1e-6), 0.98, 1.02);
        // 예시 튜브(Ø0.30 × 1.50 m, E 50 GPa, t 2.5 mm): 중간 길이 → n = 3, 설계식(W–T)과 15 % 이내
        var (pm, nm) = ContainerMaterialStudy.ExternalPressureTimoshenko(50e9, 0.3, 0.14875, 0.0025, 1.50);
        Assert.Equal(3, nm);
        Assert.InRange(pm / ContainerMaterialStudy.ClassicalExternalPressure(50e9, 0.3, 0.14875, 0.0025, 1.50), 1.0, 1.25);
        double longP = 200e9 / (4 * 0.91) * Math.Pow(0.01, 3);
        Assert.Equal(longP, ContainerMaterialStudy.ClassicalExternalPressure(200e9, 0.3, 0.1, 0.001, 1000), 0);
    }

    [Fact]
    public void Material_study_analytic_ranks_cfrp_lighter_than_aluminium_and_buckling_scales()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_mat_test_" + Guid.NewGuid().ToString("N")[..8]);
        var res = ContainerMaterialStudy.Generate(new ContainerMaterialOptions { OutDir = dir, Command = "test" });
        Assert.Equal(6 * new ContainerMaterialOptions { OutDir = "" }.Thicknesses.Length, res.Evals.Count);
        Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        // 두께가 늘면 여유가 커진다(단조)
        foreach (var g in res.Evals.GroupBy(e => e.Mat.Key))
        {
            var m = g.OrderBy(e => e.T).Select(e => Math.Min(e.MinStrengthSf, 1 / e.MaxBucklingRatio)).ToList();
            for (int i = 1; i < m.Count; i++) Assert.True(m[i] >= m[i - 1] - 1e-9, $"{g.Key} 여유 비단조");
        }
        var cf = res.Evals.Where(e => e.Mat.Key == "cfrp-qi" && e.Pass).MinBy(e => e.T);
        var al = res.Evals.Where(e => e.Mat.Key == "al6061-t6" && e.Pass).MinBy(e => e.T);
        if (cf is not null && al is not null) Assert.True(cf.Mass < al.Mass);
        Assert.NotNull(MaterialLibrary.Find("Ti-6Al-4V"));
        Assert.NotNull(MaterialLibrary.Find("GFRP"));
        Assert.NotNull(MaterialLibrary.Find("AISI 4130"));
    }
}
