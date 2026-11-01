using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>
/// 순응 접촉 구조 강성 CalculiX 교차 확인(mns loads --ccx): 접촉한 물체의 얇은 벽 이상화(tube·panel — <see cref="ContactStructure"/>)를
/// S8R 셸(중립면 R − t/2, 접촉점 쪽으로 조밀한 사상 격자)로 메시하고, 접촉점 패치에 법선 하중을 걸어 수동 관성 완화(<see cref="CcxBodyFea.BuildLoaded"/> —
/// 균일 밀도 d'Alembert 하중 + 3-2-1 정정 지지)로 푼다. 변위장에서 강체 운동(전 절점 최소제곱 병진·회전)을 빼고 패치 평균 법선 변위 / 하중 =
/// 구조 컴플라이언스 — 닫힌 식(같은 물체 요소 합: 준정적 전부 · 접촉 동안 포함 경로)과 비교한다. 요소 크기 두 단계(수렴 확인).
/// </summary>
public static class ContactFea
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Row(string Body, string Kind, Vec3 Point, Vec3 Normal, double PatchRadius, double Compliance, double ComplianceCoarse,
        int Elements, int ElementsCoarse, double ReactionResidual, bool Ok, string Error)
    {
        public double Time { get; init; } = double.NaN;
        public string Other { get; init; } = "";
        /// <summary>닫힌 식 같은 물체 요소 합 — 준정적 전부 / 접촉 동안 포함 경로만 [m/N].</summary>
        public double ClosedAll { get; init; } = double.NaN;
        public double ClosedIncluded { get; init; } = double.NaN;
        public double Ratio => ClosedAll > 0 && Compliance > 0 ? ClosedAll / Compliance : double.NaN;
        public double MeshChange => Compliance > 0 && ComplianceCoarse > 0 ? Compliance / ComplianceCoarse - 1 : double.NaN;

        public JsonObject ToJson() => new()
        {
            ["body"] = Body, ["kind"] = Kind, ["time_s"] = double.IsFinite(Time) ? Time : null, ["other"] = Other,
            ["point_brf_m"] = J.ToJson(Point), ["normal_brf"] = J.ToJson(Normal), ["patchRadius_mm"] = PatchRadius * 1e3,
            ["complianceFea_mpN"] = Fin(Compliance), ["kFea_Npm"] = Compliance > 0 ? 1 / Compliance : null,
            ["complianceFeaCoarse_mpN"] = Fin(ComplianceCoarse), ["meshChange"] = Fin(MeshChange), ["elements"] = Elements, ["elementsCoarse"] = ElementsCoarse,
            ["complianceClosedAll_mpN"] = Fin(ClosedAll), ["complianceClosedIncluded_mpN"] = Fin(ClosedIncluded), ["ratioClosedAllOverFea"] = Fin(Ratio),
            ["reactionResidual"] = Fin(ReactionResidual), ["ok"] = Ok, ["error"] = Error.Length > 0 ? Error : null,
            ["method"] = "S8R 셸(중립면) · 접촉점 패치 법선 하중 · 수동 관성 완화 + 3-2-1 지지 · 강체 운동(최소제곱) 제거 후 패치 평균 법선 변위/하중",
        };

        private static JsonNode? Fin(double v) => double.IsFinite(v) ? v : null;
    }

    /// <summary>[0,1] 사상 — u0 둘레로 조밀(국부 간격 = c·평균, 양쪽 2차 증가).</summary>
    public static double Cluster(double u, double u0, double c)
    {
        static double G(double s, double c) => c * s + (1 - c) * s * s;
        u0 = Math.Clamp(u0, 0, 1);
        if (u <= u0) return u0 <= 0 ? 0 : u0 - u0 * G((u0 - u) / u0, c);
        return u0 >= 1 ? 1 : u0 + (1 - u0) * G((u - u0) / (1 - u0), c);
    }

    /// <summary>얇은 벽 이상화 → S8R 중립면 메시(접촉점 p 쪽으로 조밀). 반환: 메시, p 의 중립면 투영, 국부 간격.</summary>
    public static (FeMesh Mesh, Vec3 PMid, double H) Mesh(ContactStructure s, Vec3 p, double w0, double refine = 1)
    {
        var sh = s.Shape; double t = s.Thickness, L = sh.Length, Rm = sh.Radius - t / 2;
        var (a, b, c) = Shape.AxisUnmap(sh.Axis, p - sh.Center);
        double th = Math.Atan2(b, a);
        bool panel = s.Kind == "panel";
        double beta = panel ? sh.SpanDeg * Math.PI / 360 : Math.PI, th0 = panel ? sh.CenterAngleDeg * Math.PI / 180 : th;
        double thLo = th0 - beta, span = 2 * beta;
        double dth = Math.Atan2(Math.Sin(th - thLo), Math.Cos(th - thLo));
        if (dth < 0) dth += 2 * Math.PI;
        double u0 = panel ? Math.Clamp(dth / span, 0, 1) : 0.5;
        double v0 = Math.Clamp((c + L / 2) / L, 0, 1);
        double h = Math.Max(Math.Max(t, w0), 0.002);
        int nu = (int)Math.Ceiling((panel ? 24 : 48) * refine), nv = (int)Math.Ceiling(48 * refine);
        double cu = Math.Clamp(h / (Rm * span / nu), 0.05, 1), cv = Math.Clamp(h / (L / nv), 0.05, 1);
        var mesh = FeMesh.Quad8(nu, nv, (u, v) =>
        {
            double ang = thLo + span * Cluster(u, u0, cu), x = -L / 2 + L * Cluster(v, v0, cv);
            return sh.Center + Shape.AxisMap(sh.Axis, Rm * Math.Cos(ang), Rm * Math.Sin(ang), x);
        }, FeType.S8R, t);
        if (!panel) mesh.WeldCoincident(Rm * 1e-7);
        var pm = sh.Center + Shape.AxisMap(sh.Axis, Rm * Math.Cos(th), Rm * Math.Sin(th), Math.Clamp(c, -L / 2, L / 2));
        return (mesh, pm, h);
    }

    /// <summary>한 물체 · 한 접촉점: 요소 두 단계(거친 = ×1, 고운 = ×1.5)로 풀어 고운 값을 쓴다.</summary>
    public static Row Run(ContactStructure s, Vec3 p, Vec3 n, double w0, CalculixInstall inst, string dir, int threads = 4)
    {
        n = n.Normalized();
        double comp = double.NaN, coarse = double.NaN, resid = double.NaN, rp = double.NaN;
        int ne = 0, neC = 0;
        try
        {
            foreach (var (refine, tag) in new[] { (1.0, "coarse"), (1.5, "fine") })
            {
                var (mesh, pm, h) = Mesh(s, p, w0, refine);
                rp = Math.Max(w0, s.Thickness);
                var mat = s.Material!;
                var fm = new BodyFeModel
                {
                    Mesh = mesh, Material = mat, BaseMaterial = mat, Shell = true, Display = new TriMesh(), Size = h, BodyName = s.Body, AttachRadius = rp,
                };
                const double F = 1000;
                var lc = new FeaLoadInput($"{s.Body} contact stiffness", 0, Vec3.Zero, Vec3.Zero, Vec3.Zero, [new PointLoad(pm, n * F, "contact")], [], [], rp);
                var (model, relief) = CcxBodyFea.BuildLoaded(fm, lc);
                var r = CcxRunner.Solve(inst, model, Path.Combine(dir, tag), "contact", TimeSpan.FromMinutes(20), default, threads);
                if (!r.Ok || r.Frd is null) throw new InvalidOperationException($"CalculiX 실패({tag}): {r.Error}");
                double c = ElasticCompliance(r.Frd.Coords, r.Displacement(), pm, n, rp + s.Thickness, F);
                if (tag == "coarse") { coarse = c; neC = mesh.Elements.Count; }
                else { comp = c; ne = mesh.Elements.Count; resid = r.Dat.ReactionTotal is { } rt ? rt.Norm / F : double.NaN; }
            }
            return new Row(s.Body, s.Kind, p, n, rp, comp, coarse, ne, neC, resid, true, "");
        }
        catch (Exception ex)
        {
            return new Row(s.Body, s.Kind, p, n, rp, comp, coarse, ne, neC, resid, false, ex.Message);
        }
    }

    /// <summary>
    /// 강체 운동(전 절점 같은 가중 최소제곱: 병진 T = 평균 변위, 회전 θ = I⁻¹ Σ r × (u − T))을 뺀 탄성 변위의 패치(점 pm 반경 r 안 절점) 평균 · n / F.
    /// </summary>
    public static double ElasticCompliance(IReadOnlyDictionary<int, Vec3> coords, Dictionary<int, Vec3> disp, Vec3 pm, Vec3 n, double r, double F)
    {
        var ids = disp.Keys.Where(coords.ContainsKey).ToList();
        if (ids.Count < 4) return double.NaN;
        var c = ids.Aggregate(Vec3.Zero, (s, i) => s + coords[i]) / ids.Count;
        var T = ids.Aggregate(Vec3.Zero, (s, i) => s + disp[i]) / ids.Count;
        double i11 = 0, i22 = 0, i33 = 0, i12 = 0, i13 = 0, i23 = 0; var m = Vec3.Zero;
        foreach (var i in ids)
        {
            var q = coords[i] - c;
            i11 += q.Y * q.Y + q.Z * q.Z; i22 += q.X * q.X + q.Z * q.Z; i33 += q.X * q.X + q.Y * q.Y;
            i12 -= q.X * q.Y; i13 -= q.X * q.Z; i23 -= q.Y * q.Z;
            m += Vec3.Cross(q, disp[i] - T);
        }
        var I = new Mat3(i11, i12, i13, i12, i22, i23, i13, i23, i33);
        var th = I.Inverse() * m;
        var patch = ids.Where(i => (coords[i] - pm).Norm <= r).ToList();
        if (patch.Count == 0) patch = ids.OrderBy(i => (coords[i] - pm).NormSq).Take(4).ToList();
        double u = patch.Average(i => Vec3.Dot(disp[i] - T - Vec3.Cross(th, coords[i] - c), n));
        return u / F;
    }
}
