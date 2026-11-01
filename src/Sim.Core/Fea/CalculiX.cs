using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>
/// CalculiX(GPL, dhondt.de) 설치 탐지(FR-FEA-01). 외부 프로세스로만 호출 — 링크하지 않는다.
/// 위치: 인자(실행 파일 또는 폴더) → 환경변수 CCX_PATH → C:\tools\CalculiX\calculix_2.23_4win.
/// </summary>
public sealed class CalculixInstall
{
    public required string Ccx { get; init; }
    public string Root => Path.GetDirectoryName(Ccx) ?? "";

    public const string DefaultRoot = @"C:\tools\CalculiX\calculix_2.23_4win";
    private static readonly string[] ExeNames = ["ccx_static.exe", "ccx_dynamic.exe", "ccx_i4.exe", "ccx.exe", "ccx_2.23.exe", "ccx"];

    public static CalculixInstall? Locate(string? path = null) => TryLocate(path, out _);

    public static CalculixInstall? TryLocate(string? path, out string why)
    {
        var cand = path ?? System.Environment.GetEnvironmentVariable("CCX_PATH") ?? DefaultRoot;
        if (string.IsNullOrWhiteSpace(cand)) { why = "CalculiX 경로가 비었습니다(CCX_PATH 지정)."; return null; }
        if (File.Exists(cand)) { why = ""; return new CalculixInstall { Ccx = Path.GetFullPath(cand) }; }
        if (!Directory.Exists(cand)) { why = $"CalculiX 설치 폴더가 없습니다: {cand} (CCX_PATH 에 ccx 실행 파일 또는 폴더 지정)"; return null; }
        foreach (var n in ExeNames)
        {
            var p = Path.Combine(cand, n);
            if (File.Exists(p)) { why = ""; return new CalculixInstall { Ccx = p }; }
        }
        why = $"{cand} 에 CalculiX 실행 파일(ccx_static.exe 등)이 없습니다.";
        return null;
    }
}

/// <summary>ModalDynamic = *FREQUENCY(STORAGE) → *MODAL DYNAMIC 모달 중첩 과도 응답(시간 이력 하중 *AMPLITUDE).</summary>
public enum CcxAnalysis { Static, Modal, Buckle, ModalDynamic }

/// <summary>
/// CalculiX 해석 모델(메시 + 재질 + 경계조건 + 하중 + 해석 종류). 단위 SI(m, N, Pa, kg).
/// 하중: 절점 힘, 요소 면 압력(양수 = 면을 누름), 균일/요소별 체적 가속도(GRAV, 단위 질량당 힘 벡터), 원심(CENTRIF).
/// </summary>
public sealed class CcxModel
{
    public required FeMesh Mesh { get; init; }
    public required Material Material { get; init; }
    public CcxAnalysis Analysis { get; set; } = CcxAnalysis.Static;
    /// <summary>고유진동·좌굴 모드 수.</summary>
    public int Modes { get; set; } = 6;
    /// <summary>비선형 정적(대변형 NLGEOM + 소성 *PLASTIC 이선형). 정적 해석에서만.</summary>
    public bool Nonlinear { get; set; }
    /// <summary>구속: (절점, 첫 자유도, 끝 자유도) 1=x 2=y 3=z (셸은 4~6 회전).</summary>
    public List<(int Node, int First, int Last)> Fixed { get; } = new();
    public List<(int Node, Vec3 Force)> NodalForces { get; } = new();
    /// <summary>
    /// (요소, Abaqus 면 번호, 압력 Pa). 솔리드 면: + 는 면을 요소 안쪽으로 누름. 셸 S6/S8R 은 면 번호 0 = 중립면 압력,
    /// + 는 요소 법선(절점 순서 오른손 법칙) 방향으로 민다(원통 내압 시험으로 확인, CcxBenchmarks.CylinderHoop).
    /// </summary>
    public List<(int Element, int Face, double Pressure)> Pressures { get; } = new();
    /// <summary>균일 체적 가속도 하중(단위 질량당 힘, m/s²) — 중력 또는 d'Alembert 관성.</summary>
    public Vec3 BodyAccel { get; set; } = Vec3.Zero;
    /// <summary>요소별 추가 체적 가속도(관성 완화의 α×r 항 포함 시 요소마다 다름). 있으면 BodyAccel 과 합쳐 요소별로 쓴다.</summary>
    public Vec3[]? ElementAccel { get; set; }
    /// <summary>원심 하중: 각속도 ω [rad/s] 와 축 위의 점.</summary>
    public (Vec3 Omega, Vec3 Point)? Centrif { get; set; }
    /// <summary>
    /// 접지 스프링(SPRING1: 절점, 자유도, 강성 N/m). 자유-자유 고유진동에서 강성 행렬을 정칙으로 만드는 약한 스프링 —
    /// 구속 없는 모델은 ccx 고유값 해석이 가짜 저주파 모드를 실행마다 다르게 낸다(실측 585·875 Hz, 정답 1254 Hz).
    /// </summary>
    public List<(int Node, int Dof, double K)> GroundSprings { get; } = new();
    /// <summary>합 반력을 출력할 지지 절점 집합 이름(NodeSets 에 있어야 함).</summary>
    public string? ReactionSet { get; set; }
    public string Title { get; set; } = "MnS CalculiX model";
    /// <summary>
    /// 연립방정식 해법(*STATIC · *FREQUENCY · *BUCKLE 의 SOLVER=). null = ccx 기본(PaStiX 혼합 정밀도).
    /// <see cref="CcxRunner.Solve"/> 는 PaStiX 반복 정밀화가 수렴하지 않으면 "SPOOLES"(배정밀도 직접법)로 한 번 다시 푼다.
    /// </summary>
    public string? Solver { get; set; }

