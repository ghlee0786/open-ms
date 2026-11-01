# 14. 스모크 · 자동 점검 실행 (`--smoke`)

앱 실행 파일에 `--smoke` 를 주면 창을 띄워 시나리오를 실행하고, 옵션대로 화면을 바꾸고 캡처한 뒤 기록을 남기고 **스스로 닫힙니다**.
매뉴얼 스크린샷(`docs/manual/_shots.json`)도 이 방식으로 재현합니다.

```powershell
MnS.Simulator.exe --smoke <scenario.json|result.mnsr> [옵션 …]
dotnet run --project src/Sim.App -- --smoke config/scenarios/demo_01_cargo_airdrop.json --at 40 --select container --dist 30 --theme dark --shot
```

- 출력 폴더: `<저장소>/results/smoke/` — 기록 `smoke.log`, 그림 `*.png`.
- 종료 코드: 0 = 정상, 3 = 그려진 프레임 10장 미만 또는 결과 없음, 4 = 예외.
- `--smoke` 실행은 창 하나 제한을 받지 않고, 사용자 설정(테마 · 장면 환경)을 읽거나 저장하지 않습니다. 업데이트 확인도 하지 않습니다.
- 첫 인자가 `.mnsr` 이면 실행 대신 결과를 불러옵니다.
- 화면에 보이는 경로(로그 첫 줄 `저장소 …` · 정보 창 · 결과 신뢰도 창 · 시나리오 설명 창)는 저장소 상대(`.` · `src\…`)나
  `%LOCALAPPDATA%\…` · `%APPDATA%\…` · `%USERPROFILE%\…` 꼴로 바꿔 보입니다 — 문서 그림에 PC 의 폴더 · 사용자 이름이 남지 않게.

## 14.1 실행 순서

1. `--size` → 시나리오 불러오기(또는 .mnsr) · `--import` → 실행
2. 셸 배치(`--unpin` · `--pin-left` · `--dash` · `--bottom` · `--drawer`) → `--browser` · `--hide`
3. 해석: `--structural` → `--fea` → `--ccx`
4. 색: `--all` · `--peak` · `--color` · `--modes` · `--split` · `--unsync` · `--flowlines`
5. `--save` · `--reload` → 장면 환경 `--env*`
6. **`--at` 이 있으면**: 그 시각으로 멈춤 · `--select` · `--dist` · `--view` · `--section` → (충격 계산을 기다림) · `--ccxshock` · `--seek-shock` → **`view.png` · `window.png` · `browser_zoom.png`** 캡처 → `--browser-menu` → `--menu` → `--ccx-dialog` → `--frames`.
   `--at` 이 없으면 20× 로 2.5초 재생만 합니다(창 캡처 없음).
7. `--gpumc` → `--helpshot` → `--shellshots` → `--updateshot` → `--theme-toggle` → `--theme` 사본 → `--windtunnel` → `--airflow`
8. 기록(`smoke.log`)을 쓰고 닫힘

## 14.2 옵션

