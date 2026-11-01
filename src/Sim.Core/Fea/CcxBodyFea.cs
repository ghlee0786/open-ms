using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>
/// Gmsh 메시 도구(tools/fea/mesh_geometry.py) 외부 호출(FR-FEA-03). python 위치: MNS_PYTHON → PATH 의 python.
/// 스크립트 위치: MNS_GMSH_TOOL → 실행 폴더·현재 폴더에서 위로 올라가며 tools/fea/mesh_geometry.py.
/// </summary>
public static class GmshMesher
{
    public const int ExitMultiShell = 3;

    public static string? FindScript()
    {
        if (System.Environment.GetEnvironmentVariable("MNS_GMSH_TOOL") is { Length: > 0 } env) return File.Exists(env) ? env : null;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            while (d is not null)
            {
                var p = Path.Combine(d.FullName, "tools", "fea", "mesh_geometry.py");
                if (File.Exists(p)) return p;
                d = d.Parent;
            }
        }
        return null;
    }

    /// <summary>STEP/STL → C3D10(order 2) 또는 C3D4 메시. 실패하면 Mesh = null, Code = 종료 코드(−1 = 실행 불가), Message = 이유.</summary>
    /// <param name="body">STEP 솔리드 번호(가져온 순서, 1부터) — 조립 STEP 에서 부품 하나만(mesh_geometry.py --body).</param>
    public static (FeMesh? Mesh, int Code, string Message, JsonObject? Summary) Mesh(string input, double size, string workDir, int order = 2, TimeSpan? timeout = null, int? body = null)
    {
        var script = FindScript();
        if (script is null) return (null, -1, "Gmsh 도구(tools/fea/mesh_geometry.py)를 찾지 못했습니다(MNS_GMSH_TOOL 지정).", null);
        Directory.CreateDirectory(workDir);
        var outInp = Path.Combine(workDir, "mesh.inp");
        var summary = Path.Combine(workDir, "mesh_summary.json");
        foreach (var f in new[] { outInp, summary }) if (File.Exists(f)) File.Delete(f);
        var py = System.Environment.GetEnvironmentVariable("MNS_PYTHON") is { Length: > 0 } p ? p : "python";
        var psi = new ProcessStartInfo(py)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workDir,
        };
        foreach (var a in new[] { script, input, "--out", outInp, "--size", size.ToString("G9", CultureInfo.InvariantCulture), "--order", order.ToString(CultureInfo.InvariantCulture), "--summary", summary })
            psi.ArgumentList.Add(a);
        if (body is { } bi) { psi.ArgumentList.Add("--body"); psi.ArgumentList.Add(bi.ToString(CultureInfo.InvariantCulture)); }
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (null, -1, $"{py} 를 시작할 수 없습니다.", null);
            var o = proc.StandardOutput.ReadToEndAsync();
            var e = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(10)).TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { }
                return (null, -1, "Gmsh 시간 초과", null);
            }
            proc.WaitForExit();
            var err = e.GetAwaiter().GetResult() + o.GetAwaiter().GetResult();
            JsonObject? sum = File.Exists(summary) ? JsonNode.Parse(File.ReadAllText(summary)) as JsonObject : null;
            if (proc.ExitCode != 0)
            {
                var msg = sum?["error"]?.GetValue<string>() ?? err.Trim().Split('\n').LastOrDefault() ?? "";
                if (msg.Contains("No module named", StringComparison.OrdinalIgnoreCase)) msg = "python 에 gmsh 가 없습니다(pip install gmsh): " + msg;
                return (null, proc.ExitCode, msg, sum);
            }
            var mesh = FeMesh.ReadInp(outInp);
            mesh.Source = $"Gmsh {(order == 2 ? "C3D10" : "C3D4")} h={size * 1000:G4} mm";
            return (mesh, 0, "", sum);
        }
        catch (System.ComponentModel.Win32Exception ex) { return (null, -1, $"python 실행 불가({py}): {ex.Message}", null); }
        catch (Exception ex) { return (null, -1, $"Gmsh 실패: {ex.Message}", null); }
    }
}

/// <summary>물체 FE 모델: 메시 + 재질(질량 환산 밀도) + 표시 메시 대응.</summary>
public sealed class BodyFeModel
{
    public required FeMesh Mesh { get; init; }
    /// <summary>질량 환산 밀도 재질(FE 질량 = 물체 질량, 내용물 질량을 구조에 퍼뜨림).</summary>
    public required Material Material { get; init; }
    /// <summary>원 재질(강도 판정용).</summary>
    public required Material BaseMaterial { get; init; }
    public double DensityScale { get; init; } = 1;
    public bool Shell { get; init; }
    public required TriMesh Display { get; init; }
    public List<FeFace> Surface { get; init; } = new();
    public double Size { get; init; }
    public List<string> Notes { get; } = new();
    public string BodyName { get; init; } = "";
    public double AttachRadius { get; init; } = 0.01;
    public double InternalPressure { get; init; }
}

/// <summary>
/// 하중 순간 입력(동체 FRD, m·N·Pa): 각속도, 기록 가속도(비교용), 연결점 힘, 표시 면 압력(+ 누름).
/// PatchRadius &gt; 0 이면 연결점 힘을 그 반경 안 표면 절점에 나눈다(사출판처럼 넓게 미는 하중).
/// </summary>
public sealed record FeaLoadInput(string Name, double Time, Vec3 Omega, Vec3 RecordedAccel, Vec3 RecordedAlpha,
    IReadOnlyList<PointLoad> Points, Vec3[] FaceCentroid, double[] FacePressure, double PatchRadius = 0)
{
    /// <summary>FE 에 걸리는 외력 크기 지표: |연결점 합 + 압력 합력| + ½ Σ|p|·A (대칭 압력도 반영). 면적 없으면 압력 항 생략.</summary>
    public double Severity(IReadOnlyList<Vec3>? normals = null, IReadOnlyList<double>? areas = null)
    {
        var net = Points.Aggregate(Vec3.Zero, (s, p) => s + p.Force);
        double mag = 0;
        if (normals is not null && areas is not null && FacePressure.Length == normals.Count)
            for (int i = 0; i < FacePressure.Length; i++) { net += normals[i] * (-FacePressure[i] * areas[i]); mag += Math.Abs(FacePressure[i]) * areas[i]; }
        return net.Norm + 0.5 * mag;
    }
}

