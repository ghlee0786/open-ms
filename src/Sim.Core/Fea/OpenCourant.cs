using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>
/// OpenCourant(OpenRadioss 커뮤니티 포크, AGPL-3.0) 설치 탐지(FR-MAT-05). 외부 프로세스로만 호출 — 링크하지 않는다.
/// 위치: 인자 → 환경변수 OPENCOURANT_PATH → C:\tools\OpenCourant.
/// </summary>
public sealed class OpenCourantInstall
{
    public required string Root { get; init; }
    public required string Starter { get; init; }
    public required string Engine { get; init; }
    public string? AnimToVtk { get; init; }
    public string? ThToCsv { get; init; }

    public const string DefaultRoot = @"C:\tools\OpenCourant";

    public static OpenCourantInstall? Locate(string? root = null) => TryLocate(root, out _);

    public static OpenCourantInstall? TryLocate(string? root, out string why)
    {
        var cand = root ?? System.Environment.GetEnvironmentVariable("OPENCOURANT_PATH") ?? DefaultRoot;
        if (string.IsNullOrWhiteSpace(cand) || !Directory.Exists(cand)) { why = $"OpenCourant 설치 폴더가 없습니다: {cand} (OPENCOURANT_PATH 지정)"; return null; }
        string? Exe(string stem)
        {
            foreach (var n in new[] { $"{stem}_win64.exe", $"{stem}_linux64_gf", $"{stem}_linux64", $"{stem}.exe", stem })
            {
                var p = Path.Combine(cand, "exec", n);
                if (File.Exists(p)) return p;
            }
            return null;
        }
        var st = Exe("starter"); var en = Exe("engine");
        if (st is null || en is null) { why = $"{cand}\\exec 에 starter/engine 실행 파일이 없습니다."; return null; }
        why = "";
        return new OpenCourantInstall { Root = cand, Starter = st, Engine = en, AnimToVtk = Exe("anim_to_vtk"), ThToCsv = Exe("th_to_csv") };
    }

    /// <summary>실행 환경: RAD_CFG_PATH, KMP_STACKSIZE, PATH 에 extlib 의 win64 런타임 폴더.</summary>
    public void ApplyEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["OPENCOURANT_PATH"] = Root;
        psi.Environment["RAD_CFG_PATH"] = Path.Combine(Root, "hm_cfg_files");
        psi.Environment["KMP_STACKSIZE"] = "400m";
        psi.Environment["OMP_NUM_THREADS"] = "1";
        var extra = new List<string>();
        var ext = Path.Combine(Root, "extlib");
        if (Directory.Exists(ext))
            extra.AddRange(Directory.EnumerateDirectories(ext, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d).Equals("win64", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).Equals("linux64", StringComparison.OrdinalIgnoreCase)));
        var path = psi.Environment.TryGetValue("PATH", out var p) ? p : System.Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, extra.Append(path ?? ""));
    }
}

/// <summary>FEA 실행 결과.</summary>
public sealed class FeaRunResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Dir { get; set; } = "";
    public List<string> AnimFiles { get; } = new();
    public List<string> VtkFiles { get; } = new();
    public string StarterLog { get; set; } = "";
    public string EngineLog { get; set; } = "";
    public double Seconds { get; set; }
    /// <summary>마지막 애니메이션 프레임(파싱됨) — 성공 시.</summary>
    public VtkData? Final { get; set; }
    /// <summary>해석 발산(절점 좌표 inf/NaN). 이 경우 Ok=false.</summary>
    public bool Diverged { get; set; }
}

