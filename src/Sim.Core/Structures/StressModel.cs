using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Structures;

/// <summary>부위 상태(FR-MAT-02). 숫자가 클수록 심각.</summary>
public enum FailureState : byte { Unknown = 0, Safe = 1, Caution = 2, Buckling = 3, Yield = 4, Fracture = 5 }

/// <summary>면 응력의 지배 성분(FX-09): 보 전체 굽힘 · 패널 국부 굽힘 · 외피 막응력 · 연결점 핫스팟.</summary>
public enum StressComponent : byte { None = 0, Beam = 1, Panel = 2, Membrane = 3, Hotspot = 4 }

/// <summary>집중 하중(동체 좌표): 연결점 힘(라이저·테더·힌지·추력·펄스).</summary>
public readonly record struct PointLoad(Vec3 Point, Vec3 Force, string Source);

/// <summary>한 순간의 응력 계산 입력. VAirBody = 대기 대비 물체 속도(동체 좌표).</summary>
/// <param name="Carried">이 물체에 실려(Stowed) 함께 움직이는 자손의 질량 [kg]·CG 위치(동체 좌표) — 관성 완화 질량·관성에 더하고,
/// 그 관성 하중(−m·a)을 그 위치 슬라이스에 건다(탑재물 → 구조 하중 전달). null/빈 목록 = 자기 질량만.</param>
public readonly record struct StressInput(Vec3 VAirBody, double Rho, double Mach, Vec3 OmegaBody, IReadOnlyList<PointLoad> PointLoads, double StrainRate = 0,
    IReadOnlyList<CarriedMass>? Carried = null);

/// <summary>실린 자손 질량: 이름·질량 [kg]·CG(동체 좌표).</summary>
public readonly record struct CarriedMass(string Name, double Mass, Vec3 Point);

/// <summary>한 순간의 면별 결과(SI). 배열 길이 = 면 수.</summary>
public sealed class StressResult
{
    public double[] Pressure = [];   // Pa (q·Cp)
    public double[] Axial = [];      // Pa (σ_x, 인장 +)
    public double[] Shear = [];      // Pa
    public double[] VonMises = [];   // Pa
    public double[] Principal = [];  // Pa (최대 주응력)
    public double[] Safety = [];     // 안전계수(재질 없으면 NaN)
    public FailureState[] State = [];
    /// <summary>국부 응력(FX-09) [Pa]: 판 굽힘 = 표면 굽힘 응력 크기(±), 막 = 후프 응력(인장 +). 국부 항 없으면 0.</summary>
    public double[] Local = [];
    /// <summary>면이 받는 순 법선 압력(공력 + 관성 − 내압, 안쪽 +) [Pa].</summary>
    public double[] NetPressure = [];
    public StressComponent[] Component = [];
    /// <summary>최대 von Mises 면의 지배 성분.</summary>
    public StressComponent DominantAtMax;
    /// <summary>선형 판 이론 최대 처짐/두께(w/t). &gt; 0.5 이면 막 작용으로 실제 응력이 더 작다(선형 = 보수적).</summary>
    public double MaxPanelDeflectionRatio;
    /// <summary>슬라이스 경계별 전단력 크기·굽힘 모멘트 크기·축력 [N, N·m, N].</summary>
    public double[] ShearForce = [], BendingMoment = [], AxialForce = [];
    public double MaxVonMises, MaxPressure, MinPressure, MinSafety = double.PositiveInfinity;
    public int MaxFace = -1;
    public FailureState Worst = FailureState.Unknown;
    /// <summary>자유단 잔차(평형 검사): |V|, |M| at x1.</summary>
    public double ResidualForce, ResidualMoment;
    public Vec3 TotalAeroForce;
    public double RateFactor = 1;
    public List<(Vec3 Point, double Sigma, string Source)> Hotspots = new();
    /// <summary>면 위치 비중력 가속도 크기 [G] (가속도계 값: 관성 완화 강체장 또는 보 동해석) · 최대.</summary>
    public double[] AccelG = [];
    public double MaxAccelG;
    /// <summary>관성 완화 강체 가속도 [m/s²]·각가속도 [rad/s²] (동체 좌표, 외력 합 / 질량).</summary>
    public Vec3 RigidAccel, RigidAlpha;
    /// <summary>관성 완화 질량 중심(실린 탑재물 포함, 동체 좌표).</summary>
    public Vec3 MassCenter;

    internal void Ensure(int n, int ns)
    {
        MaxAccelG = 0;
        if (Pressure.Length != n)
        {
            AccelG = new double[n];
            Pressure = new double[n]; Axial = new double[n]; Shear = new double[n]; VonMises = new double[n];
            Principal = new double[n]; Safety = new double[n]; State = new FailureState[n];
            Local = new double[n]; NetPressure = new double[n]; Component = new StressComponent[n];
        }
        if (ShearForce.Length != ns + 1) { ShearForce = new double[ns + 1]; BendingMoment = new double[ns + 1]; AxialForce = new double[ns + 1]; }
        MaxVonMises = 0; MaxPressure = double.NegativeInfinity; MinPressure = double.PositiveInfinity; MinSafety = double.PositiveInfinity;
        MaxFace = -1; Worst = FailureState.Unknown; Hotspots.Clear(); RateFactor = 1;
        DominantAtMax = StressComponent.None; MaxPanelDeflectionRatio = 0;
    }
}

/// <summary>
/// 하중·근사 응력 모델(PRD W, FEA 아님):
///  (1) 표면 압력(FR-STR-01): 면 법선 n 과 상대풍 방향 v̂ 로 수정 뉴턴식 Cp = Cp₀·(n·v̂)² (바람받이),
///      옆·뒷면은 기저압 Cp_b(−0.3) 로 매끈하게 연결. Cp₀ = 정체점 압력계수(아음속 압축성 포함, M→0 이면 1).
///  (2) 길이축 보(FR-STR-02a): 형상 최장축을 보 축으로 슬라이스. 하중 = 면 압력 힘 + 집중 하중.
///      관성 완화(inertia relief): 하중 합력·모멘트로 강체 가속도 a, α 를 구해 슬라이스 질량에 관성하중을 걸어
///      자유-자유 보의 평형을 보장(잔차는 슬라이스 질량 비례 우력으로 배분). 단면 내력 N, V, M, T →
///      σ = N/A + M_y·z/I_y − M_z·y/I_z, τ ≈ k·|V|/A + T·r/J, von Mises √(σ²+3τ²).
///      단면: thinWall = 면 둘레 요소 × 두께, solid = 기본 형상 해석식 또는 슬라이스 범위 타원.
///  (2b) 얇은 벽 국부 항(FX-09): 면 순압력 p_net = 공력 q·Cp + 관성(면적 비례 질량 σ_A·a_local·n) − 내압 으로
///      상자 벽(가져온 메시는 경계 상자 면) = Roark 직사각판 σ = β(a/b)·p_net·b²/t²(4변 단순지지 기본, 고정 선택) × 판 안 분포,
///      원통·외피 조각 = 후프 막응력 p·r/t(닫힌 원통 내압은 축 p·r/2t 추가), 구 = p·r/2t(2축).
///      보 응력과 2축 von Mises 로 합성(판 굽힘은 ± 표면 중 나쁜 쪽). 면별 지배 성분과 선형 판 처짐 w/t 를 기록한다.
///  (3) 연결점 국부 하중 핫스팟(FR-STR-02b): σ_h = |F|/(π r_a²) 를 반경 3·r_a 가우스 감쇠로 주변 면에 겹침.
///  (4) 재질 판정(FR-MAT-02): 연성 vm ≥ σ_y → 항복, ≥ σ_u → 파단 / 취성 σ₁ ≥ σ_u → 파단,
///      얇은 벽 압축 σ ≤ −σ_cr → 좌굴(찌그러짐) 위험(FR-MAT-04), SF &lt; 1.5 → 주의. 강도는 변형률 속도 배율(FR-MAT-03) 적용.
///      CFRP(ply 물성)는 적층 각 ply 의 Tsai-Wu 지수로 파단(FR-MAT-06).
/// </summary>
public sealed class StressModel
{
    public const double CpBase = -0.3;
    public const double CautionSafety = 1.5;

