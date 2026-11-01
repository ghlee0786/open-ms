# 시나리오 스키마 `mns.scenario/1`

모든 값은 SI. 위치는 NED(m, 원점 해발 0 m), 자세 각도는 deg(3-2-1: roll, pitch, yaw).
**점 좌표**(`mount`, `cg`, `cp`, `attach`, `pointA` …)는 동체 기준좌표(FRD, 원점 = 모델 원점, m).
좌표 변환 표: `config/frames.json`.

```jsonc
{
  "schema": "mns.scenario/1",
  "name": "…", "description": "…",
  "sim": { "dt": 0.001, "tEnd": 600, "outputRate": 50, "seed": 1, "eventTolerance": 1e-4 },
  "environment": {
    "groundAltitude": 0, "temperatureOffset": 0, "gravity": 9.80665,
    "vacuum": false, "constantDensity": null,              // 검증용
    "wind": { "speed": 5, "directionFrom": 270, "refHeight": 10, "shear": "log|power|none",
              "roughness": 0.03, "powerExponent": 0.142857, "shearTop": 300,
              "profile": [[altAgl, speed, directionFrom], …] },   // 있으면 표 보간 우선
    "turbulence": "none|light|moderate|severe",
    "soil": "sand|farmland|clay|frozen|rock",
    "geopotentialAltitude": false,   // true: US1976 표를 기하고도→지오퍼텐셜 변환으로 조회(기본 false = 기존 근사)
    "earth": { "gravity": "constant|inverse-square|wgs84-j2", "latitude": 0, "rotation": false, "radius": 6378137 }
                                     // FR-DYN-05 선택 지구 모델(평면 NED 유지): 고도별 중력, 자전(코리올리·원심·자세 지구 자전율)
  },
  "carrier": { "name": "carrier", "altitude": 1000, "speed": 139, "heading": 0, "pitch": 0,
               "position": [n,e,d], "velocity": [vn,ve,vd], "shape": {…} },
  "bodies": [ {
    "name": "box1",
    "parent": "container", "mount": [x,y,z], "mountAttitude": [r,p,y],   // 붙어 있는 물체
    "position": [n,e,d] | "altitude": 300, "velocity": […], "attitude": […], "omega": […], // 처음부터 자유물체
    "mass": 2.0, "cg": […], "inertia": [Ixx,Iyy,Izz] | [Ixx,Iyy,Izz,Ixy,Ixz,Iyz] | 3×3,
    "massProps": "path/x.massprops.json", "modelFrame": "fusion|body", "modelToBody": 3×3, "density": 1200,
    "noseAxis": "+X|-X|+Y|-Y|+Z|-Z",   // 모델 좌표에서 기수(스파이크) 방향 → 동체 +X (FX-01). fusion 기본 = +Z
    "penetrator": { "diameter": 0.016 | "area": .., "nose": "flat|cone|ogive", "lOverD": 1.9 | "noseLength": .., "n": .. },
                                        // Young 관입체(FX-02). 없으면 형상 기수 끝 단면·노즈 길이로 추정
    "shape": { "type": "box", "size": [..], "center": [..] }
           | { "type": "cylinder", "radius": r, "length": l, "axis": "x|y|z", "center": [..] }
           | { "type": "sphere", "radius": r }
           | { "type": "mesh", "file": "x.stl", "unit": "auto|mm|cm|m" }
           | { "type": "shellSegment", "radius": 0.2, "thickness": 0.003, "length": 1.7, "span": 120,
               "centerAngle": 0, "axis": "x", "center": [..] },   // 원통 껍질 부채꼴(운반 탄체 외피 조각, FR-SHL-01) — 각도: 축 x 면 +Y(0°)→+Z(90°)
    "structure": { "material": "al6061-t6" | "알루미늄 6061" | Fusion 재질명, "thickness": 0.002,
                   "section": "auto|thinWall|solid", "attachRadius": 0.01, "layup": [0,45,-45,90], "slices": 40,
                   "edge": "simple|clamped", "internalPressure_kPa": 0,
                   "override": { "yield_MPa": .., "ultimate_MPa": .., "E_GPa": .. } },
                                        // 근사 응력·재질 판정(PRD W/X). 없으면 massProps 의 Fusion 재질이 라이브러리에 있을 때만 자동
                                        // thinWall: 보 굽힘 + 국부 항(FX-09) — 상자 벽 Roark 판 굽힘(edge 가장자리 조건),
                                        // 원통·외피 조각 후프 막응력, 구 막응력. internalPressure_kPa = 내압(게이지)
    "aero": "shape" | "none" | { "cd": .., "area": .., "cdTable": [[alphaT_deg, cd], …],
                                 "cp": [..] | "auto" | "component" | "none", "refLength": .., "damping": [Clp, Cmq, Cnr],
                                 "dampingLengths": [b, c̄, b], "scale": 1,
                                 "machCorrection": false,   // 둔체 Cd 압축성 배율(M 0.3~0.7, FR-SHL-05)
                                 "fins": { "count": 4, "rootChord": .., "tipChord": .., "span": .., "sweep": .., "rootLeFromNose": .., "bodyRadius": .. },
                                 "refDiameter": .. },
                                 // cp: 메시 형상은 미지정 시 "auto"(측면 투영 면적 중심, FX-03), 기본 형상은 미지정 시 CG 작용
                                 // "component": Barrowman 성분 합성 CP + 법선력 CNα(FX-07). fins 없으면 메시에서 자동 추출
    "noseCoefficient": 0.8,            // Young N 직접 지정(구 키, penetrator.n 과 같음)
    "color": "#4f8fd8",
    "configs": { "deployed": { "mass": .., "cg": [..], "inertia": [..] } },
    "devices": [
      { "type": "parachute", "name": "chute", "d0": 1.22, "cd0": 2.2, "attach": [..], "riser": 2.5,
        "fillConstant": 8, "fillExponent": 2, "lineStretchTime": 0.35, "initialFill": 0.02, "cdsScale": 1,
        "apparentMassK0": 0,          // 겉보기 질량 유입 항(Cruz 2003 식 6), DGB 풍동 보정값 1.26(n = 8.2, j = 2 와 함께)
        "reefing": { "ratio": 0.15, "disreefDelay": 2.0 } },   // 1단 리핑(선택): 리핑 CdS/완전 CdS, 라인 신장 → 디스리프 [s]
                                      // 1단계 t_f1 = n·D₀·√r/V, 2단계 t_f2 = n·D₀·(1−√r)/V_d, 이벤트 '<body.device>.disreef'
      { "type": "multirotor", "name": "rotor", "maxThrust": 85, "tau": 0.15, "maxTiltDeg": 35,
        "cruiseSpeed": 12, "landSpeed": 2.5, "idleRatio": 0.3, "vrsDepth": 0,
        "vrsModel": "gaussian|uiuc-v2|none", "propSolidity": 0.09, "propCt0": 0.1,  // uiuc-v2: RotorModel(FR-UAV-02 v2)
        "armsConfig": "deployed", "armDeployTime": 0.5, "arms": 4, "armLength": 0, "propDiameter": 0 },
                                        // 접힘 암·프롭 3D 표시(FR-SEP-04): armsConfig 구성으로 switchConfig 되면 armDeployTime 동안 암이 펴진다
                                        // (미지정 시 소유 물체 configs 에 "deployed" 가 있으면 그것, 없으면 처음부터 펴짐).
                                        // armLength 0 = 형상 높이 기준 자동, propDiameter 0 = rotorDiskArea 에서 역산. 기록: devices.csv size = 암 전개율
                                        // 고정 팔 기체: "props": [{ "pos": [x,y,z], "axis": [0,0,-1], "spin": 1 }, ..]
                                        //   = 프롭 허브 위치(동체 좌표 m — 실모델 허브 CG 를 modelToBody 로 변환)·회전축(추력 방향, 기본 동체 −Z)·회전 방향 ±1(기본 교대).
                                        //   props 를 주면 암 개수 = props 수, 오버레이 암(막대·모터) 끔(armsOverlay 기본 false), configs.deployed 자동 감지 안 함
                                        //   (명시 armsConfig 만) — 모터가 돌 때만 허브에 2엽 블레이드·회전 원판을 그린다(정지 중엔 형상의 접힌 프롭 그대로).
                                        //   "armsOverlay": false 만 주면(props 없이) 자동 배치 허브에 프롭만 그린다.
      { "type": "pulse", "name": "shock", "peakG": 15, "duration": 0.01, "direction": [0,0,1], "shape": "halfsine|square" }
                                        // 충격 펄스(절단 폭약·사출 충격 전달, FR-SHL-03) — 액션 fire/deploy 로 시작
    ]
  } ],
  "connectors": [
    { "type": "tether", "name": "t1", "a": "box1", "pointA": [..], "b": "drone1", "pointB": [..],
      "length": 1.5, "ea": 1.5e4, "damping": 60, "breakLoad": 4020, "active": true,
      "absorber": { "force": 150, "rise": 0.1, "stroke": 0.8 } },   // 정하중형 에너지 흡수기(선택, 라인과 직렬·소성): 0→rise 선형 후 force 일정,
                                      // 행정 stroke 소진 뒤 라인 강성(이벤트 '<name>.absorberBottomed'), 요약 absorberUsed_m·absorberEnergy_J
    { "type": "rail", "name": "rail1", "parent": "box1", "child": "shell1", "axis": [0,0,1],
      "length": 0.34, "friction": 0.1, "exitPush": 0 },
    { "type": "hinge", "name": "hinge1", "a": "tail", "b": "petal1", "pointA": [..], "axisA": [0,0,1],
      "stiffness": 1e6, "rotStiffness": 5e4, "damping": -1, "rotDamping": -1, "releaseAngle": 40, "breakLoad": 60000,
      "stabilityDt": 0.001 }
                                        // 펜널티 힌지(FR-SHL-02): B 쪽 결속점·축은 분리 순간 자동 고정, 강성은 안정 한계로 자동 제한 —
                                        // 상한 기준 Δt = max(sim.dt, stabilityDt)(기본 1 ms): Δt 를 줄여도 강성이 같아 반력이 Δt 수렴(0 = 현재 dt, 수정 전 방식)
    { "type": "swing", "name": "door_L_swing", "parent": "container", "child": "door_L", "point": [..], "axis": [0,0,-1],
      "angle": 90, "duration": 0.3 }
                                        // 붙은 채 열리는 도어: activate 시 수납 자식을 부모 동체 좌표 힌지 점·축 둘레로 θ_max·(1−cos πs)/2 회전,
                                        // 질량은 부모에 남고 합성 질량특성 재계산. 끝나면 '<name>.open'
  ],
  "collisions": { "restitution": 0.3, "friction": 0.3, "bodies": [..], "parachutes": true, "until": 40, "slop": 0.002 },
                                        // 충돌 판정·충격량 접촉 + 낙하산 캐노피 간섭 기록(FR-SHL-04). 없으면 미사용
  "events": [ { "name": "eject1", "when": { … }, "do": [ { … } ] } ],
  "metrics": [ { "type": "minSeparation", "bodies": [..], "ignoreAfterSeparation": 1.0 },
               { "type": "landingSeparation", "bodies": [..], "target": 70 },
               { "type": "maxLoad", "bodies": [..], "limitG": 40 },          // 매 스텝 추적 최대 G(짧은 펄스 포함)
               { "type": "debrisNearDeploy", "devices": ["c1.drogue"], "debris": [..], "window": 3, "riskDistance": 5 } ],
  "dispersions": [ { "path": "environment.wind.speed", "dist": "normal|uniform|truncnormal|choice",
                     "mode": "add|scale|set", "mean": .., "sd": .., "min": .., "max": .., "values": [..] } ],
  "monteCarlo": { "runs": 200, "impactBody": "sensor1", "sampling": "lhs|random", "seed": 11 },
  "gpuMonteCarlo": { "runs": 100000, "seed": 7, "sampling": "lhs",
                     "model": { "mass": .., "cdA": .., "dt": 0.005, "tMax": 300,
                                "chute": { "d0": .., "cd0": .., "delay": 1.0, "lineStretchTime": .., "fillConstant": 8, "fillExponent": 2 },
                                "release": { "altitude": .., "speed": .., "heading": .., "vd": .. } },
                     "dispersions": [ { "path": "model.chute.cd0", … }, { "path": "environment.wind.speed", … } ] }
}
```