/// <summary>
/// Radioss 블록 형식 입력 덱 작성(OpenRadioss 2019+ 형식, 단위 kg·m·s). 실행 검증은 설치 환경에서 수행해야 한다
/// (이 저장소 CI 에서는 MNS_RUN_OPENCOURANT=1 일 때만 실제 실행 테스트).
/// </summary>
public static class RadiossDeck
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string I(int v) => v.ToString(Inv).PadLeft(10);
    private static string F(double v) => (Math.Abs(v) < 1e-300 ? "0" : v.ToString("G10", Inv)).PadLeft(20);

    /// <param name="ModelMass">FE 모델 총질량 [kg](셸 밀도 환산 후).</param>
    /// <param name="DensityScale">셸 밀도 배율(내용물 질량 분포 — 1 이면 빈 껍질).</param>
    public sealed record Deck(string Dir, string Name, string StarterPath, string EnginePath, int Nodes, int Elements, double ModelMass = double.NaN, double DensityScale = 1);

    /// <summary>삼각형 표면 메시 → 공유 절점(허용오차 병합) + SH3N 요소(면 i = 요소 i+1).</summary>
    public static (List<Vec3> Nodes, int[][] Tris) Weld(TriMesh mesh, double tol)
    {
        var nodes = new List<Vec3>();
        var map = new Dictionary<(long, long, long), int>();
        int Id(Vec3 p)
        {
            var key = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));
            if (map.TryGetValue(key, out var id)) return id;
            nodes.Add(p);
            map[key] = nodes.Count;
            return nodes.Count;
        }
        var tris = new int[mesh.TriangleCount][];
        for (int i = 0; i < mesh.TriangleCount; i++)
            tris[i] = [Id(mesh.Vertices[3 * i]), Id(mesh.Vertices[3 * i + 1]), Id(mesh.Vertices[3 * i + 2])];
        return (nodes, tris);
    }

    private static void Begin(StringBuilder sb, string name)
    {
        sb.AppendLine("#RADIOSS STARTER");
        sb.AppendLine("# generated by MnS Sim.Core (FR-MAT-05) — units kg, m, s");
        sb.AppendLine("/BEGIN");
        sb.AppendLine(name);
        sb.AppendLine($"{I(2019)}{I(0)}");
        sb.AppendLine($"{"kg",20}{"m",20}{"s",20}");
        sb.AppendLine($"{"kg",20}{"m",20}{"s",20}");
    }

    /// <summary>
    /// Johnson-Cook 탄소성(LAW2) — 금속은 JC 계수, 그 외는 항복·인장·연신율에서 선형 경화 근사.
    /// failure=true: EPS_max = 파단 연신율(취성은 절반) — 셸은 요소 삭제(깨짐). 고체 Taylor 시험처럼 대변형만 볼 때는 false
    /// (LAW2 고체는 EPS_max 초과 시 편차응력만 0 으로 만들어 요소가 붕괴·발산한다 — 실제 실행에서 확인).
    /// density: 밀도 덮어쓰기(셸에 내용물 질량을 퍼뜨릴 때).
    /// </summary>
    public static void Material(StringBuilder sb, int id, Material m, bool failure = true, double? density = null)
    {
        double a = m.JC?.A ?? m.Yield;
        double b = m.JC?.B ?? Math.Max(m.Ultimate - m.Yield, 0) / Math.Max(m.Elongation, 1e-3);
        double n = m.JC?.N ?? 1.0;
        double c = m.JC?.C ?? 0, eps0 = m.JC?.EpsDot0 ?? 1, mm = m.JC?.M ?? 1;
        double epsMax = !failure ? 0 : m.Brittle ? Math.Max(m.Elongation, 1e-3) * 0.5 : Math.Max(m.Elongation, 1e-3);
        sb.AppendLine($"/MAT/PLAS_JOHNS/{id}");
        sb.AppendLine(m.Name);
        sb.AppendLine("#              RHO_I");
        sb.AppendLine(F(density ?? m.Density));
        sb.AppendLine("#                  E                  nu     Iflag");
        sb.AppendLine($"{F(m.E)}{F(m.Nu)}{I(0)}");
        sb.AppendLine("#                  a                   b                   n             EPS_max            SIG_max0");
        sb.AppendLine($"{F(a)}{F(b)}{F(n)}{F(epsMax)}{F(0)}");
        sb.AppendLine("#                  c           EPS_DOT_0       ICC   Fsmooth               F_cut               Chard");
        // 변형률 속도 필터(Fsmooth=1, Fcut 10 kHz) — 없으면 경고 1220, 충격 해석에서 속도 잡음이 강도에 직접 들어감
        sb.AppendLine($"{F(c)}{F(eps0)}{I(1)}{I(1)}{F(1e4)}{F(0)}");
        sb.AppendLine("#                  m              T_melt              rhoC_p                 T_r");
        sb.AppendLine($"{F(mm)}{F(0)}{F(0)}{F(0)}");
    }

    private static void Engine(string path, string name, double tEnd, int frames)
    {
        var e = new StringBuilder();
        e.AppendLine("#RADIOSS ENGINE");
        e.AppendLine($"/RUN/{name}/1");
        e.AppendLine(F(tEnd));
        e.AppendLine("/ANIM/DT");
        e.AppendLine($"{F(0)}{F(tEnd / Math.Max(frames, 1))}");
        e.AppendLine("/ANIM/ELEM/EPSP");
        e.AppendLine("/ANIM/ELEM/VONM");
        e.AppendLine("/ANIM/VECT/DISP");
        e.AppendLine("/TFILE/0");
        e.AppendLine(F(tEnd / 200));
        e.AppendLine("/PRINT/-1000");
        e.AppendLine("/END");
        File.WriteAllText(path, e.ToString(), new UTF8Encoding(false));
    }

    private static void RigidWallAndVelocity(StringBuilder sb, Vec3 v, Vec3 wallPoint, Vec3 wallNormal, int[]? parts = null)
    {
        sb.AppendLine("/GRNOD/PART/1");
        sb.AppendLine("all");
        sb.AppendLine(string.Concat((parts ?? [1]).Select(I)));
        sb.AppendLine("/INIVEL/TRA/1");
        sb.AppendLine("impact velocity");
        sb.AppendLine("#                 Vx                  Vy                  Vz   Gnod_id   Skew_id");
        sb.AppendLine($"{F(v.X)}{F(v.Y)}{F(v.Z)}{I(1)}{I(0)}");
        sb.AppendLine("/RWALL/PLANE/1");
        sb.AppendLine("ground");
        sb.AppendLine("#  node_ID     Slide grnod_ID1 grnod_ID2");
        sb.AppendLine($"{I(0)}{I(0)}{I(1)}{I(0)}");
        // PLANE 은 Diameter·ffac·ifq 를 쓰지 않는다(값이 있으면 경고 100214) → 빈칸
        sb.AppendLine("#            Dsearch                fric");
        sb.AppendLine($"{F(0)}{F(0.2)}");
        sb.AppendLine("#                 XM                  YM                  ZM");
        sb.AppendLine($"{F(wallPoint.X)}{F(wallPoint.Y)}{F(wallPoint.Z)}");
        var p1 = wallPoint + wallNormal;
        sb.AppendLine("#                XM1                 YM1                 ZM1");
        sb.AppendLine($"{F(p1.X)}{F(p1.Y)}{F(p1.Z)}");
    }

    /// <summary>
    /// 셸 충돌 케이스: 표면 메시(SH3N, 두께 t) + 재질 + 초기 속도 v 로 강체 벽(점·법선)에 충돌.
    /// 좌표는 FE 전역(호출자가 정함). 요소 번호 = 면 순서 + 1 (결과를 3D 뷰 면 색으로 1:1 매핑).
    /// totalMass: 물체 질량(내용물 포함). 빈 껍질 질량(ρ·t·A)보다 크면 내용물 질량을 **지면 쪽 절반**(벽에서 가까운 절반 높이의 요소, 파트 1)
    /// 셸 밀도를 올려 싣는다 — 충돌 때 내용물이 그쪽 벽을 누르기 때문. 반대쪽 절반(파트 2)은 재질 밀도 그대로.
    /// (처음에는 전체에 고르게 퍼뜨렸으나 실제 실행에서 무거워진 뚜껑이 휘청여 뚜껑만 깨지는 인공 결과가 나와 바꿈.)
    /// 가정: 내용물이 지면 쪽 벽에 붙어 함께 움직임 — 내부 충돌·내용물 강성은 표현 못 함. 껍질보다 가벼우면 껍질 질량 그대로.
    /// </summary>
    public static Deck WriteShellImpact(string dir, string name, TriMesh mesh, Material mat, double thickness, Vec3 velocity,
        Vec3 wallPoint, Vec3 wallNormal, double tEnd, int frames = 20, double? totalMass = null)
    {
        if (mesh.TriangleCount == 0) throw new InvalidDataException("FEA 메시가 비었습니다.");
        if (!(thickness > 0)) throw new InvalidDataException("셸 두께가 양수여야 합니다.");
        if (!velocity.IsFinite) throw new InvalidDataException("충돌 속도가 유한하지 않습니다.");
        Directory.CreateDirectory(dir);
        var (min, max) = mesh.Bounds();
        var (nodes, tris) = Weld(mesh, Math.Max((max - min).Norm * 1e-7, 1e-9));
        // 요소별 면적·벽까지 거리 → 지면 쪽 절반(lead) / 반대쪽(trail)
        var nrm = wallNormal.Normalized();
        int nt = mesh.TriangleCount;
        var areaOf = new double[nt]; var dist = new double[nt];
        for (int i = 0; i < nt; i++)
        {
            Vec3 a = mesh.Vertices[3 * i], b = mesh.Vertices[3 * i + 1], c = mesh.Vertices[3 * i + 2];
            areaOf[i] = 0.5 * Vec3.Cross(b - a, c - a).Norm;
            dist[i] = Vec3.Dot((a + b + c) / 3 - wallPoint, nrm);
        }
        double dMid = 0.5 * (dist.Min() + dist.Max());
        var lead = dist.Select(d => d <= dMid).ToArray();
        double area = areaOf.Sum(), areaLead = areaOf.Where((_, i) => lead[i]).Sum();
        double shellMass = mat.Density * thickness * area;
        double rhoLead = mat.Density;
        if (totalMass is { } tm && tm > shellMass && areaLead > 0)
            rhoLead = (tm - mat.Density * thickness * (area - areaLead)) / (thickness * areaLead);
        double modelMass = thickness * (rhoLead * areaLead + mat.Density * (area - areaLead));
        var sb = new StringBuilder();
        Begin(sb, name);
        sb.AppendLine("/NODE");
        for (int i = 0; i < nodes.Count; i++) sb.AppendLine($"{I(i + 1)}{F(nodes[i].X)}{F(nodes[i].Y)}{F(nodes[i].Z)}");
        sb.AppendLine("/PART/1");
        sb.AppendLine("body (ground side + contents)");
        sb.AppendLine("#  prop_ID    mat_ID subset_ID");
        sb.AppendLine($"{I(1)}{I(1)}{I(0)}");
        sb.AppendLine("/PART/2");
        sb.AppendLine("body (far side)");
        sb.AppendLine("#  prop_ID    mat_ID subset_ID");
        sb.AppendLine($"{I(1)}{I(2)}{I(0)}");
        sb.AppendLine("/PROP/SHELL/1");
        sb.AppendLine("shell");
        sb.AppendLine("#   Ishell    Ismstr     Ish3n    Idrill                            P_thick_fail");
        sb.AppendLine($"{I(24)}{I(0)}{I(0)}{I(0)}{"",20}{F(0)}");
        sb.AppendLine("#                 hm                  hf                  hr                  dm                  dn");
        sb.AppendLine($"{F(0)}{F(0)}{F(0)}{F(0)}{F(0)}");
        sb.AppendLine("#        N   Istrain               Thick              Ashear              Ithick     Iplas");
        sb.AppendLine($"{I(5)}{I(0)}{F(thickness)}{F(0)}{"",10}{I(1)}{I(1)}");
        Material(sb, 1, mat, failure: true, density: rhoLead);
        Material(sb, 2, mat, failure: true);
        foreach (int part in new[] { 1, 2 })
        {
            if (!lead.Any(l => l == (part == 1))) continue;
            sb.AppendLine($"/SH3N/{part}");
            for (int i = 0; i < tris.Length; i++)
                if (lead[i] == (part == 1)) sb.AppendLine($"{I(i + 1)}{I(tris[i][0])}{I(tris[i][1])}{I(tris[i][2])}");
        }
        RigidWallAndVelocity(sb, velocity, wallPoint, wallNormal, lead.Any(l => !l) ? [1, 2] : [1]);
        sb.AppendLine("/END");
        var st = Path.Combine(dir, $"{name}_0000.rad");
        File.WriteAllText(st, sb.ToString(), new UTF8Encoding(false));
        var en = Path.Combine(dir, $"{name}_0001.rad");
        Engine(en, name, tEnd, frames);
        return new Deck(dir, name, st, en, nodes.Count, tris.Length, modelMass, rhoLead / mat.Density);
    }

    /// <summary>
    /// Taylor 충돌 시험(원통 봉 → 강체 벽) 솔리드 육면체 덱. 단면은 정사각 격자를 원으로 사상(n×n), 길이 nl 분할.
    /// 봉 축 = +z, 벽 z=0 (법선 +z), 초기 속도 −z.
    /// </summary>
    public static Deck WriteTaylorBar(string dir, string name, Material mat, double length, double diameter, double speed, int n = 6, int nl = 30, double tEnd = 8e-5)
    {
        Directory.CreateDirectory(dir);
        double r = diameter / 2;
        int Nid(int i, int j, int k) => k * (n + 1) * (n + 1) + j * (n + 1) + i + 1;
        var sb = new StringBuilder();
        Begin(sb, name);
        sb.AppendLine("/NODE");
        double gap = length * 1e-4;
        for (int k = 0; k <= nl; k++)
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    double x = -1 + 2.0 * i / n, y = -1 + 2.0 * j / n;
                    double xm = x * Math.Sqrt(1 - y * y / 2), ym = y * Math.Sqrt(1 - x * x / 2);
                    sb.AppendLine($"{I(Nid(i, j, k))}{F(r * xm)}{F(r * ym)}{F(gap + length * k / nl)}");
                }
        sb.AppendLine("/PART/1");
        sb.AppendLine("bar");
        sb.AppendLine("#  prop_ID    mat_ID subset_ID");
        sb.AppendLine($"{I(1)}{I(1)}{I(0)}");
        sb.AppendLine("/PROP/SOLID/1");
        sb.AppendLine("solid");
        sb.AppendLine("#   Isolid    Ismstr               Icpre  Itetra4  Itetra10     Imass    Iframe");
        sb.AppendLine($"{I(24)}{I(0)}{"",10}{I(0)}{I(0)}{I(0)}{I(0)}{I(0)}");
        sb.AppendLine("#                q_a                 q_b                   h            LAMBDA_V                MU_V");
        sb.AppendLine($"{F(0)}{F(0)}{F(0)}{F(0)}{F(0)}");
        sb.AppendLine("#             dt_min   istrain      IHKT");
        sb.AppendLine($"{F(0)}{I(0)}{I(0)}");
        Material(sb, 1, mat, failure: false);
        sb.AppendLine("/BRICK/1");
        int e = 0;
        for (int k = 0; k < nl; k++)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    sb.AppendLine($"{I(++e)}{I(Nid(i, j, k))}{I(Nid(i + 1, j, k))}{I(Nid(i + 1, j + 1, k))}{I(Nid(i, j + 1, k))}" +
                                  $"{I(Nid(i, j, k + 1))}{I(Nid(i + 1, j, k + 1))}{I(Nid(i + 1, j + 1, k + 1))}{I(Nid(i, j + 1, k + 1))}");
        RigidWallAndVelocity(sb, new Vec3(0, 0, -speed), Vec3.Zero, Vec3.UnitZ);
        sb.AppendLine("/END");
        var st = Path.Combine(dir, $"{name}_0000.rad");
        File.WriteAllText(st, sb.ToString(), new UTF8Encoding(false));
        var en = Path.Combine(dir, $"{name}_0001.rad");
        Engine(en, name, tEnd, 10);
        return new Deck(dir, name, st, en, (n + 1) * (n + 1) * (nl + 1), e, mat.Density * Math.PI * r * r * length);
    }

    /// <summary>
    /// 정적 한계 교차검증용 외팔보(FR-FEA-06 "OpenCourant 와 CalculiX 의 정적 한계 일치"): [0,L]×[−b/2,b/2]×[−h/2,h/2] 육면체(HEPH),
    /// x=0 고정(/BCS 111 111), 끝면 절점에 −z 힘 P(합)를 rampTime 동안 선형 증가 후 유지(/CLOAD + /FUNCT).
    /// 감쇠 없이 램프를 1차 주기의 10배 이상으로 느리게 걸면 잔류 진동이 작고, 유지 구간 한 주기 평균이 정적 처짐이 된다.
    /// 애니메이션은 유지 구간(holdStart 이후)에서만 frames 장.
    /// </summary>
    public static Deck WriteCantileverRamp(string dir, string name, Material mat, double length, double width, double height, int nl, int nw, int nh,
        double force, double rampTime, double tEnd, double holdStart, int frames)
    {
        Directory.CreateDirectory(dir);
        int Nid(int i, int j, int k) => k * (nw + 1) * (nl + 1) + j * (nl + 1) + i + 1;
        var sb = new StringBuilder();
        Begin(sb, name);
        sb.AppendLine("/NODE");
        for (int k = 0; k <= nh; k++)
            for (int j = 0; j <= nw; j++)
                for (int i = 0; i <= nl; i++)
                    sb.AppendLine($"{I(Nid(i, j, k))}{F(length * i / nl)}{F(width * ((double)j / nw - 0.5))}{F(height * ((double)k / nh - 0.5))}");
        sb.AppendLine("/PART/1");
        sb.AppendLine("beam");
        sb.AppendLine("#  prop_ID    mat_ID subset_ID");
        sb.AppendLine($"{I(1)}{I(1)}{I(0)}");
        sb.AppendLine("/PROP/SOLID/1");
        sb.AppendLine("solid");
        sb.AppendLine("#   Isolid    Ismstr               Icpre  Itetra4  Itetra10     Imass    Iframe");
        sb.AppendLine($"{I(24)}{I(0)}{"",10}{I(0)}{I(0)}{I(0)}{I(0)}{I(0)}");
        sb.AppendLine("#                q_a                 q_b                   h            LAMBDA_V                MU_V");
        sb.AppendLine($"{F(0)}{F(0)}{F(0)}{F(0)}{F(0)}");
        sb.AppendLine("#             dt_min   istrain      IHKT");
        sb.AppendLine($"{F(0)}{I(0)}{I(0)}");
        Material(sb, 1, mat, failure: false);
        sb.AppendLine("/BRICK/1");
        int e = 0;
        for (int k = 0; k < nh; k++)
            for (int j = 0; j < nw; j++)
                for (int i = 0; i < nl; i++)
                    sb.AppendLine($"{I(++e)}{I(Nid(i, j, k))}{I(Nid(i + 1, j, k))}{I(Nid(i + 1, j + 1, k))}{I(Nid(i, j + 1, k))}" +
                                  $"{I(Nid(i, j, k + 1))}{I(Nid(i + 1, j, k + 1))}{I(Nid(i + 1, j + 1, k + 1))}{I(Nid(i, j + 1, k + 1))}");
        void Group(int id, string title, IEnumerable<int> nodes)
        {
            sb.AppendLine($"/GRNOD/NODE/{id}");
            sb.AppendLine(title);
            foreach (var chunk in nodes.Chunk(10)) sb.AppendLine(string.Concat(chunk.Select(I)));
        }
        var root = new List<int>(); var tip = new List<int>();
        for (int k = 0; k <= nh; k++)
            for (int j = 0; j <= nw; j++) { root.Add(Nid(0, j, k)); tip.Add(Nid(nl, j, k)); }
        Group(1, "root", root);
        Group(2, "tip", tip);
        sb.AppendLine("/BCS/1");
        sb.AppendLine("clamp");
        sb.AppendLine("#  Tra rot   skew_ID  grnod_ID");
        sb.AppendLine($"   111 111{I(0)}{I(1)}");
        sb.AppendLine("/FUNCT/1");
        sb.AppendLine("ramp");
        sb.AppendLine("#                  X                   Y");
        sb.AppendLine($"{F(0)}{F(0)}");
        sb.AppendLine($"{F(rampTime)}{F(1)}");
        sb.AppendLine($"{F(Math.Max(tEnd, rampTime) * 10)}{F(1)}");
        sb.AppendLine("/CLOAD/1");
        sb.AppendLine("tip load");
        sb.AppendLine("#funct_IDT       Dir   skew_ID sensor_ID  grnod_ID                       Ascalex             Fscaley");
        sb.AppendLine($"{I(1)}{"Z",10}{I(0)}{I(0)}{I(2)}{"",10}{F(1)}{F(-force / tip.Count)}");
        sb.AppendLine("/END");
        var st = Path.Combine(dir, $"{name}_0000.rad");
        File.WriteAllText(st, sb.ToString(), new UTF8Encoding(false));
        var en = new StringBuilder();
        en.AppendLine("#RADIOSS ENGINE");
        en.AppendLine($"/RUN/{name}/1");
        en.AppendLine(F(tEnd));
        en.AppendLine("/ANIM/DT");
        en.AppendLine($"{F(holdStart)}{F((tEnd - holdStart) / Math.Max(frames, 1))}");
        en.AppendLine("/ANIM/VECT/DISP");
        en.AppendLine("/PRINT/-1000");
        en.AppendLine("/END");
        var ep = Path.Combine(dir, $"{name}_0001.rad");
        File.WriteAllText(ep, en.ToString(), new UTF8Encoding(false));
        return new Deck(dir, name, st, ep, (nl + 1) * (nw + 1) * (nh + 1), e, mat.Density * length * width * height);
    }

    /// <summary>
    /// Wilkins &amp; Guinan (1973) Taylor 봉 실험 상관식 L_f/L_0 ≈ exp(−ρV²/(2σ_y)) — 대표 검증 비교값(근사).
    /// </summary>
    public static double TaylorFinalLengthRatio(double density, double speed, double yield) => Math.Exp(-density * speed * speed / (2 * yield));

    /// <summary>해석 결과의 봉 길이(z 범위, 요소 절점만).</summary>
    public static double TaylorLength(VtkData v)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var p in v.ElementPoints()) { lo = Math.Min(lo, p.Z); hi = Math.Max(hi, p.Z); }
        return hi - lo;
    }
}