    public Body Body { get; }
    public StructureDef? Def { get; }
    public TriMesh Mesh { get; }
    public int FaceCount { get; }
    public Vec3[] Centroid { get; }
    public Vec3[] Normal { get; }
    public double[] Area { get; }
    /// <summary>보 축(0=x,1=y,2=z)과 범위.</summary>
    public int Axis { get; }
    public double X0 { get; }
    public double X1 { get; }
    public int Slices { get; }
    public double TotalMass { get; }
    public Vec3 MassCentroid { get; }
    /// <summary>슬라이스별 질량·질량 중심·단면 성질.</summary>
    public double[] SliceMass { get; }
    public Vec3[] SliceCenter { get; }
    public double[] SecArea { get; }
    public double[] SecIu { get; }   // ∫ v² dA  (u 축 굽힘 → v 방향 응력 분포)
    public double[] SecIv { get; }   // ∫ u² dA
    public double[] SecCu { get; }
    public double[] SecCv { get; }
    /// <summary>슬라이스별 굽힘 섬유 거리 상한 |u − c_u|, |v − c_v| [m] (얇은 벽 = 둘레 요소 + t/2). 단면 밖 면의 σ = M·y/I 과대 방지(FX-10).</summary>
    public double[] SecExtU { get; private set; } = [];
    public double[] SecExtV { get; private set; } = [];
    public double[] BucklingStress { get; }
    public int[] FaceSlice { get; }
    public bool ThinWall { get; }
    public double ShearFactor { get; }
    /// <summary>면별 국부 응력 계수: σ_local = PanelFactor·p_net [Pa/Pa] (판 안 분포 포함). 0 = 국부 항 없음.</summary>
    public double[] PanelFactor { get; }
    /// <summary>면별 국부 항 종류: 0 없음, 1 판 굽힘(± 표면), 2 막(부호: 바깥 압력 → 압축).</summary>
    public byte[] PanelKind { get; }
    /// <summary>면별 선형 판 처짐/두께 계수: w/t = PanelDeflection·p_net.</summary>
    public double[] PanelDeflection { get; }
    /// <summary>면별 패널 치수 (긴 변 a, 짧은 변 b) [m] — 판 굽힘 면만.</summary>
    public (double A, double B)[] PanelSize { get; }
    /// <summary>면적 비례 질량 밀도 σ_A [kg/m²] (관성 압력).</summary>
    public double ArealMass { get; }
    /// <summary>내압 [Pa, 게이지].</summary>
    public double InternalPressure { get; }

    private readonly int _u, _v;
    private readonly double[] _axNet, _axInt;
    private readonly Mat3 _invIBeam, _iBeam;

    private static Mat3 SafeInverse(Mat3 ib)
    {
        try { return ib.Inverse(); }
        catch { return Mat3.Diagonal(1 / Math.Max(ib.M11, 1e-9), 1 / Math.Max(ib.M22, 1e-9), 1 / Math.Max(ib.M33, 1e-9)); }
    }
    private readonly Vec3[] _sliceLoad, _sliceMoment;

