namespace Sim.Core.Models;

/// <summary>
/// 소형 프로펠러 축방향 하강 추력 모델(FR-UAV-02 v2) — 같은 회전수에서 하강 시 추력 / 정지 추력 = CT/CT0.
///  1) 고정 피치 균일유입 깃요소(BET) 추력 CT_r = (σ·a/2)(θ/3 − λ/2), λ = λc + λi, λc = J/π (J = V/(nD), 하강 음수).
///     θ 는 정지 CT0 를 재현하도록 정한다(정규화). 유도속도 λi 는 운동량 이론: 상승·정지 vi/vh = −x/2+√(x²/4+1),
///     윈드밀 상태(x ≤ −2) vi/vh = −x/2−√(x²/4−1), 그 사이(VRS)는 총유입 (Vc+vi)/vh = 1+x 로 잇는다(운동량 이론 무효 구간).
///  2) VRS 추력 손실: 경험적 가우시안 g(x) = 1 − D·exp(−((x−xc)/w)²), x = V/vh0, 정지에서 1 이 되도록 g(x)/g(0).
/// 계수 a_eff·D·xc·w 는 Shetty 2010 부록 B(UIUC, 4000 RPM) 중 TE10x5·SF9x4.7·SP10x5 로 보정했다(보정용, VV-50).
/// 나머지 5개 프로펠러가 검증용(`validation/public/rotor_vrs_uiuc`). 적용 범위: 0 ≤ V/vh0 ≤ 3.3, 9–11 in, Re 저(4000 RPM).
/// </summary>
public static class RotorModel
{
    public const double LiftSlope = 2.5, VrsDepth = 0.4, VrsCenter = 1.1, VrsWidth = 0.95;
    public const double MaxValidDescentRatio = 3.3;

    /// <summary>프로펠러 관례 CT(=T/ρn²D⁴) → 로터 관례 CT(=T/ρA(ΩR)²).</summary>
    public static double ToRotorCt(double ctProp) => ctProp * 4 / (Math.PI * Math.PI * Math.PI);

    private static double InducedRatio(double x)
    {
        if (x >= 0) return -x / 2 + Math.Sqrt(x * x / 4 + 1);
        if (x <= -2) return -x / 2 - Math.Sqrt(x * x / 4 - 1);
        return 1.0; // 총유입 = vh(1+x)
    }

    /// <summary>고정 피치 균일유입 BET 의 로터 관례 CT (축방향 전진비 J, 하강 음수).</summary>
    public static double BetCt(double j, double solidity, double theta, double a = LiftSlope)
    {
        double lc = j / Math.PI, ct = 0.01;
        for (int i = 0; i < 200; i++)
        {
            double lh = Math.Sqrt(Math.Max(ct, 1e-9) / 2);
            double li = lh * InducedRatio(lc / lh);
            double next = solidity * a / 2 * (theta / 3 - (lc + li) / 2);
            ct = 0.5 * ct + 0.5 * next;
        }
        return ct;
    }

    /// <summary>정지 CT0(로터 관례)를 재현하는 유효 피치각 θ [rad].</summary>
    public static double ThetaForHover(double ct0Rotor, double solidity, double a = LiftSlope) =>
        3 * (ct0Rotor / (solidity * a / 2) + Math.Sqrt(ct0Rotor / 2) / 2);

    /// <summary>하강비 x = V_down/vh0 (≥0) 에서 같은 회전수 추력비 T/T0. ct0Prop: 정지 CT(프로펠러 관례), solidity σ.</summary>
    public static double DescentThrustRatio(double x, double solidity, double ct0Prop)
    {
        if (x <= 0) return 1;
        double ct0r = ToRotorCt(ct0Prop);
        double theta = ThetaForHover(ct0r, solidity);
        double j = -x * Math.Sqrt(2 * ct0Prop / Math.PI);
        double baseRatio = BetCt(j, solidity, theta) / BetCt(0, solidity, theta);
        return baseRatio * VrsLoss(x) / VrsLoss(0);
    }

    /// <summary>전진비 J(하강 음수)로 표현한 CT/CT0.</summary>
    public static double CtRatioAtJ(double j, double solidity, double ct0Prop) =>
        DescentThrustRatio(-j / Math.Sqrt(2 * ct0Prop / Math.PI), solidity, ct0Prop);

    private static double VrsLoss(double x) => 1 - VrsDepth * Math.Exp(-Math.Pow((x - VrsCenter) / VrsWidth, 2));

    /// <summary>
    /// 정적 추력계수의 회전수(레이놀즈수) 의존: CT(n) = CT_ref·(n/n_ref)^p. p 는 UIUC propDB vol.1 APC 8×4·8×3.8 정적 시험
    /// 두 개로 정한 보정값(독립 검증 자료 없음 — VV-44 는 보정용). 일정 k_T(p = 0) 는 3000–7000 RPM 에서 CT 를 ±10 % 까지 틀린다.
    /// </summary>
    public const double CtRpmExponent = 0.165;

    public static double StaticCt(double ctRef, double rpmRef, double rpm, double exponent = CtRpmExponent) =>
        ctRef * Math.Pow(rpm / rpmRef, exponent);
}