/// <summary>
/// 셸 충돌 FEA 결과 판정(3단계): 삭제된 요소(EROSION_STATUS 0) → 깨짐/찢어짐, 소성변형률 ≥ 0.2% → 영구 변형(찌그러짐), 그 외 탄성.
/// 면별 소성변형률은 덱 요소 번호(= 표시 메시 면 순서)로 매핑, 삭제 요소는 파단 변형률 이상으로 표시.
/// </summary>
public sealed class FeaOutcome
{
    public const double PlasticThreshold = 0.002;
    public double[]? FacePlasticStrain { get; init; }
    public double MaxPlasticStrain { get; init; }
    public int Deleted { get; init; }
    public int Yielded { get; init; }
    public int Elements { get; init; }
    /// <summary>
    /// 강체 운동(최적 회전 + 이동)을 뺀 절점 잔차 최대 [m]: 처음 좌표 p0 → 마지막 p 에 최소제곱 강체 맞춤
    /// (공분산 H = Σ(p0−c0)(p−c)ᵀ 의 극분해 R, Higham 반복) 후 max|R(p0−c0) + c − p|. 비유한 점은 제외.
    /// </summary>
    public static double RigidResidualMax(IReadOnlyList<(Vec3 P0, Vec3 P)> pts)
    {
        var good = pts.Where(x => x.P0.IsFinite && x.P.IsFinite).ToList();
        if (good.Count < 3) return 0;
        var c0 = good.Aggregate(Vec3.Zero, (s, x) => s + x.P0) / good.Count;
        var c1 = good.Aggregate(Vec3.Zero, (s, x) => s + x.P) / good.Count;
        var h = Mat3.Zero;
        foreach (var (a, b) in good)
        {
            var d0 = a - c0; var d1 = b - c1;
            h = h + Mat3.FromColumns(d1 * d0.X, d1 * d0.Y, d1 * d0.Z); // (p−c)(p0−c0)ᵀ → R 가 p0 를 p 로 보냄
        }
        var r = h;
        if (Math.Abs(r.Determinant()) < 1e-30) return good.Max(x => ((x.P - c1) - (x.P0 - c0)).Norm);
        for (int k = 0; k < 60; k++)
        {
            var next = (r + r.Inverse().Transpose()) * 0.5;
            if (next.MaxAbsDiff(r) < 1e-14) { r = next; break; }
            r = next;
        }
        if (r.Determinant() < 0) return good.Max(x => ((x.P - c1) - (x.P0 - c0)).Norm); // 반사 — 맞춤 실패, 이동만 제거
        return good.Max(x => (r * (x.P0 - c0) + c1 - x.P).Norm);
    }

