# 공개 데이터 Validation 사례 형식 `mns.validation-case/1` (PRD O-4, VV-49~51)

사례 하나 = 폴더 하나: `validation/public/<case>/case.json`. 원문(PDF·CSV)은 저장소에 넣지 않는다.
실행: `mns validate-public [사례 루트] [--out 보고서 폴더]` → 사례별 `<id>.md` + `SUMMARY.md`(기본 `validation/reports/`). A 등급 실패가 있으면 종료코드 1.
자동 테스트: `tests/Sim.Core.Tests/PublicValidationTests.cs`(저장소 사례 전부, A 실패 금지) · `SourceDataValidationTests.cs`(원자료 사례 판정 고정).

## 원자료 → 저장소 사례 변환 (PRD FX-08)

`validation/sources/`(git 밖 원자료 — 다른 위치면 `--src` 또는 환경변수 `MNS_VALIDATION_SOURCES`)(사례별 `case.json`·`source/`·`digitized/`, 목록 INDEX.md)을 읽어
저장소 형식으로 바꾼다. 원자료가 바뀌면 다시 돌린다.

```powershell
python tools/validation/convert_public.py            # --src <원자료 루트> --dst validation/public --k0 <Cruz 보정 k0>
dotnet run --project src/Sim.Cli -c Release -- validate-public
python tools/validation/fit_rotor_vrs.py             # VRS 모델 계수 보정 재현(RotorModel 상수)
```
변환된 사례에는 `origin {source_case, sha256, converter}` 가 남는다. NASA 기준 시간이력은 변환 시 1 s 간격으로 표본화(포함 도구 평균·최소·최대)해 넣는다.

```jsonc
{
  "schema": "mns.validation-case/1",
  "id": "…", "title": "…", "vv": "VV-42",
  "grade": "A|B|C",          // A 표로 된 실측 · B 그래프 디지타이즈 · C 제조사·2차 자료
  "use": "validation|calibration|consistency",
                             // calibration: 이 자료로 모델 계수를 정함 / consistency: 모델 입력과 측정값의 출처가 같음 → '검증 통과'로 세지 않음(VV-50)
  "kind": "…", "model": "…", "envelope": "…",
  "tolerance": { "relative": 0.10 },
  "calibration": { "parameter": "soilS", "lo": 0.5, "hi": 200 },   // 선택: role=calibration 점(그룹별)으로 계수 이분법 보정
  "sources": [ { "title": "…", "url": "…", "note": "…" } ],
  "inputs": { … },                                   // 모든 점 공통 입력
  "points": [ { "label": "…", "inputs": { … }, "measured": 6.096, "unit": "m/s", "provenance": "…",
                "role": "validation|calibration|info", "group": "T7", "grade": "B", "toleranceAbs": 0.1, "note": "…" } ],
  "notes": [ "…" ],
  "origin": { "source_case": "…", "sha256": "…" }
}
```

### 판정
- 점 단위: `toleranceAbs` 가 있으면 |예측−측정| ≤ 허용, 없으면 |상대오차| ≤ `tolerance.relative`.
- **외삽**: 모델 쪽에 미리 선언한 적용 범위 밖(예: L1 형상 Cd Re ≤ 2×10⁵ `Shape.MaxValidReynolds`, 낙하산 M ≤ 0.5
  `Parachute.MaxValidMach`, Young 관입체 ≥ 5 lb `Penetration.MinValidMassSoil`, VRS V/vh ≤ 3.3)인 점은 오차만 보고하고 판정에서 뺀다(VV-32).
- 사례 상태: 판정 대상 검증점이 모두 이내 → 통과. 불합격 점 중 등급 A 가 있으면 실패, B/C 뿐이면 경고. 검증점이 없고 보정점만 → 보정용.
  판정 대상이 모두 외삽 → '외삽(모델 적용 범위 밖)'. `use` 가 calibration/consistency 면 그대로.
- 점 등급(`grade`)은 입력값 일부가 측정이 아닌 추정일 때 낮춘다(예: 가스 압력에서 추정한 충돌 속도).

## 재현기(kind)

