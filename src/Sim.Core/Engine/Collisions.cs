using System.Text.Json.Nodes;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Engine;

/// <summary>
/// 충돌·간섭 기록. Kind: contact(물체–물체 접촉) | canopy(낙하산 캐노피 구 안으로 잔해 진입) | lines(라인 원뿔 영역 진입).
/// contact 만: Normal = 접촉 법선(NED, A → B), PointA·PointB = 접촉점의 A·B 동체 좌표(BRF), Depth = 침투 깊이 [m],
/// J = B 에 준 충격량 벡터(NED, A 에는 −J), RelVel = 접촉점 상대속도 v_B − v_A (NED), Via = 판정 근거("B점→A형상" = B 의 대표점이 A 형상 안).
/// </summary>
public sealed record CollisionRecord(double Time, string A, string B, string Kind, double RelSpeed, double Impulse, Vec3 Point,
    Vec3 Normal = default, Vec3 PointA = default, Vec3 PointB = default, double Depth = 0, Vec3 J = default, Vec3 RelVel = default, string Via = "",
    ContactPulse? Pulse = null);

/// <summary>접촉 충격량 한 번(루트 물체 rb 에 +J, ra 에 −J, 작용점 NED) — 6DOF 는 속도를 접촉 시작 순간에 바꾼다(가속도 이력에 안 남음).
/// 하중 연구(PayloadShock)가 접촉력 이력으로 되살린다: Pulse(순응 접촉 — 구조 강성으로 적분한 F(t)) 가 있으면 그 모양, 없으면(충격량 모델) 가정 지속 τ 반 사인.</summary>
public readonly record struct ContactImpulse(double Time, Body RootA, Body RootB, Vec3 J, Vec3 Point, ContactPulse? Pulse = null);

/// <summary>
/// 잔해 간섭 → 낙하산 전개 이상 모델(FR-AN-03, collisions.chuteAnomaly). 값은 모두 (가정) — 시험 근거 없음:
/// 라인 신장 중 진입 → 충만 지연 Delay, 충만 중 → 충만 상한 PartialFill, 개방 후·라인 영역 → CdS × (1 − LineDamagePerHit).
/// 상대속도 MinRelSpeed 미만 진입은 간섭 기록만 남기고 효과 없음.
/// </summary>
public sealed class ChuteAnomalyModel
{
    public double Delay { get; init; } = 0.3;
    public double PartialFill { get; init; } = 0.6;
    public double LineDamagePerHit { get; init; } = 0.15;
    public double MinRelSpeed { get; init; } = 2.0;

    public static ChuteAnomalyModel? FromJson(JsonObject? o) => o is null || !Scenario.J.Bool(o, "enabled", true) ? null : new ChuteAnomalyModel
    {
        Delay = Math.Max(0, Scenario.J.Num(o, "delay", 0.3)),
        PartialFill = Math.Clamp(Scenario.J.Num(o, "partialFill", 0.6), 0.02, 1),
        LineDamagePerHit = Math.Clamp(Scenario.J.Num(o, "lineDamagePerHit", 0.15), 0, 0.95),
        MinRelSpeed = Math.Max(0, Scenario.J.Num(o, "minRelSpeed", 2.0)),
    };
}