    /// <summary>강체 운동(회전 + 이동)을 뺀 절점 변형 최대 [m] — 찌그러짐 크기(살아 있는 요소 절점).</summary>
    public double MaxDeformation { get; init; }
    public Structures.FailureState State { get; init; }
    public string Verdict { get; init; } = "";

    public static FeaOutcome From(VtkData v, RadiossDeck.Deck deck, Material mat)
    {
        int n = deck.Elements;
        var epsCells = v.PlasticStrain;
        var face = epsCells is null ? null : v.ByElement(epsCells, n);
        var ero = v.Erosion is { } e ? v.ByElement(e, n) : null;
        int deleted = 0, yielded = 0; double maxEps = 0;
        double failEps = mat.Brittle ? Math.Max(mat.Elongation, 1e-3) * 0.5 : Math.Max(mat.Elongation, 1e-3);
        for (int i = 0; i < n; i++)
        {
            bool del = ero is not null && ero[i] == 0;
            if (del) deleted++;
            if (face is null) continue;
            if (!double.IsFinite(face[i])) face[i] = 0;
            if (del) face[i] = Math.Max(face[i], failEps);
            if (face[i] >= PlasticThreshold) yielded++;
            maxEps = Math.Max(maxEps, face[i]);
        }
        double def = 0;
        if (v.Displacement is { } u && u.Length == v.Points.Count)
        {
            var used = new HashSet<int>();
            var ids = v.CellScalars.TryGetValue("ELEMENT_ID", out var ei) ? ei : null;
            var eroCells = v.Erosion;
            for (int c = 0; c < v.Cells.Count; c++)
            {
                bool model = ids is null || (c < ids.Length && ids[c] >= 1);
                bool alive = eroCells is null || c >= eroCells.Length || eroCells[c] != 0;
                if (model && alive)
                    foreach (var p in v.Cells[c]) if (p >= 0 && p < u.Length) used.Add(p);
            }
            if (used.Count > 0) def = RigidResidualMax(used.Select(p => (v.Points[p] - u[p], v.Points[p])).ToList());
        }
        var st = deleted > 0 ? Structures.FailureState.Fracture : maxEps >= PlasticThreshold ? Structures.FailureState.Yield : Structures.FailureState.Safe;
        string pct(int k) => $"{k}/{n} ({100.0 * k / n:F1}%)";
        string verdict = st switch
        {
            // 연성: 넓은 소성 영역 + 국부 찢어짐 / 취성: 소성 거의 없이 요소 삭제(깨짐)
            Structures.FailureState.Fracture when !mat.Brittle =>
                $"찌그러짐 + 국부 찢어짐 — 소성 요소 {pct(yielded)}, 삭제(찢어진) 요소 {pct(deleted)}, 최대 소성변형률 {maxEps:F3}, 변형 ≈ {def * 1000:F0} mm",
            Structures.FailureState.Fracture =>
                $"깨짐(취성 파단) — 삭제 요소 {pct(deleted)}, 소성 요소 {pct(yielded)}, 파단 기준 소성변형률 {failEps:F3}, 변형 ≈ {def * 1000:F0} mm",
            Structures.FailureState.Yield => $"찌그러짐(영구 변형) — 최대 소성변형률 {maxEps:F3}, 소성 요소 {pct(yielded)}, 변형 ≈ {def * 1000:F1} mm",
            _ => $"탄성(영구 변형 없음) — 최대 소성변형률 {maxEps:F4}",
        };
        return new FeaOutcome
        {
            FacePlasticStrain = face, MaxPlasticStrain = maxEps, Deleted = deleted, Yielded = yielded, Elements = n,
            MaxDeformation = def, State = st, Verdict = verdict,
        };
    }
}

