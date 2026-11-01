using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Structures;

/// <summary>물체 하나의 전 구간 최대값 유지(peak-hold) 결과와 최초 항복·파단·좌굴 시각.</summary>
public sealed class BodyStressHistory
{
    public required Body Body { get; init; }
    public required StressModel Model { get; init; }
    public double[] PeakVonMises = [], PeakPressure = [], MinSafety = [];
    public FailureState[] WorstState = [];
    public double PeakVm, PeakVmTime = double.NaN;
    public Vec3 PeakVmPoint;
    /// <summary>최대 응력 순간·위치의 지배 성분(보/패널/막/핫스팟, FX-09).</summary>
    public StressComponent DominantAtPeak;
    /// <summary>전 구간 선형 판 처짐/두께 최대(&gt; 0.5 이면 선형 판 응력은 보수적).</summary>
    public double MaxPanelDeflectionRatio;
    public double MaxPressure, MaxPressureTime = double.NaN;
    public double MinSafetyValue = double.PositiveInfinity, MinSafetyTime = double.NaN;
    /// <summary>속도 보정 전(정적 강도) 최소 안전계수 — 보정 전/후 비교(FR-MAT-03).</summary>
    public double MinSafetyStatic = double.PositiveInfinity;
    public FailureState Worst = FailureState.Unknown;
    public double FirstYieldTime = double.NaN, FirstFractureTime = double.NaN, FirstBucklingTime = double.NaN;
    public Vec3 FirstYieldPoint, FirstFracturePoint, FirstBucklingPoint;
    public double MaxStrainRate, RateFactorAtPeak = 1;
    public double MinBucklingStress = double.PositiveInfinity;
    public double ExternalPressureCritical = double.PositiveInfinity;
    public int FramesAnalyzed;
    public double MaxResidualRatio;
    /// <summary>착지 충돌 재질 판정(1·2단계) — 착지 기록·재질이 있을 때.</summary>
    public ImpactAssessment? Impact;

    public JsonObject ToJson()
    {
        static JsonNode? N(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
        var m = Body.Structure?.Material;
        return new JsonObject
        {
            ["structure"] = Body.Structure?.ToJson(),
            ["beamAxis"] = "xyz"[Model.Axis].ToString(),
            ["faces"] = Model.FaceCount,
            ["framesAnalyzed"] = FramesAnalyzed,
            ["peakVonMises_MPa"] = PeakVm / 1e6,
            ["peakVonMisesTime_s"] = N(PeakVmTime),
            ["peakVonMisesPoint_brf_m"] = Scenario.J.ToJson(PeakVmPoint),
            ["peakDominant"] = DominantAtPeak.ToString(),
            ["maxPanelDeflectionRatio"] = MaxPanelDeflectionRatio,
            ["maxPressure_kPa"] = MaxPressure / 1e3,
            ["maxPressureTime_s"] = N(MaxPressureTime),
            ["minSafetyFactor"] = N(MinSafetyValue),
            ["minSafetyFactorStatic"] = N(MinSafetyStatic),
            ["minSafetyTime_s"] = N(MinSafetyTime),
            ["worstState"] = Worst.ToString(),
            ["firstYield_s"] = N(FirstYieldTime),
            ["firstYieldPoint_brf_m"] = double.IsFinite(FirstYieldTime) ? Scenario.J.ToJson(FirstYieldPoint) : null,
            ["firstFracture_s"] = N(FirstFractureTime),
            ["firstFracturePoint_brf_m"] = double.IsFinite(FirstFractureTime) ? Scenario.J.ToJson(FirstFracturePoint) : null,
            ["firstBuckling_s"] = N(FirstBucklingTime),
            ["firstBucklingPoint_brf_m"] = double.IsFinite(FirstBucklingTime) ? Scenario.J.ToJson(FirstBucklingPoint) : null,
            ["minBucklingStress_MPa"] = N(MinBucklingStress / 1e6),
            ["externalPressureBuckling_kPa"] = N(ExternalPressureCritical / 1e3),
            ["externalPressureMargin"] = N(ExternalPressureCritical / Math.Max(MaxPressure, 1e-9)),
            ["maxStrainRate_1ps"] = MaxStrainRate,
            ["rateModel"] = m?.RateModel ?? "none",
            ["rateFactorAtPeak"] = RateFactorAtPeak,
            ["yieldStatic_MPa"] = m is null ? null : m.Yield / 1e6,
            ["yieldDynamic_MPa"] = m is null ? null : m.Yield * RateFactorAtPeak / 1e6,
            ["equilibriumResidualRatio"] = MaxResidualRatio,
            ["impact"] = Impact?.ToJson(),
        };
    }
}

public sealed class StructuralReport
{
    public List<BodyStressHistory> Bodies { get; } = new();
    public List<string> Warnings { get; } = new();

