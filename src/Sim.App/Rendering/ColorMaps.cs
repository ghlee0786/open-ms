using Sim.Core.Structures;

namespace Sim.App.Rendering;

/// <summary>3D 면 색 표시 모드(PRD W FR-STR-04 · X FR-MAT-02 · FR-MAT-05).</summary>
public enum ColorMode { Off, Pressure, Stress, Safety, State, FeaPlastic, CcxVonMises, CcxSafety, CcxDisplacement, ShockAccel, ShockStress, CcxShock, CfdCp }

/// <summary>면 색 값의 출처(경고 문구·레전드 각주·값 공급 분기). Shock = 개방 충격 보 동해석(시간 이력), CcxShock = CalculiX 모달 과도 응답,
/// Cfd = OpenFOAM 표면 유동장(시나리오 aero.flowField).</summary>
public enum ColorSource { None, Approx, OpenCourant, Calculix, Shock, CcxShock, Cfd }

/// <summary>
/// 면 색 모드 한 가지의 표시 정보. 새 모드 추가 = <see cref="ColorMode"/> 값 1개 + <see cref="ColorModeInfo.All"/> 1행
/// + MainWindow.FaceValues 의 값 공급 case 1개(분할 화면 콤보·보기 메뉴·smoke --color/--modes 는 이 표에서 자동으로 생긴다).
/// </summary>
/// <param name="Key">smoke 인자 이름(--color · --modes).</param>
/// <param name="ZeroBased">자동 범위를 0 부터.</param>
/// <param name="Safety">안전계수형(값이 작을수록 위험 — 색 뒤집기 · 5 에서 자름 · 최대 ≥ 1.5).</param>
/// <param name="Categorical">범주색(부위 상태).</param>
public sealed record ColorModeInfo(ColorMode Mode, string Key, string Label, string Title, string Unit, ColorSource Source,
    bool ZeroBased = false, bool Safety = false, bool Categorical = false)
{
    public static readonly ColorModeInfo[] All =
    [
        new(ColorMode.Off, "off", "끔 (물체 색)", "물체 색", "", ColorSource.None),
        new(ColorMode.Pressure, "pressure", "표면 압력 [kPa]", "표면 압력", "kPa (대기압 기준, q·Cp)", ColorSource.Approx),
        new(ColorMode.Stress, "stress", "근사 응력 von Mises [MPa]", "근사 응력 von Mises", "MPa", ColorSource.Approx, ZeroBased: true),
        new(ColorMode.Safety, "safety", "안전계수 (재질)", "안전계수", "강도 / 응력 (속도 보정)", ColorSource.Approx, ZeroBased: true, Safety: true),
        new(ColorMode.State, "state", "부위 상태 (항복·파단·좌굴)", "부위 상태", "", ColorSource.Approx, Categorical: true),
        new(ColorMode.FeaPlastic, "fea", "FEA 소성변형률 (OpenCourant)", "FEA 유효 소성변형률", "— (OpenCourant)", ColorSource.OpenCourant, ZeroBased: true),
        new(ColorMode.CcxVonMises, "ccx", "FEA von Mises [MPa] (CalculiX)", "FEA von Mises", "MPa (CalculiX)", ColorSource.Calculix, ZeroBased: true),
        new(ColorMode.CcxSafety, "ccxsf", "FEA 안전계수 (CalculiX)", "FEA 안전계수", "항복(취성: 인장) / 응력 (CalculiX)", ColorSource.Calculix, ZeroBased: true, Safety: true),
        new(ColorMode.CcxDisplacement, "ccxdisp", "FEA 변위·모드 형상 [mm] (CalculiX)", "FEA 변위", "mm (CalculiX)", ColorSource.Calculix, ZeroBased: true),
        new(ColorMode.ShockAccel, "shockg", "충격 가속도 [G] (개방·스냅, 동해석)", "충격 가속도", "G (가속도계 값)", ColorSource.Shock, ZeroBased: true),
        new(ColorMode.ShockStress, "shock", "충격 응력 von Mises [MPa] (동해석)", "충격 응력 von Mises", "MPa", ColorSource.Shock, ZeroBased: true),
        new(ColorMode.CcxShock, "ccxshock", "FEA 충격 응력 [MPa] (CalculiX 과도)", "FEA 충격 응력 von Mises", "MPa (CalculiX 모달 과도)", ColorSource.CcxShock, ZeroBased: true),
        new(ColorMode.CfdCp, "cfdcp", "CFD 표면 압력 계수 Cp (OpenFOAM)", "CFD 표면 압력 계수 Cp", "(p − p∞)/q∞", ColorSource.Cfd),
    ];

    public static ColorModeInfo Of(ColorMode m) => Array.Find(All, x => x.Mode == m) ?? All[0];

    /// <summary>smoke 인자 이름 → 모드(모르는 이름 = 끔).</summary>
    public static ColorMode Parse(string key) => Array.Find(All, x => string.Equals(x.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))?.Mode ?? ColorMode.Off;
}

/// <summary>연속 컬러맵(FR-STR-03, Turbo·Viridis 표 보간 — 구현은 Sim.Core <see cref="ColorScale"/>) · 상태 범주색.</summary>
public static class ColorMaps
{
    public static readonly string[] Names = ColorScale.Names;

    public static (float R, float G, float B) Map(string name, double t) => ColorScale.Map(name, t);

    /// <summary>부위 상태 범주색(안전 초록 → 주의 노랑 → 좌굴 보라 → 항복 주황 → 파단 빨강, 미판정 회색).</summary>
    public static (float R, float G, float B) State(FailureState s) => s switch
    {
        FailureState.Safe => (0.30f, 0.78f, 0.45f),
        FailureState.Caution => (0.98f, 0.82f, 0.25f),
        FailureState.Buckling => (0.66f, 0.45f, 0.95f),
        FailureState.Yield => (1.00f, 0.55f, 0.15f),
        FailureState.Fracture => (0.92f, 0.20f, 0.22f),
        _ => (0.55f, 0.58f, 0.62f),
    };

    public static string StateLabel(FailureState s) => s switch
    {
        FailureState.Safe => "안전 (SF ≥ 1.5)",
        FailureState.Caution => "주의 (1 ≤ SF < 1.5)",
        FailureState.Buckling => "좌굴·찌그러짐 위험",
        FailureState.Yield => "항복 (영구 변형)",
        FailureState.Fracture => "파단",
        _ => "재질 미지정",
    };
}