/// <summary>소형 검증 케이스(설치 시 테스트·적대적 검증에서 실제 솔버로 실행): 거친 상자 셸 꼭짓점 낙하.</summary>
public static class FeaBenchmarks
{
    /// <summary>상자 셸 메시(면마다 n×n 격자, 원점 중심).</summary>
    public static TriMesh CoarseBox(Vec3 size, int n)
    {
        var m = new TriMesh(); var hs = size / 2;
        Vec3 P(int x, int y, int z) => new(x * hs.X, y * hs.Y, z * hs.Z);
        m.AddGrid(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), n, n);
        m.AddGrid(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), n, n);
        m.AddGrid(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), n, n);
        m.AddGrid(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), n, n);
        m.AddGrid(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), n, n);
        m.AddGrid(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), n, n);
        return m;
    }

    /// <summary>□400×270×270 mm, 5.5 kg 상자가 꼭짓점(roll 45°, pitch 35.264°)으로 v [m/s] 강체 벽 충돌. 실패하면 예외.</summary>
    public static FeaOutcome CornerDrop(OpenCourantInstall inst, string dir, Material mat, double thickness, double speed = 7.2, int n = 10)
    {
        var q = Quat.FromEuler(45 * Math.PI / 180, 35.264 * Math.PI / 180, 0);
        var mesh = CoarseBox(new Vec3(0.40, 0.27, 0.27), n).Transformed(p => q.Rotate(p));
        double low = mesh.Vertices.Max(v => v.Z);
        var deck = RadiossDeck.WriteShellImpact(dir, "box", mesh, mat, thickness, new Vec3(0, 0, speed),
            new Vec3(0, 0, low + 0.001), new Vec3(0, 0, -1), 0.03, 10, totalMass: 5.5);
        var r = OpenCourantRunner.Run(inst, deck, TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException($"{mat.Key}: {r.Stage} — {r.Error}");
        return FeaOutcome.From(r.Final!, deck, mat);
    }
}