## 기수축·관입체·압력중심 (PRD U절 FX-01~03)

- **기수축**: 동체 +X 가 기수다. Fusion 모델은 물체마다 기수 방향이 달라(예: 스파이크가 −Z 인 센서) `noseAxis` 로 지정한다.
  변환 = `AlignToX(modelToBody·nose) · modelToBody` — ±Y/±Z 는 90° 회전, −X 는 요 180°(위·아래 유지).
  UI 가져오기는 최장축 양 끝 중 단면이 작은 쪽을 제안한다(`NoseGeometry.SuggestNoseAxis`).
- **관입체**: Young 식의 A 는 날개 포함 투영면적이 아니라 스파이크(노즈) 몸통 단면이다. 미지정이면 형상의 +X 끝 10% 구간부터
  넓혀 가며 횡폭이 생기는 단면을 찾고, 노즈 길이/지름 ≥ 0.5 면 원뿔 N=0.25·L/d+0.56 으로 본다. 충돌 순간 총받음각 > 30° 면
  기수가 선행하지 않으므로 충돌 방향 투영면적·N=0.56 을 쓴다. `summary.json` 의 `impact.youngAreaSource` 로 확인.
- **압력중심**: 메시 형상은 동체 Y·Z 방향 측면 투영 면적 중심을 CP 기본값으로 쓰고, 요약에 정적 여유
  `staticMargin = (x_cg − x_cp)/L`(+ = 기수 선행 안정)을 표시한다.