| kind | 입력(SI) | 출력 | 검증 대상 |
|---|---|---|---|
| `parachute-steady-descent` | `d0_m`, `cd0`, `mass_kg`, `altitude_m`, `geopotential`(기본 true) | 엔진 정상 하강률 [m/s] | F절 CdS₀·대기 |
| `parachute-opening-infinite-mass` | `d0_m`, `cd0`, `rho_kg_m3`, `v_m_s`, `fillConstant`, `fillExponent`, `apparentMassK0`, `measuredCx` | 최대 하중 [N] (+Cx 참고) | F절 충만·겉보기 질량(풍동) |
| `parachute-deployment-flight` | `mass_kg`, `d0_m`, `cd0`, `altitude_m`, `speed_mps`, `flightPathDeg`, `lineStretchTime_s`, 충만 계수, `peakTime_s` | 최대 하중·시각 | F절 전개(유한 질량) |
| `drag-coefficient` | `shape`, `flowDirection`, `re` | L1 형상 Cd | D절 L1 공력 |
| `sphere-free-fall` | `mass_kg`, `diameter_m`, `dropHeight_m` | 엔진 낙하 최대 속도 | L1 구 항력 + 6DOF |
| `tumbling-mean-drag` | `diameter_m`, `length_m`, `re` | 회전각 평균 Cd(원형 단면 기준) | L1 원통 항력 |
| `young-penetration` | `mass_kg`, `diameter_m`\|`area_m2`, `nose`+`lOverD`\|`noseN`, `speed_mps`, `soilS` | 관입 깊이 [m] | FR-GND-02 |
| `atmosphere-table` | `altitude_m`, `quantity`, `geopotential` | US1976 값 | FR-ENV-01 |
| `nasa-checkcase` | `vehicle`, `earth`, `windProfile`, `altitude_m`, `omegaInertial_deg_s`, `reference{t, vars{mean,lo,hi,tol}}` | 변수별 0–30 s 최대 편차 | VV-05 엔진·지구 모델 |
| `rotor-vrs-ct-ratio` | `solidity`, `ct0`, `data[[J, CT/CT0]]` | RMS(≤0.1)·최저점 J(±0.1)·최저값(참고) | FR-UAV-02 v2 |
| `propeller-static-ct` | `ctRef`, `rpmRef`, `rpm` | CT (+일정 k_T 참고) | FR-UAV-01 k_T(RPM) |
| `cylinder-crossflow-2d` | `re`, `mach` | 2-D 원통 횡류 CD(`CylinderCrossflow.Cd2D`) | FR-REL-06 ① 원통 횡류 표(aero.crossflow tr-r300) |

미구현 kind 는 '재현기 미구현'으로 보고만 한다(실패 아님).

## 결과 요약 (2026-10-07, 원자료 12개 변환 사례 + 기존 4개)

| 사례 | 판정 | 의미 |
|---|---|---|
| `nasa_6dof_checkcases` | **통과**(A) | NASA 6개 도구 합의 궤적 1~8 전부. 고도 편차 ≤ 0.11 ft(허용 3 ft), 벽돌 오일러 ≤ 0.05°, 코리올리 동쪽 속도 ±0.006 ft/s |
| `parachute_dgb_opening_cruz2003` | **통과**(A) | k0 = 1.262 를 WTMD 11·12 로 보정 → 다른 풍동 9회·8000 lb 낙하 4회 최대 하중 −16~+11 %(허용 ±25 %) |
| `rotor_vrs_uiuc` | **통과**(A) | 3개 프롭 보정, 5개 검증: RMS 0.075–0.097(≤0.1), 최저점 J 오차 ≤ 0.05. 최저 CT/CT0 깊이는 과소(참고: TE9x4.5 0.77 vs 0.88) |
| `drag_cylinder_cd_re_gowen1953` | 통과(B) | 아임계 Re 1e4–2e5 ±2.6 %. 항력 위기 이후는 외삽(L1 1.2 vs 실제 0.3: −75 %) |
| `penetration_young_thompson1969` | 경고 | 보정 S = 원자료 역산 S(1 % 이내) → **원문 계수 교체 확인**. 크로노그래프 검증점 +11.6 %. B 점: 오자이브 노즈 +49 %(Young N 과대 가능), 추정 속도 시험 −30/+35 % |
| `penetration_young_thompson1969_apriori` | 경고(B) | 표 4-2 범위에서 S 를 사전 선택하면 ±50 % 밖 다수 — 현장 S 보정 필요 |
| `tumbling_cylinder_britcher` | 경고(B) | L/D 1 텀블링 평균 Cd 를 L1 이 31–35 % 과소(회전 유발 항력 없음) |
| `parachute_dgb_flight_tmx1451`, `parachute_ringsail_flight_tmx1414` | 외삽 | M 1.4–1.6 초음속 전개: 비압축 모델은 하중 +53 %, 최대 하중 시각 0.18–0.42 s 빠름 |
| `sphere_free_fall_bacon_reid1924` | 외삽 | Re 3.4e5–1.5e6(항력 위기): L1 Cd 0.47 이면 낙하 최대 속도 −8~−37 % |
| `parachute_dgb_descent_tmx1451` | 일관성 | 23–33 km 하강속도 −4.7~+0.3 % (CD0 0.47 이 같은 자료) |
| `propeller_static_uiuc` | 보정용 | CT ∝ n^0.165 로 ±5 % 이내, 일정 k_T 면 최대 −10 % |
| 기존 4개 | 통과/보정/일관성 | `atmosphere_us1976_table`, `drag_cylinder_crossflow_finite`, `drag_sphere_cube_subcritical`, `parachute_iris_ultra_ratings` |