    // ---- 모달 과도(ModalDynamic) ----
    /// <summary>시간 증분 [s]·총 시간 [s] (*MODAL DYNAMIC).</summary>
    public double DynDt { get; set; }
    public double DynTotal { get; set; }
    /// <summary>모드 감쇠비(모든 모드, *MODAL DAMPING).</summary>
    public double ModalDampingRatio { get; set; } = 0.03;
    /// <summary>결과 출력 간격(증분 수, *NODE/EL FILE FREQUENCY).</summary>
    public int OutputEvery { get; set; } = 1;
    /// <summary>시간 이력(*AMPLITUDE): 이름 → (시각, 값) 점.</summary>
    public List<(string Name, (double T, double V)[] Points)> Amplitudes { get; } = new();
    /// <summary>시간 이력 절점 하중: (절점, 자유도 1..3, 계수 [N], 이력 이름) — 실제 힘 = 계수 × 이력 값.</summary>
    public List<(int Node, int Dof, double Scale, string Amplitude)> AmplitudeLoads { get; } = new();
    /// <summary>집중 질량(MASS 요소): 실린 탑재물 질량을 절점들에 나눠 단다 — (절점들, 절점당 질량 kg).</summary>
    public List<(int[] Nodes, double MassEach)> PointMasses { get; } = new();
}