/// <summary>VTK legacy(ASCII) 파서: POINTS · CELLS · POINT_DATA/CELL_DATA 의 SCALARS·VECTORS.</summary>
public sealed class VtkData
{
    public List<Vec3> Points { get; } = new();
    public List<int[]> Cells { get; } = new();
    public Dictionary<string, double[]> CellScalars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double[]> PointScalars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vec3[]> PointVectors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vec3[]> CellVectors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 소성 변형률 셀 배열(이름에 PLAS/EPSP 포함). anim_to_vtk 는 2D(셸)·3D(고체) 배열을 따로 쓰므로(해당 없는 셀은 0)
    /// 셀마다 절대값 최대를 합친다. 없으면 null.
    /// </summary>
    public double[]? PlasticStrain => Merged("PLAS", "EPSP");
    public double[]? VonMises => Merged("VON_MISES", "VONM");
    /// <summary>EROSION_STATUS(0 = 삭제된 요소) — 없으면 null.</summary>
    public double[]? Erosion => CellScalars.FirstOrDefault(kv => kv.Key.Contains("EROSION", StringComparison.OrdinalIgnoreCase)).Value;
    public Vec3[]? Displacement => PointVectors.FirstOrDefault(kv => kv.Key.Contains("DISP", StringComparison.OrdinalIgnoreCase)).Value;
    /// <summary>절점 좌표에 inf/NaN 이 있으면 해석이 발산한 것(실제 OpenCourant 실행에서 관찰).</summary>
    public bool HasNonFinitePoints => Points.Any(p => !p.IsFinite);
    /// <summary>모델 요소(ELEMENT_ID ≥ 1)가 쓰는 절점만 — 강체 벽 표시 점 제외.</summary>
    public IEnumerable<Vec3> ElementPoints()
    {
        var ids = CellScalars.TryGetValue("ELEMENT_ID", out var e) ? e : null;
        var used = new HashSet<int>();
        for (int i = 0; i < Cells.Count; i++)
            if (ids is null || (i < ids.Length && ids[i] >= 1))
                foreach (var p in Cells[i]) used.Add(p);
        return used.Where(p => p >= 0 && p < Points.Count).Select(p => Points[p]);
    }
    /// <summary>시간(FIELD TIME) — 없으면 NaN.</summary>
    public double Time { get; private set; } = double.NaN;

