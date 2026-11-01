using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Models;

/// <summary>
/// OpenFOAM 비정상(DES) 해석의 시간 시퀀스(schema mns.flow-seq/1 — tools/cfd/flowseq.py, 형식 docs/cfd_flow_sequence.md) 읽기.
/// 한 조건(받음각·마하/속도) 의 프레임 목록: 표면 Cp(t)(원통 외형 (x, θ) 측면 + 앞·뒤 끝면, θ 0–360° 전체 — 흘림은 비대칭),
/// 단면(cross·side) 정규 격자의 상대 유속 u·v·w/V∞ · Cp · 면 수직 와도 ω_n·d/V∞, 힘·모멘트 계수 시계열(forces.csv).
/// 좌표 = CFD 물체 좌표(동체 축과 같음, m): 진행 방향 d = (cos α, 0, sin α), 자유류 −V∞·d, θ = +z 에서 +y 쪽으로 잰 각.
/// 큰 이진 파일(float32 LE)은 프레임 단위로 필요할 때 읽는다(재생 중 메모리 절약). 앱의 'OpenFOAM 재생' 모드용 Core API.
/// </summary>
public sealed class CfdSequence
{
    public const string Schema = "mns.flow-seq/1";

    public required string Name { get; init; }
    public required string Directory { get; init; }
    public string Body { get; init; } = "";
    public required double AlphaDeg { get; init; }
    public double Mach { get; init; }
    public required double Speed { get; init; }
    public double Rho { get; init; }
    public double Q { get; init; }
    public double ReD { get; init; }
    public required double D { get; init; }
    public double RefArea { get; init; }
    public double Length { get; init; }
    public required double DOverU { get; init; }
    public required double[] TimeS { get; init; }
    public required double[] TimeDu { get; init; }
    public required SurfaceGrid Surface { get; init; }
    public required IReadOnlyList<PlaneGrid> Planes { get; init; }
    public string ForcesFile { get; init; } = "forces.csv";
    public string Turbulence { get; init; } = "";
    /// <summary>계산 상태: 부분(끝 시각 전, 주기 수 표시) 여부 · 마지막 시각(d/V) · 흘림 주기 수 · 설명.</summary>
    public required SequenceStatus Status { get; init; }
    /// <summary>흘림 Strouhal 수(횡력 CY 스펙트럼 최댓값, 없으면 NaN) · 시간 평균 계수(평균 구간).</summary>
    public double Strouhal { get; init; } = double.NaN;
    public IReadOnlyDictionary<string, double> MeanCoefficients { get; init; } = new Dictionary<string, double>();

    public int FrameCount => TimeS.Length;

    public sealed record SequenceStatus(bool Partial, bool Running, double TLastDu, double EndDu, double Cycles, string Note);

    /// <summary>원통 외형 펼침 격자: 측면 Nx×Nθ(ix 바깥, jθ 안) | 앞 끝면(x = X1) Nr×Nθ | 뒤 끝면(x = X0) Nr×Nθ.</summary>
    public sealed record SurfaceGrid(double Radius, double X0, double X1, int Nx, int Ntheta, int Nr, string File)
    {
        public int ValuesPerFrame => Nx * Ntheta + 2 * Nr * Ntheta;
    }

    /// <summary>단면 정규 격자: 점(iu, iv) = Origin + iu·Du·AxisU + iv·Du·AxisV, 장 순서 Fields, 배열 [장][iv][iu], 물체 안 NaN.</summary>
    public sealed record PlaneGrid(string Name, Vec3 Origin, Vec3 AxisU, Vec3 AxisV, Vec3 Normal, int Nu, int Nv, double Du,
        IReadOnlyList<string> Fields, string File)
    {
        public int ValuesPerField => Nu * Nv;
        public int ValuesPerFrame => Fields.Count * Nu * Nv;
        public Vec3 Point(double iu, double iv) => Origin + AxisU * (iu * Du) + AxisV * (iv * Du);
    }