/// <summary>관성 완화(수동) 결과: FE 질량특성과 외력에서 구한 강체 가속도, 반력 합(≈ 0 이어야 함).</summary>
public sealed record InertiaRelief(double Mass, Vec3 Cg, Vec3 ExternalForce, Vec3 ExternalMoment, Vec3 Accel, Vec3 Alpha, int[] Supports)
{
    public double AccelG => Accel.Norm / 9.80665;
    /// <summary>건 하중 크기 합 Σ|F|(면 압력·절점 힘) [N] — 반력 잔차 판정 기준(자기평형 하중이면 순합 F 는 0 근처라 기준이 못 된다).</summary>
    public double GrossLoad { get; init; }
    /// <summary>정정 지지 반력이 이 값을 넘으면 풀이 이상: max(1 N, 10⁻³·Σ|F|).</summary>
    public double ReactionTolerance => Math.Max(1, 1e-3 * GrossLoad);
}

/// <summary>물체 FEA 결과(표시 메시 면·정점 기준).</summary>
public sealed class BodyFeaResult
{
    public required string Analysis { get; init; }
    public double[] FaceVonMises { get; init; } = [];
    public double[] FaceSafety { get; init; } = [];
    public double[] FaceDisplacement { get; init; } = [];
    /// <summary>표시 메시 정점(3×면) 변위 [m] — 변형 형상 표시.</summary>
    public Vec3[] VertexDisplacement { get; init; } = [];
    public double MaxVonMises { get; init; }
    public Vec3 MaxVonMisesAt { get; init; }
    public double MaxDisplacement { get; init; }
    public double MinSafety { get; init; } = double.PositiveInfinity;
    public double[] FrequenciesHz { get; init; } = [];
    public double[] BucklingFactors { get; init; } = [];
    public int Mode { get; init; }
    public Vec3? Reaction { get; init; }
    public InertiaRelief? Relief { get; init; }
    public string Verdict { get; init; } = "";
    public double PeakPlasticStrain { get; init; }

    public JsonObject ToJson() => new()
    {
        ["analysis"] = Analysis, ["mode"] = Mode,
        ["maxVonMises_Pa"] = Fin(MaxVonMises), ["maxVonMisesAt_brf_m"] = J.ToJson(MaxVonMisesAt), ["maxDisplacement_m"] = Fin(MaxDisplacement),
        ["minSafety"] = Fin(MinSafety), ["peakPlasticStrain"] = Fin(PeakPlasticStrain),
        ["frequencies_Hz"] = new JsonArray(FrequenciesHz.Select(f => (JsonNode)f).ToArray()),
        ["bucklingFactors"] = new JsonArray(BucklingFactors.Select(f => (JsonNode)f).ToArray()),
        ["reaction_N"] = Reaction is { } r ? J.ToJson(r) : null,
        ["inertiaRelief"] = Relief is { } ir ? new JsonObject
        {
            ["mass_kg"] = ir.Mass, ["cg_brf_m"] = J.ToJson(ir.Cg), ["externalForce_N"] = J.ToJson(ir.ExternalForce),
            ["accel_mps2"] = J.ToJson(ir.Accel), ["accel_g"] = ir.AccelG, ["alpha_rps2"] = J.ToJson(ir.Alpha),
        } : null,
        ["verdict"] = Verdict,
    };

    private static double? Fin(double v) => double.IsFinite(v) ? v : null;
}

/// <summary>
/// 시뮬레이션 물체 → CalculiX 정적·진동·좌굴(FR-FEA-01·02·03):
/// 메시(얇은 벽 = 표시 메시 S6 셸, 솔리드 = Gmsh C3D10 → 없으면 복셀 C3D8I), 하중 순간(기록 프레임 또는 loadcases.json),
/// 수동 관성 완화(자기평형 d'Alembert 하중 + 3-2-1 정정 지지), 결과를 표시 메시 면 색으로.
/// FE 좌표 = 동체 FRD(물체 기준점 원점) — StressModel·loadcases 와 같다.
/// </summary>
public static class CcxBodyFea
{
    public static Material WithDensity(Material m, double rho) => new()
    {
        Key = m.Key, Name = m.Name, Class = m.Class, Brittle = m.Brittle, Density = rho, E = m.E, Nu = m.Nu, Yield = m.Yield, Ultimate = m.Ultimate,
        Elongation = m.Elongation, JC = m.JC, DifK = m.DifK, DifEpsDot0 = m.DifEpsDot0, Ply = m.Ply, Layup = m.Layup, DefaultThickness = m.DefaultThickness,
        Aliases = m.Aliases, Source = m.Source,
    };

    /// <summary>
    /// 기본 요소 크기(솔리드): min(최대 치수 / 25, ∛(부피 / 5000)) — 가는 부품(스파이크·날개)도 두께 방향 요소가 생기게.
    /// 셸은 표시 메시 그대로.
    /// </summary>
    public static double DefaultSize(TriMesh display)
    {
        var (min, max) = display.Bounds();
        var s = max - min;
        double h = Math.Max(s.X, Math.Max(s.Y, s.Z)) / 25;
        try
        {
            var (vol, _, _) = display.VolumeProperties();
            if (vol > 0) h = Math.Min(h, Math.Cbrt(vol / 5000));
        }
        catch (InvalidDataException) { }
        return h;
    }