### 설계에 주는 의미
1. **센서 관입**: 이전 Young 계수가 1.77배 과대였다(원문 대조로 수정). 소형 센서(1.0 kg)는 원문 적용 하한(2.27 kg) 밑이라 관입 깊이는 '외삽'.
   노즈 계수 N 의 효과가 실측(오자이브)보다 크게 나왔으므로 스파이크 원뿔 노즈 관입은 과대 추정 가능성 — 현장 투하로 S·N 보정 필요.
2. **컨테이너 공력**: Ø0.41 m·최대 139 m/s 면 Re ~4×10⁶ — 매끈한 원통 기준 초임계라 L1 횡류 Cd(0.74–1.2)가 과대일 수 있다(CFD 필요).
3. **낙하산 개방하중**: 아음속·대형 DGB 에서 겉보기 질량 모델(k0)로 ±16 % 재현. 소형(< 10 kg) 원형 낙하산 개방하중 시계열은 공개 자료가 없다.
4. **드론 VRS**: 하강비 x ≈ 1.1 에서 추력이 정지 대비 10–25 % 감소하고 x > 1.5 부터 회복 — `vrsModel:"uiuc-v2"` 로 사용 가능(9–11 in·4000 RPM 기준).

### 2차 수집 반영 (2026-10-07, PRD FR-REL-06)

| 사례·자료 | 판정 | 반영 |
|---|---|---|
| `drag_cylinder_crossflow_mach_tr_r300` | 일관성(A) | NASA TR R-300 표 II(일정 마하) 를 모델 표로(`reference_data.json`), 판정 줄 = 표 II 점(구현 확인), 표 I(a) 원점 133개 = 참고(잔차 → UQ 모델 형태 산포). 컨테이너 aero `crossflow: "tr-r300"` — M 0.5·Re 4.4×10⁶ 에서 2-D CD ≈ 1.10, η(4.7) ≈ 0.63 → 횡류 CD ≈ 0.70(이전 L1 0.83) |
| `parachute_cross40in_opening_noltr73157` | **통과**(A) | 소형 40 in 십자형 27구성(풍동, 무한 질량). 투과도별 k0 보정(8 cfm 1.19 · 80 cfm 0.46 · 208 cfm 0.31) 후 나머지 예측 이내 — 8 cfm k0 가 대형 DGB(Cruz 1.26)와 거의 같음 → 저투과 소형 캐노피에도 모델 형태가 맞음 |
| `parachute_cross40in_opening_noltr73157_apriori` | 경고(B) | DGB k0 를 그대로 쓰면 8 cfm −10~+4 %, 고투과(80·208 cfm) +15~+28 % 과대(보수적). 계수 전이 = 판단이라 B |
| Knacke 표 5-6 n · 표 5-2 CD0 | 입력 반영 | 박스·주 낙하산 n 8(범위 7–12), 드로그(hemisflo) n 14·CD0 0.46. 시나리오가 k0 = 0 으로 돌던 것을 검증된 k0(1.26, 범위 0.31–1.26)로 바꿈 |
| V-T-295 나일론 실 80 lb(AFFDL 표 4.3) | 입력 반영(A) | 브레이크 코드 356 N 은 이 규격 최소값과 같음 → 출처 A, 분산 −0/+25 % |
| LIROS 2 mm SK78 / Marlow D12 | 입력 반영(C) | 테더 EA 15 kN(가정) → 45 kN(범위 45–75 kN) — 스냅 하중 증가 |
| ARPRO 5135 45 g/l | 입력 반영(C) | 패드 평탄응력 0.30 → 0.29 MPa, 분산 ×0.8–1.8(동적 배율 포함) |
| MIL-STD-810H 516.8 · BMI088 · UN 38.3 | 판정 기준 | `reference_data.json` `shockCriteria`(20 g/11 ms 기능, 40 g 충돌 위험, LiPo 150 g) |
| NASA-STD-7009A 부록 E | 채점 기준 | 8요인 수준 정의 원문 → `mns credibility` (validation/reports/credibility) |

### 변환하지 않은 원자료
- `parachute_small_round`: NACA 반구형 CD 는 하강 평형 하중이 표에 없어 재현 대상이 없음, Dragonfly 하강률은 C(전이 포함 평균), Fruity Chutes 는 기존 `parachute_iris_ultra_ratings` 와 중복.
- TM X-1451 whirltower 3회: 탑재체 질량 미기재. Cruz DTST 037/038: 유한 질량 효과(최대 하중이 충만 66 %) — 유한 질량 재현기 필요.
- `freefall_cylinder` 물속 MIDEX: 매질이 다름(시계열 원자료 없음).
- Delany–Sorensen 2-D 각기둥 Cd: L1 상자는 3-D 라 직접 비교 불가.
