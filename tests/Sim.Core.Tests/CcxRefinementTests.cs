using Sim.Core.Fea;

namespace Sim.Core.Tests;

/// <summary>
/// CalculiX(PaStiX 혼합 정밀도) 반복 정밀화 미수렴 감지 — ccx 는 미수렴이어도 'Job finished' 로 끝나므로 로그의 마지막 오차로 판정한다
/// (2026-10-11 A48 간헐 실패: 오차 7.65 로 끝난 실행의 반력 715 N). 로그 형식은 실측 ccx 2.23 / PaStiX 6.4.0 그대로.
/// </summary>
public class CcxRefinementTests
{
    private static string Log(params string[] errors) =>
        "  Factorization task:\n    Time to solve                         1.766200e-01 s\n" +
        string.Concat(errors.Select((e, i) => $"    - iteration {i + 1} :\n         total iteration time                   0.118 s\n         error                                  {e}\n")) +
        "    Time for refinement                   9.443939e+00 s\n Job finished\n";

    [Fact]
    public void Converged_log_reports_small_last_error()
    {
        var e = CcxRunner.LastRefinementError(Log("28.508", "0.58143", "1.5039e-09"));
        Assert.Equal(1.5039e-09, e!.Value, 15);
        Assert.True(e <= CcxRunner.RefinementTolerance);
    }

    [Fact]
    public void Stalled_log_is_flagged_unconverged()
    {
        var e = CcxRunner.LastRefinementError(Log("347.78", "120.82", "7.6518"));
        Assert.Equal(7.6518, e!.Value, 6);
        Assert.True(e > CcxRunner.RefinementTolerance);
    }

    [Fact]
    public void Log_without_pastix_refinement_has_no_error_value()
    {
        // SPOOLES · 모달 등 정밀화 기록이 없으면 null — 다시 풀지 않는다. '*ERROR' 줄은 정밀화 오차로 읽지 않는다.
        Assert.Null(CcxRunner.LastRefinementError(" Static analysis was selected\n *ERROR in something\n Job finished\n"));
    }
}