    /// <summary>
    /// 물체 FE 메시. mesher: "auto"(Gmsh → 실패 시 복셀) | "gmsh" | "voxel". 얇은 벽(structure.section=thinWall)은 항상 표시 메시 S6 셸.
    /// </summary>
    /// <param name="cadPath">CAD 원본(Fusion STEP 권장 — 바디 간 접촉면 결합, 또는 STL). 있으면 표시 메시 대신 이것을 Gmsh 로 메시하고
    /// cadToBody(기본 = 물체의 modelFrame·noseAxis 회전, 단위 m)로 동체 좌표에 놓는다.</param>
    public static BodyFeModel BuildModel(Body b, TriMesh display, string workDir, double? size = null, string mesher = "auto", string? cadPath = null, Mat3? cadToBody = null)
    {
        var mat = b.Structure?.Material ?? throw new InvalidDataException($"{b.Name}: 재질(structure.material 또는 Fusion 재질)이 없어 FEA 를 할 수 없습니다.");
        // CAD 원본(STEP)은 실제 벽 두께를 가진 형상이므로 얇은 벽 물체도 솔리드 C3D10 으로 메시(표시 메시 1:1 셸은 면이 많으면 ccx 메모리 초과 — 실측)
        bool shell = b.Structure.Section == "thinWall" && cadPath is null;
        double h = size ?? DefaultSize(display);
        if (!(h > 0)) throw new InvalidDataException("요소 크기가 양수여야 합니다.");
        var notes = new List<string>();
        FeMesh mesh;
        if (shell)
        {
            double t = b.Structure.Thickness ?? throw new InvalidDataException($"{b.Name}: 얇은 벽에는 두께가 필요합니다.");
            // 셸 요소는 표시 메시 면과 1:1 — 면이 너무 많으면 거칠게(구조 격자 기본 형상은 이미 적당)
            mesh = FeMesh.ShellFromTriangles(display, t);
            notes.Add($"얇은 벽 → S6 셸 {mesh.Elements.Count:N0}개(표시 면 1:1), t = {t * 1000:G4} mm");
        }
        else
        {
            FeMesh? m = null;
            if (cadPath is not null && mesher == "voxel") throw new InvalidDataException("CAD 원본 메시는 Gmsh 로만 만듭니다(--mesher auto|gmsh).");
            if (mesher is "auto" or "gmsh")
            {
                var dir = Path.Combine(workDir, "gmsh");
                Directory.CreateDirectory(dir);
                string input;
                if (cadPath is not null)
                {
                    if (!File.Exists(cadPath)) throw new FileNotFoundException($"CAD 파일이 없습니다: {cadPath}");
                    input = Path.GetFullPath(cadPath);
                }
                else
                {
                    input = Path.Combine(dir, "body.stl");
                    display.SaveBinaryStl(input);
                }
                var (gm, code, msg, _) = GmshMesher.Mesh(input, h, dir);
                if (gm is not null && cadPath is not null)
                {
                    var rot = cadToBody ?? b.ModelToBody;
                    for (int i = 0; i < gm.Nodes.Count; i++) gm.Nodes[i] = rot * gm.Nodes[i];
                    var (fmin, fmax) = gm.Bounds(); var (dmin, dmax) = display.Bounds();
                    double mis = ((fmin - dmin).Norm + (fmax - dmax).Norm) / Math.Max((dmax - dmin).Norm, 1e-9);
                    notes.Add($"CAD 원본 {Path.GetFileName(cadPath)} → Gmsh C3D10 {gm.Elements.Count:N0}개, h = {h * 1000:G4} mm, 동체 좌표 변환 후 표시 메시와 경계 상자 차 {mis:P1}");
                    if (mis > 0.05) notes.Add("경고: CAD 메시와 표시 메시의 위치가 5% 이상 다릅니다 — 단위(STEP 은 m 로 변환)·modelFrame·noseAxis 확인");
                    m = gm;
                }
                else if (gm is not null) { m = gm; notes.Add($"Gmsh C3D10 {gm.Elements.Count:N0}개, h = {h * 1000:G4} mm"); }
                else if (cadPath is not null) throw new InvalidOperationException($"CAD Gmsh 메시 실패(코드 {code}): {msg}");
                else if (mesher == "gmsh") throw new InvalidOperationException($"Gmsh 메시 실패(코드 {code}): {msg}");
                else notes.Add($"Gmsh 사용 불가(코드 {code}: {msg}) → 복셀 대체");
            }
            if (m is null)
            {
                double hv = h;
                for (int k = 0; ; k++)
                {
                    try { m = FeMesh.Voxel(display, hv, 60_000); break; }
                    catch (InvalidDataException) when (k < 6) { hv *= 1.3; }
                }
                notes.Add($"복셀 C3D20 {m.Elements.Count:N0}개, h = {hv * 1000:G4} mm (계단 표면 — 모서리 국부 응력은 참고용, 면으로 이어진 최대 덩어리만)");
                h = hv;
            }
            int dropped = m.KeepLargestConnected();
            if (dropped > 0) notes.Add($"떨어진 조각 요소 {dropped}개 제외(연결되지 않은 바디)");
            mesh = m;
        }
        var (feMass, _, _) = mesh.MassProperties(mat.Density);
        if (!(feMass > 0)) throw new InvalidDataException($"{b.Name}: FE 모델 질량이 0 입니다(메시 확인).");
        double scale = b.OwnMass > 0 ? b.OwnMass / feMass : 1;
        if (Math.Abs(scale - 1) > 0.02)
            notes.Add($"질량 환산: FE 재질 질량 {feMass:G4} kg → 물체 질량 {b.OwnMass:G4} kg (밀도 ×{scale:G3}{(scale > 1 ? ", 내용물 질량을 구조에 고르게 분포 — 가정" : "")})");
        return new BodyFeModel
        {
            Mesh = mesh, Material = WithDensity(mat, mat.Density * scale), BaseMaterial = mat, DensityScale = scale, Shell = shell, Display = display,
            Surface = shell ? new() : mesh.ExteriorFaces(), Size = h, BodyName = b.Name, AttachRadius = b.Structure.AttachRadius,
            InternalPressure = b.Structure.InternalPressure,
        }.WithNotes(notes);
    }

    private static BodyFeModel WithNotes(this BodyFeModel m, IEnumerable<string> notes) { m.Notes.AddRange(notes); return m; }

    /// <summary>기록 프레임의 하중 순간: 압력(수정 뉴턴, StressModel) · 연결점 힘 · 각속도 · 기록 가속도(프레임 차분).</summary>
    public static FeaLoadInput FromWorld(World w, StressModel model, int frameIndex, string name)
    {
        var frames = w.Recorder.Frames;
        var f = frames[frameIndex];
        var b = model.Body;
        var s = f.Bodies[b.Index];
        int i0 = Math.Max(0, frameIndex - 1), i1 = Math.Min(frames.Count - 1, frameIndex + 1);
        double dt = frames[i1].T - frames[i0].T;
        var acc = dt > 0 ? (frames[i1].Bodies[b.Index].Velocity - frames[i0].Bodies[b.Index].Velocity) / dt : Vec3.Zero;
        var alpha = dt > 0 ? (frames[i1].Bodies[b.Index].Omega - frames[i0].Bodies[b.Index].Omega) / dt : Vec3.Zero;
        var inp = model.InputFrom(w, f);
        var res = model.Evaluate(inp);
        return new FeaLoadInput(name, f.T, s.Omega, s.Q.InverseRotate(acc - w.Env.GravityVector), alpha, inp.PointLoads,
            model.Centroid.ToArray(), res.Pressure.ToArray());
    }