| 옵션 | 값 | 동작 · 출력 |
| --- | --- | --- |
| `--smoke` | `[시나리오 \| .mnsr]` | 자동 점검 실행 |
| `--size` | `WxH` (예 `1920x1009`) | 창 크기 · 위치(0,0) — 그 크기에서 잘리지 않는지 확인 |
| `--theme` | `dark` \| `light` | 그 실행의 테마(저장 안 함). 캡처를 `results/smoke/theme_<테마>/` 에도 복사 |
| `--theme-toggle` | — | (`--at` 과 함께) 실행 중 테마 전환 후 `view_toggled.png` · `window_toggled.png` |
| `--import` | 파일 | 시나리오를 불러온 뒤 Fusion 모델 가져오기(STL · massprops.json) |
| `--unpin` | `dash,bottom` | 대시보드 · 아래 서랍 고정 해제(겹쳐 뜨기) |
| `--pin-left` | — | 왼쪽 서랍 고정 |
| `--dash` | `closed` | 대시보드 닫기 |
| `--bottom` | `closed` | 그래프 · 로그 서랍 닫기 |
| `--drawer` | `input` \| `mc` \| `json` | 왼쪽 서랍을 그 페이지로 열기 |
| `--browser` | `collapsed` \| `hidden` | 브라우저 접기(머리줄만) · 숨기기(Ctrl+B 와 같음) |
| `--hide` | `이름,이름` | 그 물체 눈 끄기 |
| `--tree-expand` | — | 브라우저 모두 펼치기(머리줄 '모두 펼치기' 와 같음) |
| `--structural` | — | 구조 분석(F6) |
| `--fea` | 물체 | OpenCourant 충돌 FEA (설치 필요) |
| `--ccx` | 물체 | CalculiX FEA (설치 필요) — `--ccx-analysis static\|modal\|buckle` · `--ccx-g G`(대표 G) · `--ccx-dir x\|-x\|z\|-z` · `--ccx-mesher auto\|gmsh\|voxel` |
| `--all` | — | 색상 옵션 '선택 물체만' 끄기 |
| `--peak` | — | 색상 옵션 '최대값 유지' 켜기 |
| `--color` | 키 | 색 모드: `off` `pressure` `stress` `safety` `state` `fea` `ccx` `ccxsf` `ccxdisp` `shockg` `shock` `ccxshock` `cfdcp` |
| `--modes` | `키,키,키,키` | 분할 화면별 색 모드(0번 = 상단 '색상') |
| `--split` | `1` \| `2` \| `4` | 화면 분할 |
| `--unsync` | — | 카메라 동기 끄기 |
| `--flowlines` | — | 'CFD 유선' 레이어 켜기 |
| `--save` | 파일 | 결과 저장(.mnsr) — `--reload` 면 저장 후 다시 불러오기 |
| `--env` | `id` \| `auto` | 장면 환경(그 실행만) |
| `--env-file` | 파일 · 폴더 | 사용자 환경 불러오기(environment.json · .hdr · .png) |
| `--env-rot` · `--env-bright` · `--env-ev` | ° · 배율 · EV | 환경 회전 · 밝기 · 노출 |
| `--env-bg` | `solid` | 단색 배경 |
| `--env-refl` | 거칠기 | 지면 반사 켜기 + 거칠기 |
| `--env-offset` | m | 지면 높이 오프셋 |
| `--env-off` | `ground,shadows,fog,flatten` | 지면 표시 · 그림자 · 안개 · 평탄화 끄기 |
| `--at` | s | 그 시각에서 멈추고 캡처 → `view.png`(3D) · `window.png`(창 전체) · `browser_zoom.png`(브라우저 2배) |
| `--select` | 물체 | (`--at` 과 함께) 선택 |
| `--dist` | m | (`--at` 과 함께) 카메라 거리로 초기화 |
| `--view` | `top` \| `north` \| `east` \| `iso` | (`--at` 과 함께) 카메라 시점 프리셋(키 1 · 2 · 3 · 4 와 같음) |
| `--section` | — | (`--at` 과 함께) 단면(내부) |
| `--ccxshock` | — | (`--at` 과 함께) CalculiX 모달 과도 응답 |
| `--shot-name` | 이름 | `view.png` 대신 `<이름>.png` |
| `--browser-menu` | — | (`--at` 과 함께) 선택 행 오른쪽 클릭 메뉴 → `window_menu.png` · `browser_menu_zoom.png` |
| `--seek-shock` | N | (`--at` 과 함께) Ctrl+] 를 N 번 누른 것과 같음 — N 번째 충격 창 시작(촉발 − 10 ms)으로 이동 · 슬로모션. `--seek-dt s` 로 s 초 더 이동. 충격 색 모드(`--color shockg`)와 함께, 자유 비행 중인 시각에서 |
| `--menu` | 이름 | (`--at` 과 함께) 머리글이 그 이름으로 시작하는 메인 메뉴를 펼쳐 `window_mainmenu.png` — `--submenu 이름` 이면 그 하위 메뉴도 펼침 |
| `--ccx-dialog` | 물체 | (`--at` 과 함께) CalculiX 옵션 창을 열어 `dialog_ccx.png` 를 찍고 실행하지 않고 닫음(CalculiX 설치 불필요) |
| `--frames` | `t0:t1:n[,…]` | (`--at` 과 함께) 구간마다 n 개 시각에서 창 캡처 → `frames/frame_0001.png …`(데모 GIF 프레임) |
| `--gpumc` | — | GPU MC 20,000회(`gpuMonteCarlo` 절이 있을 때) — `--at` 캡처 **뒤에** 실행 |
| `--helpshot` | — | 섹션 ? 말풍선을 모두 열어 창 안 배치 확인 + `callout_<키>.png` · `help.png` · `help_chapter.png` · `help_search.png` · `help_callout.png` · `scenario_info.png` · `dialog_credibility.png` · `dialog_materials.png` (`--at` 과 함께) |
| `--shellshots` | — | 셸 상태별 `shell_default.png` · `shell_drawer_input.png` · `shell_drawer_mc.png` · `shell_drawer_json_pinned.png` · `shell_closed.png` · `shell_overlay.png` · `shell_railgroup.png` |
| `--windtunnel` | α | 풍동 보기를 그 받음각으로 열어 `windtunnel.png` — `--wt-field cp\|speed\|cf` |
| `--airflow` | α | GPU 가상 풍동을 열어 프레임을 기다린 뒤 `airflow.png` — `--airflow-beta β` · `--airflow-field speed\|vort` · `--airflow-res low\|medium\|high` · `--airflow-frames N`(기본 60) · `--airflow-wait s`(기본 180) |
| `--updateshot` | — | 가짜 새 릴리스로 `update_dialog.png` · `update_dialog_progress.png`(42 %) · `update_latest.png` · `update_about.png` |
| `--shot` | — | 끝나기 전 0.5초 대기(캡처 파일 쓰기 여유). 캡처 자체는 `--at` · `--helpshot` 등이 함 |
| `--real-models` · `--synthetic` | 폴더 · — | 실모델 폴더 · 합성 대체 강제(CLI 와 같음) |

