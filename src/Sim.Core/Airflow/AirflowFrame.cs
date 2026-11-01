using System.Text.Json.Nodes;
using Sim.Core.Numerics;

namespace Sim.Core.Airflow;

/// <summary>공력 계수(OpenFOAM DB 와 같은 축: CD 흐름 방향, CL 풍동 +Z, CY 풍동 +Y, 모두 /(q∞ S_ref)).</summary>
public readonly record struct FlowCoeffs(double CD, double CL, double CY)
{
    public static FlowCoeffs? From(JsonNode? n) =>
        n is JsonObject o && o["CD"] is not null ? new FlowCoeffs(Num(o, "CD"), Num(o, "CL"), Num(o, "CY")) : null;

    private static double Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
}

/// <summary>단면(풍동 좌표 m): 원점(셀 [0,0] 모서리) + a 축(흐름 방향 격자 x) · b 축 벡터, 셀 수.</summary>
public sealed record FlowSlice(string Plane, Vec3 Origin, Vec3 AxisA, Vec3 AxisB, int NA, int NB, float[] Speed, float[] Vorticity)
{
    /// <summary>값 [a, b] (C 순서 — a 가 바깥).</summary>
    public float At(float[] f, int a, int b) => f[a * NB + b];
}

/// <summary>
/// 흐름 화면 한 프레임(GPU 실시간 또는 OpenFOAM 재생) — 모두 풍동 좌표(진행 +X, 흐름 −X, 물체 중심 원점, m).
/// 단면 속도는 |U|/V∞, 와도는 ω·L_ref/V∞(면에 수직 성분, NaN = 물체 안). 입자 = (x, y, z, |U|/V∞) 4 개씩, 유선 = 점 4 개씩 + 선별 점 수.
/// </summary>
public sealed class AirflowFrame
{
    public string Source { get; init; } = "";          // "XLB" | "selftest" | "OpenFOAM" …
    public string Device { get; init; } = "";
    public long Step { get; init; }
    public int Frame { get; init; }
    public double TimePhys { get; init; }
    public double StepsPerSecond { get; init; }
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Speed { get; init; }
    public double Re { get; init; }
    public double ReMax { get; init; }
    public bool ReClamped { get; init; }
    public double RePhys { get; init; }
    public double Mach { get; init; }
    public double Dx { get; init; }
    public int[] Shape { get; init; } = [];
    public string Resolution { get; init; } = "";
    public double RefArea { get; init; }
    public double RefLength { get; init; }
    public double FlowThroughs { get; init; }
    public FlowCoeffs? Coeffs { get; init; }
    public FlowCoeffs? CoeffsMean { get; init; }
    public bool Settling { get; init; }
    public int MeanSamples { get; init; }
    public FlowSlice? Slice { get; init; }
    public float[] Particles { get; init; } = [];
    public float[] Lines { get; init; } = [];
    public int[] LineCounts { get; init; } = [];
    public JsonObject Header { get; init; } = new();

    public int ParticleCount => Particles.Length / 4;

