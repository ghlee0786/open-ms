namespace Sim.App.Rendering;

/// <summary>3D 뷰 표시 레이어(보기 메뉴의 체크 항목).</summary>
public enum ViewLayer
{
    Grid, Compass, Wind,
    Bodies, Carrier, Stowed, Selection,
    Trajectories, Parachutes, Connectors, Impacts,
    BodyAxes, Velocity,
    McPoints, McRings,
    Hud,
    Legend, Hotspots,
    Rotors,
    CfdStreamlines,
}

/// <summary>레이어 표시 상태. 기본값 = 전부 표시(CFD 유선 겹쳐 보기만 처음엔 끔).</summary>
public sealed class ViewLayers
{
    /// <summary>메뉴 순서·문구. Group 이 바뀌는 곳에 구분선.</summary>
    public static readonly (ViewLayer Layer, string Label, string Group)[] All =
    [
        (ViewLayer.Grid, "지면 격자", "env"),
        (ViewLayer.Compass, "방위 (북쪽 화살표)", "env"),
        (ViewLayer.Wind, "바람 벡터 (지상 10 m)", "env"),
        (ViewLayer.Bodies, "물체 3D 모델", "body"),
        (ViewLayer.Carrier, "운반체", "body"),
        (ViewLayer.Stowed, "수납 상태 물체", "body"),
        (ViewLayer.Selection, "선택 물체 강조", "body"),
        (ViewLayer.Trajectories, "궤적", "dyn"),
        (ViewLayer.Parachutes, "낙하산 · 라이저", "dyn"),
        (ViewLayer.Rotors, "드론 암 전개 · 프롭 회전", "dyn"),
        (ViewLayer.Connectors, "테더 · 레일 연결", "dyn"),
        (ViewLayer.Impacts, "착지 지점 표식", "dyn"),
        (ViewLayer.BodyAxes, "선택 물체 동체축 (x 빨강 · y 초록 · z 파랑)", "sel"),
        (ViewLayer.Velocity, "선택 물체 속도 벡터 (0.5 s)", "sel"),
        (ViewLayer.CfdStreamlines, "선택 물체 CFD 유선 (가장 가까운 받음각 · aero.flowField)", "sel"),
        (ViewLayer.McPoints, "MC 착탄점", "mc"),
        (ViewLayer.McRings, "MC CEP 원 · 95% 타원", "mc"),
        (ViewLayer.Hud, "화면 정보 (HUD)", "hud"),
        (ViewLayer.Legend, "색상 레전드 (응력·압력 막대)", "hud"),
        (ViewLayer.Hotspots, "연결점 하중 핫스팟 표식", "hud"),
    ];

    /// <summary>처음엔 CFD 유선 겹쳐 보기만 끔(물체 둘레가 선으로 덮이지 않게 — 보기 메뉴에서 켬). '기본 — 모두 표시'는 전부 켬.</summary>
    private readonly bool[] _on = Enumerable.Range(0, All.Length).Select(i => (ViewLayer)i != ViewLayer.CfdStreamlines).ToArray();

    public event Action? Changed;

    public bool this[ViewLayer l]
    {
        get => _on[(int)l];
        set
        {
            if (_on[(int)l] == value) return;
            _on[(int)l] = value;
            Changed?.Invoke();
        }
    }

    public void ResetDefaults()
    {
        Array.Fill(_on, true);
        Changed?.Invoke();
    }
}
