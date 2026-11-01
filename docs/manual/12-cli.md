# 12. 명령줄 도구 `mns`

`mns` 는 앱과 같은 엔진(Sim.Core)을 창 없이 씁니다. 소스에서는 `dotnet run --project src/Sim.Cli -- <명령> …`, 빌드한 실행 파일은 `mns.exe <명령> …`.
`mns`, `mns help`, `mns -h` 는 사용법을 출력합니다. 결과 폴더 기본값은 대개 `results/<시나리오>_<종류>_<날짜_시각>`.

**공통 옵션**: `--real-models DIR`(시나리오의 `massProps: "real:…"` 실모델 폴더, = 환경 변수 `MNS_REAL_MODELS`) · `--synthetic`(합성 대체 형상 강제).
종료 코드: 0 = 성공, 1 = 오류(또는 검증 실패), `run` 은 수치 '의심' 이면 2.

## 12.1 명령 표

| 명령 | 사용법 | 하는 일 · 출력 |
| --- | --- | --- |
| `run` | `mns run <scenario.json> [--out DIR] [--dt S] [--tend S] [--archive FILE.mnsr] [--structural] [--loadcases]` | 단일 실행 → `timeseries.csv` · `devices.csv` · `events.json` · `summary.json` · `scene.json`. `--dt` · `--tend` = `sim.dt` · `sim.tEnd` 덮어쓰기, `--archive` = 앱용 .mnsr, `--structural` = 근사 응력 · 재질 판정(`structural.json`, 이벤트 추가), `--loadcases` = `DIR/loadcases/` |
| `open` | `mns open <result.mnsr>` | 결과 파일 요약(불러오기 검사) — 시나리오 · 저장 시각 · 프레임 · 구조/MC 포함 여부 |
| `validate` | `mns validate <scenario.json>` | 시나리오 검증 — 물체 · 연결 · 이벤트 수, 해시, 물체별 모드 · 질량 · CG · 공력 · 장치 · 압력 중심 · 정적 여유 |
| `mc` | `mns mc <scenario.json> --runs N [--body NAME] [--seed S] [--sampling lhs\|random] [--out DIR]` | 전체 6DOF Monte Carlo(CPU 다중 코어, `dispersions`) → `mc_runs.csv` · `mc_summary.json`. 기본 100회, 물체 = `monteCarlo.impactBody` |
| `gpumc` | `mns gpumc <scenario.json> [--runs N] [--cpu] [--seed S] [--out DIR]` | 단순 모델(3DOF + 낙하산) Monte Carlo — ILGPU(GPU), `--cpu` = CPU 병렬 → `gpumc_runs.csv` · `gpumc_summary.json`. `gpuMonteCarlo` 절 필요 |
| `gpuinfo` | `mns gpuinfo` | GPU 장치 목록과 선택된 가속기 |
| `massprops` | `mns massprops <file.stl> (--mass KG \| --density KG_M3) [--unit mm\|cm\|m] [--fusion] [--out FILE.json]` | STL 균일 밀도 질량특성(Fusion JSON 이 없을 때) — 부피 · CG · 관성 · 주관성 모멘트. 기본 단위 mm, 밀도 1000 |
| `materials` | `mns materials` | 재질 라이브러리(키 · ρ · E · σy · σu · 취성/연성 · 속도 모델 · 출처) |
| `ccx` | `mns ccx <result.mnsr\|scenario.json> --body NAME [--analysis static\|modal\|buckle] [--nonlinear] [--case K \| --time T \| --loadcases DIR/loadcases.json \| --g G [--dir -x] [--at x,y,z] [--patch R]] [--size M] [--mesher auto\|gmsh\|voxel] [--cad PART.step [--cad-frame body\|fusion]] [--converge] [--out DIR] [--archive FILE.mnsr] [--install PATH] [--deck-only]` | CalculiX 정적 · 진동 · 좌굴. 하중 = 하중 순간 목록(기본 최대 연결점 하중) 또는 대표 G. 얇은 벽 = S6 셸, 솔리드 = Gmsh C3D10(없으면 복셀). 관성 완화. → `fea_result.json` · `faces.csv`. `--converge` = 요소 크기 3단계 수렴(솔리드만) → `convergence.json`, `--deck-only` = 입력 덱만 |
| `ccx-verify` | `mns ccx-verify [--out validation/reports] [--install PATH]` | CalculiX 해석해 검증 + OpenCourant 정적 한계 교차검증 → `fea_calculix_verification.md`. 하나라도 실패면 1 |
| `ccx-mesh` | `mns ccx-mesh <part.step\|part.stl> --size M [--order 1\|2] [--out mesh.inp]` | Gmsh 메시(기본 크기 0.005 m, 2차) → CalculiX `.inp` |
| `simpkg` | `mns simpkg <pkg.adsksimpkg> [--step-dir DIR[;DIR…]] [--out DIR] [--run] [--install PATH] [--modal [--modes N]] [--converge …] [--mesh-max MM] [--mesh-min MM] [--mesh-thick N] [--fusion-report FILE [--fusion-legend SPEC]] [--design-results FILE [--design-case AX]] [--threads N]` | Fusion 해석 패키지 요약 · CalculiX 재현 — 옵션 표는 [10.3절](10-fusion-blender.md#103-fusion-해석-패키지-simpkg) |
| `fea` | `mns fea <result.mnsr\|scenario.json> --body NAME [--out DIR] [--deck-only] [--install DIR] [--timeout S]` | OpenCourant(Radioss) 착지 충돌 셸 해석 — 덱 작성 · 실행 · VTK 변환 · 판정. 기본 제한 1800 s |
| `fea-taylor` | `mns fea-taylor [--material KEY] [--speed V] [--out DIR] [--deck-only] [--install DIR]` | 대표 검증: Taylor 봉 충돌(Wilkins–Guinan 상관식 비교, 15 % 이내면 0). 기본 `al6061-t6` · 200 m/s |
| `validate-public` | `mns validate-public [CASE_ROOT=validation/public] [--out validation/reports]` | 공개 데이터 검증 — 사례별 `case.json` 재현 · 판정 → 보고서. A 등급 실패 시 1 |
| `adversarial` | `mns adversarial [--out validation/reports/adversarial]` | 적대적 시나리오 자체 검증 → `SUMMARY.md`. 실패가 있으면 1 |
| `credibility` | `mns credibility <scenario.json …> [--runs N] [--out validation/reports/credibility] [--raw DIR] [--fea-gci DIR] [--loads FILE]` | 신뢰도 분석 — 입력 출처 등급 · 불확도 정량화 · NASA-STD-7009A 8요인 · 결과별 신뢰/참고/추정 · 90 % 구간 · 시험 우선순위 → `README.md` · `credibility.json`(앱이 읽음, [7.3절](07-monte-carlo-credibility.md#73-결과-신뢰도-nasa-std-7009a)) |
| `scenario-report` | `mns scenario-report <scenario.json> [--variant <scenario.json>] [--runs N] [--ccx] [--install PATH] [--out DIR] [--raw DIR]` | 운용 시나리오 분석 보고서 — 단계 타임라인 · 텀블링 · 잔해 간섭 · 드로그 지연 민감도 · MC 전개 이상 확률 · 단계 응력(+ CalculiX). 기본 MC 64회 |
| `opening-shock` | `mns opening-shock <scenario.json> [--variant <scenario.json>] [--runs N] [--ccx] [--install PATH] [--no-sweep] [--out validation/reports/opening_shock] [--raw DIR]` | 낙하산 개방 충격 · 브레이크 코드 · 내부 하중 경로 — 단계별 G, 부품 항복 안전계수(근사 + CalculiX), 압괴 패드 비교 · 두께 스윕, 개방 하중 불확도 MC 안전계수 분포 |
| `tumble-study` | `mns tumble-study <scenario.json> [--runs N] [--seed S] [--aero-db DB.json] [--damping Clp,Cmq,Cnr] [--no-sweeps] [--no-maps] [--fatigue-simpkg summary.json] [--out DIR]` | 분리 충격 조건(Δv · 비대칭 · 지연 · 받음각 · 감쇠 · 압력 중심 · 드로그 지연) 변화에 따른 컨테이너 텀블링 등급 지도 · 1-D 스윕 · LHS MC · PRCC · 하중 포락선. 기본 256회 · 시드 4009. `--fatigue-simpkg` = 진동 피로 근사 절([8.9절](08-structural-impact.md#89-진동-피로-근사)) |
| `container-material` | `mns container-material [--scenario FILE] [--envelope envelope.json] [--ccx] [--ccx-jobs N] [--thickness-mm a,b] [--mesh-theta N] [--mesh-length N] [--install PATH] [--out DIR]` | 원통 컨테이너 튜브 재질 · 두께 비교: 항복/파손 · 외압 · 축압축 좌굴 · 굽힘(NASA SP-8007) + CalculiX 셸 좌굴/정적, 질량 예산 |
| `loads` | `mns loads <scenario.json> [--runs N] [--seed S] [--ccx] [--ccx-threads N] [--no-aero] [--parts FILE] [--envelope FILE] [--sweep "경로:값1,값2[:설명]"]… [--raw DIR] [--out DIR]` | 낙하산 개방 충격 → 내부 수납물 부품별 G · 하중 · 응력 · 항복 SF(6DOF + SRS 동적 증폭 + 불확도 MC + CalculiX 대조), 분리 직후 하중, 컨테이너 외부 공력(CFD Cp → CalculiX 셸 정적 · 좌굴) + 조합 하중 + 요약 판정. 기본 128회 |
| `aero-compare` | `mns aero-compare <scenario.json> --db <aero_db.json …> [--body NAME] [--out FILE.md]` | 같은 시나리오를 기본 공력과 CFD 공력 DB 로 각각 돌려 핵심 출력 변화를 보고(사용법 문자열에는 없음, 기본 물체 `container`) |

> `credibility` · `tumble-study` · `loads` · `container-material` 은 시나리오를 주지 않으면 앱과 같은 규칙으로 `config/scenarios` 의 이름순 첫 파일을 씁니다.
> `loads` 의 부품 하중 경로(`--parts`)는 기본으로 `config/models/*_payload_parts.json` 을 찾으므로, 그 폴더가 없으면 직접 지정하십시오.
> 이 네 명령과 `scenario-report` · `opening-shock` 은 운반 물체 안에 내부 수납물이 있는 다단계 시나리오용 분석 보고서입니다.

## 12.2 예시 (공개 데모)

```powershell
# 한 번 실행 + 구조 분석 + 앱에서 볼 결과 파일
mns run config/scenarios/demo_01_cargo_airdrop.json --structural --archive results/demo01.mnsr
mns open results/demo01.mnsr

# 시나리오 검증
mns validate config/scenarios/demo_02_impact_materials.json

# 6DOF Monte Carlo 200회 (crate_chute 기준)
mns mc config/scenarios/demo_02_impact_materials.json --runs 200

# GPU 단순 모델 Monte Carlo 10만 회
mns gpumc config/scenarios/demo_03_wind_dispersion.json --runs 100000

# CalculiX 정적 해석 — 대표 G 하중
mns ccx config/scenarios/demo_02_impact_materials.json --body spike --g 10 --dir -x

# OpenCourant 착지 충돌 덱만
mns fea config/scenarios/demo_02_impact_materials.json --body crate_free --deck-only

# STL 질량특성
mns massprops samples/fusion_export/spike_sensor_sample.stl --density 2700 --unit mm

# 공개 데이터 검증 · 신뢰도
mns validate-public
mns credibility config/scenarios/demo_01_cargo_airdrop.json --runs 64
```

## 12.3 앱 실행 파일의 창 없는 보고서

`MnS.Simulator.exe --opening-shock-report <scenario.json> [--variant <scenario.json>] [--runs N] [--ccx] [--install PATH] [--no-sweep] [--out DIR] [--raw DIR] [--real-models DIR]`
— `mns opening-shock` 과 같은 보고서를 창 없이 만듭니다. GUI 실행 파일이라 콘솔이 없을 수 있어 진행 로그는 `<out>/generation.log` 에 남습니다.