## 14.3 기록 (`smoke.log`)

한 줄씩 `키=값` 꼴로 화면 상태를 남깁니다 — 예: `ui.theme` · `scenario=… simEnd=… stop=…` · `treeBodies` · `ui.layout` · `ui.shell`(장면 영역 · 패널 위치 · 고정) ·
`ui.automation`(자동화 이름 점검) · `ui.browser` · `ui.structural` · `ui.impact` · `ui.ccx` · `ui.fea` · `ui.shock` · `ui.section` · `ui.save` · `ui.env` · `mc=… cep=…` ·
`ui.legend` · `ui.split` · `ui.pane<n>` · `ui.callout` · `ui.windtunnel` · `ui.airflow` · `ui.dashboard` · `ui.struct` · `gl` · `framesRendered` · 요약 원문 · 세션 로그.
충격 창 시각(`ui.shock … <창> t0-t1`)을 보고 `--at` 을 맞추거나 `--seek-shock 1` 로 이동하면 충격 장면을 찍을 수 있습니다(`ui.seekshock n=… t=…`).
`ui.menu` · `ui.ccxdialog` · `ui.frames` 는 메뉴 펼침 · CalculiX 옵션 창 · 프레임 캡처 결과입니다.

## 14.4 예시 (매뉴얼 스크린샷)

```powershell
# 메인 창 — 다크 / 라이트
MnS.Simulator.exe --smoke config/scenarios/demo_01_cargo_airdrop.json --at 40 --select container --dist 30 --theme dark --shot
MnS.Simulator.exe --smoke config/scenarios/demo_01_cargo_airdrop.json --at 40 --select container --dist 30 --theme light --shot

# 구조 분석 · 4 분할
MnS.Simulator.exe --smoke config/scenarios/demo_02_impact_materials.json --structural --all --split 4 --modes off,stress,pressure,safety --at 30 --select crate_free --dist 12 --theme dark --shot

# 장면 환경 자동(토양 frozen → 눈)
MnS.Simulator.exe --smoke config/scenarios/demo_03_wind_dispersion.json --env auto --at 60 --select probe1 --dist 25 --theme light --shot

# GPU MC 결과(셸 캡처는 MC 뒤에 찍힘)
MnS.Simulator.exe --smoke config/scenarios/demo_03_wind_dispersion.json --gpumc --shellshots --theme dark --shot
```

찍을 목록 전체와 각 그림이 어느 출력 파일에서 오는지는 [`_shots.json`](_shots.json) 에 있습니다.
`python scripts/make_readme_assets.py` 가 그 목록대로 앱을 빌드 · 실행해 그림을 제자리에 복사하고 데모 GIF(`assets/demo.gif`)를 합성합니다
(`--only <글자>` 로 일부만, `--list` 로 목록만). 앱 밖 화면(설치 마법사)은 `manual` 에 적힌 방법으로 따로 찍습니다.