/// <summary>CalculiX 입력 덱(.inp) 작성.</summary>
public static class CcxDeck
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("G12", Inv);

    /// <summary>덱 작성 → 경로. 작업 이름은 영숫자·_ 만(ccx 는 경로의 공백에 약함).</summary>
    public static string Write(CcxModel m, string dir, string job)
    {
        Validate(m);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        var mesh = m.Mesh;
        sb.AppendLine("** generated by MnS Sim.Core (FR-FEA-01) — CalculiX 2.23, units m N Pa kg");
        sb.AppendLine("*HEADING");
        sb.AppendLine(m.Title.Replace('\n', ' '));
        var used = mesh.UsedNodes();
        sb.AppendLine("*NODE, NSET=NALL");
        for (int i = 0; i < mesh.Nodes.Count; i++)
            if (used[i]) sb.Append(i + 1).Append(", ").Append(F(mesh.Nodes[i].X)).Append(", ").Append(F(mesh.Nodes[i].Y)).Append(", ").AppendLine(F(mesh.Nodes[i].Z));
        foreach (var g in Enumerable.Range(0, mesh.Elements.Count).GroupBy(e => mesh.Elements[e].Type))
        {
            sb.AppendLine($"*ELEMENT, TYPE={g.Key}, ELSET=E{g.Key}");
            foreach (var e in g)
            {
                var n = mesh.Elements[e].N;
                sb.Append(e + 1);
                for (int k = 0; k < n.Length; k++)
                {
                    // 한 줄 최대 16 항목 — 20절점은 이어 쓰기
                    if (k == 15) sb.AppendLine(",");
                    else sb.Append(", ");
                    sb.Append(n[k] + 1);
                }
                sb.AppendLine();
            }
        }
        var types = mesh.Elements.Select(e => e.Type).Distinct().ToList();
        sb.AppendLine("*ELSET, ELSET=EALL");
        sb.AppendLine(string.Join(", ", types.Select(t => $"E{t}")));
        int springId = mesh.Elements.Count;
        foreach (var g in m.GroundSprings.GroupBy(s => (s.Dof, s.K)))
        {
            var set = $"ESPR{springId + 1}";
            sb.AppendLine($"*ELEMENT, TYPE=SPRING1, ELSET={set}");
            foreach (var s in g) sb.AppendLine($"{++springId}, {s.Node + 1}");
            sb.AppendLine($"*SPRING, ELSET={set}");
            sb.AppendLine(g.Key.Dof.ToString(Inv));
            sb.AppendLine(F(g.Key.K));
        }
        int massGroup = 0;
        foreach (var (nodes, me) in m.PointMasses)
        {
            if (nodes.Length == 0 || !(me > 0)) continue;
            var set = $"EMASS{++massGroup}";
            sb.AppendLine($"*ELEMENT, TYPE=MASS, ELSET={set}");
            foreach (var nd in nodes) sb.AppendLine($"{++springId}, {nd + 1}");
            sb.AppendLine($"*MASS, ELSET={set}");
            sb.AppendLine(F(me));
        }
        foreach (var (name, pts) in m.Amplitudes)
        {
            sb.AppendLine($"*AMPLITUDE, NAME={name}");
            foreach (var chunk in pts.Chunk(4)) sb.AppendLine(string.Join(", ", chunk.Select(p => $"{F(p.T)}, {F(p.V)}")));
        }
        foreach (var (name, nodes) in mesh.NodeSets)
        {
            sb.AppendLine($"*NSET, NSET={name}");
            foreach (var chunk in nodes.Distinct().Chunk(12)) sb.AppendLine(string.Join(", ", chunk.Select(n => n + 1)));
        }
        // 재질
        var mat = m.Material;
        sb.AppendLine("*MATERIAL, NAME=MAT1");
        sb.AppendLine("*ELASTIC");
        sb.AppendLine($"{F(mat.E)}, {F(mat.Nu)}");
        sb.AppendLine("*DENSITY");
        sb.AppendLine(F(mat.Density));
        if (m.Nonlinear && m.Analysis == CcxAnalysis.Static && mat.Yield > 0)
        {
            // 이선형 경화: (σy, 0) → (σu, εf − σu/E). 연신율 없거나 σu ≤ σy 면 완전 소성.
            sb.AppendLine("*PLASTIC");
            sb.AppendLine($"{F(mat.Yield)}, 0");
            double epu = Math.Max(mat.Elongation - mat.Ultimate / mat.E, 1e-3);
            if (mat.Ultimate > mat.Yield) sb.AppendLine($"{F(mat.Ultimate)}, {F(epu)}");
        }
        foreach (var t in types)
        {
            if (FeMesh.IsShell(t)) { sb.AppendLine($"*SHELL SECTION, ELSET=E{t}, MATERIAL=MAT1"); sb.AppendLine(F(mesh.Thickness!.Value)); }
            else if (FeMesh.IsPlane(t)) { sb.AppendLine($"*SOLID SECTION, ELSET=E{t}, MATERIAL=MAT1"); sb.AppendLine(F(mesh.Thickness!.Value)); }
            else sb.AppendLine($"*SOLID SECTION, ELSET=E{t}, MATERIAL=MAT1");
        }
        if (m.Fixed.Count > 0)
        {
            sb.AppendLine("*BOUNDARY");
            foreach (var (n, a, b) in m.Fixed) sb.AppendLine($"{n + 1}, {a}, {b}");
        }
        if (m.Analysis == CcxAnalysis.ModalDynamic)
        {
            // 1) 고유진동(모드 저장) → 2) 모달 중첩 과도: 시간 이력 절점 하중, 모드 감쇠, 출력 간격
            sb.AppendLine("*STEP");
            // 기본 해법(PaStiX). SPOOLES 는 4 s 로 빠르지만 이 설치(2.23 win)에서 모달 과도 응답이 발산(막대 DAF 수천, 실측) — 쓰지 않는다.
            sb.AppendLine("*FREQUENCY, STORAGE=YES");
            sb.AppendLine(m.Modes.ToString(Inv));
            sb.AppendLine("*END STEP");
            sb.AppendLine($"*STEP, INC={(int)Math.Ceiling(m.DynTotal / m.DynDt) + 10}");
            sb.AppendLine("*MODAL DYNAMIC");
            sb.AppendLine($"{F(m.DynDt)}, {F(m.DynTotal)}");
            sb.AppendLine("*MODAL DAMPING");
            sb.AppendLine($"1, {m.Modes}, {F(m.ModalDampingRatio)}");
            foreach (var g in m.AmplitudeLoads.GroupBy(x => x.Amplitude))
            {
                sb.AppendLine($"*CLOAD, AMPLITUDE={g.Key}");
                foreach (var (n, d, s, _) in g) sb.AppendLine($"{n + 1}, {d}, {F(s)}");
            }
            sb.AppendLine($"*EL FILE, FREQUENCY={Math.Max(1, m.OutputEvery)}");
            sb.AppendLine("S");
            sb.AppendLine("*END STEP");
            var pathD = Path.Combine(dir, job + ".inp");
            File.WriteAllText(pathD, sb.ToString(), new UTF8Encoding(false));
            return pathD;
        }
        var gravGroups = m.Analysis != CcxAnalysis.Modal ? GravGroups(sb, m) : null;
        sb.AppendLine("*STEP" + (m.Nonlinear && m.Analysis == CcxAnalysis.Static ? ", NLGEOM, INC=200" : ""));
        switch (m.Analysis)
        {
            case CcxAnalysis.Static:
                sb.AppendLine("*STATIC" + SolverOpt(m));
                if (m.Nonlinear) sb.AppendLine("0.1, 1.0, 1e-5, 0.25");
                break;
            case CcxAnalysis.Modal: sb.AppendLine("*FREQUENCY" + SolverOpt(m)); sb.AppendLine(m.Modes.ToString(Inv)); break;
            case CcxAnalysis.Buckle: sb.AppendLine("*BUCKLE" + SolverOpt(m)); sb.AppendLine(m.Modes.ToString(Inv)); break;
        }
        if (m.Analysis != CcxAnalysis.Modal) WriteLoads(sb, m, gravGroups);
        if (m.ReactionSet is { } rs && m.Analysis == CcxAnalysis.Static)
        {
            sb.AppendLine($"*NODE PRINT, NSET={rs}, TOTALS=ONLY");
            sb.AppendLine("RF");
        }
        sb.AppendLine("*NODE FILE");
        sb.AppendLine(m.Analysis == CcxAnalysis.Static ? "U, RF" : "U");
        sb.AppendLine("*EL FILE");
        sb.AppendLine(m.Analysis == CcxAnalysis.Static && m.Nonlinear ? "S, PEEQ" : "S");
        sb.AppendLine("*END STEP");
        var path = Path.Combine(dir, job + ".inp");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string SolverOpt(CcxModel m) => m.Solver is { Length: > 0 } s ? $", SOLVER={s}" : "";

    /// <summary>요소별 GRAV 를 집합으로 묶기 시작하는 요소 수(그 아래는 요소별 그대로 — 기존 결과 불변).</summary>
    public const int GravGroupThreshold = 20000;
    public const int MaxGravGroups = 512;

    /// <summary>
    /// 요소별 GRAV 가 많으면 ccx 2.23 이 body load 색인(ipobody ~ 요소 수 × 하중 정의 수)을 int32 로 할당하다 넘친다(실측: 3.3 만 요소에서
    /// 'u_calloc … num&lt;0'). → 가속도를 양자화해 요소 집합(≤ MaxGravGroups)으로 묶어(모델 정의부 *ELSET) 집합별 GRAV 로 쓴다.
    /// 성분 오차 ≤ 양자화 간격/2(집합 평균 사용), 덱 주석에 간격 기록. 요소 수가 문턱 이하이면 null(요소별 그대로).
    /// </summary>
    private static List<(string Set, Vec3 G)>? GravGroups(StringBuilder sb, CcxModel m)
    {
        if (m.ElementAccel is not { } eaq || eaq.Length <= GravGroupThreshold) return null;
        var g = eaq.Select(x => x + m.BodyAccel).ToArray();
        double gmax = g.Max(x => x.Norm);
        var groups = new List<(string, Vec3)>();
        if (gmax <= 0) return groups;
        for (int n = 400; ; n = Math.Max(2, n * 3 / 4))
        {
            double step = gmax / n;
            var bins = new Dictionary<(long, long, long), List<int>>();
            for (int e = 0; e < g.Length; e++)
            {
                if (g[e].NormSq == 0) continue;
                var k = ((long)Math.Round(g[e].X / step), (long)Math.Round(g[e].Y / step), (long)Math.Round(g[e].Z / step));
                if (!bins.TryGetValue(k, out var l)) bins[k] = l = new List<int>();
                l.Add(e);
            }
            if (bins.Count > MaxGravGroups && n > 2) continue;
            sb.AppendLine($"** 요소별 관성 가속도를 {bins.Count}개 집합으로 양자화(간격 {F(step)} m/s², 최대 |g| {F(gmax)}) — ccx int32 색인 한계 회피");
            int gi = 0;
            foreach (var (_, list) in bins)
            {
                var mean = list.Aggregate(Vec3.Zero, (a, e) => a + g[e]) / list.Count;
                string set = $"GR{++gi}";
                sb.AppendLine($"*ELSET, ELSET={set}");
                for (int i = 0; i < list.Count; i += 16) sb.AppendLine(string.Join(", ", list.Skip(i).Take(16).Select(e => (e + 1).ToString(Inv))));
                groups.Add((set, mean));
            }
            return groups;
        }
    }

    private static void WriteLoads(StringBuilder sb, CcxModel m, List<(string Set, Vec3 G)>? groups)
    {
        if (m.NodalForces.Count > 0)
        {
            // 같은 절점·자유도는 합산(ccx 는 같은 자유도 *CLOAD 를 덮어쓴다)
            var sum = new Dictionary<int, Vec3>();
            foreach (var (n, f) in m.NodalForces) sum[n] = (sum.TryGetValue(n, out var s) ? s : Vec3.Zero) + f;
            sb.AppendLine("*CLOAD");
            foreach (var (n, f) in sum.OrderBy(x => x.Key))
                for (int d = 0; d < 3; d++)
                {
                    double v = d == 0 ? f.X : d == 1 ? f.Y : f.Z;
                    if (v != 0) sb.AppendLine($"{n + 1}, {d + 1}, {F(v)}");
                }
        }
        bool anyDload = m.Pressures.Count > 0 || m.BodyAccel.NormSq > 0 || m.ElementAccel is not null || m.Centrif is not null;
        if (!anyDload) return;
        sb.AppendLine("*DLOAD");
        foreach (var (e, face, p) in m.Pressures)
            sb.AppendLine(face == 0 ? $"{e + 1}, P, {F(p)}" : $"{e + 1}, P{face}, {F(p)}");
        if (groups is not null)
        {
            foreach (var (set, gv) in groups)
            {
                double a = gv.Norm;
                if (a <= 0) continue;
                var d = gv / a;
                sb.AppendLine($"{set}, GRAV, {F(a)}, {F(d.X)}, {F(d.Y)}, {F(d.Z)}");
            }
        }
        else if (m.ElementAccel is { } ea)
        {
            for (int e = 0; e < ea.Length; e++)
            {
                var g = ea[e] + m.BodyAccel;
                double a = g.Norm;
                if (a <= 0) continue;
                var d = g / a;
                sb.AppendLine($"{e + 1}, GRAV, {F(a)}, {F(d.X)}, {F(d.Y)}, {F(d.Z)}");
            }
        }
        else if (m.BodyAccel.NormSq > 0)
        {
            double a = m.BodyAccel.Norm; var d = m.BodyAccel / a;
            sb.AppendLine($"EALL, GRAV, {F(a)}, {F(d.X)}, {F(d.Y)}, {F(d.Z)}");
        }
        if (m.Centrif is { } c && c.Omega.NormSq > 0)
        {
            double w = c.Omega.Norm; var ax = c.Omega / w;
            sb.AppendLine($"EALL, CENTRIF, {F(w * w)}, {F(c.Point.X)}, {F(c.Point.Y)}, {F(c.Point.Z)}, {F(ax.X)}, {F(ax.Y)}, {F(ax.Z)}");
        }
    }

    /// <summary>덱 작성 전 검사: 재질·두께·하중·구속 — ccx 는 잘못된 입력에서 메시지 없이 비정상 종료하므로 여기서 막는다.</summary>
    public static void Validate(CcxModel m)
    {
        var mesh = m.Mesh;
        if (mesh.Elements.Count == 0) throw new InvalidDataException("FEA 메시에 요소가 없습니다.");
        foreach (var e in mesh.Elements)
        {
            if (e.N.Length != FeMesh.NodeCount(e.Type)) throw new InvalidDataException($"{e.Type} 요소 절점 수 {e.N.Length} ≠ {FeMesh.NodeCount(e.Type)}");
            foreach (var n in e.N) if (n < 0 || n >= mesh.Nodes.Count) throw new InvalidDataException($"요소가 없는 절점 {n + 1} 을 참조합니다.");
        }
        if (mesh.Nodes.Any(p => !p.IsFinite)) throw new InvalidDataException("절점 좌표가 유한하지 않습니다.");
        var mat = m.Material;
        if (!(mat.E > 0) || !(mat.Density > 0) || !(mat.Nu > -1 && mat.Nu < 0.5)) throw new InvalidDataException($"재질 {mat.Name}: E·밀도·포아송비가 유효하지 않습니다.");
        if ((mesh.HasShells || mesh.HasPlane) && !(mesh.Thickness > 0)) throw new InvalidDataException("셸/평면 요소에는 양의 두께가 필요합니다.");
        if (m.NodalForces.Any(f => !f.Force.IsFinite) || m.Pressures.Any(p => !double.IsFinite(p.Pressure)) || !m.BodyAccel.IsFinite
            || (m.ElementAccel?.Any(a => !a.IsFinite) ?? false) || (m.Centrif is { } c && !(c.Omega.IsFinite && c.Point.IsFinite)))
            throw new InvalidDataException("하중에 NaN/무한대가 있습니다.");
        if (m.ElementAccel is { } ea && ea.Length != mesh.Elements.Count) throw new InvalidDataException("요소별 가속도 배열 길이가 요소 수와 다릅니다.");
        // 고유진동은 자유-자유(구속 없음, 강체 모드 6개 ≈ 0 Hz) 허용
        if (m.GroundSprings.Any(s => !(s.K > 0) || s.Dof is < 1 or > 3 || s.Node < 0 || s.Node >= mesh.Nodes.Count)) throw new InvalidDataException("접지 스프링 정의가 잘못되었습니다.");
        if (m.Analysis == CcxAnalysis.ModalDynamic)
        {
            if (!(m.DynDt > 0) || !(m.DynTotal >= m.DynDt)) throw new InvalidDataException("모달 과도: 시간 증분·총 시간이 유효해야 합니다.");
            if (m.AmplitudeLoads.Count == 0) throw new InvalidDataException("모달 과도: 시간 이력 하중이 없습니다.");
            var names = m.Amplitudes.Select(a => a.Name).ToHashSet();
            if (m.AmplitudeLoads.Any(l => !names.Contains(l.Amplitude) || l.Dof is < 1 or > 3 || !double.IsFinite(l.Scale)))
                throw new InvalidDataException("모달 과도: 하중이 정의되지 않은 이력을 참조하거나 자유도·계수가 잘못되었습니다.");
            if (m.Amplitudes.Any(a => a.Points.Length < 2 || a.Points.Any(p => !double.IsFinite(p.T) || !double.IsFinite(p.V))))
                throw new InvalidDataException("모달 과도: 시간 이력(*AMPLITUDE)에 점이 부족하거나 NaN 이 있습니다.");
        }
        if (m.Fixed.Count == 0 && m.GroundSprings.Count == 0 && m.Analysis is not (CcxAnalysis.Modal or CcxAnalysis.ModalDynamic)) throw new InvalidDataException("구속(경계조건)이 없습니다 — 강체 운동으로 강성 행렬이 특이합니다.");
        if (m.PointMasses.Any(p => !(p.MassEach > 0) || p.Nodes.Any(n => n < 0 || n >= mesh.Nodes.Count))) throw new InvalidDataException("집중 질량 정의가 잘못되었습니다.");
        if (m.Analysis != CcxAnalysis.Static && m.Modes < 1) throw new InvalidDataException("모드 수는 1 이상이어야 합니다.");
        if (m.Analysis == CcxAnalysis.Buckle && m.NodalForces.Count == 0 && m.Pressures.Count == 0 && m.BodyAccel.NormSq == 0 && m.ElementAccel is null)
            throw new InvalidDataException("좌굴 해석에는 기준 하중이 필요합니다.");
    }
}