    /// <summary>
    /// 대표 하중 케이스(FR-FEA-07: 사출 G · 개방 충격 · 착지): 물체 질량 × G × g₀ 의 외력을 방향 dir 로 점 at 에 건다
    /// (at 이 없으면 표시 메시에서 −dir 쪽 끝 정점 — 그쪽 면이 밀린다, 예: 기수 착지면 dir = −x 이고 at = 기수 끝).
    /// 관성 완화가 같은 크기의 관성 하중으로 평형을 맞춘다. 공력 압력 없음.
    /// </summary>
    public static FeaLoadInput Synthetic(double mass, TriMesh display, double gLoad, Vec3 dir, Vec3? at = null, string? name = null, double patch = 0)
    {
        if (!(mass > 0) || !double.IsFinite(gLoad) || dir.NormSq == 0) throw new InvalidDataException("대표 하중: 질량·G·방향이 유효해야 합니다.");
        if (!(patch >= 0)) throw new InvalidDataException("하중 패치 반경은 0 이상이어야 합니다.");
        var d = dir.Normalized();
        var p = at ?? display.Vertices.MaxBy(v => Vec3.Dot(v, -d));
        var f = d * (mass * gLoad * 9.80665);
        return new FeaLoadInput(name ?? $"{gLoad:G3} G {d} @ {p}{(patch > 0 ? $" 반경 {patch * 1000:F0} mm" : "")}", 0, Vec3.Zero, f / mass, Vec3.Zero,
            [new PointLoad(p, f, "synthetic")], [], [], patch);
    }

