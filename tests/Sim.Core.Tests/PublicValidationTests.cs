using Sim.Core.Validation;

namespace Sim.Core.Tests;

/// <summary>공개 데이터 Validation(VV-49~51): 저장소 사례 자동 재현·판정 + 판정 규칙(VV-50).</summary>
public class PublicValidationTests
{
    private static string Root => Path.Combine(TestUtil.RepoRoot, "validation", "public");

    [Fact(DisplayName = "VV-49 저장소 공개 데이터 사례: 전부 재현되고 A 등급 실패 없음")]
    public void RepositoryCasesReproduce()
    {
        var results = PublicValidation.RunAll(Root);
        Assert.True(results.Count >= 4, $"사례 {results.Count}");
        foreach (var r in results)
        {
            Assert.NotEqual(CaseStatus.Unsupported, r.Status);
            Assert.NotEmpty(r.Points);
            Assert.True(r.Points.All(p => double.IsFinite(p.Predicted)), r.Id);
            Assert.NotEqual(CaseStatus.Fail, r.Status);
        }
        // 대기 표: 독립 자료 → 검증 통과여야 함
        Assert.Equal(CaseStatus.Pass, results.Single(r => r.Id == "atmosphere_us1976_table").Status);
        // 일관성·보정 사례도 허용치 안이어야 한다(모델 구현이 자료와 어긋나면 회귀)
        foreach (var r in results.Where(r => r.Status is CaseStatus.Calibration or CaseStatus.Consistency))
            Assert.True(r.AllWithin, $"{r.Id}: 최대 오차 {r.MaxAbsRelError:P1}");
    }

    private static string WriteCase(string dir, string id, string grade, string use, double measured, string kind = "atmosphere-table")
    {
        var d = Path.Combine(dir, id);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "case.json"), $$"""
        { "schema":"mns.validation-case/1", "id":"{{id}}", "title":"t", "vv":"VV-x", "grade":"{{grade}}", "use":"{{use}}",
          "kind":"{{kind}}", "model":"m", "tolerance":{"relative":0.01},
          "points":[{"label":"ρ0","inputs":{"altitude_m":0,"quantity":"density"},"measured":{{measured.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}] }
        """);
        return d;
    }

    [Fact(DisplayName = "VV-45 재현기 young-penetration = Penetration.Young (원뿔 N 포함)")]
    public void YoungKind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_vy_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "y"));
            File.WriteAllText(Path.Combine(dir, "y", "case.json"), """
            { "schema":"mns.validation-case/1", "id":"y", "grade":"A", "kind":"young-penetration", "tolerance":{"relative":0.3},
              "inputs":{"soilS":8,"nose":"cone","lOverD":2},
              "points":[{"label":"p","inputs":{"mass_kg":10,"diameter_m":0.05,"speed_mps":50},"measured":1.0}] }
            """);
            var r = PublicValidation.RunAll(dir).Single();
            var expect = Sim.Core.Models.Penetration.Young(10, Math.PI / 4 * 0.05 * 0.05, 50, 8, 0.25 * 2 + 0.56).Depth;
            Assert.Equal(expect, r.Points[0].Predicted, 12);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact(DisplayName = "VV-50 판정 규칙: A 불합격=실패, C 불합격=경고, 보정용은 통과 주장 안 함, 미지원 kind 표시")]
    public void GradingPolicy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_vv_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteCase(dir, "a_fail", "A", "validation", 1.5);
            WriteCase(dir, "b_pass", "B", "validation", 1.225);
            WriteCase(dir, "c_warn", "C", "validation", 1.5);
            WriteCase(dir, "d_calib", "A", "calibration", 1.225);
            WriteCase(dir, "e_unsup", "A", "validation", 1, "vrs-thrust-curve");
            var r = PublicValidation.RunAll(dir).ToDictionary(x => x.Id);
            Assert.Equal(CaseStatus.Fail, r["a_fail"].Status);
            Assert.Equal(CaseStatus.Pass, r["b_pass"].Status);
            Assert.Equal(CaseStatus.Warn, r["c_warn"].Status);
            Assert.Equal(CaseStatus.Calibration, r["d_calib"].Status);
            Assert.Equal(CaseStatus.Unsupported, r["e_unsup"].Status);

            var outDir = Path.Combine(dir, "reports");
            PublicValidation.WriteReports(r.Values.ToList(), dir, outDir);
            var summary = File.ReadAllText(Path.Combine(outDir, "SUMMARY.md"));
            Assert.Contains("대체할 수 없는 항목", summary);
            Assert.Contains("독립 검증 통과 사례: 1 / 5", summary);
            Assert.Contains("**초과**", File.ReadAllText(Path.Combine(outDir, "a_fail.md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