    /// <summary>힘 계수 시계열 한 행(물체축 CX·CY·CZ, Cl·Cm·Cn, 항력 CD, x–z 면 횡력 CL).</summary>
    public sealed record ForceSample(double TimeS, double TimeDu, double CX, double CY, double CZ, double Cl, double Cm, double Cn, double CD, double CL);

    public static CfdSequence Load(string manifestPath)
    {
        var o = J.ParseObject(File.ReadAllText(manifestPath));
        if (J.Str(o, "schema", "") != Schema) throw new InvalidDataException($"{manifestPath}: schema {Schema} 이 아닙니다.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var fl = o["flow"]!.AsObject();
        var rf = o["ref"]!.AsObject();
        var tm = o["time"]!.AsObject();
        var s = o["surface"]!.AsObject();
        if (J.Str(s, "kind", "") != "cylinder") throw new InvalidDataException($"{manifestPath}: surface.kind 는 cylinder 만 지원합니다.");
        var surf = new SurfaceGrid(J.NumReq(s, "radius", "surface"), J.NumReq(s, "x0", "surface"), J.NumReq(s, "x1", "surface"),
            (int)J.NumReq(s, "nx", "surface"), (int)J.NumReq(s, "ntheta", "surface"), (int)J.NumReq(s, "nr", "surface"), J.Str(s, "file", "surface_cp.f32"));
        var planes = new List<PlaneGrid>();
        if (o["planes"] is JsonArray pa)
            foreach (var pn in pa)
            {
                var p = pn!.AsObject();
                planes.Add(new PlaneGrid(J.StrReq(p, "name", "plane"), V(p, "origin"), V(p, "axisU"), V(p, "axisV"), V(p, "normal"),
                    (int)J.NumReq(p, "nu", "plane"), (int)J.NumReq(p, "nv", "plane"), J.NumReq(p, "du", "plane"),
                    p["fields"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(), J.StrReq(p, "file", "plane")));
            }
        var ts = J.Arr(tm["t_s"]!);
        var td = J.Arr(tm["t_du"]!);
        if (ts.Length != td.Length) throw new InvalidDataException($"{manifestPath}: time.t_s 와 t_du 길이가 다릅니다.");
        var st = o["status"]?.AsObject() ?? new JsonObject();
        var an = o["forces"]?["analysis"]?.AsObject() ?? new JsonObject();
        var mean = new Dictionary<string, double>();
        if (an["mean"] is JsonObject mo)
            foreach (var (k, v) in mo)
                if (v is not null) mean[k] = J.ToDouble(v);
        var seq = new CfdSequence
        {
            Name = J.Str(o, "name", Path.GetFileName(dir)), Directory = dir, Body = J.Str(o, "body", ""),
            AlphaDeg = J.NumReq(fl, "alpha_deg", "flow"), Mach = J.Num(fl, "mach", 0), Speed = J.NumReq(fl, "speed", "flow"),
            Rho = J.Num(fl, "rho", 0), Q = J.Num(fl, "q", 0), ReD = J.Num(fl, "re_d", 0),
            D = J.NumReq(rf, "d", "ref"), RefArea = J.Num(rf, "area", 0), Length = J.Num(rf, "length", 0),
            DOverU = J.NumReq(rf, "d_over_u", "ref"), TimeS = ts, TimeDu = td, Surface = surf, Planes = planes,
            ForcesFile = J.Str(o["forces"]?.AsObject() ?? new JsonObject(), "file", "forces.csv"),
            Turbulence = J.Str(o["method"]?.AsObject() ?? new JsonObject(), "turbulence", ""),
            Status = new SequenceStatus(J.Bool(st, "partial", true), J.Bool(st, "running", false), J.Num(st, "t_last_du", double.NaN),
                J.Num(st, "end_du", double.NaN), J.Num(st, "cycles", 0), J.Str(st, "note", "")),
            Strouhal = J.Num(an, "St", double.NaN), MeanCoefficients = mean,
        };
        seq.CheckFileSizes();
        return seq;
    }

    private static Vec3 V(JsonObject o, string k)
    {
        var a = J.Arr(o[k] ?? throw new InvalidDataException($"plane.{k} 없음"));
        return new Vec3(a[0], a[1], a[2]);
    }

    /// <summary>이진 파일 길이 = 프레임 수 × 프레임당 값 × 4 B 인지(쓰는 중 잘린 파일 방지).</summary>
    private void CheckFileSizes()
    {
        void Check(string file, int per)
        {
            var p = Path.Combine(Directory, file);
            if (!File.Exists(p)) throw new FileNotFoundException($"시퀀스 파일 없음: {p}");
            long want = (long)FrameCount * per * 4;
            if (new FileInfo(p).Length != want) throw new InvalidDataException($"{p}: 길이 {new FileInfo(p).Length} B ≠ 프레임 {FrameCount} × {per} × 4 B");
        }
        Check(Surface.File, Surface.ValuesPerFrame);
        foreach (var pl in Planes) Check(pl.File, pl.ValuesPerFrame);
    }

    private float[] ReadBlock(string file, long offsetValues, int count)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("float32 LE 시퀀스는 리틀 엔디언에서만 읽는다.");
        var res = new float[count];
        using var f = File.OpenRead(Path.Combine(Directory, file));
        f.Seek(offsetValues * 4, SeekOrigin.Begin);
        var buf = new byte[count * 4];
        f.ReadExactly(buf);
        Buffer.BlockCopy(buf, 0, res, 0, buf.Length);
        return res;
    }

    private void CheckFrame(int i)
    {
        if (i < 0 || i >= FrameCount) throw new ArgumentOutOfRangeException(nameof(i), $"프레임 {i} (0…{FrameCount - 1})");
    }

    /// <summary>프레임 i 의 표면 Cp 전체([측면 | 앞 끝면 | 뒤 끝면]).</summary>
    public float[] ReadSurfaceFrame(int i)
    {
        CheckFrame(i);
        return ReadBlock(Surface.File, (long)i * Surface.ValuesPerFrame, Surface.ValuesPerFrame);
    }

    public PlaneGrid Plane(string name) => Planes.FirstOrDefault(p => p.Name == name) ?? throw new KeyNotFoundException($"단면 없음: {name}");

    /// <summary>프레임 i 단면 장 하나([iv][iu], Nv×Nu — 물체 안 NaN).</summary>
    public float[] ReadPlaneField(string plane, int i, string field)
    {
        CheckFrame(i);
        var pl = Plane(plane);
        int k = pl.Fields.ToList().IndexOf(field);
        if (k < 0) throw new KeyNotFoundException($"단면 {plane} 에 장 {field} 없음({string.Join(",", pl.Fields)})");
        return ReadBlock(pl.File, (long)i * pl.ValuesPerFrame + (long)k * pl.ValuesPerField, pl.ValuesPerField);
    }

    /// <summary>재생 시각(d/V) → 프레임 번호. loop = 시퀀스 길이로 되감기(반복 재생).</summary>
    public int FrameAt(double tDu, bool loop = true)
    {
        if (FrameCount == 0) return -1;
        double t0 = TimeDu[0], t1 = TimeDu[^1];
        if (loop && t1 > t0)
        {
            double span = t1 - t0 + (FrameCount > 1 ? (t1 - t0) / (FrameCount - 1) : 0);
            tDu = t0 + ((tDu - t0) % span + span) % span;
        }
        int idx = Array.BinarySearch(TimeDu, tDu);
        if (idx >= 0) return idx;
        idx = ~idx;
        if (idx <= 0) return 0;
        if (idx >= FrameCount) return FrameCount - 1;
        return tDu - TimeDu[idx - 1] <= TimeDu[idx] - tDu ? idx - 1 : idx;
    }

    /// <summary>표면 프레임 값을 CFD 물체 좌표 점에서(가장 가까운 외형 면 격자 쌍선형, θ 주기). 원통 밖·안 점은 투영.</summary>
    public double SurfaceValue(float[] frame, Vec3 p)
    {
        var g = Surface;
        double r = Math.Sqrt(p.Y * p.Y + p.Z * p.Z);
        double th = (Math.Atan2(p.Y, p.Z) * 180 / Math.PI + 360) % 360;
        double jt = th / 360 * g.Ntheta;
        double dSide = Math.Abs(g.Radius - r) + Math.Max(0, g.X0 - p.X) + Math.Max(0, p.X - g.X1);
        double dFwd = Math.Abs(p.X - g.X1) + Math.Max(0, r - g.Radius), dAft = Math.Abs(p.X - g.X0) + Math.Max(0, r - g.Radius);
        if (dSide <= dFwd && dSide <= dAft)
            return Periodic(frame, 0, g.Nx, g.Ntheta, (p.X - g.X0) / (g.X1 - g.X0) * (g.Nx - 1), jt);
        int off = g.Nx * g.Ntheta + (dFwd <= dAft ? 0 : g.Nr * g.Ntheta);
        return Periodic(frame, off, g.Nr, g.Ntheta, Math.Min(r / g.Radius, 1) * (g.Nr - 1), jt);
    }

    private static double Periodic(float[] v, int off, int n0, int nt, double u, double w)
    {
        u = Math.Clamp(u, 0, n0 - 1);
        int i = Math.Min((int)u, Math.Max(n0 - 2, 0));
        double fu = n0 == 1 ? 0 : u - i;
        int j = (int)Math.Floor(w);
        double fw = w - j;
        j = ((j % nt) + nt) % nt;
        int j1 = (j + 1) % nt, i1 = Math.Min(i + 1, n0 - 1);
        double V(int a, int b) => v[off + a * nt + b];
        return (1 - fu) * ((1 - fw) * V(i, j) + fw * V(i, j1)) + fu * ((1 - fw) * V(i1, j) + fw * V(i1, j1));
    }

    /// <summary>단면 장 값을 CFD 물체 좌표 점에서(면에 투영 후 쌍선형, 격자 밖·물체 안 NaN).</summary>
    public static double PlaneValue(PlaneGrid pl, float[] field, Vec3 p)
    {
        var rel = p - pl.Origin;
        double u = Vec3.Dot(rel, pl.AxisU) / pl.Du, w = Vec3.Dot(rel, pl.AxisV) / pl.Du;
        if (u < 0 || w < 0 || u > pl.Nu - 1 || w > pl.Nv - 1) return double.NaN;
        int i = Math.Min((int)u, pl.Nu - 2), j = Math.Min((int)w, pl.Nv - 2);
        double fu = u - i, fw = w - j;
        double F(int a, int b) => field[b * pl.Nu + a];
        return (1 - fu) * ((1 - fw) * F(i, j) + fw * F(i, j + 1)) + fu * ((1 - fw) * F(i + 1, j) + fw * F(i + 1, j + 1));
    }

    /// <summary>forces.csv 전체(머리말 t_s,t_du,CX,CY,CZ,Cl,Cm,Cn,CD,CL).</summary>
    public IReadOnlyList<ForceSample> ReadForces()
    {
        var p = Path.Combine(Directory, ForcesFile);
        if (!File.Exists(p)) return [];
        var lines = File.ReadAllLines(p);
        if (lines.Length == 0) return [];
        var head = lines[0].Split(',').Select(x => x.Trim()).ToList();
        int Col(string k) => head.IndexOf(k) is var c and >= 0 ? c : throw new InvalidDataException($"{p}: 열 {k} 없음");
        int[] c = new[] { "t_s", "t_du", "CX", "CY", "CZ", "Cl", "Cm", "Cn", "CD", "CL" }.Select(Col).ToArray();
        var res = new List<ForceSample>(lines.Length - 1);
        foreach (var ln in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(ln)) continue;
            var t = ln.Split(',');
            double X(int k) => double.Parse(t[c[k]], NumberStyles.Float, CultureInfo.InvariantCulture);
            res.Add(new ForceSample(X(0), X(1), X(2), X(3), X(4), X(5), X(6), X(7), X(8), X(9)));
        }
        return res;
    }
}