    /// <summary>loadcases.json(FR-STR-05, mns.loadcases/1) + 면 CSV → 하중 순간 목록(물체 이름 필터 선택).</summary>
    public static List<FeaLoadInput> FromLoadCasesJson(string path, string? body = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"loadcases.json 이 없습니다: {path}");
        var root = J.ParseObject(File.ReadAllText(path));
        if (J.Str(root, "schema", "") != "mns.loadcases/1") throw new InvalidDataException($"{path}: schema 가 mns.loadcases/1 이 아닙니다.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var list = new List<FeaLoadInput>();
        foreach (var c in root["cases"]?.AsArray() ?? new JsonArray())
        {
            if (c is not JsonObject o) continue;
            var bn = J.Str(o, "body", "");
            if (body is not null && bn != body) continue;
            var pts = (o["pointLoads"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(p => new PointLoad(J.V3(p, "point_brf_m", Vec3.Zero), J.V3(p, "force_brf_N", Vec3.Zero), J.Str(p, "source", ""))).ToList();
            var cent = new List<Vec3>(); var pr = new List<double>();
            if (J.StrOpt(o, "facesCsv") is { } csv && File.Exists(Path.Combine(dir, csv)))
            {
                foreach (var line in File.ReadLines(Path.Combine(dir, csv)).Skip(1))
                {
                    var t = line.Split(',');
                    if (t.Length < 9) throw new InvalidDataException($"{csv}: 열이 부족합니다.");
                    double D(int k) => double.Parse(t[k], NumberStyles.Float, CultureInfo.InvariantCulture);
                    cent.Add(new Vec3(D(1), D(2), D(3))); pr.Add(D(8));
                }
            }
            list.Add(new FeaLoadInput($"{bn} @ {J.Num(o, "time_s", 0):F3}s {J.Str(o, "reason", "")}", J.Num(o, "time_s", 0),
                J.V3(o, "omegaBody_rps", Vec3.Zero), J.V3(o, "accelNonGravBody_mps2", Vec3.Zero), J.V3(o, "alphaBody_rps2", Vec3.Zero),
                pts, cent.ToArray(), pr.ToArray()));
        }
        return list;
    }

    /// <summary>
    /// 정적(또는 좌굴 기준 하중) 모델: 면 압력 + 연결점 힘(반경 안 표면 절점에 분배) + 수동 관성 완화
    /// (a = F/m, α = I⁻¹(M − ω×Iω) → 요소별 GRAV −(a + α×r), CENTRIF ω) + 3-2-1 정정 지지.
    /// </summary>
    public static (CcxModel Model, InertiaRelief Relief) BuildLoaded(BodyFeModel fm, FeaLoadInput lc, CcxAnalysis analysis = CcxAnalysis.Static, bool nonlinear = false)
    {
        var mesh = fm.Mesh;
        var m = new CcxModel { Mesh = mesh, Material = fm.Material, Analysis = analysis, Nonlinear = nonlinear, Modes = 3, Title = $"{fm.BodyName} {lc.Name}" };
        var (mass, cg, inertia) = mesh.MassProperties(fm.Material.Density);
        var F = Vec3.Zero; var M = Vec3.Zero; double gross = 0;
        // 1) 면 압력: 표시 면 압력 → FE 면(최근접 표시 면 중심)
        if (lc.FacePressure.Length > 0 && lc.FacePressure.Length == lc.FaceCentroid.Length)
        {
            if (fm.Shell)
            {
                // 요소 i = 표시 면 i (같은 바깥 법선). 바깥 압력 p(+ 누름) → 셸 P = −p, 내압 +pin
                int n = Math.Min(mesh.Elements.Count, lc.FacePressure.Length);
                for (int e = 0; e < n; e++)
                {
                    double p = lc.FacePressure[e] - fm.InternalPressure;
                    if (p == 0 || !double.IsFinite(p)) continue;
                    m.Pressures.Add((e, 0, -p));
                    var el = mesh.Elements[e];
                    Vec3 a = mesh.Nodes[el.N[0]], b = mesh.Nodes[el.N[1]], c = mesh.Nodes[el.N[2]];
                    var nA = Vec3.Cross(b - a, c - a) * 0.5;
                    var force = nA * -p;
                    var ctr = (a + b + c) / 3;
                    gross += force.Norm;
                    F += force; M += Vec3.Cross(ctr - cg, force);
                }
            }
            else
            {
                var hash = new SpatialHash(lc.FaceCentroid, Math.Max(fm.Size, 1e-6));
                foreach (var face in fm.Surface)
                {
                    int k = hash.Nearest(face.Center, out _, 24);
                    if (k < 0) continue;
                    double p = lc.FacePressure[k];
                    if (p == 0 || !double.IsFinite(p)) continue;
                    m.Pressures.Add((face.Element, face.Face, p));
                    var force = face.Normal * (-p * face.Area);
                    gross += force.Norm;
                    F += force; M += Vec3.Cross(face.Center - cg, force);
                }
            }
        }
        // 2) 연결점 힘: 반경 안 표면 절점(없으면 최근접 4개)에 고르게
        var usedNodes = mesh.UsedNodes();
        var surfNodes = fm.Shell ? Enumerable.Range(0, mesh.Nodes.Count).Where(i => usedNodes[i]).ToList()
            : fm.Surface.SelectMany(f => f.Corners).Distinct().ToList();
        var loaded = new HashSet<int>();
        foreach (var pl in lc.Points)
        {
            if (!pl.Force.IsFinite || pl.Force.NormSq == 0) continue;
            double r = lc.PatchRadius > 0 ? lc.PatchRadius : Math.Max(fm.AttachRadius, 1.5 * fm.Size);
            var near = surfNodes.Where(n => (mesh.Nodes[n] - pl.Point).Norm <= r).ToList();
            if (near.Count == 0) near = surfNodes.OrderBy(n => (mesh.Nodes[n] - pl.Point).NormSq).Take(4).ToList();
            foreach (var n in near)
            {
                loaded.Add(n);
                var f = pl.Force / near.Count;
                m.NodalForces.Add((n, f));
                gross += f.Norm;
                F += f; M += Vec3.Cross(mesh.Nodes[n] - cg, f);
            }
        }
        // 3) 관성 완화
        var w = lc.Omega;
        var a0 = mass > 0 ? F / mass : Vec3.Zero;
        var alpha = inertia.Determinant() > 0 ? inertia.Inverse() * (M - Vec3.Cross(w, inertia * w)) : Vec3.Zero;
        var ea = new Vec3[mesh.Elements.Count];
        for (int e = 0; e < ea.Length; e++) ea[e] = -(a0 + Vec3.Cross(alpha, mesh.ElementCentroid(e) - cg));
        m.ElementAccel = ea;
        if (w.NormSq > 0) m.Centrif = (w, cg);
        // 4) 3-2-1 정정 지지 — 집중 하중 절점은 피한다(구속 자유도에 건 힘은 그대로 반력이 됨: 끝 하중 = 가장 먼 절점이라 실측 9% 반력)
        var sup = Supports321(mesh, cg, loaded);
        m.Fixed.AddRange(sup.Fixed);
        mesh.NodeSets["SUPPORT"] = sup.Fixed.Select(x => x.Node).Distinct().ToList();
        m.ReactionSet = "SUPPORT";
        return (m, new InertiaRelief(mass, cg, F, M, a0, alpha, sup.Fixed.Select(x => x.Node).Distinct().ToArray()) { GrossLoad = gross });
    }

    /// <summary>
    /// 3-2-1 정정 지지(자기평형 하중에서 반력 0): n1 = CG 에서 가장 먼 절점(x,y,z), n2 = n1 에서 가장 먼 절점(n1→n2 방향과 가장 가까운 축 제외 2개),
    /// n3 = n1–n2 선에서 가장 먼 절점(세 점 평면 법선과 가장 가까운 축 1개).
    /// </summary>
    public static (List<(int Node, int First, int Last)> Fixed, int[] Nodes) Supports321(FeMesh mesh, Vec3 cg, IReadOnlySet<int>? avoid = null)
    {
        var used = mesh.UsedNodes();
        var ids = Enumerable.Range(0, mesh.Nodes.Count).Where(i => used[i] && (avoid is null || !avoid.Contains(i))).ToList();
        if (ids.Count < 3) ids = Enumerable.Range(0, mesh.Nodes.Count).Where(i => used[i]).ToList();
        int n1 = ids.MaxBy(i => (mesh.Nodes[i] - cg).NormSq);
        int n2 = ids.MaxBy(i => (mesh.Nodes[i] - mesh.Nodes[n1]).NormSq);
        var d12 = (mesh.Nodes[n2] - mesh.Nodes[n1]).Normalized();
        int n3 = ids.MaxBy(i => Vec3.Cross(mesh.Nodes[i] - mesh.Nodes[n1], d12).NormSq);
        var nrm = Vec3.Cross(d12, mesh.Nodes[n3] - mesh.Nodes[n1]).Normalized();
        static int Axis(Vec3 v) { var a = new[] { Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z) }; return Array.IndexOf(a, a.Max()) + 1; }
        int ax12 = Axis(d12), axN = Axis(nrm);
        var fixedList = new List<(int, int, int)> { (n1, 1, 3) };
        foreach (int d in new[] { 1, 2, 3 }) if (d != ax12) fixedList.Add((n2, d, d));
        fixedList.Add((n3, axN, axN));
        return (fixedList, [n1, n2, n3]);
    }

    /// <summary>
    /// 자유-자유 고유진동. 구속 없는 강성 행렬은 ccx 고유값 해석에서 가짜 모드를 내므로(실측) 3-2-1 지지 절점에 약한 접지 스프링
    /// (강성 = 모델 대표 강성 E·L × 1e−10)을 단다 → 강체 모드는 ≈ 0 Hz 근처, 탄성 모드는 영향 없음. 강체 모드는 RigidHz 문턱으로 뺀다.
    /// </summary>
    public static CcxModel BuildModal(BodyFeModel fm, int modes = 6)
    {
        var m = new CcxModel { Mesh = fm.Mesh, Material = fm.Material, Analysis = CcxAnalysis.Modal, Modes = modes + 6, Title = $"{fm.BodyName} free-free modal" };
        var (_, cg, _) = fm.Mesh.MassProperties(fm.Material.Density);
        var (min, max) = fm.Mesh.Bounds();
        double k = fm.Material.E * Math.Max((max - min).Norm, 1e-3) * 1e-10;
        foreach (var n in Supports321(fm.Mesh, cg).Nodes.Distinct())
            for (int d = 1; d <= 3; d++) m.GroundSprings.Add((n, d, k));
        return m;
    }

    /// <summary>결과 → 표시 메시 면·정점 값(von Mises 최대, 안전계수, 변위).</summary>
    public static BodyFeaResult Evaluate(BodyFeModel fm, CcxResult r, CcxAnalysis analysis, int mode = 0, InertiaRelief? relief = null)
    {
        if (!r.Ok || r.Frd is null) throw new InvalidOperationException($"CalculiX 결과가 없습니다: {r.Error}");
        var disp = r.Displacement(mode);
        var vm = r.VonMises(mode);
        var s1 = r.MaxPrincipal(mode);
        var peeq = r.Peeq();
        var coords = r.Frd.Coords;
        var keys = coords.Keys.ToArray();
        var pts = keys.Select(k => coords[k]).ToArray();
        var disp0 = fm.Display;
        int nf = disp0.TriangleCount;
        double cell = Math.Max(fm.Size, (fm.Mesh.Thickness ?? 0) * 2);
        // 솔리드: 표면 절점만(내부 절점 배제), 셸: 모든 frd 절점(확장 ±t/2)
        IEnumerable<int>? subset = null;
        if (!fm.Shell)
        {
            var surf = new HashSet<int>(fm.Surface.SelectMany(f => f.Corners).Select(n => n + 1));
            subset = Enumerable.Range(0, keys.Length).Where(i => surf.Contains(keys[i]));
        }
        var hash = new SpatialHash(pts, Math.Max(cell, 1e-6), subset);
        var faceVm = new double[nf]; var faceSf = new double[nf]; var faceU = new double[nf];
        var vtxU = new Vec3[disp0.Vertices.Count];
        var mat = fm.BaseMaterial;
        double Sf(int id)
        {
            double v = vm.GetValueOrDefault(id);
            if (mat.Brittle) { double p = Math.Max(s1.GetValueOrDefault(id), 0); return p > 0 ? mat.Ultimate / p : double.PositiveInfinity; }
            return v > 0 ? mat.Yield / v : double.PositiveInfinity;
        }
        double t2 = (fm.Mesh.Thickness ?? 0) / 2;
        for (int f = 0; f < nf; f++)
        {
            Vec3 a = disp0.Vertices[3 * f], b = disp0.Vertices[3 * f + 1], c = disp0.Vertices[3 * f + 2];
            var n = Vec3.Cross(b - a, c - a).Normalized();
            var probes = fm.Shell
                ? new[] { a + n * t2, a - n * t2, b + n * t2, b - n * t2, c + n * t2, c - n * t2, (a + b + c) / 3 + n * t2, (a + b + c) / 3 - n * t2 }
                : new[] { (a + b + c) / 3, a, b, c };
            double best = 0, sf = double.PositiveInfinity, u = 0;
            foreach (var p in probes)
            {
                int k = hash.Nearest(p, out _, 24);
                if (k < 0) continue;
                int id = keys[k];
                best = Math.Max(best, vm.GetValueOrDefault(id));
                sf = Math.Min(sf, Sf(id));
                if (disp.TryGetValue(id, out var d)) u = Math.Max(u, d.Norm);
            }
            faceVm[f] = best; faceSf[f] = sf; faceU[f] = u;
            for (int j = 0; j < 3; j++)
            {
                int k = hash.Nearest(disp0.Vertices[3 * f + j], out _, 24);
                vtxU[3 * f + j] = k >= 0 && disp.TryGetValue(keys[k], out var d) ? d : Vec3.Zero;
            }
        }
        // 최대값은 FE 절점 전체 기준(표면 매핑과 별개)
        double maxVm = 0; Vec3 at = Vec3.Zero;
        foreach (var (id, v) in vm) if (v > maxVm && coords.TryGetValue(id, out var p)) { maxVm = v; at = p; }
        double maxU = disp.Count > 0 ? disp.Values.Max(d => d.Norm) : 0;
        double minSf = vm.Keys.Select(Sf).DefaultIfEmpty(double.PositiveInfinity).Min();
        if (analysis != CcxAnalysis.Static)
        {
            // 고유진동·좌굴의 응력은 모드 형상 정규화 값이라 크기에 물리적 의미가 없다 — 안전계수를 내지 않는다
            // (실모델 센서 modal 에서 'SF 0.0002' 로 표시되던 문제, 2026-10-07).
            minSf = double.PositiveInfinity;
            Array.Fill(faceSf, double.PositiveInfinity);
        }
        double maxPe = peeq.Count > 0 ? peeq.Values.Max() : 0;
        string verdict = analysis switch
        {
            CcxAnalysis.Modal => r.Dat.FrequenciesHz.Where(x => x > RigidHz(r.Dat.FrequenciesHz)).Take(3) is var fs && fs.Any()
                ? $"탄성 고유진동수 {string.Join(", ", fs.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)))} Hz (자유-자유, 강체 모드 제외)" : "탄성 모드 없음",
            CcxAnalysis.Buckle => r.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min() is var bf && double.IsFinite(bf)
                ? (bf < 1 ? $"좌굴 — 하중 계수 {bf:G3} < 1 (이 하중에서 좌굴)" : $"좌굴 여유 — 하중 계수 {bf:G3}") : "좌굴 계수 없음",
            _ => maxPe > 0.002 ? $"소성 변형 — 등가 소성변형률 최대 {maxPe:G3}, σvm 최대 {maxVm / 1e6:G4} MPa"
                : mat.Brittle ? $"σvm 최대 {maxVm / 1e6:G4} MPa, 최소 안전계수(인장강도/최대 주응력) {minSf:G3}{(minSf < 1 ? " — 파단" : "")}"
                : $"σvm 최대 {maxVm / 1e6:G4} MPa, 최소 안전계수(항복) {minSf:G3}{(minSf < 1 ? " — 항복" : minSf < StressModel.CautionSafety ? " — 주의" : "")}",
        };
        return new BodyFeaResult
        {
            Analysis = analysis.ToString(), FaceVonMises = faceVm, FaceSafety = faceSf, FaceDisplacement = faceU, VertexDisplacement = vtxU,
            MaxVonMises = maxVm, MaxVonMisesAt = at, MaxDisplacement = maxU, MinSafety = minSf, Mode = mode,
            FrequenciesHz = r.Dat.FrequenciesHz.ToArray(), BucklingFactors = r.Dat.BucklingFactors.ToArray(),
            Reaction = r.Dat.ReactionTotal, Relief = relief, Verdict = verdict, PeakPlasticStrain = maxPe,
        };
    }

    /// <summary>
    /// 강체 모드 판정 문턱(자유-자유 + 약한 접지 스프링): 낮은 쪽 6개가 강체 모드 → 6번째와 7번째 진동수의 기하 평균.
    /// 모드가 6개 이하면 0.5 Hz.
    /// </summary>
    public static double RigidHz(IReadOnlyList<double> f)
    {
        var s = f.OrderBy(x => x).ToList();
        return s.Count > 6 ? Math.Sqrt(Math.Max(s[5], 1e-6) * s[6]) : 0.5;
    }

    /// <summary>한 번 실행: 모델 → 덱 → ccx → 표시 메시 결과. 모달은 첫 탄성 모드 형상을 색으로.</summary>
    public static (CcxResult Run, BodyFeaResult? Result) Solve(CalculixInstall inst, BodyFeModel fm, CcxAnalysis analysis, FeaLoadInput? lc, string dir,
        bool nonlinear = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        CcxModel model; InertiaRelief? relief = null;
        if (analysis == CcxAnalysis.Modal) model = BuildModal(fm);
        else
        {
            if (lc is null) throw new InvalidDataException("정적·좌굴 해석에는 하중 순간이 필요합니다.");
            (model, relief) = BuildLoaded(fm, lc, analysis, nonlinear);
        }
        var r = CcxRunner.Solve(inst, model, dir, "fea", timeout ?? TimeSpan.FromMinutes(30), ct);
        if (!r.Ok) return (r, null);
        // 관성 완화 정적 해는 정정 지지 반력 ≈ 0 이어야 한다 — 넘으면 다중 스레드 방정식 풀이 이상(부하 큰 동시 실행에서 드물게)으로 보고
        // 단일 스레드로 한 번 다시 풀어 반력이 작은 쪽을 쓴다(같은 덱, 하위 폴더).
        if (analysis == CcxAnalysis.Static && relief is not null && r.Dat.ReactionTotal is { } rt && rt.Norm > relief.ReactionTolerance)
        {
            var r1 = CcxRunner.Solve(inst, model, Path.Combine(dir, "retry1"), "fea", timeout ?? TimeSpan.FromMinutes(30), ct, threads: 1);
            if (r1.Ok && r1.Dat.ReactionTotal is { } rt1 && rt1.Norm < rt.Norm) r = r1;
        }
        int mode = 0;
        if (analysis == CcxAnalysis.Modal)
        {
            double rigid = RigidHz(r.Dat.FrequenciesHz);
            int k = r.Dat.FrequenciesHz.FindIndex(x => x > rigid);
            mode = k >= 0 ? k + 1 : 1;
        }
        else if (analysis == CcxAnalysis.Buckle) mode = 1;
        return (r, Evaluate(fm, r, analysis, mode, relief));
    }

    /// <summary>
    /// 메시 수렴(FR-FEA-04): 요소 크기 h, h·r, h·r² … 로 정적 해석 → 최대 von Mises 변화율. 마지막 변화 &lt; 5% 면 수렴.
    /// 세분할수록 최대 응력이 계속 커지고 변화율이 줄지 않으면 응력 특이점(날카로운 모서리·점하중) 경고.
    /// </summary>
    public static ConvergenceReport Converge(Func<double, BodyFeModel> build, Func<BodyFeModel, (CcxResult, BodyFeaResult?)> solve, double h0, int levels = 3, double ratio = 0.7)
    {
        var rep = new ConvergenceReport();
        double h = h0;
        for (int i = 0; i < levels; i++, h *= ratio)
        {
            var fm = build(h);
            var (r, res) = solve(fm);
            if (!r.Ok || res is null) { rep.Error = $"h={h * 1000:G4} mm: {r.Error}"; break; }
            rep.Levels.Add(new ConvergenceLevel(h, fm.Mesh.Elements.Count, res.MaxVonMises, res.MaxVonMisesAt, res.MaxDisplacement));
        }
        rep.Judge();
        return rep;
    }
}

/// <summary>
/// 결과 파일(.mnsr fea.json, schema mns.fea/1)에 넣는 물체별 FEA 기록: 솔버·해석·하중 순간·메모 + 표시 면 값.
/// CalculiX(정적·진동·좌굴) 와 OpenCourant(충돌 소성변형률) 공용.
/// </summary>
public sealed class FeaRecord
{
    public const string Schema = "mns.fea/1";
    public required string Body { get; init; }
    public required string Solver { get; init; }
    public string LoadCase { get; init; } = "";
    public List<string> Notes { get; init; } = new();
    public JsonObject Summary { get; init; } = new();
    public double[] FaceVonMises { get; init; } = [];
    public double[] FaceSafety { get; init; } = [];
    public double[] FaceDisplacement { get; init; } = [];
    public double[] FacePlasticStrain { get; init; } = [];
    public Vec3[] VertexDisplacement { get; init; } = [];

