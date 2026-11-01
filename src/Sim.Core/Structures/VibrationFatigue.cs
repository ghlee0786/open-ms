namespace Sim.Core.Structures;

/// <summary>
/// S-N 곡선 하나(완전 반복 R = −1, 무노치): 응력 진폭 S_a [Pa] → 파손 반복 수 N. 근거 문헌과 식을 함께 들고 다닌다.
/// </summary>
public sealed record SnCurve(string Key, string Name, string Form, string Source, Func<double, double> CyclesAt);

/// <summary>
/// 진동 피로(근사) — 와류 흘림 진동수 f_s = St·V/D 와 구조 고유진동수 f_n 의 근접(공진 여유) 판정 + 공개 S-N 자료로 Miner 손상 추정.
/// 비정상 CFD · 진동 시험 없이 공개 범위로 크기를 가늠하는 용도(설계 판정 아님):
/// - 공진 여유: f_s 범위(속도 범위 × St 범위)가 f_n 을 지나면 '공진 통과', f_s &lt; 0.8 f_n 이면 '여유', 그 사이 '근접'.
///   0.8 은 ASME PTC 19.3 TW-2016(서모웰 와류 흘림)의 횡방향 주파수 제한(f_s &lt; 0.8 f_n)을 공개 해설에서 옮긴 값 — 표준 원문 미확인.
/// - 응력 진폭: σ_a = (σ_ref / a_ref) × a_dyn × H(r), σ_ref = 정적 해석 응력(그 해석 가속도 a_ref [G]), a_dyn = 변동 양력 가속도 진폭 [G],
///   H(r) = 1/√((1−r²)² + (2ζr)²) 1자유도 증폭(r = f_s/f_n, 공진 통과면 r = 1).
/// - 손상: D = n / N(σ_a), n = f_s × 노출 시간(한 진폭 블록 — 보수적으로 최대 진폭을 전 구간에).
/// </summary>
public static class VibrationFatigue
{
    private const double Ksi = 6.894757e6;

    /// <summary>
    /// 6061-T6: MMPDS-04(2008) Fig. 3.6.2.2.8 "Best-fit S/N curves for unnotched 6061-T6, wrought products"(Kt 1.0, 축하중, 55 시편, R −1~0.5):
    /// log N_f = 20.68 − 9.84 log S_eq, S_eq = S_max (1−R)^0.63 [ksi]. 완전 반복(R = −1, S_max = S_a) → S_eq = 2^0.63 S_a.
    /// </summary>
    public static readonly SnCurve Al6061T6 = new("al6061-t6", "알루미늄 6061-T6",
        "log N = 20.68 − 9.84·log S_eq, S_eq = 2^0.63·S_a [ksi] (R = −1)",
        "MMPDS-04 (2008) Fig. 3.6.2.2.8 — 6061-T6 단련재 무노치(Kt 1.0) 축하중 최적 맞춤 S/N, R −1~0.5, 55 시편",
        sa => Math.Pow(10, 20.68 - 9.84 * Math.Log10(Math.Pow(2, 0.63) * sa / Ksi)));

    /// <summary>
    /// 7075-T6: Basquin S_a = σ'_f (2N)^b, σ'_f = 1576 MPa, b = −0.1609 — Ince &amp; Glinka, Fatigue Fract. Engng Mater. Struct. 34 (2011) 854–867,
    /// Table 2(7075-T651, Zhao &amp; Jiang 2008 변형률 제어 시험의 응력-수명 항). T6 단련재 대신 T651 자료.
    /// </summary>
    public static readonly SnCurve Al7075T6 = new("al7075-t6", "알루미늄 7075-T6(T651 자료)",
        "S_a = 1576 MPa·(2N)^−0.1609",
        "Ince & Glinka (2011) Fatigue Fract. Engng Mater. Struct. 34:854–867, Table 2 — 7075-T651(Zhao & Jiang 2008) Basquin 항",
        sa => 0.5 * Math.Pow(sa / 1576e6, 1 / -0.1609));