/// <summary>
/// 시퀀스 요약 색인(schema mns.flow-seq-index/1, config/aero_db/&lt;body&gt;_flowseq.json): 조건별 St·평균 계수·주기 수·부분 여부 ·
/// 미리보기(CY·CD 시계열, 시간 평균 표면 Cp) 와 큰 시퀀스 manifest 의 저장소 기준 상대 경로(results/cfd/flowseq/…, git 밖 —
/// 없으면 미리보기만 쓸 수 있다).
/// </summary>
public sealed class CfdSequenceIndex
{
    public const string Schema = "mns.flow-seq-index/1";

    public required string IndexPath { get; init; }
    public required IReadOnlyList<Entry> Sequences { get; init; }

    public sealed record Entry(string Name, string Group, string Label, double Alpha, double Mach, double Speed, string Manifest,
        int Frames, bool Partial, string Note, double Strouhal, double Cycles, IReadOnlyDictionary<string, double> Mean,
        double[] PreviewTimeDu, double[] PreviewCY, double[] MeanCp);

    public static CfdSequenceIndex Load(string path)
    {
        var o = J.ParseObject(File.ReadAllText(path));
        if (J.Str(o, "schema", "") != Schema) throw new InvalidDataException($"{path}: schema {Schema} 이 아닙니다.");
        var list = new List<Entry>();
        foreach (var en in o["sequences"]!.AsArray())
        {
            var e = en!.AsObject();
            var st = e["status"]?.AsObject() ?? new JsonObject();
            var mean = new Dictionary<string, double>();
            if (e["mean"] is JsonObject mo)
                foreach (var (k, v) in mo)
                    if (v is not null) mean[k] = J.ToDouble(v);
            var pv = e["preview"] as JsonObject;
            list.Add(new Entry(J.StrReq(e, "name", "sequence"), J.Str(e, "group", ""), J.Str(e, "label", ""), J.NumReq(e, "alpha", "sequence"),
                J.Num(e, "mach", 0), J.Num(e, "speed", 0), J.StrReq(e, "manifest", "sequence"), (int)J.Num(e, "frames", 0),
                J.Bool(st, "partial", true), J.Str(st, "note", ""), J.Num(e, "St", double.NaN), J.Num(e, "cycles", 0), mean,
                pv?["t_du"] is JsonNode a ? J.Arr(a) : [], pv?["CY"] is JsonNode b ? J.Arr(b) : [],
                e["meanCp"] is JsonArray mc ? J.Arr(mc) : []));
        }
        return new CfdSequenceIndex { IndexPath = Path.GetFullPath(path), Sequences = list };
    }

    /// <summary>manifest 절대 경로: 저장소 루트(색인이 config/aero_db/ 에 있으면 두 단계 위, 또는 지정) 기준.</summary>
    public string ManifestPath(Entry e, string? repoRoot = null)
    {
        repoRoot ??= Path.GetFullPath(Path.Combine(Path.GetDirectoryName(IndexPath)!, "..", ".."));
        return Path.GetFullPath(Path.Combine(repoRoot, e.Manifest));
    }

    /// <summary>큰 시퀀스를 열 수 있으면 열고(없으면 null — 미리보기만).</summary>
    public CfdSequence? TryOpen(Entry e, string? repoRoot = null)
    {
        var p = ManifestPath(e, repoRoot);
        return File.Exists(p) ? CfdSequence.Load(p) : null;
    }
}