    private StressModel(Body b, TriMesh mesh)
    {
        Body = b;
        Def = b.Structure;
        Mesh = mesh;
        FaceCount = mesh.TriangleCount;
        Centroid = new Vec3[FaceCount]; Normal = new Vec3[FaceCount]; Area = new double[FaceCount];
        for (int i = 0; i < FaceCount; i++)
        {
            var a = mesh.Vertices[3 * i]; var bb = mesh.Vertices[3 * i + 1]; var c = mesh.Vertices[3 * i + 2];
            var cr = Vec3.Cross(bb - a, c - a);
            double n = cr.Norm;
            Area[i] = n / 2;
            Normal[i] = n > 1e-300 ? cr / n : Vec3.Zero;
            Centroid[i] = (a + bb + c) / 3;
        }
        var (min, max) = mesh.Bounds();
        var size = max - min;
        Axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        if (b.Shape is { Kind: ShapeKind.Cylinder or ShapeKind.ShellSegment } s0) Axis = s0.Axis;
        _u = (Axis + 1) % 3; _v = (Axis + 2) % 3;
        X0 = min[Axis]; X1 = max[Axis];
        if (X1 - X0 < 1e-9) X1 = X0 + 1e-9;
        Slices = Def?.Slices ?? 40;
        ThinWall = Def?.Section == "thinWall";
        ShearFactor = ThinWall ? 2.0 : 1.5;
        TotalMass = Math.Max(b.OwnMass, 1e-9);

        int ns = Slices;
        double dx = (X1 - X0) / ns;
        FaceSlice = new int[FaceCount];
        for (int i = 0; i < FaceCount; i++) FaceSlice[i] = Math.Clamp((int)((Centroid[i][Axis] - X0) / dx), 0, ns - 1);

        // ---- 단면 성질(슬라이스별): 옆면(축과 거의 평행한 면) 둘레 요소 ----
        SecArea = new double[ns]; SecIu = new double[ns]; SecIv = new double[ns]; SecCu = new double[ns]; SecCv = new double[ns];
        BucklingStress = new double[ns];
        var ds = new double[ns]; var su = new double[ns]; var sv = new double[ns];
        var umin = Enumerable.Repeat(double.MaxValue, ns).ToArray(); var umax = Enumerable.Repeat(double.MinValue, ns).ToArray();
        var vmin = Enumerable.Repeat(double.MaxValue, ns).ToArray(); var vmax = Enumerable.Repeat(double.MinValue, ns).ToArray();
        var faceArea = new double[ns];
        // 면이 걸친 슬라이스들에 x 겹침 비율로 나눠 담는다(면 경계와 슬라이스 경계가 어긋나도 단면 성질이 고르게)
        var spans = new (int K0, int K1, double Lo, double Hi)[FaceCount];
        for (int i = 0; i < FaceCount; i++)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int j = 0; j < 3; j++) { double x = mesh.Vertices[3 * i + j][Axis]; lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            int k0 = Math.Clamp((int)((lo - X0) / dx), 0, ns - 1), k1 = Math.Clamp((int)((hi - X0) / dx), 0, ns - 1);
            if (hi > lo && k1 > k0 && (X0 + k1 * dx) >= hi - 1e-12 * dx) k1--; // 경계에 정확히 닿는 끝은 다음 슬라이스 아님
            spans[i] = (k0, Math.Max(k0, k1), lo, hi);
        }
        double Weight(int i, int k)
        {
            var (k0, k1, lo, hi) = spans[i];
            if (k0 == k1 || hi - lo < 1e-12) return k == FaceSlice[i] || k0 == k1 ? 1 : 0;
            double a = Math.Max(lo, X0 + k * dx), bnd = Math.Min(hi, X0 + (k + 1) * dx);
            return Math.Max(0, bnd - a) / (hi - lo);
        }
        for (int i = 0; i < FaceCount; i++)
        {
            double na = Math.Abs(Normal[i][Axis]);
            double lFull = na > 0.7 ? 0 : Area[i] * Math.Sqrt(Math.Max(0, 1 - na * na)) / dx;
            for (int k = spans[i].K0; k <= spans[i].K1; k++)
            {
                double wk = Weight(i, k);
                if (wk <= 0) continue;
                faceArea[k] += Area[i] * wk;
                for (int j = 0; j < 3; j++)
                {
                    var p = mesh.Vertices[3 * i + j];
                    umin[k] = Math.Min(umin[k], p[_u]); umax[k] = Math.Max(umax[k], p[_u]);
                    vmin[k] = Math.Min(vmin[k], p[_v]); vmax[k] = Math.Max(vmax[k], p[_v]);
                }
                if (lFull <= 0) continue;
                double l = lFull * wk;
                ds[k] += l; su[k] += l * Centroid[i][_u]; sv[k] += l * Centroid[i][_v];
            }
        }
        double t = Def?.Thickness ?? 0;
        var iuAcc = new double[ns]; var ivAcc = new double[ns];
        SecExtU = new double[ns]; SecExtV = new double[ns];
        for (int k = 0; k < ns; k++)
        {
            bool haveRange = umax[k] > umin[k];
            SecCu[k] = ds[k] > 0 ? su[k] / ds[k] : haveRange ? (umin[k] + umax[k]) / 2 : 0;
            SecCv[k] = ds[k] > 0 ? sv[k] / ds[k] : haveRange ? (vmin[k] + vmax[k]) / 2 : 0;
            // 섬유 거리 상한(기본): 슬라이스에 걸친 모든 꼭짓점 범위
            SecExtU[k] = haveRange ? Math.Max(umax[k] - SecCu[k], SecCu[k] - umin[k]) : 0;
            SecExtV[k] = vmax[k] > vmin[k] ? Math.Max(vmax[k] - SecCv[k], SecCv[k] - vmin[k]) : 0;
        }
        if (ThinWall)
        {
            // 얇은 벽: 단면 = 둘레 요소. 섬유 거리 상한 = 둘레 요소 중심의 최대 거리 + t/2 (끝판·격벽 면이 단면 밖 거리로 σ = M·y/I 를 부풀리지 않게)
            var extU = new double[ns]; var extV = new double[ns];
            for (int i = 0; i < FaceCount; i++)
            {
                var nrm = Normal[i];
                double na = Math.Abs(nrm[Axis]);
                if (na > 0.7) continue;
                double lFull = Area[i] * Math.Sqrt(Math.Max(0, 1 - na * na)) / dx;
                // 벽 자체 두께 방향 관성 t²/12·n̂² (단면 내 법선 성분) — 둘레가 한 줄로 늘어선 퇴화 단면(평판 띠)에서도 I > 0 (FX-10)
                double nu2 = nrm[_u] * nrm[_u], nv2 = nrm[_v] * nrm[_v], nn = nu2 + nv2;
                if (nn > 1e-12) { nu2 /= nn; nv2 /= nn; }
                for (int k = spans[i].K0; k <= spans[i].K1; k++)
                {
                    double wk = Weight(i, k);
                    if (wk <= 0) continue;
                    double du = Centroid[i][_u] - SecCu[k], dv = Centroid[i][_v] - SecCv[k];
                    double dA = t * lFull * wk;
                    iuAcc[k] += dA * (dv * dv + t * t * nv2 / 12); ivAcc[k] += dA * (du * du + t * t * nu2 / 12);
                    extU[k] = Math.Max(extU[k], Math.Abs(du) + t / 2); extV[k] = Math.Max(extV[k], Math.Abs(dv) + t / 2);
                }
            }
            for (int k = 0; k < ns; k++)
                if (ds[k] > 0) { SecExtU[k] = extU[k]; SecExtV[k] = extV[k]; }
        }
        for (int k = 0; k < ns; k++)
        {
            bool haveRange = umax[k] > umin[k];
            if (ThinWall && ds[k] > 0)
            {
                SecArea[k] = t * ds[k]; SecIu[k] = iuAcc[k]; SecIv[k] = ivAcc[k];
            }
            else
            {
                (SecArea[k], SecIu[k], SecIv[k]) = SolidSection(b.Shape, haveRange ? umax[k] - umin[k] : 0, haveRange ? vmax[k] - vmin[k] : 0);
            }
        }
        // 빈 슬라이스(면 없음)는 이웃 값으로
        for (int pass = 0; pass < 2; pass++)
            for (int k = 0; k < ns; k++)
            {
                if (SecArea[k] > 0) continue;
                int j = k > 0 && SecArea[k - 1] > 0 ? k - 1 : k + 1 < ns && SecArea[k + 1] > 0 ? k + 1 : -1;
                if (j < 0) continue;
                SecArea[k] = SecArea[j]; SecIu[k] = SecIu[j]; SecIv[k] = SecIv[j]; SecCu[k] = SecCu[j]; SecCv[k] = SecCv[j];
                SecExtU[k] = Math.Max(SecExtU[k], SecExtU[j]); SecExtV[k] = Math.Max(SecExtV[k], SecExtV[j]);
            }
        for (int k = 0; k < ns; k++)
        {
            if (SecArea[k] <= 0) { SecArea[k] = 1e-6; SecIu[k] = SecIv[k] = 1e-12; }
            SecIu[k] = Math.Max(SecIu[k], 1e-14); SecIv[k] = Math.Max(SecIv[k], 1e-14);
            BucklingStress[k] = ThinWall && Def?.Material is { } mat ? Buckling(b.Shape, mat, t, SecArea[k], SecIu[k] + SecIv[k],
                Math.Max(umax[k] - umin[k], vmax[k] - vmin[k])) : double.PositiveInfinity;
        }

        // ---- 질량 분포: thinWall = 면적 비례, solid = 단면적 비례 ----
        SliceMass = new double[ns]; SliceCenter = new Vec3[ns];
        double wsum = 0;
        var w = new double[ns];
        for (int k = 0; k < ns; k++) { w[k] = ThinWall ? faceArea[k] : SecArea[k]; wsum += w[k]; }
        var mc = Vec3.Zero;
        for (int k = 0; k < ns; k++)
        {
            SliceMass[k] = wsum > 0 ? TotalMass * w[k] / wsum : TotalMass / ns;
            SliceCenter[k] = AxisPoint(X0 + (k + 0.5) * dx, SecCu[k], SecCv[k]);
            mc += SliceCenter[k] * SliceMass[k];
        }
        MassCentroid = mc / TotalMass;
        // 보 이산 질량 관성(축 방향 성분은 물체 고유 관성으로 보강 — 축 위 점질량은 축 관성 0)
        var ib = Mat3.Zero;
        for (int k = 0; k < ns; k++) ib = ib + Mat3.ParallelAxis(SliceMass[k], SliceCenter[k] - MassCentroid);
        var own = b.OwnInertia;
        double axial = Axis switch { 0 => own.M11, 1 => own.M22, _ => own.M33 };
        ib = ib + Axis switch
        {
            0 => Mat3.Diagonal(Math.Max(axial, 1e-9), 0, 0),
            1 => Mat3.Diagonal(0, Math.Max(axial, 1e-9), 0),
            _ => Mat3.Diagonal(0, 0, Math.Max(axial, 1e-9)),
        };
        _iBeam = ib;
        _invIBeam = SafeInverse(ib);
        _sliceLoad = new Vec3[ns]; _sliceMoment = new Vec3[ns];