    /// <summary>재료 이름(패키지 · 라이브러리)에 6061 · 7075 가 있으면 그 곡선, 아니면 null(공개 S-N 자료 없음 — 손상 추정 안 함).</summary>
    public static SnCurve? ForMaterial(string? name) =>
        name is null ? null : name.Contains("6061") ? Al6061T6 : name.Contains("7075") ? Al7075T6 : null;

    /// <summary>ASME PTC 19.3 TW-2016 횡방향 주파수 제한(공개 해설): f_s &lt; 0.8 f_n.</summary>
    public const double FrequencyLimit = 0.8;

    public static double SheddingHz(double strouhal, double speed, double diameter) => strouhal * speed / diameter;

    /// <summary>1자유도 정상 상태 증폭 H(r) = 1/√((1−r²)² + (2ζr)²).</summary>
    public static double Amplification(double r, double zeta) => 1 / Math.Sqrt(Math.Pow(1 - r * r, 2) + Math.Pow(2 * zeta * r, 2));

    /// <summary>변동 양력 가속도 진폭 [G]: √2·C'_L(rms)·q·D·L / (m·g).</summary>
    public static double LiftAccelAmplitudeG(double clRms, double q, double diameter, double length, double mass) =>
        Math.Sqrt(2) * clRms * q * diameter * length / (mass * 9.80665);

    public sealed record Resonance(double Fn, double FsLo, double FsHi, double Ratio, bool Crosses, string Verdict);

    /// <summary>f_s 범위 [fsLo, fsHi] 와 고유진동수 fn 의 근접 판정. Ratio = fn 에 가장 가까운 f_s/f_n(통과면 1).</summary>
    public static Resonance CheckResonance(double fn, double fsLo, double fsHi, double limit = FrequencyLimit)
    {
        if (fsLo > fsHi) (fsLo, fsHi) = (fsHi, fsLo);
        bool crosses = fsLo <= fn && fn <= fsHi;
        double ratio = crosses ? 1 : fsHi < fn ? fsHi / fn : fsLo / fn;
        string v = crosses ? "공진 통과(속도 범위 안에서 f_s = f_n)"
            : fsHi < limit * fn ? $"여유(f_s < {limit:0.0} f_n)"
            : fsHi < fn ? $"근접({limit:0.0} f_n ≤ f_s < f_n)"
            : "f_s > f_n 전 구간(공진 위)";
        return new Resonance(fn, fsLo, fsHi, ratio, crosses, v);
    }

    /// <summary>Miner 누적 손상 Σ n_i / N_i(σ_a ≤ 0 블록은 0).</summary>
    public static double MinerDamage(IEnumerable<(double Sa, double Cycles)> blocks, SnCurve c) =>
        blocks.Where(b => b.Sa > 0 && b.Cycles > 0).Sum(b => b.Cycles / c.CyclesAt(b.Sa));

    public sealed record PartEstimate(string Part, string Material, string Curve, double SigmaRef, double AccelRefG, double AccelDynG, double Ratio, double H,
        double SigmaA, double Cycles, double CyclesToFailure, double Damage);

    /// <summary>
    /// 부품 하나: σ_a = σ_ref/a_ref × a_dyn × H(ratio), n = fs × seconds, D = n/N(σ_a). S-N 곡선이 없는 재료는 null.
    /// </summary>
    public static PartEstimate? EstimatePart(string part, string material, double sigmaRef, double accelRefG, double accelDynG, double ratio, double zeta, double fs, double seconds)
    {
        if (ForMaterial(material) is not { } c || !(accelRefG > 0) || !(sigmaRef > 0)) return null;
        double h = Amplification(ratio, zeta);
        double sa = sigmaRef / accelRefG * accelDynG * h;
        double n = fs * seconds, nf = c.CyclesAt(sa);
        return new PartEstimate(part, material, c.Key, sigmaRef, accelRefG, accelDynG, ratio, h, sa, n, nf, n / nf);
    }
}