/// <summary>.frd(ASCII) 결과: 절점 좌표 + 결과 블록(DISP, STRESS, PE…).</summary>
public sealed class FrdData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public Dictionary<int, Vec3> Coords { get; } = new();
    public List<FrdBlock> Blocks { get; } = new();

    public sealed class FrdBlock
    {
        public required string Name { get; init; }
        /// <summary>100CL 값: 정적 = 시간, 고유진동 = 진동수[Hz], 좌굴 = 좌굴 계수.</summary>
        public double Value { get; init; }
        public int Step { get; init; }
        public int Mode { get; set; }
        public Dictionary<int, double[]> Values { get; } = new();
    }

    /// <summary>이름이 같은 마지막(정적) 또는 mode 번째(1 기반) 블록.</summary>
    public FrdBlock? Block(string name, int mode = 0)
    {
        var l = Blocks.Where(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (l.Count == 0) return null;
        if (mode <= 0) return l[^1];
        return l.FirstOrDefault(b => b.Mode == mode);
    }

    /// <param name="onBlock">있으면 결과 블록이 끝날 때마다 넘기고 보관하지 않는다(과도 응답 수십 블록을 메모리에 쌓지 않는 스트리밍).</param>
    public static FrdData Parse(string path, Action<FrdData, FrdBlock>? onBlock = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CalculiX 결과(.frd)가 없습니다: {path}");
        var d = new FrdData();
        FrdBlock? cur = null;
        double clValue = 0; int clStep = 0; int mode = 0;
        bool inNodes = false, inElems = false;
        int nComp = 0;
        int lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (line.Length < 3) continue;
            if (line.StartsWith("    2C", StringComparison.Ordinal)) { inNodes = true; continue; }
            if (line.StartsWith("    3C", StringComparison.Ordinal)) { inElems = true; continue; }
            if (inElems) { if (line.StartsWith(" -3", StringComparison.Ordinal)) inElems = false; continue; }
            if (line.StartsWith("    1PMODE", StringComparison.Ordinal)) { mode = int.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], Inv); continue; }
            if (line.StartsWith("  100CL", StringComparison.Ordinal))
            {
                // "  100CL  101 1.000000000           8 ..." — 값 = 12..24 열
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                clValue = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, Inv, out var v) ? v : 0;
                clStep++;
                continue;
            }
            var tag = line[..3];
            if (tag == " -4")
            {
                var parts = line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cur = new FrdBlock { Name = parts[0], Value = clValue, Step = clStep, Mode = mode };
                nComp = parts.Length > 1 ? int.Parse(parts[1], Inv) : 0;
                d.Blocks.Add(cur);
                continue;
            }
            if (tag == " -1")
            {
                if (line.Length < 13) throw new InvalidDataException($".frd 줄 {lineNo} 이 잘렸습니다.");
                int id = int.Parse(line.AsSpan(3, 10), NumberStyles.Integer, Inv);
                var vals = new List<double>();
                for (int o = 13; o + 12 <= line.Length; o += 12)
                {
                    var s = line.AsSpan(o, 12);
                    if (!double.TryParse(s, NumberStyles.Float, Inv, out var x))
                        x = s.ToString().Contains("nan", StringComparison.OrdinalIgnoreCase) ? double.NaN
                            : throw new InvalidDataException($".frd 줄 {lineNo} 숫자가 손상되었습니다: '{s.ToString()}'");
                    vals.Add(x);
                }
                if (inNodes)
                {
                    if (vals.Count < 3) throw new InvalidDataException($".frd 줄 {lineNo} 절점 좌표가 잘렸습니다.");
                    d.Coords[id] = new Vec3(vals[0], vals[1], vals[2]);
                }
                else if (cur is not null)
                {
                    // DISP 는 성분 4(D1~D3 + ALL)로 선언하지만 값은 3개 — 성분 수 대신 STRESS(6)·DISP(3) 최소만 확인
                    int need = cur.Name.Equals("STRESS", StringComparison.OrdinalIgnoreCase) ? 6 : cur.Name.Equals("DISP", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                    if (nComp > 0 && vals.Count < need) throw new InvalidDataException($".frd 줄 {lineNo} 결과 값이 잘렸습니다.");
                    cur.Values[id] = vals.ToArray();
                }
                continue;
            }
            if (tag == " -3")
            {
                if (cur is not null && onBlock is not null) { d.Blocks.Remove(cur); onBlock(d, cur); }
                inNodes = false; cur = null; continue;
            }
        }
        if (d.Coords.Count == 0) throw new InvalidDataException($".frd 에 절점이 없습니다(손상): {path}");
        return d;
    }
}