        // ---- 얇은 벽 국부 항(FX-09) ----
        PanelFactor = new double[FaceCount]; PanelKind = new byte[FaceCount]; PanelDeflection = new double[FaceCount];
        PanelSize = new (double, double)[FaceCount];
        _axNet = new double[FaceCount]; _axInt = new double[FaceCount];
        double totalArea = Area.Sum();
        ArealMass = totalArea > 0 ? TotalMass / totalArea : 0;
        InternalPressure = Def?.InternalPressure ?? 0;
        if (ThinWall && t > 0) BuildPanels(b.Shape, t, Def?.Material?.E ?? 70e9, Def?.Material?.Nu ?? 0.3, Def?.Edge == "clamped", min, max);
    }

    // Roark & Young, Formulas for Stress and Strain 7th ed., Table 11.4 (균일 하중 직사각판, ν = 0.3):
    // case 1 4변 단순지지 — 중앙 σ = β·q·b²/t², 처짐 y = α·q·b⁴/(E·t³); case 8 4변 고정 — 긴 변 중앙 σ = β1·q·b²/t², 중앙 β2, y = α·q·b⁴/(E·t³).
    private static readonly (double R, double Beta, double Alpha)[] RoarkSimple =
    [
        (1.0, 0.2874, 0.0444), (1.2, 0.3762, 0.0616), (1.4, 0.4530, 0.0770), (1.6, 0.5172, 0.0906), (1.8, 0.5688, 0.1017),
        (2.0, 0.6102, 0.1110), (3.0, 0.7134, 0.1335), (4.0, 0.7410, 0.1400), (5.0, 0.7476, 0.1417), (1e9, 0.7500, 0.1421),
    ];
    private static readonly (double R, double Beta1, double Beta2, double Alpha)[] RoarkClamped =
    [
        (1.0, 0.3078, 0.1386, 0.0138), (1.2, 0.3834, 0.1794, 0.0188), (1.4, 0.4356, 0.2094, 0.0226), (1.6, 0.4680, 0.2286, 0.0251),
        (1.8, 0.4872, 0.2406, 0.0267), (2.0, 0.4974, 0.2472, 0.0277), (1e9, 0.5000, 0.2500, 0.0284),
    ];

    /// <summary>Roark 직사각판 계수(a/b 선형 보간): 단순지지 (β, α), 고정 (β1 가장자리, β2 중앙, α).</summary>
    public static (double Beta, double BetaCenter, double Alpha) PlateCoefficients(double aOverB, bool clamped)
    {
        double r = Math.Max(aOverB, 1);
        if (!clamped)
        {
            for (int i = 1; i < RoarkSimple.Length; i++)
                if (r <= RoarkSimple[i].R)
                {
                    var (r0, b0, a0) = RoarkSimple[i - 1]; var (r1, b1, a1) = RoarkSimple[i];
                    double f = (r - r0) / (r1 - r0);
                    double beta = b0 + (b1 - b0) * f;
                    return (beta, beta, a0 + (a1 - a0) * f);
                }
            return (0.75, 0.75, 0.1421);
        }
        for (int i = 1; i < RoarkClamped.Length; i++)
            if (r <= RoarkClamped[i].R)
            {
                var p = RoarkClamped[i - 1]; var q = RoarkClamped[i];
                double f = (r - p.R) / (q.R - p.R);
                return (p.Beta1 + (q.Beta1 - p.Beta1) * f, p.Beta2 + (q.Beta2 - p.Beta2) * f, p.Alpha + (q.Alpha - p.Alpha) * f);
            }
        return (0.5, 0.25, 0.0284);
    }

    /// <summary>
    /// 판 안 분포(최대 = 1): 단순지지 = sin πξ · sin πη(중앙 최대), 고정 = 짧은 방향 η 로 가장자리 1 → 중앙 β2/β1(부호 바뀜, 크기만)
    /// × 긴 방향 sin πξ. ξ = 긴 변 방향, η = 짧은 변 방향 위치 ∈ [0,1].
    /// </summary>
    public static double PlateShape(double xi, double eta, bool clamped, double centerRatio)
    {
        xi = Math.Clamp(xi, 0, 1); eta = Math.Clamp(eta, 0, 1);
        if (!clamped) return Math.Sin(Math.PI * xi) * Math.Sin(Math.PI * eta);
        double c2 = Math.Cos(Math.PI * eta); c2 *= c2;
        return Math.Abs((1 + centerRatio) * c2 - centerRatio) * Math.Sin(Math.PI * xi);
    }

    private void BuildPanels(Shape? s, double t, double e, double nu, bool clamped, Vec3 meshMin, Vec3 meshMax)
    {
        double flex = 12 * (1 - nu * nu);
        switch (s?.Kind)
        {
            case ShapeKind.Cylinder:
            {
                double r = s.Radius;
                for (int i = 0; i < FaceCount; i++)
                {
                    if (Math.Abs(Normal[i][s.Axis]) < 0.7)
                    {
                        PanelKind[i] = 2; PanelFactor[i] = r / t; _axInt[i] = r / (2 * t); // 후프 + 닫힌 끝 축 막응력(내압)
                    }
                    else
                    {
                        // 끝판: 단순지지 원판 σ_r = 3(3+ν)/8 · p · (a² − ρ²)/t², 처짐 w = p a⁴ (5+ν) / (64 D (1+ν))
                        var d = Centroid[i] - s.Center; double rho2 = d.NormSq - d[s.Axis] * d[s.Axis];
                        PanelKind[i] = 1;
                        PanelFactor[i] = 3 * (3 + nu) / 8 * Math.Max(r * r - rho2, 0) / (t * t);
                        PanelDeflection[i] = Math.Pow(r, 4) * (5 + nu) * flex / (64 * e * t * t * t * (1 + nu)) / t * Math.Max(1 - rho2 / (r * r), 0);
                        PanelSize[i] = (2 * r, 2 * r);
                    }
                }
                break;
            }
            case ShapeKind.ShellSegment:
                for (int i = 0; i < FaceCount; i++) { PanelKind[i] = 2; PanelFactor[i] = s.Radius / t; }
                break;
            case ShapeKind.Sphere:
                for (int i = 0; i < FaceCount; i++) { PanelKind[i] = 2; PanelFactor[i] = s.Radius / (2 * t); _axNet[i] = s.Radius / (2 * t); }
                break;
            default:
            {
                // 상자(또는 가져온 메시의 경계 상자) 각 면 = 직사각판. 메시는 경계 평면 위(크기의 2% 이내)에 있는 평평한 면만.
                Vec3 lo, hi;
                if (s?.Kind == ShapeKind.Box) { lo = s.Center - s.Size / 2; hi = s.Center + s.Size / 2; }
                else { lo = meshMin; hi = meshMax; }
                var size = hi - lo;
                for (int i = 0; i < FaceCount; i++)
                {
                    var n = Normal[i];
                    int k = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
                    if (Math.Abs(n[k]) < 0.9) continue;
                    double plane = n[k] > 0 ? hi[k] : lo[k];
                    if (Math.Abs(Centroid[i][k] - plane) > 0.02 * Math.Max(size[k], 1e-9) + 1e-9) continue;
                    int i1 = (k + 1) % 3, i2 = (k + 2) % 3;
                    double d1 = size[i1], d2 = size[i2];
                    if (d1 <= 0 || d2 <= 0) continue;
                    // a = 긴 변, b = 짧은 변
                    int ia = d1 >= d2 ? i1 : i2, ib = d1 >= d2 ? i2 : i1;
                    double a = size[ia], bb = size[ib];
                    double xi = (Centroid[i][ia] - lo[ia]) / a, eta = (Centroid[i][ib] - lo[ib]) / bb;
                    var (beta, betaC, alpha) = PlateCoefficients(a / bb, clamped);
                    double shape = PlateShape(xi, eta, clamped, betaC / beta);
                    PanelKind[i] = 1;
                    PanelFactor[i] = beta * bb * bb / (t * t) * shape;
                    double sw = Math.Sin(Math.PI * xi) * Math.Sin(Math.PI * eta);
                    // Roark α 는 ν = 0.3 기준 — 처짐 ∝ (1−ν²) 로 보정
                    PanelDeflection[i] = alpha * Math.Pow(bb, 4) / (e * t * t * t) / t * (clamped ? sw * sw : sw) * (1 - nu * nu) / 0.91;
                    PanelSize[i] = (a, bb);
                }
                break;
            }
        }
    }

    private Vec3 AxisPoint(double x, double u, double v)
    {
        var a = new double[3];
        a[Axis] = x; a[_u] = u; a[_v] = v;
        return new Vec3(a[0], a[1], a[2]);
    }

    /// <summary>기본 형상 해석 단면(solid) — 상자: 직사각형, 원통(축 정렬): 원, 그 외: 범위 타원.</summary>
    private (double A, double Iu, double Iv) SolidSection(Shape? s, double wu, double wv)
    {
        if (s?.Kind == ShapeKind.Box)
        {
            double bu = s.Size[_u], bv = s.Size[_v];
            return (bu * bv, bu * bv * bv * bv / 12, bv * bu * bu * bu / 12);
        }
        if (s?.Kind == ShapeKind.Cylinder && s.Axis == Axis)
        {
            double r = s.Radius;
            double i = Math.PI * r * r * r * r / 4;
            return (Math.PI * r * r, i, i);
        }
        double a = wu / 2, bb = wv / 2;
        if (a <= 0 || bb <= 0) return (0, 0, 0);
        return (Math.PI * a * bb, Math.PI * a * bb * bb * bb / 4, Math.PI * a * a * a * bb / 4);
    }

    /// <summary>
    /// 얇은 벽 압축 좌굴 응력(FR-MAT-04): 원통·곡면 = 고전식 E·t/(R√(3(1−ν²))) × 녹다운 γ(NASA SP-8007:
    /// γ = 1 − 0.901(1 − e^{−φ}), φ = √(R/t)/16), 상자 벽 = 평판 k=4 단순지지 4π²E/(12(1−ν²))·(t/b)².
    /// R = 단면 회전반경(얇은 원관이면 반경), b = 단면 최대 폭.
    /// </summary>
    public static double Buckling(Shape? s, Material m, double t, double area, double polarI, double width)
    {
        if (t <= 0) return double.PositiveInfinity;
        double e = m.E, nu = m.Nu;
        if (s?.Kind == ShapeKind.Box)
        {
            double b = Math.Max(width, t);
            return 4 * Math.PI * Math.PI * e / (12 * (1 - nu * nu)) * (t / b) * (t / b);
        }
        double r = s?.Kind == ShapeKind.ShellSegment ? s.Radius : Math.Sqrt(Math.Max(polarI / Math.Max(area, 1e-12), 1e-12));
        double phi = Math.Sqrt(r / t) / 16;
        double gamma = 1 - 0.901 * (1 - Math.Exp(-phi));
        return gamma * e * t / (r * Math.Sqrt(3 * (1 - nu * nu)));
    }

    /// <summary>
    /// 외압 좌굴 임계 압력(짧은 원통, Windenburg–Trilling 근사): p_cr = 0.807·E·t²/(L·R)·(t²/(R²(1−ν²)³))^¼.
    /// </summary>
    public static double ExternalPressureBuckling(double e, double nu, double t, double r, double l)
    {
        if (t <= 0 || r <= 0 || l <= 0) return double.PositiveInfinity;
        double k = 1 - nu * nu;
        return 0.807 * e * t * t / (l * r) * Math.Pow(t * t / (r * r * k * k * k), 0.25);
    }

    /// <summary>정체점 압력계수(아음속 등엔트로피): Cp₀ = 2/(γM²)·[(1+(γ−1)/2·M²)^(γ/(γ−1)) − 1], M→0 이면 1.</summary>
    public static double StagnationCp(double mach)
    {
        if (mach < 1e-3) return 1;
        const double g = 1.4;
        double m2 = mach * mach;
        return 2 / (g * m2) * (Math.Pow(1 + (g - 1) / 2 * m2, g / (g - 1)) - 1);
    }

    /// <summary>수정 뉴턴 + 기저압: cosθ = n·v̂ (v̂ = 대기 대비 물체 이동 방향, n 바깥 법선).</summary>
    public static double PressureCoefficient(double cosIncidence, double mach)
    {
        if (cosIncidence > 0) return StagnationCp(mach) * cosIncidence * cosIncidence;
        // 옆면(0) → 뒷면: 0 에서 −0.25 사이 선형으로 기저압에 연결
        return CpBase * Math.Min(1, -cosIncidence / 0.25);
    }

    /// <summary>표시·응력 메시 길이 방향 분할 수(슬라이스 40 의 배수 → 경계 정렬).</summary>
    public const int AxialDivisions = 80;

    /// <summary>
    /// 세분 표시 메시(동체 좌표). 기본 형상은 구조 격자(보 축 80 등분, 단면 방향 ≤ 16)로 대칭을 유지하고,
    /// 가져온 메시는 최장 변 ≤ 최대 치수/60 까지 너비 우선 이분할(삼각형 상한 60k).
    /// </summary>
    public static TriMesh DisplayMesh(Shape s)
    {
        switch (s.Kind)
        {
            case ShapeKind.Box:
            {
                var z = s.Size;
                double l = Math.Max(z.X, Math.Max(z.Y, z.Z));
                double h = l / AxialDivisions;
                // 최장축은 80 등분, 나머지는 셀이 길이 셀의 2배 이하가 되게(최대 16)
                double ht = Math.Max(h, Math.Min(z.X, Math.Min(z.Y, z.Z)) / 16);
                var m = new TriMesh();
                int N(double len, bool longest) => longest ? AxialDivisions : Math.Clamp((int)Math.Ceiling(len / ht), 2, 16);
                int ax = z.X >= z.Y && z.X >= z.Z ? 0 : z.Y >= z.Z ? 1 : 2;
                int nx = N(z.X, ax == 0), ny = N(z.Y, ax == 1), nz = N(z.Z, ax == 2);
                var c = s.Center; var hs = z / 2;
                Vec3 P(int x, int y, int zz) => c + new Vec3(x * hs.X, y * hs.Y, zz * hs.Z);
                m.AddGrid(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), ny, nz);
                m.AddGrid(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), nz, ny);
                m.AddGrid(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), nz, nx);
                m.AddGrid(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), nx, nz);
                m.AddGrid(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), nx, ny);
                m.AddGrid(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), ny, nx);
                return m;
            }
            case ShapeKind.Cylinder:
                return TriMesh.CylinderGrid(s.Radius, s.Length, s.Axis, s.Center, 48, AxialDivisions, 4);
            case ShapeKind.ShellSegment:
                return TriMesh.ShellSegment(s.Radius, s.Thickness, s.Length, s.SpanDeg, s.CenterAngleDeg, s.Axis, s.Center, 0, AxialDivisions);
            case ShapeKind.Sphere:
                return TriMesh.Sphere(s.Radius, s.Center, 16, 32);
            default:
            {
                var m = s.BuildMesh();
                var (min, max) = m.Bounds();
                var size = max - min;
                double l = Math.Max(size.X, Math.Max(size.Y, size.Z));
                return m.Subdivided(l / 60, Math.Max(60000, m.TriangleCount));
            }
        }
    }

    public static string ComponentLabel(StressComponent c) => c switch
    {
        StressComponent.Beam => "보 전체 굽힘",
        StressComponent.Panel => "패널 국부 굽힘",
        StressComponent.Membrane => "외피 막응력",
        StressComponent.Hotspot => "연결점 핫스팟",
        _ => "—",
    };

    public static StressModel? Build(Body b, TriMesh? displayMesh = null)
    {
        if (b.Shape is null || b.OwnMass <= 0) return null;
        return new StressModel(b, displayMesh ?? DisplayMesh(b.Shape));
    }

    /// <summary>한 순간의 면별 압력·응력·상태.</summary>
    /// <summary>슬라이스(보 절점)별 외력 [N]·원점 기준 모멘트 [N·m] (동체 좌표): 표면 압력 + 연결점 하중. 중력(균일)은 내력을 만들지 않아 제외.</summary>
    public void ExternalSliceLoads(in StressInput inp, Vec3[] force, Vec3[] momentAboutOrigin)
    {
        // 스레드 안전(공유 버퍼 안 씀) — 충격 응답 해석은 배경 스레드에서 돈다
        var r = new StressResult();
        r.Ensure(FaceCount, Slices);
        Array.Clear(force, 0, Slices); Array.Clear(momentAboutOrigin, 0, Slices);
        AccumulateLoads(inp, r, force, momentAboutOrigin);
    }

    /// <summary>보 축 좌표계(축 x, 단면 u, v) 성분 → 동체 좌표 벡터.</summary>
    public Vec3 FromAxisFrame(double x, double u, double v) => AxisPoint(x, u, v);

    /// <summary>동체 좌표 벡터 → 보 축 좌표계 성분 (x, u, v).</summary>
    public (double X, double U, double V) ToAxisFrame(Vec3 p) => (p[Axis], p[_u], p[_v]);

    public const double G0 = 9.80665;

    /// <summary>슬라이스 중심값을 보 축 좌표 x 에서 선형 보간(양 끝은 끝 슬라이스 값).</summary>
    public Vec3 InterpSlice(Vec3[] v, double x)
    {
        double dx = (X1 - X0) / Slices;
        double s = (x - X0) / dx - 0.5;
        if (s <= 0) return v[0];
        if (s >= Slices - 1) return v[Slices - 1];
        int k = (int)s; double f = s - k;
        return v[k] * (1 - f) + v[k + 1] * f;
    }

    /// <summary>보 축 좌표 x 의 슬라이스(0..Slices−1).</summary>
    public int SliceAt(double x) => Math.Clamp((int)((x - X0) / ((X1 - X0) / Slices)), 0, Slices - 1);

    private void AccumulateLoads(in StressInput inp, StressResult r, Vec3[] sliceLoad, Vec3[] sliceMoment)
    {
        int ns = Slices, n = FaceCount;
        double dx = (X1 - X0) / ns;
        double v = inp.VAirBody.Norm;
        double q = 0.5 * inp.Rho * v * v;
        var vh = v > 1e-9 ? inp.VAirBody / v : Vec3.Zero;
        var fAero = Vec3.Zero;
        for (int i = 0; i < n; i++)
        {
            double p = v > 1e-9 ? q * PressureCoefficient(Vec3.Dot(Normal[i], vh), inp.Mach) : 0;
            r.Pressure[i] = p;
            if (p > r.MaxPressure) r.MaxPressure = p;
            if (p < r.MinPressure) r.MinPressure = p;
            var f = Normal[i] * (-p * Area[i]);
            int k = FaceSlice[i];
            sliceLoad[k] += f;
            sliceMoment[k] += Vec3.Cross(Centroid[i], f);
            fAero += f;
        }
        r.TotalAeroForce = fAero;
        foreach (var pl in inp.PointLoads)
        {
            int k = Math.Clamp((int)((pl.Point[Axis] - X0) / dx), 0, ns - 1);
            sliceLoad[k] += pl.Force;
            sliceMoment[k] += Vec3.Cross(pl.Point, pl.Force);
        }
    }

    /// <summary>
    /// 한 순간의 면별 압력·응력·상태. sliceAccel(선택) = 보 동해석(ShockResponse)의 슬라이스별 비중력 가속도 [m/s², 동체 좌표] —
    /// 있으면 관성 완화(강체 a, α) 대신 −m_k·a_k 를 관성 하중으로 써서 동적 내력(충격 응답)을 낸다(원심 ω×ω×r 은 그대로 더함).
    /// </summary>
    public StressResult Evaluate(in StressInput inp, StressResult? reuse = null, Vec3[]? sliceAccel = null)
    {
        var r = reuse ?? new StressResult();
        int ns = Slices, n = FaceCount;
        r.Ensure(n, ns);
        Array.Clear(_sliceLoad); Array.Clear(_sliceMoment);
        double dx = (X1 - X0) / ns;
        if (sliceAccel is not null && sliceAccel.Length != ns) throw new ArgumentException("sliceAccel 길이 ≠ 슬라이스 수");
        AccumulateLoads(inp, r, _sliceLoad, _sliceMoment);
        var fTotal = Vec3.Zero; var mTotal = Vec3.Zero; // 원점 기준
        for (int k = 0; k < ns; k++) { fTotal += _sliceLoad[k]; mTotal += _sliceMoment[k]; }

        // ---- 관성 완화: a = F/m, α = I⁻¹(M_c), 원심 포함, 잔차는 질량 비례 우력 ----
        // 실린 자손(탑재물) 질량을 합성: 질량 중심·관성
        var carried = inp.Carried ?? [];
        double mTot = TotalMass; var cmSum = MassCentroid * TotalMass;
        foreach (var cj in carried) { mTot += cj.Mass; cmSum += cj.Point * cj.Mass; }
        var c = cmSum / mTot;
        var invI = _invIBeam;
        if (carried.Count > 0)
        {
            var it = _iBeam + Mat3.ParallelAxis(TotalMass, MassCentroid - c);
            foreach (var cj in carried) it = it + Mat3.ParallelAxis(cj.Mass, cj.Point - c);
            invI = SafeInverse(it);
        }
        var a = fTotal / mTot;
        var mc = mTotal - Vec3.Cross(c, fTotal);
        var w = inp.OmegaBody;
        var alpha = invI * mc;
        var resid = Vec3.Zero;
        var inertF = new Vec3[ns];
        var fRes = fTotal;
        for (int k = 0; k < ns; k++)
        {
            var rho = SliceCenter[k] - c;
            var ak = sliceAccel is null ? a + Vec3.Cross(alpha, rho) : sliceAccel[k];
            inertF[k] = (ak + Vec3.Cross(w, Vec3.Cross(w, rho))) * -SliceMass[k];
            fRes += inertF[k];
        }
        // 탑재물 관성 하중 −m_j·a(p_j) → 그 위치 슬라이스(마운트를 통한 하중 전달)
        var carF = new Vec3[ns]; var carM = new Vec3[ns];
        foreach (var cj in carried)
        {
            var rho = cj.Point - c;
            var aj = sliceAccel is null ? a + Vec3.Cross(alpha, rho) : InterpSlice(sliceAccel, cj.Point[Axis]);
            var fi = (aj + Vec3.Cross(w, Vec3.Cross(w, rho))) * -cj.Mass;
            int k = SliceAt(cj.Point[Axis]);
            carF[k] += fi; carM[k] += Vec3.Cross(cj.Point, fi);
            fRes += fi;
        }
        for (int k = 0; k < ns; k++)
        {
            // 동해석 가속도: 하중 보간 차이로 남는 합력 잔차를 질량 비례로 상쇄(관성 완화 경로는 0)
            if (sliceAccel is not null) inertF[k] -= fRes * (SliceMass[k] / TotalMass);
            resid += Vec3.Cross(SliceCenter[k] - c, inertF[k]) + carM[k] - Vec3.Cross(c, carF[k]);
            inertF[k] += carF[k];
        }
        r.RigidAccel = a;
        r.RigidAlpha = alpha;
        r.MassCenter = c;
        // 잔차 모멘트(외력 모멘트 + 관성 모멘트)를 질량 비례 우력으로 상쇄 → 자유단 M = 0
        var mCorr = (mc + resid) * -1;

        // ---- 단면 내력 누적(왼쪽 부분 하중 합), 슬라이스 중앙에서 평가 ----
        var s1 = Vec3.Zero; var s2 = Vec3.Zero;
        var nAt = new double[ns]; var mAt = new Vec3[ns]; var vAt = new double[ns]; var tAt = new double[ns];
        for (int k = 0; k < ns; k++)
        {
            var fk = _sliceLoad[k] + inertF[k];
            // inertF 에 탑재물 힘이 합쳐져 있으므로 모멘트는 슬라이스 중심 대신 탑재물 실제 위치 기준으로 바로잡는다
            var mk = _sliceMoment[k] + Vec3.Cross(SliceCenter[k], inertF[k] - carF[k]) + carM[k] + mCorr * (SliceMass[k] / TotalMass);
            var sec = AxisPoint(X0 + (k + 0.5) * dx, SecCu[k], SecCv[k]);
            // 경계 k (슬라이스 k 왼쪽 끝)
            var cb = AxisPoint(X0 + k * dx, SecCu[k], SecCv[k]);
            var mb = (s2 - Vec3.Cross(cb, s1)) * -1;
            var fb = s1 * -1;
            r.AxialForce[k] = fb[Axis];
            r.ShearForce[k] = Math.Sqrt(fb[_u] * fb[_u] + fb[_v] * fb[_v]);
            r.BendingMoment[k] = Math.Sqrt(mb[_u] * mb[_u] + mb[_v] * mb[_v]);
            // 중앙: 절반 하중 포함
            var h1 = s1 + fk * 0.5; var h2 = s2 + mk * 0.5;
            var fint = h1 * -1;
            var mint = (h2 - Vec3.Cross(sec, h1)) * -1;
            nAt[k] = fint[Axis];
            vAt[k] = Math.Sqrt(fint[_u] * fint[_u] + fint[_v] * fint[_v]);
            mAt[k] = mint;
            tAt[k] = mint[Axis];
            s1 += fk; s2 += mk;
        }
        var cEnd = AxisPoint(X1, SecCu[ns - 1], SecCv[ns - 1]);
        var mEnd = s2 - Vec3.Cross(cEnd, s1);
        r.AxialForce[ns] = -s1[Axis];
        r.ShearForce[ns] = Math.Sqrt(s1[_u] * s1[_u] + s1[_v] * s1[_v]);
        r.BendingMoment[ns] = Math.Sqrt(mEnd[_u] * mEnd[_u] + mEnd[_v] * mEnd[_v]);
        r.ResidualForce = s1.Norm;
        r.ResidualMoment = mEnd.Norm;

        // ---- 면별 응력 ----
        var mat = Def?.Material;
        double rate = mat?.RateFactor(inp.StrainRate) ?? 1;
        r.RateFactor = rate;
        double sy = (mat?.Yield ?? 0) * rate, su = (mat?.Ultimate ?? 0) * rate;
        for (int i = 0; i < n; i++)
        {
            int k = FaceSlice[i];
            double du = Centroid[i][_u] - SecCu[k], dv = Centroid[i][_v] - SecCv[k];
            du = Math.Clamp(du, -SecExtU[k], SecExtU[k]); dv = Math.Clamp(dv, -SecExtV[k], SecExtV[k]);
            var m = mAt[k];
            double sig = nAt[k] / SecArea[k] + m[_u] * dv / SecIu[k] - m[_v] * du / SecIv[k];
            double rr = Math.Sqrt(du * du + dv * dv);
            double j = SecIu[k] + SecIv[k];
            double tau = ShearFactor * vAt[k] / SecArea[k] + Math.Abs(tAt[k]) * rr / Math.Max(j, 1e-14);
            double vmBeam = Math.Sqrt(sig * sig + 3 * tau * tau);
            double local = 0, sy2 = 0, sxLoc = 0;
            byte kind = PanelKind[i];
            // 면 위치의 비중력 가속도(가속도계가 재는 값): 관성 완화 강체장 또는 보 동해석(슬라이스 중심 사이 선형 보간) + 원심
            var rho = Centroid[i] - c;
            var aLoc = (sliceAccel is null ? a + Vec3.Cross(alpha, rho) : InterpSlice(sliceAccel, Centroid[i][Axis])) + Vec3.Cross(w, Vec3.Cross(w, rho));
            r.AccelG[i] = aLoc.Norm / G0;
            if (r.AccelG[i] > r.MaxAccelG) r.MaxAccelG = r.AccelG[i];
            if (kind != 0)
            {
                // 순 법선 압력(안쪽 +): 공력 + 관성(σ_A·a_local·n) − 내압
                double pNet = r.Pressure[i] + ArealMass * Vec3.Dot(aLoc, Normal[i]) - InternalPressure;
                r.NetPressure[i] = pNet;
                if (kind == 1)
                {
                    local = Math.Abs(PanelFactor[i] * pNet);
                    sy2 = local; // ± 표면 중 나쁜 쪽(아래 von Mises 에서 |σx·σy| 로)
                    double wt = Math.Abs(PanelDeflection[i] * pNet);
                    if (wt > r.MaxPanelDeflectionRatio) r.MaxPanelDeflectionRatio = wt;
                }
                else
                {
                    local = -PanelFactor[i] * pNet; // 바깥 압력 → 후프 압축
                    sy2 = local;
                    sxLoc = -_axNet[i] * pNet + _axInt[i] * InternalPressure;
                }
            }
            sig += sxLoc;
            r.Axial[i] = sig; r.Shear[i] = tau; r.Local[i] = local;
            if (kind == 1)
            {
                r.VonMises[i] = Math.Sqrt(sig * sig + Math.Abs(sig * sy2) + sy2 * sy2 + 3 * tau * tau);
                r.Principal[i] = (sig + sy2) / 2 + Math.Sqrt((sig - sy2) * (sig - sy2) / 4 + tau * tau);
            }
            else
            {
                r.VonMises[i] = Math.Sqrt(sig * sig - sig * sy2 + sy2 * sy2 + 3 * tau * tau);
                r.Principal[i] = (sig + sy2) / 2 + Math.Sqrt((sig - sy2) * (sig - sy2) / 4 + tau * tau);
            }
            double vmLocal = kind == 0 ? 0 : Math.Sqrt(sxLoc * sxLoc - sxLoc * sy2 + sy2 * sy2);
            r.Component[i] = r.VonMises[i] <= 0 ? StressComponent.None : vmLocal > vmBeam ? (kind == 1 ? StressComponent.Panel : StressComponent.Membrane) : StressComponent.Beam;
        }

        // ---- 연결점 국부 핫스팟 ----
        double ra = Def?.AttachRadius ?? 0.01;
        double rh = Math.Max(3 * ra, 0.02);
        foreach (var pl in inp.PointLoads)
        {
            double fm = pl.Force.Norm;
            if (fm <= 0) continue;
            double sh = fm / (Math.PI * ra * ra);
            r.Hotspots.Add((pl.Point, sh, pl.Source));
            for (int i = 0; i < n; i++)
            {
                double d2 = (Centroid[i] - pl.Point).NormSq;
                if (d2 > 9 * rh * rh) continue;
                double loc = sh * Math.Exp(-d2 / (rh * rh));
                if (loc > r.VonMises[i]) { r.VonMises[i] = loc; r.Principal[i] = Math.Max(r.Principal[i], loc); r.Component[i] = StressComponent.Hotspot; }
            }
        }

        // ---- 재질 판정 ----
        for (int i = 0; i < n; i++)
        {
            double vm = r.VonMises[i];
            if (vm > r.MaxVonMises) { r.MaxVonMises = vm; r.MaxFace = i; }
            FailureState st;
            double sf;
            if (mat is null) { st = FailureState.Unknown; sf = double.NaN; }
            else
            {
                if (mat.Ply is { } ply && (Def?.Layup ?? mat.Layup) is { Length: > 0 } lay)
                {
                    double fi = TsaiWuMax(ply, lay, r.Axial[i], r.Shear[i], rate);
                    sf = fi > 1e-12 ? 1 / Math.Sqrt(fi) : double.PositiveInfinity;
                    st = fi >= 1 ? FailureState.Fracture : sf < CautionSafety ? FailureState.Caution : FailureState.Safe;
                }
                else if (mat.Brittle)
                {
                    double p1 = r.Principal[i];
                    sf = p1 > 1e-9 ? su / p1 : double.PositiveInfinity;
                    st = p1 >= su ? FailureState.Fracture : sf < CautionSafety ? FailureState.Caution : FailureState.Safe;
                }
                else
                {
                    sf = vm > 1e-9 ? sy / vm : double.PositiveInfinity;
                    st = vm >= su ? FailureState.Fracture : vm >= sy ? FailureState.Yield : sf < CautionSafety ? FailureState.Caution : FailureState.Safe;
                }
                if (st < FailureState.Buckling && -r.Axial[i] >= BucklingStress[FaceSlice[i]]) st = FailureState.Buckling;
            }
            r.Safety[i] = sf;
            r.State[i] = st;
            if (sf < r.MinSafety) r.MinSafety = sf;
            if (st > r.Worst) r.Worst = st;
        }
        if (n == 0) { r.MaxPressure = 0; r.MinPressure = 0; }
        if (r.MaxFace >= 0) r.DominantAtMax = r.Component[r.MaxFace];
        return r;
    }

    /// <summary>
    /// Tsai-Wu 파손 지수 최대값(FR-MAT-06). 고전 적층 이론(막 상태, 같은 두께 ply): 적층 평균 응력 (σx, 0, τxy) →
    /// 평균 강성 Ā = mean(Q̄(θ)) 로 변형률 ε = Ā⁻¹σ → ply 응력 Q̄(θ)ε → 재료축 (σ1, σ2, τ12).
    /// FI = F1σ1 + F2σ2 + F11σ1² + F22σ2² + F66τ12² + 2F12σ1σ2, F12 = −½√(F11F22). 강도는 속도 배율 rate 적용.
    /// </summary>
    public static double TsaiWuMax(PlyProps p, double[] layup, double sx, double txy, double rate = 1)
    {
        double xt = p.Xt * rate, xc = p.Xc * rate, yt = p.Yt * rate, yc = p.Yc * rate, s = p.S * rate;
        double f1 = 1 / xt - 1 / xc, f2 = 1 / yt - 1 / yc, f11 = 1 / (xt * xc), f22 = 1 / (yt * yc), f66 = 1 / (s * s);
        double f12 = -0.5 * Math.Sqrt(f11 * f22);
        double nu21 = p.Nu12 * p.E2 / p.E1, den = 1 - p.Nu12 * nu21;
        double q11 = p.E1 / den, q22 = p.E2 / den, q12 = p.Nu12 * p.E2 / den, q66 = p.G12;
        Mat3 QBar(double deg)
        {
            double th = deg * Math.PI / 180, c = Math.Cos(th), sn = Math.Sin(th);
            double c2 = c * c, s2 = sn * sn, c4 = c2 * c2, s4 = s2 * s2, cs = c * sn;
            double b11 = q11 * c4 + 2 * (q12 + 2 * q66) * s2 * c2 + q22 * s4;
            double b22 = q11 * s4 + 2 * (q12 + 2 * q66) * s2 * c2 + q22 * c4;
            double b12 = (q11 + q22 - 4 * q66) * s2 * c2 + q12 * (s4 + c4);
            double b16 = (q11 - q12 - 2 * q66) * c2 * cs + (q12 - q22 + 2 * q66) * s2 * cs;
            double b26 = (q11 - q12 - 2 * q66) * s2 * cs + (q12 - q22 + 2 * q66) * c2 * cs;
            double b66 = (q11 + q22 - 2 * q12 - 2 * q66) * s2 * c2 + q66 * (s4 + c4);
            return new Mat3(b11, b12, b16, b12, b22, b26, b16, b26, b66);
        }
        var a = Mat3.Zero;
        foreach (var deg in layup) a = a + QBar(deg) * (1.0 / layup.Length);
        var eps = a.Inverse() * new Vec3(sx, 0, txy); // (εx, εy, γxy)
        double worst = 0;
        foreach (var deg in layup)
        {
            var sg = QBar(deg) * eps; // 전역 ply 응력 (σx, σy, τxy)
            double th = deg * Math.PI / 180, c = Math.Cos(th), sn = Math.Sin(th);
            double s1 = c * c * sg.X + sn * sn * sg.Y + 2 * c * sn * sg.Z;
            double s2 = sn * sn * sg.X + c * c * sg.Y - 2 * c * sn * sg.Z;
            double t12 = -c * sn * sg.X + c * sn * sg.Y + (c * c - sn * sn) * sg.Z;
            double fi = f1 * s1 + f2 * s2 + f11 * s1 * s1 + f22 * s2 * s2 + f66 * t12 * t12 + 2 * f12 * s1 * s2;
            worst = Math.Max(worst, fi);
        }
        return worst;
    }

    /// <summary>
    /// 기록 프레임에서 이 물체의 응력 입력 구성: 대기상대속도(동체), 밀도·마하, 각속도, 연결점 하중(라이저·테더·힌지·추력·펄스).
    /// </summary>
    public StressInput InputFrom(World w, Frame f, double strainRate = 0)
    {
        var s = f.Bodies[Body.Index];
        var vAirBody = s.Q.InverseRotate(s.AirVelocity);
        var atm = w.Env.Atmosphere.At(-s.Cg.Z);
        double mach = atm.SpeedOfSound > 0 ? s.AirVelocity.Norm / atm.SpeedOfSound : 0;
        var loads = new List<PointLoad>();
        int di = 0;
        foreach (var ob in w.Bodies)
            foreach (var d in ob.Devices)
            {
                if (di >= f.Devices.Length) break;
                var ds = f.Devices[di++];
                if (ob != Body || !ds.Visible || ds.Force <= 0) continue;
                Vec3 dirW; Vec3 pW;
                switch (d)
                {
                    case Parachute:
                        dirW = (ds.Tip - ds.Anchor).Normalized(); pW = ds.Anchor; break;
                    case Multirotor:
                        dirW = s.Q.Rotate(new Vec3(0, 0, -1)); pW = ds.Anchor; break;
                    default:
                        dirW = (ds.Tip - ds.Anchor).Normalized(); pW = ds.Anchor; break;
                }
                if (dirW.NormSq < 1e-12) continue;
                loads.Add(new PointLoad(ToBrf(s, pW), s.Q.InverseRotate(dirW * ds.Force), d.FullName));
            }
        for (int ci = 0; ci < w.Connectors.Count && ci < f.Connectors.Length; ci++)
        {
            var cs = f.Connectors[ci];
            if (!cs.Visible || cs.Tension <= 0) continue;
            // 힌지: 기록된 반력 벡터(B 쪽, NED) — 펜널티 간극 방향(B−A)은 µm 수준 잡음이라 쓰지 않는다(없으면 = 불러온 결과의 근사)
            if (w.Connectors[ci] is Hinge hv && cs.ForceB.NormSq > 0)
            {
                if (hv.B == Body) loads.Add(new PointLoad(ToBrf(s, cs.B), s.Q.InverseRotate(cs.ForceB), hv.Name));
                else if (hv.A == Body) loads.Add(new PointLoad(ToBrf(s, cs.A), s.Q.InverseRotate(cs.ForceB * -1), hv.Name));
                continue;
            }
            var dir = cs.B - cs.A;
            if (dir.NormSq < 1e-18) continue;
            dir = dir.Normalized();
            switch (w.Connectors[ci])
            {
                case Tether t when t.A == Body:
                    loads.Add(new PointLoad(ToBrf(s, cs.A), s.Q.InverseRotate(dir * cs.Tension), t.Name)); break;
                case Tether t when t.B == Body:
                    loads.Add(new PointLoad(ToBrf(s, cs.B), s.Q.InverseRotate(dir * -cs.Tension), t.Name)); break;
                case Hinge h when h.B == Body:
                    loads.Add(new PointLoad(ToBrf(s, cs.B), s.Q.InverseRotate(dir * -cs.Tension), h.Name)); break;
                case Hinge h when h.A == Body:
                    loads.Add(new PointLoad(ToBrf(s, cs.A), s.Q.InverseRotate(dir * cs.Tension), h.Name)); break;
            }
        }
        return new StressInput(vAirBody, atm.Density, mach, s.Omega, loads, strainRate, CarriedAt(w, f));
    }

    /// <summary>프레임 f 에서 이 물체에 실린(Stowed 사슬이 이 물체에서 끝나는) 자손의 질량·CG(동체 좌표).</summary>
    public List<CarriedMass> CarriedAt(World w, Frame f)
    {
        var s = f.Bodies[Body.Index];
        var list = new List<CarriedMass>();
        foreach (var ob in w.Bodies)
        {
            if (ob == Body || ob.Index >= f.Bodies.Length || f.Bodies[ob.Index].Mode != BodyMode.Stowed || ob.OwnMass <= 0) continue;
            // 실행 뒤 분리된 물체는 Parent = null → 분리 전 부모(FormerParent)로 그 프레임의 사슬을 되짚는다
            var p = ob.Parent ?? ob.FormerParent;
            while (p is not null && p != Body && f.Bodies[p.Index].Mode == BodyMode.Stowed) p = p.Parent ?? p.FormerParent;
            if (p != Body) continue;
            list.Add(new CarriedMass(ob.Name, ob.OwnMass, ToBrf(s, f.Bodies[ob.Index].Cg)));
        }
        return list;
    }

    private static Vec3 ToBrf(BodySample s, Vec3 pw) => s.Q.InverseRotate(pw - s.Origin);
}