    public static FeaRecord FromCcx(string body, BodyFeaResult r, string loadCase, IEnumerable<string> notes) => new()
    {
        Body = body, Solver = "CalculiX", LoadCase = loadCase, Notes = notes.ToList(), Summary = r.ToJson(),
        FaceVonMises = r.FaceVonMises, FaceSafety = r.FaceSafety, FaceDisplacement = r.FaceDisplacement, VertexDisplacement = r.VertexDisplacement,
    };

    private static JsonArray Arr(double[] a) => new(a.Select(x => (JsonNode)(double.IsFinite(x) ? x : -1)).ToArray());
    private static double[] Back(JsonNode? n, bool inf = false) =>
        n is JsonArray a ? a.Select(x => { double v = J.ToDouble(x!); return inf && v < 0 ? double.PositiveInfinity : v; }).ToArray() : [];

    public JsonObject ToJson() => new()
    {
        ["body"] = Body, ["solver"] = Solver, ["loadCase"] = LoadCase, ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
        ["summary"] = Summary.DeepClone(),
        // 안전계수 ∞(응력 0) 는 −1 로 저장
        ["faceVonMises_Pa"] = Arr(FaceVonMises), ["faceSafety"] = Arr(FaceSafety), ["faceDisplacement_m"] = Arr(FaceDisplacement),
        ["facePlasticStrain"] = Arr(FacePlasticStrain),
        ["vertexDisplacement_m"] = new JsonArray(VertexDisplacement.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode)x).ToArray()),
    };

    public static FeaRecord FromJson(JsonObject o)
    {
        var vd = Back(o["vertexDisplacement_m"]);
        return new FeaRecord
        {
            Body = J.Str(o, "body", ""), Solver = J.Str(o, "solver", ""), LoadCase = J.Str(o, "loadCase", ""),
            Notes = (o["notes"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()).ToList(),
            Summary = o["summary"] as JsonObject is { } s ? (JsonObject)s.DeepClone() : new(),
            FaceVonMises = Back(o["faceVonMises_Pa"]), FaceSafety = Back(o["faceSafety"], inf: true), FaceDisplacement = Back(o["faceDisplacement_m"]),
            FacePlasticStrain = Back(o["facePlasticStrain"]),
            VertexDisplacement = Enumerable.Range(0, vd.Length / 3).Select(i => new Vec3(vd[3 * i], vd[3 * i + 1], vd[3 * i + 2])).ToArray(),
        };
    }

    public static JsonObject Pack(IEnumerable<FeaRecord> records) => new()
    {
        ["schema"] = Schema, ["bodies"] = new JsonArray(records.Select(r => (JsonNode)r.ToJson()).ToArray()),
    };

    public static List<FeaRecord> Unpack(JsonObject? o)
    {
        if (o is null) return new();
        if (J.Str(o, "schema", "") != Schema) throw new InvalidDataException($"fea.json schema 가 {Schema} 가 아닙니다.");
        return (o["bodies"] as JsonArray ?? new()).OfType<JsonObject>().Select(FromJson).ToList();
    }
}