- **성분 합성(FX-07)**: `aero.cp: "component"` 는 메시 단면(36 구역 × 240 단면)에서 몸체 반경과 핀을 분리해
  세장체 몸체 CNα(급수축 > 30° 는 박리 베이스로 제외) + Barrowman 핀 CNα·CP 를 합성하고, 법선력 q·A·CNα·sinα·|cosα| 를 CP 에 더한다.
  `mns validate <시나리오>` 가 성분별 CNα·위치·핀 치수·(x_cg−x_cp)/d 를 출력한다. 측면 투영 중심은 핀의 큰 CNα 를 반영하지 못해
  실모델 센서에서 −4.7 %(꼬리 선행)로 나왔고, 성분 합성은 +3.5 %(0.13 구경, 스파이크 선행)이다.

## Trigger (`when`)

| type | 필드 | 의미 |
|---|---|---|
| `time` | `t` | 절대 시각 |
| `after` | `event`, `delay` | 다른 이벤트(또는 내장 이벤트) 후 지연 |
| `altitude` | `body`, `below`/`above`, `msl` | AGL(기본) 고도 조건 — 이분 탐색 |
| `speed` | `body`, `below`/`above`, `airspeed` | 대기/지면 속도 조건 |
| `distance` | `body`, `point` 또는 `to`, `below`/`above`, `horizontal` | 거리 조건 |
| `arrived` | `device`(multirotor), `radius` | 목표 수평 반경 도착 |
| `accel` | `body`, `above`(G) | 비중력 가속도(충격 감지) |
| `tension` | `connector`, `above`(N) | 테더 장력 |
| `and` / `or` | `all` / `any` | 논리 조합 |