/// <summary>.dat 출력: 고유진동수(Hz), 좌굴 계수, *NODE PRINT TOTALS 합 반력.</summary>
public sealed class DatData
{
    public List<double> FrequenciesHz { get; } = new();
    public List<double> BucklingFactors { get; } = new();
    public Vec3? ReactionTotal { get; set; }

    public static DatData Parse(string path)
    {
        var d = new DatData();
        if (!File.Exists(path)) return d;
        var lines = File.ReadAllLines(path).Select(l => l.TrimEnd('\r')).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Contains("E I G E N V A L U E   O U T P U T"))
            {
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var p = lines[j].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 5 && int.TryParse(p[0], out _) && double.TryParse(p[3], NumberStyles.Float, Inv, out var hz)) d.FrequenciesHz.Add(hz);
                    else if (d.FrequenciesHz.Count > 0 && lines[j].Trim().Length == 0) { i = j; break; }
                }
            }
            else if (l.Contains("B U C K L I N G   F A C T O R   O U T P U T"))
            {
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var p = lines[j].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 2 && int.TryParse(p[0], out _) && double.TryParse(p[1], NumberStyles.Float, Inv, out var f)) d.BucklingFactors.Add(f);
                    else if (d.BucklingFactors.Count > 0 && lines[j].Trim().Length == 0) { i = j; break; }
                }
            }
            else if (l.TrimStart().StartsWith("total force (fx,fy,fz)", StringComparison.Ordinal))
            {
                for (int j = i + 1; j < lines.Length && j < i + 4; j++)
                {
                    var p = lines[j].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 3 && p.All(x => double.TryParse(x, NumberStyles.Float, Inv, out _)))
                    {
                        d.ReactionTotal = new Vec3(double.Parse(p[0], Inv), double.Parse(p[1], Inv), double.Parse(p[2], Inv));
                        break;
                    }
                }
            }
        }
        return d;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}

