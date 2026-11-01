using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>
/// 부품 하나 CalculiX 정적 대조(mns loads --ccx): 실모델 조립 STEP 에서 바디 하나(가져온 순서 = massprops parts 순서, 부피로 확인)를
/// Gmsh C3D10 로 메시 → 고정 띠(좌표축 한쪽 범위 절점 전 자유도 구속) · 하중 띠(반대쪽 범위 절점에 하중 균등 분배) → 선형 정적.
/// 출력: 단위 하중당 최대 von Mises(응력 계수 [Pa/N], 닫힌 식과 비교) · 하중 띠 평균 변위로 강성 k = F/δ [N/m].
/// 좌표 = STEP(Fusion 조립 좌표, m). 접촉·볼트 예압·인접 부품 강성은 없음 — 고정 띠는 완전 고정(강성 과대, 응력은 보통 보수).
/// </summary>
public static class PartFea
{
    public sealed record Spec(string Step, int Body, double VolumeCm3, double Size, int FixAxis, double? FixMin, double? FixMax,
        int LoadAxis, double? LoadMin, double? LoadMax, Vec3 Dir, string Basis);

    /// <param name="CoefAwayPaPerN">고정 띠 경계(완전 고정 모서리 = 응력 특이점, 메시 의존)에서 요소 1.5개 넘게 떨어진 절점의 최대 von Mises / F —
    /// FR-FEA-04 특이점 경고: 판정은 이 값과 닫힌 식 중 큰 쪽, 경계 최대(CoefPaPerN)는 상한으로만.</param>
    public sealed record Result(bool Ok, string Error, double CoefPaPerN, double StiffnessNpm, double VolumeCm3, int Elements, int FixedNodes, int LoadNodes,
        Vec3 PeakAt, double Seconds, string Mesh, double CoefAwayPaPerN = double.NaN);

    public static Spec Parse(JsonObject o, string where)
    {
        var fix = o["fix"] as JsonObject ?? throw new InvalidDataException($"{where}.ccx.fix 가 필요합니다.");
        var load = o["load"] as JsonObject ?? throw new InvalidDataException($"{where}.ccx.load 가 필요합니다.");
        return new Spec(J.StrReq(o, "step", where), (int)J.NumReq(o, "body", where), J.Num(o, "volume_cm3", double.NaN), J.Num(o, "size", 0.001),
            (int)J.NumReq(fix, "axis", where), J.NumOpt(fix, "min"), J.NumOpt(fix, "max"),
            (int)J.NumReq(load, "axis", where), J.NumOpt(load, "min"), J.NumOpt(load, "max"),
            (J.V3Opt(o, "dir") ?? Vec3.UnitZ).Normalized(), J.Str(o, "basis", ""));
    }

    private static double Axis(Vec3 p, int a) => a switch { 0 => p.X, 1 => p.Y, _ => p.Z };

    private static bool InBand(Vec3 p, int axis, double? lo, double? hi)
    {
        double v = Axis(p, axis);
        return (lo is null || v >= lo.Value - 1e-9) && (hi is null || v <= hi.Value + 1e-9);
    }

    /// <summary>실행(F_ref = 1000 N 선형 — 결과는 단위 하중당 값).</summary>
    public static Result Run(Spec s, string realDir, Material mat, CalculixInstall inst, string workDir, int threads = 4)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var step = Path.IsPathRooted(s.Step) ? s.Step : Path.Combine(realDir, s.Step);
        if (!File.Exists(step)) return Fail($"STEP 이 없습니다: {step}");
        var (mesh, code, msg, sum) = GmshMesher.Mesh(step, s.Size, workDir, 2, TimeSpan.FromMinutes(10), s.Body);
        if (mesh is null) return Fail($"Gmsh 실패(코드 {code}): {msg}");
        double vol = sum?["bodyVolume_m3"] is JsonValue jv && jv.TryGetValue(out double v) ? v * 1e6 : double.NaN;
        if (double.IsFinite(s.VolumeCm3) && double.IsFinite(vol) && Math.Abs(vol / s.VolumeCm3 - 1) > 0.05)
            return Fail($"STEP 바디 {s.Body} 부피 {vol:F3} cm³ ≠ 기대 {s.VolumeCm3:F3} cm³ — 바디 번호(massprops parts 순서) 확인");
        var fixedN = mesh.NodesWhere(p => InBand(p, s.FixAxis, s.FixMin, s.FixMax));
        var loadN = mesh.NodesWhere(p => InBand(p, s.LoadAxis, s.LoadMin, s.LoadMax)).Except(fixedN).ToList();
        if (fixedN.Count < 3 || loadN.Count < 1) return Fail($"고정 절점 {fixedN.Count}개·하중 절점 {loadN.Count}개 — 띠 범위 확인");
        const double fRef = 1000;
        var m = new CcxModel { Mesh = mesh, Material = mat, Analysis = CcxAnalysis.Static, Title = $"part body {s.Body}" };
        foreach (var n in fixedN) m.Fixed.Add((n, 1, 3));
        var fn = s.Dir * (fRef / loadN.Count);
        foreach (var n in loadN) m.NodalForces.Add((n, fn));
        var r = CcxRunner.Solve(inst, m, workDir, "part", TimeSpan.FromMinutes(15), threads: threads);
        if (!r.Ok) return Fail($"CalculiX 실패: {r.Error}");
        double vmMax = 0, vmAway = 0; Vec3 at = Vec3.Zero;
        double edge = s.FixMax ?? s.FixMin ?? double.NaN;
        foreach (var (id, st) in r.Stress())
        {
            double vm = Tensor.VonMises(st);
            var pos = r.Frd is not null && r.Frd.Coords.TryGetValue(id, out var p) ? p : Vec3.Zero;
            if (vm > vmMax) { vmMax = vm; at = pos; }
            if (double.IsFinite(edge) && Math.Abs(Axis(pos, s.FixAxis) - edge) > 1.5 * s.Size && !InBand(pos, s.FixAxis, s.FixMin, s.FixMax)) vmAway = Math.Max(vmAway, vm);
        }
        var disp = r.Displacement();
        double dsum = 0; int dn = 0;
        foreach (var n in loadN) if (disp.TryGetValue(n + 1, out var d)) { dsum += Vec3.Dot(d, s.Dir); dn++; }
        double k = dn > 0 && dsum > 0 ? fRef / (dsum / dn) : double.NaN;
        return new Result(true, "", vmMax / fRef, k, vol, mesh.Elements.Count, fixedN.Count, loadN.Count, at, sw.Elapsed.TotalSeconds, mesh.Source, vmAway / fRef);

        Result Fail(string e) => new(false, e, double.NaN, double.NaN, double.NaN, 0, 0, 0, Vec3.Zero, sw.Elapsed.TotalSeconds, "");
    }
}