public sealed record ConvergenceLevel(double Size, int Elements, double MaxVonMises, Vec3 At, double MaxDisplacement);

/// <summary>메시 수렴 판정 결과.</summary>
public sealed class ConvergenceReport
{
    public const double Tolerance = 0.05;
    public List<ConvergenceLevel> Levels { get; } = new();
    public string? Error { get; set; }
    public bool Converged { get; private set; }
    public bool SingularityWarning { get; private set; }
    public double LastChange { get; private set; } = double.NaN;
    public string Message { get; private set; } = "";

    /// <summary>판정: 마지막 상대 변화 &lt; 5% → 수렴. 단조 증가 + 변화율이 줄지 않음(마지막 ≥ 0.6 × 이전) → 특이점 경고.</summary>
    public void Judge()
    {
        if (Levels.Count < 2) { Message = Error ?? "단계가 2개 미만이라 수렴을 판정할 수 없습니다."; return; }
        var s = Levels.Select(l => l.MaxVonMises).ToList();
        var ch = new List<double>();
        for (int i = 1; i < s.Count; i++) ch.Add(Math.Abs(s[i] - s[i - 1]) / Math.Max(Math.Abs(s[i]), 1e-30));
        LastChange = ch[^1];
        Converged = LastChange < Tolerance;
        bool increasing = s.Zip(s.Skip(1)).All(p => p.Second > p.First);
        SingularityWarning = !Converged && increasing && (ch.Count < 2 || ch[^1] >= 0.6 * ch[^2]);
        var at = Levels[^1].At;
        Message = Converged
            ? $"수렴: 최대 σvm 변화 {LastChange:P1} < {Tolerance:P0} ({Levels.Count}단계, 요소 {Levels[0].Elements:N0} → {Levels[^1].Elements:N0})"
            : SingularityWarning
                ? $"응력 특이점 의심: 세분할수록 최대 σvm 이 계속 증가(마지막 변화 {LastChange:P1}) — 위치 {at} 의 날카로운 모서리·점하중·지지점. 그 점 값은 신뢰하지 말고 주변(반경 2~3 요소) 값이나 필렛 반영 형상으로 판단"
                : $"미수렴: 최대 σvm 변화 {LastChange:P1} ≥ {Tolerance:P0} — 더 세분 필요";
    }