/// <summary>CalculiX 실행 결과(예외 없이 실패를 돌려준다).</summary>
public sealed class CcxResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Job { get; set; } = "";
    public string Log { get; set; } = "";
    public double Seconds { get; set; }
    /// <summary>PaStiX 반복 정밀화의 마지막 오차(로그 '- iteration N : … error x'). PaStiX 를 쓰지 않았으면 null.</summary>
    public double? RefinementError { get; set; }
    /// <summary>해법 대체 기록(예: PaStiX 정밀화 미수렴 → SPOOLES 로 다시 풂). 없으면 빈 문자열.</summary>
    public string SolverNote { get; set; } = "";
    public FrdData? Frd { get; set; }
    public DatData Dat { get; set; } = new();

    public string InpPath => Path.Combine(Dir, Job + ".inp");
    public string FrdPath => Path.Combine(Dir, Job + ".frd");

    /// <summary>절점 번호 → 변위(정적 또는 mode 번째 모드).</summary>
    public Dictionary<int, Vec3> Displacement(int mode = 0)
    {
        var r = new Dictionary<int, Vec3>();
        if (Frd?.Block("DISP", mode) is { } b)
            foreach (var (id, v) in b.Values) if (v.Length >= 3) r[id] = new Vec3(v[0], v[1], v[2]);
        return r;
    }

    /// <summary>절점 번호 → 응력 텐서 성분 (xx, yy, zz, xy, yz, zx).</summary>
    public Dictionary<int, double[]> Stress(int mode = 0) =>
        Frd?.Block("STRESS", mode) is { } b ? b.Values.Where(kv => kv.Value.Length >= 6).ToDictionary(kv => kv.Key, kv => kv.Value) : new();

    /// <summary>절점 번호 → von Mises [Pa].</summary>
    public Dictionary<int, double> VonMises(int mode = 0) => Stress(mode).ToDictionary(kv => kv.Key, kv => Tensor.VonMises(kv.Value));

    /// <summary>절점 번호 → 최대 주응력 [Pa].</summary>
    public Dictionary<int, double> MaxPrincipal(int mode = 0) => Stress(mode).ToDictionary(kv => kv.Key, kv => Tensor.Principal(kv.Value)[0]);

    /// <summary>등가 소성변형률(비선형 정적, 없으면 빈 사전).</summary>
    public Dictionary<int, double> Peeq() =>
        Frd?.Block("PE") is { } b ? b.Values.ToDictionary(kv => kv.Key, kv => kv.Value[0]) : new();
}