    /// <summary>IPC 프레임 메시지 → 프레임(없는 필드는 기본값). type 이 frame 이 아니면 InvalidDataException.</summary>
    public static AirflowFrame FromMessage(AirflowMessage m)
    {
        var h = m.Header;
        if (m.Type != "frame") throw new InvalidDataException($"프레임 메시지가 아닙니다: {m.Type}");
        double N(string k, double d = 0) => h[k] is JsonValue v && v.TryGetValue<double>(out var x) ? x : d;
        string S(string k) => h[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
        float[] F(string k) => m.Arrays.TryGetValue(k, out var a) && a.F4 is { } f ? f : [];
        FlowSlice? slice = null;
        if (h["slice"] is JsonObject so && m.Arrays.TryGetValue("slice_speed", out var sp) && sp.Shape.Length == 2)
        {
            static Vec3 V(JsonNode? n) => n is JsonArray a && a.Count == 3 ? new Vec3(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>()) : Vec3.Zero;
            var vort = F("slice_vort");
            slice = new FlowSlice(so["plane"]?.GetValue<string>() ?? "xz", V(so["origin"]), V(so["axisA"]), V(so["axisB"]), sp.Shape[0], sp.Shape[1],
                sp.F4!, vort.Length == sp.F4!.Length ? vort : new float[sp.F4.Length]);
        }
        var counts = m.Arrays.TryGetValue("line_counts", out var lc) && lc.I4 is { } ic ? ic : [];
        var lines = F("lines");
        if (counts.Sum() * 4 != lines.Length) counts = [];   // 일관성 없으면 유선 버림
        return new AirflowFrame
        {
            Source = S("backend"), Device = S("device"), Step = (long)N("step"), Frame = (int)N("frame"), TimePhys = N("timePhys"),
            StepsPerSecond = N("stepsPerSecond"), Alpha = N("alpha"), Beta = N("beta"), Speed = N("speed"), Re = N("re"), ReMax = N("reMax"),
            ReClamped = h["reClamped"] is JsonValue rc && rc.TryGetValue<bool>(out var b) && b, RePhys = N("rePhys"), Mach = N("mach"),
            Dx = N("dx"), Shape = h["shape"] is JsonArray sh ? sh.Select(x => x!.GetValue<int>()).ToArray() : [], Resolution = S("resolution"),
            RefArea = N("refArea"), RefLength = N("refLength"), FlowThroughs = N("flowThroughs"),
            Coeffs = FlowCoeffs.From(h["coeffs"]), CoeffsMean = FlowCoeffs.From(h["coeffsMean"]),
            Settling = h["settling"] is JsonValue st && st.TryGetValue<bool>(out var sb) && sb, MeanSamples = (int)N("meanSamples"),
            Slice = slice, Particles = F("particles"), Lines = counts.Length > 0 ? lines : [], LineCounts = counts, Header = h,
        };
    }

    /// <summary>유선 i 의 점(풍동 좌표)과 속력비.</summary>
    public IEnumerable<(Vec3[] P, double[] S)> Streamlines()
    {
        int o = 0;
        foreach (var n in LineCounts)
        {
            var p = new Vec3[n];
            var s = new double[n];
            for (int k = 0; k < n; k++)
            {
                int j = 4 * (o + k);
                p[k] = new Vec3(Lines[j], Lines[j + 1], Lines[j + 2]);
                s[k] = Lines[j + 3];
            }
            o += n;
            yield return (p, s);
        }
    }
}

/// <summary>GPU 가상 풍동 좌표(tools/airflow/lattice.py 와 같은 식).</summary>
public static class AirflowFrames
{
    /// <summary>동체 → 풍동 회전 R = Rz(−β)·Ry(α): R·d = +X, d = (cos α cos β, sin β, sin α cos β). β = 0 이면 WindTunnel.ToTunnel 과 같다.</summary>
    public static Mat3 BodyToTunnel(double alphaDeg, double betaDeg)
    {
        double a = alphaDeg * Math.PI / 180, b = betaDeg * Math.PI / 180;
        double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b);
        var ry = new Mat3(ca, 0, sa, 0, 1, 0, -sa, 0, ca);
        var rz = new Mat3(cb, sb, 0, -sb, cb, 0, 0, 0, 1);
        return rz * ry;
    }

    public static Vec3 TravelDirection(double alphaDeg, double betaDeg)
    {
        double a = alphaDeg * Math.PI / 180, b = betaDeg * Math.PI / 180;
        return new Vec3(Math.Cos(a) * Math.Cos(b), Math.Sin(b), Math.Sin(a) * Math.Cos(b));
    }

    /// <summary>화면에 늘 띄우는 물리 한계(격자 볼츠만).</summary>
    public const string Limitation =
        "한계: 격자 볼츠만(XLB, D3Q27 KBC)은 저속 비압축 근사(격자 마하 ≈ 0.1) — 마하 0.5 의 압축성 효과는 반영되지 않습니다. " +
        "격자 해상도 한계로 레이놀즈 수는 실제(10⁶~10⁷)보다 훨씬 낮은 '계산 Re'(수백~수천)로 풉니다. 닫힌 시험부(벽 반사·막힘) 포함. " +
        "흐름 모양(박리·후류 와류·와류 흘림)을 이해하기 위한 시각화이며, 힘·Cp 정량값은 OpenFOAM 결과를 쓰십시오.";

    /// <summary>신뢰 등급(PRD AB 절 틀): 이 모드의 출력 등급 — 시각화(근사).</summary>
    public const string Grade = "시각화(근사)";

    public const string GradeNote = "검증 사례 없음 · 계산 Re ≪ 실제 Re · 비압축 — 의사결정 수치로 쓰지 않음(흐름 모양 이해용)";
}