    /// <summary>3 단계 이상이면 마지막 3 단계로 GCI(Celik 2008) — 최대 σvm·최대 변위(FR-REL-03 ② 수치 불확도). 2차 사면체 이론 차수 2(변위)·2(응력, 보수).</summary>
    public (Reliability.GciResult? Stress, Reliability.GciResult? Displacement) GciEstimates()
    {
        if (Levels.Count < 3) return (null, null);
        var l = Levels.TakeLast(3).Reverse().ToList();   // 1 = 가장 촘촘
        return (Reliability.Gci.Compute(l[0].Size, l[1].Size, l[2].Size, l[0].MaxVonMises, l[1].MaxVonMises, l[2].MaxVonMises),
                Reliability.Gci.Compute(l[0].Size, l[1].Size, l[2].Size, l[0].MaxDisplacement, l[1].MaxDisplacement, l[2].MaxDisplacement));
    }

    private static JsonObject? GciJson(Reliability.GciResult? g) => g is null ? null : new JsonObject
    {
        ["p"] = double.IsFinite(g.P) ? g.P : null, ["pUsed"] = g.PUsed, ["fs"] = g.Fs, ["extrapolated"] = g.PhiExt,
        ["approxRelError"] = g.ApproxRelError, ["gci21"] = g.Gci21, ["convergence"] = g.Convergence, ["note"] = g.Note,
    };

    public JsonObject ToJson() => new()
    {
        ["converged"] = Converged, ["singularityWarning"] = SingularityWarning, ["lastChange"] = double.IsFinite(LastChange) ? LastChange : null,
        ["message"] = Message, ["error"] = Error,
        ["gciStress"] = GciJson(GciEstimates().Stress), ["gciDisplacement"] = GciJson(GciEstimates().Displacement),
        ["levels"] = new JsonArray(Levels.Select(l => (JsonNode)new JsonObject
        {
            ["size_m"] = l.Size, ["elements"] = l.Elements, ["maxVonMises_Pa"] = l.MaxVonMises, ["at_m"] = J.ToJson(l.At), ["maxDisplacement_m"] = l.MaxDisplacement,
        }).ToArray()),
    };
}