/// <summary>응력 텐서 유틸(성분 순서 xx, yy, zz, xy, yz, zx — frd 순서).</summary>
public static class Tensor
{
    public static double VonMises(double[] s)
    {
        double a = s[0] - s[1], b = s[1] - s[2], c = s[2] - s[0];
        return Math.Sqrt(0.5 * (a * a + b * b + c * c) + 3 * (s[3] * s[3] + s[4] * s[4] + s[5] * s[5]));
    }

    /// <summary>주응력 내림차순.</summary>
    public static double[] Principal(double[] s)
    {
        var m = new Mat3(s[0], s[3], s[5], s[3], s[1], s[4], s[5], s[4], s[2]);
        return m.SymmetricEigenvalues().OrderByDescending(x => x).ToArray();
    }
}

/// <summary>CalculiX 실행기: ccx -i job (작업 폴더에서). 종료 코드·"Job finished"·.frd 를 확인한다.</summary>
public static class CcxRunner
{
    /// <param name="parseFrd">false = .frd 를 읽지 않음(과도 응답처럼 큰 결과는 호출자가 FrdData.Parse(…, onBlock) 로 스트리밍).</param>
    public static CcxResult Run(CalculixInstall inst, string inpPath, TimeSpan timeout, CancellationToken ct = default, int threads = 0, bool parseFrd = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(inpPath))!;
        var job = Path.GetFileNameWithoutExtension(inpPath);
        var res = new CcxResult { Dir = dir, Job = job };
        var sw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(inpPath)) { res.Error = $"입력 덱이 없습니다: {inpPath}"; return res; }
            foreach (var ext in new[] { ".frd", ".dat", ".sta", ".cvg" })
            {
                var old = Path.Combine(dir, job + ext);
                if (File.Exists(old)) File.Delete(old);
            }
            var psi = new ProcessStartInfo(inst.Ccx, $"-i {job}")
            {
                WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            int n = threads > 0 ? threads : Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8);
            psi.Environment["OMP_NUM_THREADS"] = n.ToString(CultureInfo.InvariantCulture);
            psi.Environment["CCX_NPROC_EQUATION_SOLVER"] = n.ToString(CultureInfo.InvariantCulture);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{inst.Ccx} 를 시작할 수 없습니다.");
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            {
                if (!p.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    res.Error = $"시간 초과({timeout.TotalSeconds:F0} s)";
                    return res;
                }
                p.WaitForExit();
            }
            ct.ThrowIfCancellationRequested();
            var stdout = outTask.GetAwaiter().GetResult();
            var stderr = errTask.GetAwaiter().GetResult();
            res.Log = stdout + stderr;
            File.WriteAllText(Path.Combine(dir, job + ".ccx.log"), res.Log);
            bool finished = stdout.Contains("Job finished", StringComparison.OrdinalIgnoreCase);
            var errors = res.Log.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("*ERROR", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            if (p.ExitCode != 0 || !finished || errors.Count > 0)
            {
                var tail = errors.Count > 0 ? string.Join(" | ", errors)
                    : string.Join(" | ", res.Log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(4));
                res.Error = $"ccx 비정상 종료(코드 {p.ExitCode}{(finished ? "" : ", 'Job finished' 없음")}) — {tail}";
                return res;
            }
            if (!File.Exists(res.FrdPath)) { res.Error = "결과 파일(.frd)이 없습니다."; return res; }
            res.RefinementError = LastRefinementError(res.Log);
            if (parseFrd) res.Frd = FrdData.Parse(res.FrdPath);
            res.Dat = DatData.Parse(Path.Combine(dir, job + ".dat"));
            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException) { res.Error = "취소됨"; return res; }
        catch (Exception ex) { res.Error = $"실행 오류: {ex.Message}"; return res; }
        finally { res.Seconds = sw.Elapsed.TotalSeconds; }
    }

    /// <summary>
    /// PaStiX 반복 정밀화가 이 값보다 큰 오차로 끝나면 미수렴(답이 틀림). ccx 2.23 은 혼합 정밀도(IPARM_MIXED=1, 단정밀도 분해 + 최대 50회 정밀화)로
    /// 풀고, 정밀화가 수렴하지 않아도 'Job finished' 로 끝난다 — 실측(2026-10-11, A48 닫힌 상자 내압 · CPU 부하 중 40회 중 1회):
    /// 정상 실행은 오차 ≈ 1e-9 로 끝나 반력 ≈ 1e-6 N, 미수렴 실행은 오차 7.65 로 끝나 반력 715 N · 최대 응력 119 MPa(정상 72.4).
    /// </summary>
    public const double RefinementTolerance = 1e-4;

    /// <summary>로그에서 PaStiX 반복 정밀화의 마지막 'error' 값. 정밀화 기록이 없으면 null.</summary>
    public static double? LastRefinementError(string log)
    {
        double? last = null;
        bool inIter = false;
        foreach (var raw in log.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("- iteration", StringComparison.Ordinal)) { inIter = true; continue; }
            if (inIter && l.StartsWith("error", StringComparison.Ordinal)
                && double.TryParse(l["error".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
            {
                last = e;
                inIter = false;
            }
        }
        return last;
    }

    /// <summary>
    /// 덱 작성 + 실행. PaStiX 반복 정밀화가 수렴하지 않은 정적 · 고유진동 · 좌굴 해석은 배정밀도 직접법(SPOOLES)으로 한 번 다시 푼다
    /// (모달 과도는 SPOOLES 가 이 설치에서 발산하므로 제외). 다시 푼 것도 실패하면 틀린 답 대신 실패로 돌려준다.
    /// </summary>
    /// <param name="threads">ccx 스레드 수(0 = min(코어/2, 8)) — 다른 계산과 CPU 를 나눠 쓸 때 상한.</param>
    public static CcxResult Solve(CalculixInstall inst, CcxModel m, string dir, string job, TimeSpan timeout, CancellationToken ct = default, int threads = 0)
    {
        var prev = m.Solver;
        try
        {
            return Solve(inst, solver => { m.Solver = solver ?? prev; return CcxDeck.Write(m, dir, job); }, dir, job, timeout, ct, threads,
                retry: m.Analysis != CcxAnalysis.ModalDynamic && prev is not { Length: > 0 });
        }
        finally { m.Solver = prev; }
    }

    /// <summary>
    /// 덱 작성 함수(인자 = 해법 이름, null = 기본)로 같은 PaStiX 미수렴 → SPOOLES 1 스레드 재풀이 — CcxModel 로 표현하지 않는 덱(여러 재료·*TIE 조립체)용.
    /// </summary>
    public static CcxResult Solve(CalculixInstall inst, Func<string?, string> writeDeck, string dir, string job, TimeSpan timeout, CancellationToken ct = default, int threads = 0, bool retry = true)
    {
        string inp;
        try { inp = writeDeck(null); }
        catch (Exception ex) { return new CcxResult { Dir = dir, Job = job, Error = $"덱 작성 실패: {ex.Message}" }; }
        var r = Run(inst, inp, timeout, ct, threads);
        if (!r.Ok || r.RefinementError is not { } err || err <= RefinementTolerance || !retry) return r;

        string note = $"PaStiX 혼합 정밀도 반복 정밀화 미수렴(마지막 오차 {err.ToString("G3", CultureInfo.InvariantCulture)}) → SPOOLES(배정밀도 직접법, 1 스레드)로 다시 풂";
        try { inp = writeDeck("SPOOLES"); }
        catch (Exception ex) { return new CcxResult { Dir = dir, Job = job, Error = $"{note} — 덱 작성 실패: {ex.Message}" }; }
        // 1 스레드: 다중 스레드 SPOOLES 도 실행마다 답이 조금씩 다르고, CPU 부하 중 한 번은 틀린 답(최대 응력 81.7 MPa, 반력 0.16 N — 정답 72.4 · 1e-6)을 냈다.
        // 1 스레드는 같은 덱에서 세 번 모두 같은 답(실측 2026-10-11, A48 덱 ≈ 10 s).
        var r2 = Run(inst, inp, timeout, ct, threads: 1);
        r2.Seconds += r.Seconds;
        if (!r2.Ok) { r2.Error = $"{note} — 실패: {r2.Error}"; return r2; }
        r2.SolverNote = note;
        r2.Log = note + "\n" + r2.Log;
        return r2;
    }
}