    private double[]? Merged(params string[] keys)
    {
        double[]? r = null;
        foreach (var kv in CellScalars)
        {
            if (!keys.Any(k => kv.Key.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            if (r is null) { r = (double[])kv.Value.Clone(); continue; }
            for (int i = 0; i < r.Length && i < kv.Value.Length; i++)
                if (Math.Abs(kv.Value[i]) > Math.Abs(r[i])) r[i] = kv.Value[i];
        }
        return r;
    }

    /// <summary>
    /// 셀 값 → 덱 요소 번호(1..count) 순 배열. ELEMENT_ID 가 있으면 그것으로 — anim_to_vtk 는 강체 벽을 ELEMENT_ID 0 인 셀로
    /// 앞에 넣으므로(실제 출력 확인) 셀 순서 ≠ 요소 번호. 없으면 셀 순서 그대로. 값이 없는 요소는 NaN.
    /// </summary>
    public double[] ByElement(double[] cellValues, int count)
    {
        var r = Enumerable.Repeat(double.NaN, count).ToArray();
        var ids = CellScalars.TryGetValue("ELEMENT_ID", out var e) ? e : null;
        for (int i = 0; i < cellValues.Length; i++)
        {
            int id = ids is not null ? (i < ids.Length ? (int)ids[i] : 0) : i + 1;
            if (id >= 1 && id <= count) r[id - 1] = cellValues[i];
        }
        return r;
    }

    public static VtkData Parse(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"VTK 파일이 없습니다: {path}");
        var tok = new Tokenizer(File.ReadAllText(path));
        var d = new VtkData();
        string? section = null; int count = 0;
        if (!tok.Peek()?.StartsWith('#') ?? true) throw new InvalidDataException("VTK legacy 머리글(# vtk DataFile)이 없습니다.");
        tok.SkipLine(); tok.SkipLine(); // 머리글·제목
        var fmt = tok.Next();
        if (!string.Equals(fmt, "ASCII", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"ASCII VTK 만 지원합니다(형식: {fmt}).");
        while (tok.Next() is { } kw)
        {
            switch (kw.ToUpperInvariant())
            {
                case "DATASET": tok.Next(); break;
                case "POINTS":
                {
                    int n = tok.Int(); tok.Next();
                    for (int i = 0; i < n; i++) d.Points.Add(new Vec3(tok.Num(), tok.Num(), tok.Num()));
                    break;
                }
                case "CELLS":
                {
                    int n = tok.Int(); tok.Int();
                    for (int i = 0; i < n; i++)
                    {
                        int k = tok.Int();
                        var c = new int[k];
                        for (int j = 0; j < k; j++) c[j] = tok.Int();
                        d.Cells.Add(c);
                    }
                    break;
                }
                case "CELL_TYPES": { int n = tok.Int(); for (int i = 0; i < n; i++) tok.Int(); break; }
                case "POINT_DATA": section = "point"; count = tok.Int(); break;
                case "CELL_DATA": section = "cell"; count = tok.Int(); break;
                case "SCALARS":
                {
                    var name = tok.Next() ?? "s"; tok.Next();
                    // 선택적 numComp
                    int comps = 1;
                    if (tok.Peek() is { } pk && int.TryParse(pk, out var nc)) { comps = nc; tok.Next(); }
                    if (string.Equals(tok.Peek(), "LOOKUP_TABLE", StringComparison.OrdinalIgnoreCase)) { tok.Next(); tok.Next(); }
                    var arr = new double[count];
                    for (int i = 0; i < count; i++) { arr[i] = tok.Num(); for (int c = 1; c < comps; c++) tok.Num(); }
                    (section == "cell" ? d.CellScalars : d.PointScalars)[name] = arr;
                    break;
                }
                case "VECTORS":
                {
                    var name = tok.Next() ?? "v"; tok.Next();
                    var arr = new Vec3[count];
                    for (int i = 0; i < count; i++) arr[i] = new Vec3(tok.Num(), tok.Num(), tok.Num());
                    (section == "cell" ? d.CellVectors : d.PointVectors)[name] = arr;
                    break;
                }
                case "FIELD":
                {
                    tok.Next(); int arrays = tok.Int();
                    for (int a = 0; a < arrays; a++)
                    {
                        var name = tok.Next() ?? "f"; int comps = tok.Int(); int tuples = tok.Int(); tok.Next();
                        var arr = new double[tuples];
                        for (int i = 0; i < tuples; i++) { arr[i] = tok.Num(); for (int c = 1; c < comps; c++) tok.Num(); }
                        if (section is null && name.Equals("TIME", StringComparison.OrdinalIgnoreCase) && tuples > 0) { d.Time = arr[0]; continue; }
                        if (section is null) continue; // 데이터셋 전역 FIELD(CYCLE 등)
                        if (comps == 1) (section == "cell" ? d.CellScalars : d.PointScalars)[name] = arr;
                    }
                    break;
                }
                default: break;
            }
        }
        if (d.Points.Count == 0) throw new InvalidDataException("VTK 에 POINTS 가 없습니다(손상).");
        return d;
    }

    private sealed class Tokenizer(string text)
    {
        private int _i;
        public string? Peek() { int save = _i; var t = Next(); _i = save; return t; }
        public void SkipLine() { while (_i < text.Length && text[_i] != '\n') _i++; if (_i < text.Length) _i++; }
        public string? Next()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
            if (_i >= text.Length) return null;
            int s = _i;
            while (_i < text.Length && !char.IsWhiteSpace(text[_i])) _i++;
            return text[s.._i];
        }
        public double Num()
        {
            var t = Next() ?? throw new InvalidDataException("VTK 숫자 자료가 잘렸거나 손상되었습니다.");
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            // C 런타임 표기(anim_to_vtk 가 발산한 해석에서 씀): inf, -inf, nan, -nan(ind), 1.#INF, 1.#QNAN
            var u = t.ToLowerInvariant();
            if (u.Contains("nan")) return double.NaN;
            if (u.Contains("inf")) return u.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            throw new InvalidDataException($"VTK 숫자 자료가 손상되었습니다: '{(t.Length > 20 ? t[..20] : t)}'");
        }
        public int Int() => (int)Num();
    }
}

/// <summary>OpenCourant 실행기: starter → engine → anim_to_vtk. 실패·시간 초과·출력 없음을 오류로 돌려준다(예외 없음).</summary>
public static class OpenCourantRunner
{
    public static FeaRunResult Run(OpenCourantInstall inst, RadiossDeck.Deck deck, TimeSpan timeout, CancellationToken ct = default)
    {
        var res = new FeaRunResult { Dir = deck.Dir };
        var sw = Stopwatch.StartNew();
        try
        {
            res.Stage = "starter";
            var (okS, logS) = Exec(inst, inst.Starter, $"-i \"{Path.GetFileName(deck.StarterPath)}\" -np 1", deck.Dir, timeout, ct);
            res.StarterLog = logS;
            var outS = Path.Combine(deck.Dir, $"{deck.Name}_0000.out");
            if (!okS || !Normal(outS)) { res.Error = $"starter 실패 — {Tail(outS, logS)}"; return res; }
            res.Stage = "engine";
            var (okE, logE) = Exec(inst, inst.Engine, $"-i \"{Path.GetFileName(deck.EnginePath)}\"", deck.Dir, timeout, ct);
            res.EngineLog = logE;
            var outE = Path.Combine(deck.Dir, $"{deck.Name}_0001.out");
            if (!okE || !Normal(outE)) { res.Error = $"engine 실패 — {Tail(outE, logE)}"; return res; }
            res.Stage = "post";
            res.AnimFiles.AddRange(Directory.GetFiles(deck.Dir, $"{deck.Name}A*").Where(f => !f.EndsWith(".vtk", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f));
            if (res.AnimFiles.Count == 0) { res.Error = "애니메이션 결과(A###)가 없습니다."; return res; }
            if (inst.AnimToVtk is null) { res.Error = "anim_to_vtk 실행 파일이 없어 결과를 변환할 수 없습니다."; return res; }
            foreach (var a in res.AnimFiles)
            {
                var vtk = a + ".vtk";
                var (okV, logV) = Exec(inst, inst.AnimToVtk, $"\"{Path.GetFileName(a)}\"", deck.Dir, timeout, ct, stdoutFile: vtk);
                if (okV && File.Exists(vtk) && new FileInfo(vtk).Length > 0) res.VtkFiles.Add(vtk);
                else { res.Error = $"anim_to_vtk 실패: {logV}"; return res; }
            }
            res.Final = VtkData.Parse(res.VtkFiles[^1]);
            if (res.Final.HasNonFinitePoints)
            {
                res.Diverged = true;
                res.Error = $"해석 발산 — 마지막 프레임 절점 좌표에 inf/NaN ({res.Final.Points.Count(p => !p.IsFinite)}개). 요소 크기·재질·시간 스텝 확인: {Path.GetFileName(res.VtkFiles[^1])}";
                return res;
            }
            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException) { res.Error = "취소됨"; return res; }
        catch (Exception ex) { res.Error = $"{res.Stage} 실행 오류: {ex.Message}"; return res; }
        finally { res.Seconds = sw.Elapsed.TotalSeconds; }
    }

    /// <summary>정상 종료 판정: "NORMAL TERMINATION" 또는 "TERMINATION WITH WARNING" + "0 ERROR(S)" (경고만 있는 종료는 정상).</summary>
    public static bool Normal(string outFile)
    {
        if (!File.Exists(outFile)) return false;
        var text = File.ReadAllText(outFile);
        if (text.Contains("NORMAL TERMINATION", StringComparison.OrdinalIgnoreCase)) return true;
        return text.Contains("TERMINATION WITH WARNING", StringComparison.OrdinalIgnoreCase)
               && System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\s)0 ERROR\(S\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string Tail(string outFile, string log)
    {
        var text = File.Exists(outFile) ? File.ReadAllText(outFile) : log;
        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var err = lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        return string.Join(" | ", err.Count > 0 ? err : lines.TakeLast(5));
    }

    private static (bool Ok, string Log) Exec(OpenCourantInstall inst, string exe, string args, string dir, TimeSpan timeout, CancellationToken ct, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        inst.ApplyEnvironment(psi);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} 를 시작할 수 없습니다.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        if (!p.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (false, $"시간 초과({timeout.TotalSeconds:F0} s)");
        }
        ct.ThrowIfCancellationRequested();
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();
        if (stdoutFile is not null) File.WriteAllText(stdoutFile, stdout);
        File.AppendAllText(Path.Combine(dir, "mns_fea_console.log"), $"> {Path.GetFileName(exe)} {args}\n{(stdoutFile is null ? stdout : "(stdout → " + Path.GetFileName(stdoutFile) + ")")}\n{stderr}\n");
        return (p.ExitCode == 0, stdoutFile is null ? (stdout + stderr) : stderr);
    }
}

/// <summary>
/// 시뮬레이션 → FEA 충돌 케이스(3단계, FR-MAT-05): 선택 물체의 착지 순간(또는 최대 충돌) 자세·속도로 표면 셸 모델을 지면 강체 벽에 충돌시킨다.
/// FE 좌표 = NED 를 그대로(지면 법선 = −z). 요소 i = 표시 메시 면 i → 결과를 3D 뷰 면 색으로 1:1 표시.
/// </summary>
public static class FeaImpactCase
{
    public sealed record Setup(TriMesh MeshFe, Vec3 Velocity, Vec3 WallPoint, Vec3 WallNormal, double TEnd, Material Material, double Thickness, string Note, double Mass = double.NaN);