내장 이벤트(`after` 에서 참조 가능): `<body>.separated`, `<body>.landed`, `<rail>.exit`, `<tether>.released`, `<tether>.broken`.

## Action (`do`)

| type | 필드 |
|---|---|
| `separate` | `body`, `relVel`(부모 동체계), `relOmega` — 상태 상속 + 상대속도, 반작용·운동량 보존 |
| `deploy` / `release` | `device` (`body.device`) 또는 `connector` |
| `activate` / `deactivate` | `device` 또는 `connector` |
| `set` | `device` + 파라미터(multirotor: `mode` off/stabilize/hold/goto/land, `target`, `targetRelative`, `targetAgl`, `cruiseSpeed`, `landSpeed`, `idleRatio`) |
| `startRail` | `connector` |
| `fire` | `device`(pulse) — 충격 펄스 시작 |
| `switchConfig` | `body`, `config` |
| `stop`, `log` | — |

내장 이벤트 추가: `<hinge>.released`(열림각·반력 이탈), `collision:<a>-<b>`(접촉 시작), `interference:<body>-<body.device>`(캐노피 간섭),
구조 분석 후 `<body>.yield` / `<body>.fracture` / `<body>.buckling`(최초 항복·파단·좌굴 위험, 근사).

## 운반 탄체 3분할 외피 분리 (PRD V절)