/// <summary>
/// 물체 간 충돌 판정·접촉(FR-SHL-04) — 근사:
///  · 광역: 형상 경계 구 겹침.
///  · 협역: 한 물체의 대표점(접촉점 집합)이 다른 물체 형상 안에 들어가면 침투(<see cref="Geometry.Shape.Penetration"/>),
///    가장 깊은 점·가까운 면 법선 사용. 모서리끼리 교차(정점 미포함)는 놓칠 수 있다.
///  · 응답: 반발계수 e · 쿨롱 마찰 μ 충격량(강체 관성 포함) + 침투 위치 보정. 운반체(규정 궤적)·착지 물체는 무한 질량.
///  · 접촉력 이력(contact.model): impulse(기본 — 하중 연구가 가정 지속 τ 반 사인으로 되살림) | compliant(순응 접촉 — 접촉 쌍의 직렬 강성
///    (국부 접촉 + 양쪽 구조 경로, <see cref="ContactStructure"/> 닫힌 식) · 반발 e 와 같은 손실 감쇠로 접촉 동안 적분한 F(t), <see cref="ContactPulse"/>).
///    두 모델의 운동량 교환(충격량)은 같다 — 6DOF 궤적·접촉 순서 동일, 접촉력 이력만 다르다.
///  · 수납(붙어 있는) 물체는 루트 물체로 힘이 전달된다. 같은 루트끼리는 검사하지 않는다.
///  · 분리 직후처럼 처음부터 겹친(포개진) 쌍은 한 번 떨어질 때까지 무시한다(수납 상태의 중첩을 충돌로 오판하지 않게).
///  · 낙하산(전개 중·후) 캐노피를 구(반경 D₀/2·max(충만비,0.3))로 보고 잔해 진입을 '간섭'으로 기록(물리 응답 없음).
/// 물체 간 충돌 역학은 PRD 범위 밖(판정 중심)이므로 응답은 겹침 해소·대략적 운동량 교환 수준이다.
/// </summary>
public sealed class CollisionModel
{
    public double Restitution { get; init; } = 0.3;
    public double Friction { get; init; } = 0.3;
    /// <summary>검사 대상 물체 이름(null = 형상 있는 전 물체).</summary>
    public HashSet<string>? Bodies { get; init; }
    public bool Parachutes { get; init; } = true;
    /// <summary>침투 허용 여유 [m] — 이보다 얕으면 위치 보정 안 함.</summary>
    public double Slop { get; init; } = 0.002;
    public int MaxRecords { get; init; } = 5000;
    /// <summary>이 시각 이후로는 검사하지 않음(물체가 충분히 흩어진 뒤 계산 절약) [s].</summary>
    public double Until { get; init; } = double.PositiveInfinity;

    /// <summary>
    /// 낙하산 영역: "sphere"(기본, 캐노피 구 vs 경계 구 — 기존 결과 유지) | "sphere+lines"(캐노피 구 + 결속점→캐노피 라인 원뿔,
    /// 대표점 협역 판정, FR-AN-03).
    /// </summary>
    public string CanopyRegion { get; init; } = "sphere";
    /// <summary>전개 이상 모델(null = 간섭 기록만).</summary>
    public ChuteAnomalyModel? Anomaly { get; init; }
    /// <summary>접촉력 이력 모델: impulse | compliant (collisions.contact.model).</summary>
    public string ContactModel { get; init; } = "impulse";
    /// <summary>순응 접촉 직렬 강성 배율(collisions.contact.stiffnessScale — 강성 불확도 MC·민감도 스윕, 기본 1).</summary>
    public double StiffnessScale { get; init; } = 1;
    /// <summary>강성 근거 문장(collisions.contact.basis — 보고서 표기).</summary>
    public string ContactBasis { get; init; } = "";
    /// <summary>탄성-소성 접촉(collisions.contact.plasticity = elastoplastic — 경도·벽 압괴 힘 상한, 소성 눌림). null = 탄성 순응 접촉.</summary>
    public ContactPlasticityModel? Plasticity { get; init; }
    public bool Compliant => ContactModel == "compliant";
    private readonly Dictionary<int, ContactStructure?> _structs = new();

    public List<CollisionRecord> Records { get; } = new();
    /// <summary>null 이 아니면 Respond 가 적용한 충격량(벡터)을 모두 덧붙인다(하중 연구용, 기본 꺼짐).</summary>
    public List<ContactImpulse>? ImpulseLog { get; set; }
    public int ContactCount { get; private set; }
    public int InterferenceCount { get; private set; }
    public int AnomalyCount { get; private set; }

    private readonly Dictionary<long, PairState> _pairs = new();
    private readonly HashSet<long> _canopy = new();
    private readonly HashSet<long> _lines = new();
    private readonly HashSet<long> _jointed = new();
    private List<Body>? _cands;

    [Flags]
    private enum PairState : byte { None = 0, SameRootLast = 1, Ignored = 2, Touching = 4, Seen = 8 }