    public BodyStressHistory? For(int bodyIndex) => Bodies.FirstOrDefault(b => b.Body.Index == bodyIndex);

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var b in Bodies) o[b.Body.Name] = b.ToJson();
        return new JsonObject
        {
            ["schema"] = "mns.structural/1",
            ["note"] = "근사(FEA 아님): 수정 뉴턴 표면 압력 + 길이축 보(관성 완화) + 얇은 벽 국부 항(Roark 판 굽힘·막응력) + 연결점 핫스팟. 재질값은 라이브러리 대표값.",
            ["bodies"] = o,
            ["warnings"] = new JsonArray(Warnings.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        };
    }
}

/// <summary>
/// 실행 후 구조 분석(FR-STR-04 peak-hold · FR-MAT-02 최초 항복/파단 이벤트 · FR-MAT-03 속도 보정 · FR-MAT-04 좌굴).
/// 기록 프레임(최대 maxFrames 개 균등 추출 + 물체별 최대 하중 순간)을 평가한다.
/// </summary>
public static class StructuralAnalysis
{
    /// <summary>bodies: 분석할 물체(null = structure(재질) 정의가 있는 비운반체 물체).</summary>
    public static StructuralReport Analyze(World w, IEnumerable<Body>? bodies = null, int maxFrames = 1500, CancellationToken ct = default)
    {
        var rep = new StructuralReport();
        var list = (bodies ?? w.Bodies.Where(b => b.Structure is not null)).Where(b => b.Mode != BodyMode.Kinematic && b.Shape is not null).ToList();
        var frames = w.Recorder.Frames;
        if (frames.Count == 0) return rep;
        foreach (var b in list)
        {
            ct.ThrowIfCancellationRequested();
            var model = StressModel.Build(b);
            if (model is null) continue;
            var h = Analyze(w, model, maxFrames, ct);
            rep.Bodies.Add(h);
            if (b.Structure is { Material: null, RequestedMaterial: { } req })
                rep.Warnings.Add($"{b.Name}: 미지원 재질 '{req}' — 재질 판정 생략");
        }
        return rep;
    }