템플릿 `06_munition_3petal_split.json` · `07_munition_tail_cut_aft_exit.json` 은 `tools/scenario_gen/make_munition_split.py` 가 만든다(값은 모두 가정).

- 운반체 `munition` 은 형상 없는 기준 궤적(1 km, M 0.5 = ISA 168.2 m/s). 노즈·꼬리·외피 조각 3개·컨테이너 4개가 수납 상태로 시작.
- t = 0.5 s 기폭: 조각마다 `separate` 에 **후방 힌지 둘레 회전**(앞끝 방사 속도 12 m/s → ω = 12/L, CG 속도 6 m/s)과 기폭 시각 편차(0.3 ms)를 준다.
  균일 방사 Δv 만 주면 힌지가 순간적으로 그 운동을 막아야 해 반력이 비현실적으로 커진다(MN 급).
- 조각 공력 = 곡면 판 횡류 독립 원리: 받음각이 생기면 오목면이 바람을 받아 법선력이 조각을 더 벌린다 → 40° 에서 힌지 이탈.
- 컨테이너는 노출과 동시에 `pulse`(15 G·10 ms) 를 받고 지연 후 드로그 → 주낙하산. 지표: 컨테이너 최대 G(설계 50 G), 드로그 전개 주변 잔해,
  잔해–컨테이너 최소 이격, 착지 이격. `mns mc` 결과 JSON 의 `debrisInterferenceProbability` = 잔해 간섭 확률.

## 실모델 운용 시나리오 (PRD AA절)

실제 CAD 그룹 내보내기(Fusion `MnS_ExportGroups`, git 밖)를 물체마다 참조하는 운용 시나리오는 아래 요소로 쓴다.
설계별 시나리오와 그 설명은 설계 자료와 함께 저장소 밖(또는 비공개 문서)에 둔다.
`mns loads` · `mns tumble-study` · `mns scenario-report` 는 이런 시나리오(3분할 · 레일 · 테더 · 낙하산 2단)를 대상으로 하는 연구 명령이다.

- **실모델 참조**: `massProps: "real:<그룹>.massprops.json"` + `fallback{shape, mass, cg, inertia, aero…}`. 찾는 순서 `MNS_REAL_MODELS`(CLI·앱 `--real-models DIR`)
  → 루트 `realModels.dir`(시나리오 기준 상대) → 없으면 fallback(합성 대체, 경고) — 파일마다 먼저 있는 폴더. `realModels.mode`: `auto` | `synthetic`(강제 대체, `--synthetic`) | `required`(없으면 오류).
  `realModels.document`(선택): 그 CAD 문서에서 내보낸 폴더(`groups_summary.json` 의 `document` 가 같은 것)만 쓴다 — 다른 설계 폴더(예: 새 설계 폴더를 이전 설계 시나리오에)는 경고 후 건너뛴다.
  요약 `bodies.<b>.modelSource` = real | synthetic. 실모델 + `massCorrection{mass, cg, inertia}` 이면 질량특성만 그 값(massSource `fusion-corrected`).