    private static long Key(int i, int j) => ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);

    public static CollisionModel? FromJson(JsonObject? o)
    {
        if (o is null || !Scenario.J.Bool(o, "enabled", true)) return null;
        return new CollisionModel
        {
            Restitution = Scenario.J.Num(o, "restitution", 0.3),
            Friction = Scenario.J.Num(o, "friction", 0.3),
            Bodies = o["bodies"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToHashSet() : null,
            Parachutes = Scenario.J.Bool(o, "parachutes", true),
            Slop = Scenario.J.Num(o, "slop", 0.002),
            Until = Scenario.J.Num(o, "until", double.PositiveInfinity),
            CanopyRegion = Scenario.J.Str(o, "canopyRegion", "sphere") is var cr && cr is "sphere" or "sphere+lines"
                ? cr : throw new InvalidDataException($"collisions.canopyRegion: sphere | sphere+lines ('{cr}')"),
            Anomaly = ChuteAnomalyModel.FromJson(o["chuteAnomaly"] as JsonObject),
            ContactModel = o["contact"] is JsonObject co ? Scenario.J.Str(co, "model", "impulse") is var cm && cm is "impulse" or "compliant"
                ? cm : throw new InvalidDataException($"collisions.contact.model: impulse | compliant ('{cm}')") : "impulse",
            StiffnessScale = o["contact"] is JsonObject co2 ? Scenario.J.Num(co2, "stiffnessScale", 1) is var ss && ss > 0
                ? ss : throw new InvalidDataException("collisions.contact.stiffnessScale 은 양수") : 1,
            ContactBasis = o["contact"] is JsonObject co3 ? Scenario.J.Str(co3, "basis", "") : "",
            Plasticity = ContactPlasticityModel.FromJson(o["contact"] as JsonObject),
        };
    }

    /// <summary>물체의 순응 접촉 구조 이상화(처음 쓸 때 만든다).</summary>
    public ContactStructure? StructureOf(Body b)
    {
        if (!_structs.TryGetValue(b.Index, out var s)) _structs[b.Index] = s = ContactStructure.For(b);
        return s;
    }

    public void Check(World w)
    {
        if (w.T > Until) return;
        _cands ??= w.Bodies.Where(b => b.Shape is not null && (Bodies is null || Bodies.Contains(b.Name))).ToList();
        var cands = _cands;
        // 힌지로 묶인 쌍(관절 접합면 자체가 힌지): 같은 루트처럼 검사하지 않고, 이탈 뒤에는 한 번 떨어질 때까지 무시
        // — 위치 보정·충격량이 힌지 펜널티 스프링과 싸워 반력이 튀던 문제(시나리오 09 꼬리–꽃잎 접촉 수십 건)
        _jointed.Clear();
        foreach (var c in w.Connectors)
            if (c is Hinge h && h.IsEngaged) _jointed.Add(Key(h.A.Index, h.B.Index));
        for (int x = 0; x < cands.Count; x++)
        {
            var bi = cands[x];
            for (int y = x + 1; y < cands.Count; y++)
            {
                var bj = cands[y];
                long key = Key(bi.Index, bj.Index);
                _pairs.TryGetValue(key, out var st);
                var ra = bi.Root; var rb = bj.Root;
                if (ra == rb || _jointed.Contains(key)) { _pairs[key] = st | PairState.SameRootLast | PairState.Seen; continue; }
                if (ra.Mode != BodyMode.Free && rb.Mode != BodyMode.Free) { _pairs[key] = (st & ~PairState.SameRootLast) | PairState.Seen; continue; }
                bool overlap = Overlap(bi, bj, out var point, out var normal, out var depth, out bool jInI);
                bool firstLook = (st & PairState.Seen) == 0;
                bool wasSame = (st & PairState.SameRootLast) != 0;
                st = (st & ~PairState.SameRootLast) | PairState.Seen;
                if (overlap && (firstLook || wasSame)) st |= PairState.Ignored;
                if (!overlap) { st &= ~(PairState.Ignored | PairState.Touching); _pairs[key] = st; continue; }
                if ((st & PairState.Ignored) != 0) { _pairs[key] = st; continue; }
                Respond(w, bi, bj, point, normal, depth, (st & PairState.Touching) == 0, jInI);
                st |= PairState.Touching;
                _pairs[key] = st;
            }
        }
        if (Parachutes) CheckCanopies(w, cands);
    }

    /// <summary>bi·bj 겹침: 서로의 대표점이 상대 형상 안에 있는지. normal = bi → bj (NED). jInI = 가장 깊은 점이 bj 의 대표점(bi 형상 안).</summary>
    private static bool Overlap(Body bi, Body bj, out Vec3 point, out Vec3 normal, out double depth, out bool jInI)
    {
        point = Vec3.Zero; normal = Vec3.Zero; depth = 0; jInI = false;
        var si = bi.Shape!; var sj = bj.Shape!;
        var ci = bi.PointWorld(si.BoundingCenter); var cj = bj.PointWorld(sj.BoundingCenter);
        if ((ci - cj).Norm > si.BoundingRadius + sj.BoundingRadius) return false;
        bool hit = false;
        // bj 의 점이 bi 안
        var qi = bi.WorldQ;
        foreach (var q in sj.SupportPoints())
        {
            var pw = bj.PointWorld(q);
            if (si.Penetration(bi.WorldToBrf(pw), out double d, out var n) && d > depth)
            {
                depth = d; point = pw; normal = qi.Rotate(n); hit = true; jInI = true;
            }
        }
        var qj = bj.WorldQ;
        foreach (var q in si.SupportPoints())
        {
            var pw = bi.PointWorld(q);
            if (sj.Penetration(bj.WorldToBrf(pw), out double d, out var n) && d > depth)
            {
                depth = d; point = pw; normal = -qj.Rotate(n); hit = true; jInI = false;
            }
        }
        return hit;
    }

    private static double InvMass(Body r) => r.Mode == BodyMode.Free && r.Mass > 0 ? 1 / r.Mass : 0;

    private static Vec3 InvInertiaWorld(Body r, Vec3 x) =>
        r.Mode == BodyMode.Free ? r.Q.Rotate(r.InvInertia * r.Q.InverseRotate(x)) : Vec3.Zero;

    private static Vec3 VelAt(Body r, Vec3 p) =>
        r.Mode switch
        {
            BodyMode.Free => r.V + Vec3.Cross(r.Q.Rotate(r.W), p - r.R),
            BodyMode.Kinematic => r.V,
            _ => Vec3.Zero,
        };

    private void Respond(World w, Body bi, Body bj, Vec3 p, Vec3 n, double depth, bool newContact, bool jInI)
    {
        var ra = bi.Root; var rb = bj.Root;
        double ima = InvMass(ra), imb = InvMass(rb);
        if (ima + imb <= 0) return;
        var rA = p - ra.R; var rB = p - rb.R;
        var vrel = VelAt(rb, p) - VelAt(ra, p);
        double vn = Vec3.Dot(vrel, n);
        double j = 0, jt = 0;
        var Jv = Vec3.Zero;
        ContactPulse? pulse = null;
        if (vn < 0)
        {
            double k = ima + imb + Vec3.Dot(n, Vec3.Cross(InvInertiaWorld(ra, Vec3.Cross(rA, n)), rA) + Vec3.Cross(InvInertiaWorld(rb, Vec3.Cross(rB, n)), rB));
            j = -(1 + Restitution) * vn / Math.Max(k, 1e-12);
            // 순응 접촉: 같은 유효 질량 1/k · 접근 속도 −v_n 으로 접촉 동안 적분한 접촉력 이력(운동량 교환은 위 충격량과 같다)
            if (Compliant)
                pulse = ContactPulse.Compute(bi, StructureOf(bi), bj, StructureOf(bj), p, n, 1 / Math.Max(k, 1e-12), -vn, Restitution, StiffnessScale, Plasticity);
            var J = n * j;
            var vt = vrel - n * vn;
            double vtn = vt.Norm;
            if (vtn > 1e-9 && Friction > 0)
            {
                var t = vt / vtn;
                double kt = ima + imb + Vec3.Dot(t, Vec3.Cross(InvInertiaWorld(ra, Vec3.Cross(rA, t)), rA) + Vec3.Cross(InvInertiaWorld(rb, Vec3.Cross(rB, t)), rB));
                jt = Math.Min(Friction * j, vtn / Math.Max(kt, 1e-12));
                J -= t * jt;
            }
            Apply(rb, J, rB);
            Apply(ra, J * -1, rA);
            ImpulseLog?.Add(new ContactImpulse(w.T, ra, rb, J, p, pulse));
            Jv = J;
        }
        // 위치 보정(침투 해소, 역질량 비)
        double corr = Math.Max(depth - Slop, 0) * 0.8;
        if (corr > 0)
        {
            double s = ima + imb;
            if (ra.Mode == BodyMode.Free) ra.R -= n * (corr * ima / s);
            if (rb.Mode == BodyMode.Free) rb.R += n * (corr * imb / s);
        }
        if (newContact)
        {
            ContactCount++;
            // 접촉점·법선(FR-SHL-04 근거 추적): 위치 보정 전 값. 동체 좌표는 각 물체 BRF(원점 = 모델 원점)
            var pa = bi.WorldToBrf(p); var pb = bj.WorldToBrf(p);
            var na = bi.WorldQ.InverseRotate(n);
            string via = jInI ? $"{bj.Name}점→{bi.Name}형상" : $"{bi.Name}점→{bj.Name}형상";
            if (Records.Count < MaxRecords)
                Records.Add(new CollisionRecord(w.T, bi.Name, bj.Name, "contact", -vn, Math.Sqrt(j * j + jt * jt), p, n, pa, pb, depth, Jv, vrel, via, pulse));
            w.Log($"collision:{bi.Name}-{bj.Name}", $"충돌 {bi.Name} ↔ {bj.Name}: 접근속도 {Math.Max(-vn, 0):F2} m/s, 충격량 {Math.Sqrt(j * j + jt * jt):F1} N·s"
                + $" | 접촉점 {bi.Name} {V3(pa)} · {bj.Name} {V3(pb)} m(동체) · NED {V3(p)} | 법선({bi.Name}→{bj.Name}) {bi.Name}동체 {V3(na, "F2")} · NED {V3(n, "F2")}"
                + $" | 침투 {depth * 1000:F1} mm ({via}) | 충격량 벡터(→{bj.Name}, NED) {V3(Jv, "F1")} N·s | 상대속도 v{bj.Name}−v{bi.Name} {V3(vrel, "F2")} m/s"
                + (pulse is null ? "" : $" | 순응 접촉 {pulse.Summary()}"));
        }
    }

    private static string V3(Vec3 v, string f = "F3")
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return $"({v.X.ToString(f, c)}, {v.Y.ToString(f, c)}, {v.Z.ToString(f, c)})";
    }

    private static void Apply(Body r, Vec3 J, Vec3 rArm)
    {
        if (r.Mode != BodyMode.Free) return;
        r.V += J / r.Mass;
        r.W += r.InvInertia * r.Q.InverseRotate(Vec3.Cross(rArm, J));
    }

    private void CheckCanopies(World w, List<Body> cands)
    {
        foreach (var owner in w.Bodies)
            foreach (var d in owner.Devices)
            {
                if (d is not Parachute ch || ch.State is ChuteState.Stowed or ChuteState.Released || owner.Root.Mode != BodyMode.Free) continue;
                var s = ch.Sample(w);
                if (!s.Visible) continue;
                double rc = ch.D0 / 2 * Math.Max(s.Fill, 0.3);
                bool lines = CanopyRegion == "sphere+lines";
                foreach (var b in cands)
                {
                    if (b.Root == owner.Root || b.Root.Mode == BodyMode.Kinematic) continue;
                    long key = ((long)b.Index << 32) | (uint)(owner.Index * 64 + owner.Devices.IndexOf(d));
                    var bc = b.PointWorld(b.Shape!.BoundingCenter);
                    double rb = b.Shape.BoundingRadius;
                    bool inCanopy, inLines = false;
                    Vec3 at = s.Tip;
                    if (!lines) inCanopy = (bc - s.Tip).Norm < rc + rb;
                    else
                    {
                        // 광역: 경계 구 vs (캐노피 구 ∪ 라인 원뿔 외접 캡슐), 협역: 대표점이 캐노피 구 또는 원뿔(결속점 반경 0 → 캐노피 반경 rc) 안
                        inCanopy = false;
                        var axis = s.Tip - s.Anchor;
                        double len = axis.Norm;
                        bool near = (bc - s.Tip).Norm < rc + rb || (len > 1e-9 && SegmentDistance(bc, s.Anchor, axis, len, out _) < rc + rb);
                        if (near)
                            foreach (var q in b.Shape.SupportPoints())
                            {
                                var p = b.PointWorld(q);
                                if ((p - s.Tip).Norm < rc) { inCanopy = true; at = p; break; }
                                if (!inLines && len > 1e-9 && SegmentDistance(p, s.Anchor, axis, len, out double f) < rc * f) { inLines = true; at = p; }
                            }
                        if (inCanopy) inLines = false;
                    }
                    if (!inCanopy) _canopy.Remove(key);
                    if (!inLines) _lines.Remove(key);
                    string? region = inCanopy && _canopy.Add(key) ? "canopy" : inLines && _lines.Add(key) ? "lines" : null;
                    if (region is null) continue;
                    InterferenceCount++;
                    var rel = (VelAt(b.Root, at) - (lines ? VelAt(owner.Root, at) : owner.Root.V)).Norm;
                    if (Records.Count < MaxRecords)
                        Records.Add(new CollisionRecord(w.T, b.Name, d.FullName, region, rel, 0, at));
                    w.Log($"interference:{b.Name}-{d.FullName}", region == "canopy"
                        ? $"간섭 {b.Name} → 낙하산 {d.FullName} 캐노피(반경 {rc:F2} m) 진입, 상대속도 {rel:F1} m/s"
                        : $"간섭 {b.Name} → 낙하산 {d.FullName} 라인 영역 진입({ch.State}), 상대속도 {rel:F1} m/s");
                    if (Anomaly is not null && ch.ApplyDebrisAnomaly(w, b.Name, region, rel, Anomaly) is not null)
                    {
                        AnomalyCount++;
                        if (Records.Count < MaxRecords) Records.Add(new CollisionRecord(w.T, b.Name, d.FullName, "anomaly", rel, 0, at));
                    }
                }
            }
    }

    /// <summary>점 p 와 선분 a→a+axis(길이 len) 거리, f = 선분 위 최근접점 비율(0 = a, 1 = 끝).</summary>
    private static double SegmentDistance(Vec3 p, Vec3 a, Vec3 axis, double len, out double f)
    {
        f = Math.Clamp(Vec3.Dot(p - a, axis) / (len * len), 0, 1);
        return (p - (a + axis * f)).Norm;
    }

    public JsonObject ToJson()
    {
        // contact: 처음·최대 충격량 접촉의 접촉점(양 물체 동체 좌표·NED)·법선·충격량 벡터 — 응답 방향이 물리적으로 맞는지 되짚기 위해
        static JsonObject Detail(CollisionRecord r) => new()
        {
            ["time_s"] = r.Time, ["via"] = r.Via, ["pointA_brf_m"] = Scenario.J.ToJson(r.PointA), ["pointB_brf_m"] = Scenario.J.ToJson(r.PointB),
            ["point_ned_m"] = Scenario.J.ToJson(r.Point), ["normalAB_ned"] = Scenario.J.ToJson(r.Normal), ["depth_mm"] = r.Depth * 1000,
            ["impulseOnB_ned_Ns"] = Scenario.J.ToJson(r.J), ["relVelBA_ned_mps"] = Scenario.J.ToJson(r.RelVel), ["approach_mps"] = r.RelSpeed,
            ["compliant"] = r.Pulse?.ToJson(),
        };
        var pairs = Records.GroupBy(r => (r.A, r.B, r.Kind)).Select(g =>
        {
            var o = new JsonObject
            {
                ["a"] = g.Key.A, ["b"] = g.Key.B, ["kind"] = g.Key.Kind, ["count"] = g.Count(),
                ["firstTime_s"] = g.Min(r => r.Time), ["maxRelSpeed_mps"] = g.Max(r => r.RelSpeed), ["maxImpulse_Ns"] = g.Max(r => r.Impulse),
            };
            if (g.Key.Kind == "contact")
            {
                o["first"] = Detail(g.MinBy(r => r.Time)!);
                o["maxImpulse"] = Detail(g.MaxBy(r => r.Impulse)!);
            }
            return (JsonNode)o;
        }).ToArray();
        return new JsonObject
        {
            ["contacts"] = ContactCount,
            ["interferences"] = InterferenceCount,
            ["chuteAnomalies"] = AnomalyCount,
            ["canopyRegion"] = CanopyRegion,
            ["restitution"] = Restitution,
            ["friction"] = Friction,
            ["pairs"] = new JsonArray(pairs),
            ["contactModel"] = ContactModel,
            ["stiffnessScale"] = Compliant ? StiffnessScale : null,
            ["plasticity"] = Compliant ? Plasticity?.ToJson() ?? new JsonObject { ["model"] = "elastic" } : null,
            ["model"] = Compliant ? "대표점-형상 내부 판정 + 충격량 접촉(근사, FR-SHL-04) + 순응 접촉력 이력(구조 강성 닫힌 식·서브스텝 적분)"
                : "대표점-형상 내부 판정 + 충격량 접촉(근사, FR-SHL-04)",
        };
    }
}