    /// <summary>분석할 프레임 인덱스: 균등 추출 + 이 물체 하중 최대 프레임.</summary>
    public static List<int> SampleFrames(World w, Body b, int maxFrames)
    {
        var frames = w.Recorder.Frames;
        var idx = new List<int>();
        int first = -1, last = -1, peak = -1; double peakG = -1;
        for (int i = 0; i < frames.Count; i++)
        {
            var s = frames[i].Bodies[b.Index];
            if (s.Mode != BodyMode.Free) continue;
            if (first < 0) first = i;
            last = i;
            if (s.LoadG > peakG) { peakG = s.LoadG; peak = i; }
        }
        if (first < 0) return idx;
        int count = last - first + 1;
        int stride = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(maxFrames, 1)));
        for (int i = first; i <= last; i += stride) idx.Add(i);
        if (peak >= 0 && !idx.Contains(peak)) { idx.Add(peak); idx.Sort(); }
        return idx;
    }

    public static BodyStressHistory Analyze(World w, StressModel model, int maxFrames = 1500, CancellationToken ct = default)
    {
        var b = model.Body;
        int n = model.FaceCount;
        var h = new BodyStressHistory
        {
            Body = b, Model = model,
            PeakVonMises = new double[n], PeakPressure = new double[n], MinSafety = Enumerable.Repeat(double.PositiveInfinity, n).ToArray(),
            WorstState = new FailureState[n],
        };
        h.MinBucklingStress = model.BucklingStress.Min();
        var mat = b.Structure?.Material;
        if (mat is not null && b.Structure!.Section == "thinWall" && b.Shape is { Kind: Geometry.ShapeKind.Cylinder or Geometry.ShapeKind.ShellSegment } sh)
            h.ExternalPressureCritical = StressModel.ExternalPressureBuckling(mat.E, mat.Nu, b.Structure.Thickness ?? 0, sh.Radius, sh.Length);
        var frames = w.Recorder.Frames;
        var idx = SampleFrames(w, b, maxFrames);
        var res = new StressResult();
        double prevSig = double.NaN, prevT = double.NaN;
        var pulses = b.Devices.OfType<Pulse>().ToList();
        foreach (int i in idx)
        {
            ct.ThrowIfCancellationRequested();
            var f = frames[i];
            // 변형률 속도 추정(FR-MAT-03): 프레임 간 최대 응력 변화율 / E, 펄스 작동 중이면 상승시간(τ/2) 기준값 중 큰 쪽
            var inp = model.InputFrom(w, f, EstimateRate(h, mat, pulses, f.T));
            model.Evaluate(inp, res);
            if (mat is not null && double.IsFinite(prevSig) && f.T > prevT)
                h.MaxStrainRate = Math.Max(h.MaxStrainRate, Math.Abs(res.MaxVonMises - prevSig) / (mat.E * (f.T - prevT)));
            prevSig = res.MaxVonMises; prevT = f.T;
            h.FramesAnalyzed++;
            h.MaxPanelDeflectionRatio = Math.Max(h.MaxPanelDeflectionRatio, res.MaxPanelDeflectionRatio);
            double mMax = res.BendingMoment.Max();
            if (mMax > 1e-6) h.MaxResidualRatio = Math.Max(h.MaxResidualRatio, res.ResidualMoment / mMax);
            for (int k = 0; k < n; k++)
            {
                if (res.VonMises[k] > h.PeakVonMises[k]) h.PeakVonMises[k] = res.VonMises[k];
                if (res.Pressure[k] > h.PeakPressure[k]) h.PeakPressure[k] = res.Pressure[k];
                if (res.Safety[k] < h.MinSafety[k]) h.MinSafety[k] = res.Safety[k];
                if (res.State[k] > h.WorstState[k]) h.WorstState[k] = res.State[k];
            }
            if (res.MaxVonMises > h.PeakVm)
            {
                h.PeakVm = res.MaxVonMises; h.PeakVmTime = f.T; h.RateFactorAtPeak = res.RateFactor; h.DominantAtPeak = res.DominantAtMax;
                if (res.MaxFace >= 0) h.PeakVmPoint = model.Centroid[res.MaxFace];
            }
            if (res.MaxPressure > h.MaxPressure) { h.MaxPressure = res.MaxPressure; h.MaxPressureTime = f.T; }
            if (res.MinSafety < h.MinSafetyValue) { h.MinSafetyValue = res.MinSafety; h.MinSafetyTime = f.T; }
            if (mat is not null && res.MaxVonMises > 0)
            {
                double stat = mat.Brittle ? mat.Ultimate / res.MaxVonMises : mat.Yield / res.MaxVonMises;
                h.MinSafetyStatic = Math.Min(h.MinSafetyStatic, stat);
            }
            if (res.Worst > h.Worst) h.Worst = res.Worst;
            First(ref h.FirstYieldTime, ref h.FirstYieldPoint, FailureState.Yield, res, model, f.T);
            First(ref h.FirstFractureTime, ref h.FirstFracturePoint, FailureState.Fracture, res, model, f.T);
            First(ref h.FirstBucklingTime, ref h.FirstBucklingPoint, FailureState.Buckling, res, model, f.T);
        }
        ApplyImpact(h, w.Env.Gravity);
        return h;
    }

    /// <summary>착지 충돌 판정을 이력에 합친다: 접촉부 면 상태(peak-hold), 최악 상태, 최초 항복·파단 시각(비행 중 없었으면 착지 시각).</summary>
    public static void ApplyImpact(BodyStressHistory h, double gravity)
    {
        var a = ImpactDamage.Assess(h.Body, gravity);
        h.Impact = a;
        if (a is null) return;
        ImpactDamage.Paint(a, h.Model.Centroid, h.WorstState);
        if (a.State > h.Worst) h.Worst = a.State;
        if (a.State is FailureState.Yield or FailureState.Fracture && !double.IsFinite(h.FirstYieldTime)) { h.FirstYieldTime = a.Time; h.FirstYieldPoint = a.ContactPointBrf; }
        if (a.State == FailureState.Fracture && !double.IsFinite(h.FirstFractureTime)) { h.FirstFractureTime = a.Time; h.FirstFracturePoint = a.ContactPointBrf; }
    }

    private static double EstimateRate(BodyStressHistory h, Material? mat, List<Pulse> pulses, double t)
    {
        if (mat is null) return 0;
        double rate = h.MaxStrainRate;
        foreach (var p in pulses)
            if (p.Weight(t) > 0 && h.PeakVm > 0) rate = Math.Max(rate, h.PeakVm / (mat.E * Math.Max(p.Duration / 2, 1e-4)));
        return rate;
    }

    private static void First(ref double time, ref Vec3 point, FailureState s, StressResult r, StressModel m, double t)
    {
        if (double.IsFinite(time)) return;
        for (int k = 0; k < r.State.Length; k++)
        {
            if (s == FailureState.Yield ? r.State[k] is FailureState.Yield or FailureState.Fracture : r.State[k] == s)
            {
                time = t; point = m.Centroid[k];
                return;
            }
        }
    }

    /// <summary>최초 항복·파단·좌굴을 이벤트 로그에 시각 순으로 넣는다(FR-MAT-02).</summary>
    public static void AppendEvents(World w, StructuralReport rep)
    {
        foreach (var h in rep.Bodies)
        {
            var name = h.Body.Name;
            var mat = h.Body.Structure?.Material?.Name ?? "?";
            void Add(double t, string kind, string label, Vec3 p)
            {
                if (!double.IsFinite(t)) return;
                var e = new LogEntry(t, $"{name}.{kind}", $"{name} ({mat}) 최초 {label} — 위치(동체) {p} [근사, FEA 아님]");
                int at = w.EventLog.FindIndex(x => x.Time > t);
                if (at < 0) w.EventLog.Add(e); else w.EventLog.Insert(at, e);
            }
            Add(h.FirstYieldTime, "yield", "항복(영구 변형)", h.FirstYieldPoint);
            Add(h.FirstFractureTime, "fracture", "파단", h.FirstFracturePoint);
            Add(h.FirstBucklingTime, "buckling", "좌굴(찌그러짐) 위험", h.FirstBucklingPoint);
            if (h.Impact is { } a && double.IsFinite(a.Time))
            {
                var e = new LogEntry(a.Time, $"{name}.impactDamage", $"{name} ({mat}) 착지 {a.NormalSpeed:F2} m/s {a.Contact}: {a.Verdict} [근사, FEA 아님]");
                int at = w.EventLog.FindIndex(x => x.Time > a.Time);
                if (at < 0) w.EventLog.Add(e); else w.EventLog.Insert(at, e);
            }
        }
    }
}