- **조립 좌표 장착**: `mountFrame: "model"` + `assemblyOffset`(모델 좌표 m) — 같은 CAD 조립 좌표에서 내보낸 그룹을 그대로 붙인다(MountQ = P_부모·P_자식ᵀ).
- `aeroShape`: 공력 전용 기본 형상(표시·접촉·응력은 `shape`/메시).
- parachute `failure{mode: none|noDeploy|delay|partial|lineTwist, delay, fill, cdsLoss, probability}` — 고장 주입(FR-PAR-05, 시드별 재현).
- collisions `canopyRegion: "sphere+lines"`(캐노피 구 + 결속점→캐노피 라인 원뿔, 대표점 판정), `chuteAnomaly{delay, partialFill, lineDamagePerHit, minRelSpeed}` —
  라인 신장 중 진입 = 충만 지연(+라인 손상), 충만 중 = 부분 충만, 개방 후 = CdS 손상. 이벤트 `anomaly:<body.device>`, MC `chuteAnomalyProbability`.
- pulse `reaction: <body>` — 같은 힘을 반대로 모체에(사출기 반작용). switchConfig 구성 `inertiaScale`(현재 관성에 배율). `sim.eventBurst`(s): 이벤트 직후 매 스텝 기록.
- 지표: `tumbling`(from/to/attitudeAt/thresholdDeg), `pendulum`(axis/from/settle/to/events/responseWindow), `canopyClearance`(devices), `massBudget`(limit),
  `chuteAnomalies`, `phaseTimeline`(phases[{label,event,video}]), `ejection`(parent/after/ignore/window).

## 낙하산 개방 충격 · 브레이크 코드 · 박스 내부 하중 경로 (PRD AA절 FR-AN-08)

템플릿 `10_box_chute_opening_shock.json`(기준안) · `10b_box_chute_opening_shock_crush.json`(압괴 패드안)은 `tools/scenario_gen/make_box_opening_shock.py` 가 만든다.
보고서: `mns opening-shock <10> --variant <10b> [--runs N] [--ccx] [--no-sweep] [--real-models DIR]`
(앱 실행 파일에서도 창 없이: `MnS.Simulator --opening-shock-report <10> …`) → `validation/reports/opening_shock/README.md`.

- 연결 `breakCord` {a, pointA, b, pointB, length, breakLoad(N), breakElongation(0.15), dampingRatio(0.05)}: 정적 끈 + 약한 고리. 인장 전용, EA = breakLoad/breakElongation,
  파단 시 이벤트 `<name>.broken`(→ 낙하산 deploy 를 건다). 요약: snapped·tautTime·tautSpeed·breakTime·impulse.
- 장치 `cushionMount`(박스 소유) {payload(직계 자식), pads[{axis:"+x"…, material(폼), area, thickness, E_MPa?, plateau_MPa?, densification?}],
  pins{count, diameter+material | stiffness, length?, axes:"xyz", breakLoad?}, padDampingRatio, wallGap, wallStiffness, recordAfter,
  channels[{name, connector|device}], parts[], stages[{label, from, to?|duration, offset?, pad?}]}: 결합체 강체 6DOF 위에서 드론 상대운동 2질량 적분(적응 서브스텝).
  폼 = 탄성–평탄–치밀화 + 하역 이력, 감쇠는 이력 안쪽(하역·재부하)만. 이벤트 `<body>.<mount>.pinsBroken`.
