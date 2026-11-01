using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>진동 피로(근사): 공개 S-N 곡선 값 · 공진 여유 판정 · Miner 누적 · 부품 추정 · 텀블링 보고서 7b절.</summary>
public class VibrationFatigueTests
{
    [Fact]
    public void Sn_curves_match_hand_values_of_the_cited_fits()
    {
        // MMPDS-04 6061-T6: S_a 100 MPa = 14.504 ksi → S_eq = 2^0.63 × 14.504 = 22.45 ksi → log N = 20.68 − 9.84 × 1.3512 = 7.385 → N ≈ 2.43e7
        Assert.Equal(2.43e7, VibrationFatigue.Al6061T6.CyclesAt(100e6), 0.02 * 2.43e7);
        // Ince & Glinka 7075-T651: 300 MPa → 2N = (300/1576)^(−1/0.1609) = 3.00e4 → N ≈ 1.50e4
        Assert.Equal(1.50e4, VibrationFatigue.Al7075T6.CyclesAt(300e6), 0.02 * 1.50e4);
        // 응력이 클수록 수명이 짧다
        Assert.True(VibrationFatigue.Al6061T6.CyclesAt(150e6) < VibrationFatigue.Al6061T6.CyclesAt(100e6));
        Assert.Same(VibrationFatigue.Al6061T6, VibrationFatigue.ForMaterial("알루미늄 6061"));
        Assert.Same(VibrationFatigue.Al7075T6, VibrationFatigue.ForMaterial("Aluminum 7075-T6"));
        Assert.Null(VibrationFatigue.ForMaterial("CFRP"));
    }

    [Fact]
    public void Resonance_margin_verdicts()
    {
        Assert.Equal(25, VibrationFatigue.Amplification(1, 0.02), 9);    // 1/(2ζ)
        Assert.Equal(1, VibrationFatigue.Amplification(0, 0.02), 12);
        Assert.Equal(100, VibrationFatigue.SheddingHz(0.2, 150, 0.30), 9);
        var ok = VibrationFatigue.CheckResonance(100, 50, 70);
        Assert.False(ok.Crosses); Assert.Equal(0.7, ok.Ratio, 12); Assert.StartsWith("여유", ok.Verdict);
        Assert.StartsWith("근접", VibrationFatigue.CheckResonance(100, 85, 95).Verdict);
        var cross = VibrationFatigue.CheckResonance(100, 110, 90);           // 순서 무관
        Assert.True(cross.Crosses); Assert.Equal(1, cross.Ratio); Assert.StartsWith("공진 통과", cross.Verdict);
        var above = VibrationFatigue.CheckResonance(100, 120, 150);
        Assert.Equal(1.2, above.Ratio, 12); Assert.Contains("공진 위", above.Verdict);
    }

    [Fact]
    public void Miner_sum_and_part_estimate()
    {
        var c = VibrationFatigue.Al6061T6;
        double n1 = c.CyclesAt(120e6), n2 = c.CyclesAt(80e6);
        Assert.Equal(1.0, VibrationFatigue.MinerDamage([(120e6, n1 / 2), (80e6, n2 / 2), (0, 1e9)], c), 9);
        // 40 G 에서 40 MPa → 1 G 당 1 MPa, 변동 1 G, H = 1(r = 0) → σ_a = 1 MPa
        var p = VibrationFatigue.EstimatePart("Standoff", "알루미늄 6061", 40e6, 40, 1, 0, 0.02, 100, 2)!;
        Assert.Equal(1e6, p.SigmaA, 6);
        Assert.Equal(200, p.Cycles, 9);
        Assert.Equal(200 / c.CyclesAt(1e6), p.Damage, 15);
        Assert.Null(VibrationFatigue.EstimatePart("Plate", "CFRP", 40e6, 40, 1, 0, 0.02, 100, 2));
        Assert.Equal(Math.Sqrt(2) * 0.3 * 1000 * 0.3 * 1.5 / (10 * 9.80665), VibrationFatigue.LiftAccelAmplitudeG(0.3, 1000, 0.3, 1.5, 10), 12);
    }

    [Fact]
    public void Tumble_report_fatigue_section_uses_simpkg_modes_and_p99()
    {
        var run = new TumbleStudy.Run { Tag = "nominal", Values = new() };
        run.TExposed = 4; run.TDrogue = 6; run.MaxQ = 12000; run.MinSpeedFree = 120; run.MaxSpeedFree = 160;
        var bl = new TumbleStudy.Baseline { Exposed = "container_exposed", PetalVel = [], PetalOmega = [], PetalT = [], Diameter = 0.30, Length = 1.50, Mass = 30 };
        // 고유진동 입력 없음 → f_s 와 가속도만
        var noModes = TumbleStudy.FatigueLines(run, bl, null);
        Assert.Contains(noModes, l => l.Contains("구조 고유진동수 입력 없음"));
        var path = Path.Combine(Path.GetTempPath(), $"mns_fat_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, new JsonObject
        {
            ["project"] = "합성", ["study"] = "50G", ["acceleration_mps2"] = new JsonArray(0, 0, -490.33),
            ["modal"] = new JsonObject { ["boundary"] = "고정", ["modes"] = new JsonArray(new JsonObject { ["mode"] = 1, ["hz"] = 110.0, ["dominantBody"] = "Sensor" }) },
            ["bodies"] = new JsonArray(
                new JsonObject { ["body"] = "Standoff", ["material"] = "알루미늄 6061", ["fixed"] = false, ["p99VonMises_Pa"] = 160e6 },
                new JsonObject { ["body"] = "Plate", ["material"] = "CFRP", ["fixed"] = false, ["p99VonMises_Pa"] = 100e6 },
                new JsonObject { ["body"] = "Rail", ["material"] = "알루미늄 6061", ["fixed"] = true, ["p99VonMises_Pa"] = 300e6 }),
        }.ToJsonString());
        try
        {
            var lines = TumbleStudy.FatigueLines(run, bl, path);
            double fsLo = 0.20 * 120 / 0.30, fsHi = 0.256 * 160 / 0.30;    // 80.0–136.5 Hz → 110 Hz 통과
            Assert.Contains(lines, l => l.Contains($"{fsLo:F0}–{fsHi:F0} Hz"));
            Assert.Contains(lines, l => l.StartsWith("| 1 | 110.0 | Sensor | 1.00 | 공진 통과"));
            var sb = lines.Single(l => l.StartsWith("| Standoff"));
            double aHi = VibrationFatigue.LiftAccelAmplitudeG(0.3, 12000, 0.30, 1.50, 30);
            double sa = 160e6 / (490.33 / 9.80665) * aHi * 25;               // 통과 → H = 1/(2ζ) = 25
            double d = fsHi * 2 / VibrationFatigue.Al6061T6.CyclesAt(sa);
            Assert.Contains($"**{d:G3}**", sb);
            Assert.DoesNotContain(lines, l => l.StartsWith("| Plate") || l.StartsWith("| Rail"));   // S-N 없는 재료 · 고정 바디 제외
        }
        finally { File.Delete(path); }
    }
}