    public static Setup FromLanding(World w, Body b, TriMesh displayMesh)
    {
        var mat = b.Structure?.Material ?? throw new InvalidDataException($"{b.Name}: 재질(structure.material)이 없어 FEA 를 만들 수 없습니다.");
        double t = b.Structure.Thickness ?? mat.DefaultThickness ?? throw new InvalidDataException($"{b.Name}: 셸 두께(structure.thickness)가 필요합니다.");
        if (b.Impact is not { } imp) throw new InvalidDataException($"{b.Name}: 착지 기록이 없습니다(지면 충돌 전 종료).");
        var q = Quat.FromEuler(imp.EulerDeg.X * Math.PI / 180, imp.EulerDeg.Y * Math.PI / 180, imp.EulerDeg.Z * Math.PI / 180);
        // 동체 → NED 회전, 원점 = CG 를 0 으로, 지면(NED +z 가 아래)은 최저점에서 1 mm 아래
        var meshFe = displayMesh.Transformed(p => q.Rotate(p - b.OwnCg));
        double lowest = meshFe.Vertices.Max(v => v.Z);
        var wall = new Vec3(0, 0, lowest + 0.001);
        double size = (meshFe.Bounds().Max - meshFe.Bounds().Min).Norm;
        double v = Math.Max(imp.Velocity.Norm, 0.1);
        double tEnd = Math.Clamp(0.5 * size / v, 2e-3, 0.05);
        double mass = imp.Mass > 0 ? imp.Mass : b.OwnMass;
        return new Setup(meshFe, imp.Velocity, wall, new Vec3(0, 0, -1), tEnd, mat, t,
            $"{b.Name} 착지 충돌 {v:F2} m/s, 자세 {imp.EulerDeg}, 셸 t={t * 1000:F2} mm, {mat.Name}, 질량 {mass:F2} kg", mass);
    }

    public static JsonObject Describe(Setup s, RadiossDeck.Deck d) => new()
    {
        ["note"] = s.Note, ["dir"] = d.Dir, ["nodes"] = d.Nodes, ["elements"] = d.Elements, ["tEnd_s"] = s.TEnd,
        ["material"] = s.Material.Key, ["thickness_m"] = s.Thickness, ["speed_mps"] = s.Velocity.Norm,
    };
}