- `parts[]` {name, label, kind: plate|lug|bolt|hook|cantilever|bar, material, 치수…, kt?, share?, load{channel:"chute+cord" | "mount:pin"} 또는 {inertiaMass, frame: payload|base, axis?}}:
  닫힌 식 등가응력(Roark 원판·러그·볼트 전단/인장·Winkler 곡선보·외팔보) → SF = 항복(취성: 인장강도) × 변형률 속도 배율 / σ.

## 하중·응력·재질 (PRD W·X) — 근사, FEA 아님

- 표면 압력: 수정 뉴턴 Cp = Cp₀(M)·cos²θ(바람받이), 옆·뒷면 기저압 −0.3. 응력: 형상 최장축(원통·조각은 그 축) 보 모델 + 관성 완화
  (하중 합력으로 강체 가속도 → 관성하중, 자유단 평형 잔차 ~1e-13), 연결점(라이저·테더·힌지·추력·펄스) 국부 핫스팟 σ = F/(π r_a²).
- 재질 라이브러리(`src/Sim.Core/Structures/materials.json`, 12종, 출처 표기): 연성 = von Mises ≥ 항복 → 영구 변형, ≥ 인장 → 파단,
  취성 = 최대 주응력 ≥ 인장 → 파단, 얇은 벽 압축 ≥ 좌굴 응력 → 찌그러짐 위험, SF < 1.5 → 주의. CFRP 는 고전 적층 이론 + Tsai-Wu.
  변형률 속도: 금속 Johnson-Cook 속도 항, 고분자 DIF(log10). `mns materials` 로 목록·출처.
- **착지 충돌 재질 판정**(1·2단계, `ImpactDamage`): 엔진은 착지를 순간 정지로 처리하므로 착지 기록(속도·자세·질량)으로 따로 판정한다.
  접촉 형태(상자 면/모서리/꼭짓점, 원통 끝면/테두리/옆면)별로
  - `thinWall`(structure.thickness 있음): 접촉부 벽 탄성 에너지 용량 U = σ²/(2E)·V/3 (V = 접촉부 벽 폭 × t × min(높이, 2H), H = Wierzbicki 접힘 반파장).
    법선 운동에너지 ≤ U → 손상 없음. 연성(금속·PC·나일론): 남는 에너지 / 평균 압궤력(사각관 Wierzbicki–Abramowicz 9.56σ0t^(5/3)b^(1/3) × 코너 요소 몫,
    원통 Alexander 6σ0t^1.5√D, 옆면 링 압궤 σ0t²L/R) = **찌그러짐 깊이**, 0.73·높이 초과 → 전면 압궤. 취성(ABS·PLA·CFRP): 넘으면 **깨짐**.
  - `solid`: 강체 지면 1D 파동 응력 ρcv(무른 흙이면 지면 한계 m·g·G/A 로 상한) → 연성 Taylor 영구 단축, 취성 σ ≥ σu → 깨짐.
  - 폼(EPP·EVA): 평탄 응력 × 접촉 면적 일정력 압축, 0.7·높이 넘으면 바닥 침.
  - 변형률 속도 배율(JC·DIF) 적용, 보정 전 정적 용량도 함께 보고. **손상 시작 속도**(이 재질·두께·질량에서 몇 m/s 부터 찌그러짐/깨짐)를 낸다.
  - 결과: 구조 분석(F6 / `mns run … --structural`)의 `impact` 항목, 이벤트 `<물체>.impactDamage`, 접촉부 면 색(부위 상태).
  - 예: 템플릿 `08_material_drop_compare.json`(같은 상자, 재질만 다르게 3 m 낙하).
- 3단계 OpenCourant(Radioss): 선택 물체 착지 충돌을 표면 셸(SH3N, 요소 = 표시 면) 해석 → 소성변형률 면 색, 삭제 요소 = 깨짐/찢어짐.
  내용물 질량은 지면 쪽 절반 셸 밀도에 싣는다. `mns fea <시나리오|r.mnsr> --body <이름>`, `mns fea-taylor`. 설치되어 있으면 테스트가 소형 케이스를 실제 실행
  (`MNS_SKIP_OPENCOURANT=1` 이면 생략).
