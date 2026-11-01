using Sim.Core.Validation;
using Sim.Gpu;

namespace Sim.Core.Tests;

/// <summary>적대적 시나리오 자체 검증(PRD Y, VV-60/61): ≥ 10개, 전 항목 통과(UI 스모크는 앱 실행이 필요해 정보 항목).</summary>
public class AdversarialTests
{
    [Fact]
    public void All_adversarial_scenarios_pass()
    {
        var results = AdversarialSuite.Run(TestUtil.RepoRoot, () => GpuDropBackend.TryCreate(allowCpu: true, out _),
            smokeLog: Path.Combine(Path.GetTempPath(), "mns_no_smoke_log.txt"));
        Assert.True(results.Count >= 10);
        var cats = results.Select(r => r.Category[..1]).Distinct().Count();
        Assert.True(cats >= 10, $"범주 {cats}개");
        var failed = results.Where(r => r.Status == AdvStatus.Fail).ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(f => $"{f.Id} {f.Title}: {f.Measured} (기준 {f.Criterion})")));
        Assert.Contains("적대적", AdversarialSuite.Report(results));
    }
}
