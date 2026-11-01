# 매뉴얼 대조 체크리스트

실제 코드에서 뽑은 메뉴 · 패널 · 단추 · 단축키 · 창 · 명령 · 옵션 목록과, 각 항목을 설명하는 매뉴얼 장.

- 원천의 파일 경로는 따로 적지 않으면 `src/Sim.App/` 기준이고, 줄 번호는 `main`(eb2851f — 업데이트 · Fusion 내보내기 1.1 · 공개 데모 병합 후) 기준입니다.
- `브랜치 a6b961e` = 아직 병합 전인 `worktree-agent-a6b961e7190271899`(simpkg 정확도 · 진동 피로).
- 장 번호: 01 설치 · 02 빠른 시작 · 03 화면 · 04 3D/카메라/재생 · 05 장면 환경 · 06 시나리오 · 07 MC/신뢰도 · 08 구조 · 09 유동 · 10 Fusion/Blender · 11 결과 · 12 CLI · 13 단축키 · 14 스모크.

## 1. 메뉴 — 파일 · 시뮬레이션 · 해석

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 파일 | 파일(_F) | Alt+F | MainWindow.axaml:257 | [03](03-screen-layout.md) |
| 파일 | MiOpen "시나리오 열기…" | Ctrl+O | MainWindow.axaml:258 | [06](06-scenario.md) |
| 파일 | MiImport "모델 가져오기 (Fusion STL/JSON)…" | Ctrl+I | MainWindow.axaml:259 | [10](10-fusion-blender.md) |
| 파일 | MiLoadResult "결과 불러오기 (.mnsr)…" | Ctrl+L | MainWindow.axaml:261 | [11](11-results.md) |
| 파일 | MiSave "결과 저장 (.mnsr)…" | Ctrl+S | MainWindow.axaml:262 | [11](11-results.md) |
| 파일 | MiExport "결과 폴더 내보내기 (CSV · Blender · scene)…" | — | MainWindow.axaml:263 | [11](11-results.md) |
| 파일 | MiShot "3D 화면 캡처 (PNG)…" | — | MainWindow.axaml:265 | [11](11-results.md) |
| 파일 | MiSaveLog "로그 저장…" | — | MainWindow.axaml:266 | [11](11-results.md) |
| 파일 | MiExit "종료" | Alt+F4 | MainWindow.axaml:268 | [13](13-shortcuts.md) |
| 시뮬레이션 | 시뮬레이션(_S) | Alt+S | MainWindow.axaml:270 | [03](03-screen-layout.md) |
| 시뮬레이션 | MiRun "실행" | F5 | MainWindow.axaml:271 | [02](02-quick-start.md) |
| 시뮬레이션 | MiCancel "실행 중인 작업 취소" | Esc | MainWindow.axaml:272 | [02](02-quick-start.md) |
| 시뮬레이션 | MiGpuMc "GPU Monte Carlo (단순 모델)" | — | MainWindow.axaml:274 | [07](07-monte-carlo-credibility.md) |
| 시뮬레이션 | MiCpuMc "CPU Monte Carlo (전체 6DOF)" | — | MainWindow.axaml:275 | [07](07-monte-carlo-credibility.md) |
| 시뮬레이션 | MiApplyJson "시나리오 JSON 적용·검증" | — | MainWindow.axaml:277 | [06](06-scenario.md) |
| 해석 | 해석(_A) | Alt+A | MainWindow.axaml:279 | [03](03-screen-layout.md) |
| 해석 | MiStructural "구조 분석 — 전 구간 최대값 · 최초 항복/파단 (근사)" | F6 | MainWindow.axaml:280 | [08](08-structural-impact.md) |
| 해석 | MiLoadCases "FEA 하중 케이스 내보내기…" | — | MainWindow.axaml:281 | [08](08-structural-impact.md) |
| 해석 | MiCcx "정적/진동/좌굴 FEA — CalculiX (선택 물체)…" | F7 | MainWindow.axaml:283 | [08](08-structural-impact.md) |
| 해석 | MiCcxDeform "FEA 변형 표시 배율" — 끔 (원래 형상) · 자동 (최대 변위 = 크기의 5%) · ×1 (실제) · ×10 · ×100 · ×1000 | — | MainWindow.axaml:284, MainWindow.Fea.cs:37 | [08](08-structural-impact.md) |
| 해석 | MiCcxCheck "CalculiX · Gmsh 설치 확인" | — | MainWindow.axaml:285, MainWindow.Fea.cs:29 | [08](08-structural-impact.md) |
| 해석 | MiShock "개방 충격 응답 — 보 동해석 (근사 C, 충격 가속도·응력 색)" | — | MainWindow.axaml:287, MainWindow.Shock.cs:241 | [08](08-structural-impact.md) |
| 해석 | MiCcxShock "개방 충격 과도 응답 — CalculiX 모달 과도 (선택 물체 · 현재/다음 충격 창 · 솔리드 단면만)" | — | MainWindow.axaml:288, MainWindow.Shock.cs:289 | [08](08-structural-impact.md) |
| 해석 | MiShockNext "다음 충격 창으로 이동 (슬로모션 0.005×)" | Ctrl+] | MainWindow.axaml:289, MainWindow.Shock.cs:229 | [08](08-structural-impact.md) |
| 해석 | MiShockPrev "이전 충격 창으로 이동" | Ctrl+[ | MainWindow.axaml:290 | [08](08-structural-impact.md) |
| 해석 | MiFea "충돌 FEA — OpenCourant 착지 충돌 해석 (선택 물체)…" | — | MainWindow.axaml:292, MainWindow.Analysis.cs:542 | [08](08-structural-impact.md) |
| 해석 | MiFeaCheck "OpenCourant 설치 확인" | — | MainWindow.axaml:293, MainWindow.Analysis.cs:71 | [08](08-structural-impact.md) |
| 해석 | MiMaterials "재질 라이브러리 보기" | — | MainWindow.axaml:295, MainWindow.Analysis.cs:575 | [08](08-structural-impact.md) |
| 해석 | MiCredibility "결과 신뢰도 — 7009A 요인 · 신뢰 등급 · 시험 우선순위…" | — | MainWindow.axaml:296, MainWindow.Credibility.cs:90 | [07](07-monte-carlo-credibility.md) |
| 해석 | MiWindTunnel "풍동 보기 — CFD 표면 압력 · 3-D 유선 · 입자 흐름 (선택 물체)…" | F8 | MainWindow.axaml:298, MainWindow.WindTunnel.cs | [09](09-flow-wind-tunnel.md) |
| 해석 | MiAirflow "GPU 가상 풍동 — 실시간 흐름 (XLB 격자 볼츠만 · 연기 · 와류 · 시각화 근사)…" | Shift+F8 | MainWindow.axaml:299, MainWindow.Airflow.cs | [09](09-flow-wind-tunnel.md) |

## 2. 메뉴 — 보기 (코드에서 생성)

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 보기 | 보기(_V) — ViewMenu | Alt+V | MainWindow.axaml:302, MainWindow.axaml.cs:506 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | "3D 표시 레이어" (머리글) | — | MainWindow.axaml.cs:508 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 지면 격자 | — | Rendering/ViewLayers.cs:23 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 방위 (북쪽 화살표) | — | Rendering/ViewLayers.cs:24 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 바람 벡터 (지상 10 m) | — | Rendering/ViewLayers.cs:25 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 물체 3D 모델 | — | Rendering/ViewLayers.cs:26 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 운반체 | — | Rendering/ViewLayers.cs:27 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 수납 상태 물체 | — | Rendering/ViewLayers.cs:28 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 선택 물체 강조 | — | Rendering/ViewLayers.cs:29 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 궤적 | — | Rendering/ViewLayers.cs:30 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 낙하산 · 라이저 | — | Rendering/ViewLayers.cs:31 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 드론 암 전개 · 프롭 회전 | — | Rendering/ViewLayers.cs:32 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 테더 · 레일 연결 | — | Rendering/ViewLayers.cs:33 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 착지 지점 표식 | — | Rendering/ViewLayers.cs:34 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 선택 물체 동체축 (x 빨강 · y 초록 · z 파랑) | — | Rendering/ViewLayers.cs:35 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 선택 물체 속도 벡터 (0.5 s) | — | Rendering/ViewLayers.cs:36 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 선택 물체 CFD 유선 (가장 가까운 받음각 · aero.flowField) — 처음엔 끔 | — | Rendering/ViewLayers.cs:37, :46 | [04](04-3d-view-camera-playback.md), [09](09-flow-wind-tunnel.md) |
| 보기 › 레이어 | MC 착탄점 | — | Rendering/ViewLayers.cs:38 | [04](04-3d-view-camera-playback.md), [07](07-monte-carlo-credibility.md) |
| 보기 › 레이어 | MC CEP 원 · 95% 타원 | — | Rendering/ViewLayers.cs:39 | [04](04-3d-view-camera-playback.md), [07](07-monte-carlo-credibility.md) |
| 보기 › 레이어 | 화면 정보 (HUD) | — | Rendering/ViewLayers.cs:40 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 색상 레전드 (응력·압력 막대) | — | Rendering/ViewLayers.cs:41 | [04](04-3d-view-camera-playback.md) |
| 보기 › 레이어 | 연결점 하중 핫스팟 표식 | — | Rendering/ViewLayers.cs:42 | [04](04-3d-view-camera-playback.md) |
| 보기 › 패널 | "패널" (머리글) | — | MainWindow.axaml.cs:520 | [04](04-3d-view-camera-playback.md) |
| 보기 › 패널 | 입력 패널 (시나리오 · 조건 · JSON) | — | MainWindow.axaml.cs:80 | [04](04-3d-view-camera-playback.md), [03](03-screen-layout.md) |
| 보기 › 패널 | 브라우저 표시 (어셈블리 · 3D 위) | Ctrl+B | MainWindow.axaml.cs:81, :524 | [04](04-3d-view-camera-playback.md), [03](03-screen-layout.md) |
| 보기 › 패널 | 텔레메트리 대시보드 · 요약 · 이벤트 | — | MainWindow.axaml.cs:82 | [04](04-3d-view-camera-playback.md), [03](03-screen-layout.md) |
| 보기 › 패널 | 그래프 섹션 | — | MainWindow.axaml.cs:83 | [04](04-3d-view-camera-playback.md), [03](03-screen-layout.md) |
| 보기 › 패널 | 로그 섹션 | — | MainWindow.axaml.cs:84 | [04](04-3d-view-camera-playback.md), [03](03-screen-layout.md) |
| 보기 › 색상 | "색상 표시 (응력 · 압력 · 재질)" 하위 메뉴(라디오) | — | MainWindow.Analysis.cs:377 | [04](04-3d-view-camera-playback.md) |
| 보기 › 색상 | 끔 (물체 색) — 키 `off` | — | Rendering/ColorMaps.cs:25 | [04](04-3d-view-camera-playback.md) |
| 보기 › 색상 | 표면 압력 [kPa] — `pressure` | — | Rendering/ColorMaps.cs:26 | [04](04-3d-view-camera-playback.md) |
| 보기 › 색상 | 근사 응력 von Mises [MPa] — `stress` | — | Rendering/ColorMaps.cs:27 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | 안전계수 (재질) — `safety` | — | Rendering/ColorMaps.cs:28 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | 부위 상태 (항복·파단·좌굴) — `state` | — | Rendering/ColorMaps.cs:29, :64 | [04](04-3d-view-camera-playback.md) |
| 보기 › 색상 | FEA 소성변형률 (OpenCourant) — `fea` | — | Rendering/ColorMaps.cs:30 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | FEA von Mises [MPa] (CalculiX) — `ccx` | — | Rendering/ColorMaps.cs:31 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | FEA 안전계수 (CalculiX) — `ccxsf` | — | Rendering/ColorMaps.cs:32 | [04](04-3d-view-camera-playback.md) |
| 보기 › 색상 | FEA 변위·모드 형상 [mm] (CalculiX) — `ccxdisp` | — | Rendering/ColorMaps.cs:33 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | 충격 가속도 [G] (개방·스냅, 동해석) — `shockg` | — | Rendering/ColorMaps.cs:34 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | 충격 응력 von Mises [MPa] (동해석) — `shock` | — | Rendering/ColorMaps.cs:35 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | FEA 충격 응력 [MPa] (CalculiX 과도) — `ccxshock` | — | Rendering/ColorMaps.cs:36 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 보기 › 색상 | CFD 표면 압력 계수 Cp (OpenFOAM) — `cfdcp` | — | Rendering/ColorMaps.cs:37, MainWindow.Flow.cs:11 | [04](04-3d-view-camera-playback.md), [09](09-flow-wind-tunnel.md) |
| 보기 › 테마 | "테마" — 다크 · 라이트(라디오) | — | MainWindow.axaml.cs:533 | [03](03-screen-layout.md) |
| 보기 › 장면 환경 | "장면 환경" 하위 메뉴 | — | MainWindow.axaml.cs:550, :572 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 기본 (분석) — `analysis` | — | MainWindow.axaml.cs:587, config/environments/analysis | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 자동 (시나리오 토양) — `auto` | — | MainWindow.axaml.cs:588, Rendering/SceneEnvironment.cs:427 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 풀밭 — `grass` | — | config/environments/grass/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 흙 (마른 땅) — `dirt` | — | config/environments/dirt/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 모래 — `sand` | — | config/environments/sand/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 암반 — `rock` | — | config/environments/rock/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 눈 — `snow` | — | config/environments/snow/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 숲 가장자리 — `forest-edge` | — | config/environments/forest-edge/environment.json | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 사용자 환경 불러오기… (environment.json · .hdr · .png) | — | MainWindow.axaml.cs:591, :632 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 밝기 — 50 · 75 · 100 · 125 · 150 · 200 % | — | MainWindow.axaml.cs:595 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 카메라 노출 (EV) — −2 ~ +2 EV | — | MainWindow.axaml.cs:599 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 환경 회전 (태양 방위) — 0° ~ 315° | — | MainWindow.axaml.cs:603 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 배경 — 환경 (하늘) · 단색 (테마 배경) | — | MainWindow.axaml.cs:610 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 표시 | — | MainWindow.axaml.cs:615 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 평탄화 (지평선까지) | — | MainWindow.axaml.cs:616 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 반사 | — | MainWindow.axaml.cs:617 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 반사 거칠기 — 매끈 (0.1) · 보통 (0.5) · 거침 (0.9) | — | MainWindow.axaml.cs:618 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 높이 오프셋 — −1.0 ~ +1.0 m | — | MainWindow.axaml.cs:622 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 지면 그림자 | — | MainWindow.axaml.cs:626 | [05](05-scene-environment.md) |
| 보기 › 장면 환경 | 거리 안개 | — | MainWindow.axaml.cs:627 | [05](05-scene-environment.md) |
| 보기 | "기본 — 모두 표시 (레이어 · 패널 · 숨긴 물체)" | Ctrl+0 | MainWindow.axaml.cs:544, :697 | [04](04-3d-view-camera-playback.md) |

## 3. 메뉴 — 카메라 · 재생 · 도움말

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 카메라 | 카메라(_C) | Alt+C | MainWindow.axaml:303 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiFollow "선택 물체 추적" (체크) | T | MainWindow.axaml:304 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiFocus "선택 물체로 확대" | C | MainWindow.axaml:305 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiFit "전체 보기" | F | MainWindow.axaml:306 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiResetCam "카메라 초기화" | Home | MainWindow.axaml:307 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiZoomIn "확대   (+ · 휠)" | + | MainWindow.axaml:309 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiZoomOut "축소   (- · 휠)" | − | MainWindow.axaml:310 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiTop "위에서 (평면, 북쪽 위)   (1)" | 1 | MainWindow.axaml:312 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiNorth "정면 (북쪽을 봄)   (2)" | 2 | MainWindow.axaml:313 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiEast "측면 (동쪽을 봄)   (3)" | 3 | MainWindow.axaml:314 | [04](04-3d-view-camera-playback.md) |
| 카메라 | MiIso "등각   (4)" | 4 | MainWindow.axaml:315 | [04](04-3d-view-camera-playback.md) |
| 재생 | 재생(_P) | Alt+P | MainWindow.axaml:317 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiPlay "재생 / 일시정지" | Space | MainWindow.axaml:318 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiRewind "처음으로" | Ctrl+Home | MainWindow.axaml:319 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiStepBack "이전 프레임   (,)" | , | MainWindow.axaml:320 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiStepFwd "다음 프레임   (.)" | . | MainWindow.axaml:321 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiPrevEvent "이전 이벤트" | PageUp | MainWindow.axaml:323 | [04](04-3d-view-camera-playback.md) |
| 재생 | MiNextEvent "다음 이벤트" | PageDown | MainWindow.axaml:324 | [04](04-3d-view-camera-playback.md) |
| 도움말 | 도움말(_H) | Alt+H | MainWindow.axaml:326 | [13](13-shortcuts.md) |
| 도움말 | MiHelp "도움말 · 조작법 · 단축키" | F1 | MainWindow.axaml:327, HelpWindow.cs | [13](13-shortcuts.md) |
| 도움말 | MiScenarioInfo "현재 시나리오 설명…" | — | MainWindow.axaml:328, MainWindow.axaml.cs:1513 | [06](06-scenario.md) |
| 도움말 | MiCheckUpdate "업데이트 확인…" | — | MainWindow.axaml:330, MainWindow.Update.cs:50 | [01](01-install-update.md) |
| 도움말 | MiAutoUpdate "시작할 때 업데이트 자동 확인 (하루 한 번)" (체크) | — | MainWindow.axaml:331, MainWindow.Update.cs:51 | [01](01-install-update.md) |
| 도움말 | MiAbout "정보 (버전)…" | — | MainWindow.axaml:333, MainWindow.Update.cs:59, :200 | [01](01-install-update.md) |

## 4. 제목 표시줄 · 상태 표시줄

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 제목 표시줄 | TitleBrand · TitleText(아이콘 · 제목, 폭 1320 미만이면 제목 숨김) | — | MainWindow.axaml:250, MainWindow.Shell.cs:57 | [03](03-screen-layout.md) |
| 제목 표시줄 | DragArea — 끌기 이동 · 더블클릭 최대화 | — | MainWindow.axaml:338, MainWindow.Shell.cs:41 | [03](03-screen-layout.md) |
| 빠른 단추 | TbOpen "열기" | Ctrl+O | MainWindow.axaml:342 | [03](03-screen-layout.md), [06](06-scenario.md) |
| 빠른 단추 | TbLoad "불러오기" | Ctrl+L | MainWindow.axaml:345 | [03](03-screen-layout.md), [11](11-results.md) |
| 빠른 단추 | TbSave "저장" | Ctrl+S | MainWindow.axaml:348 | [03](03-screen-layout.md), [11](11-results.md) |
| 빠른 단추 | TbRun "실행" (주색) | F5 | MainWindow.axaml:352 | [02](02-quick-start.md) |
| 빠른 단추 | TbStruct "구조 분석" | F6 | MainWindow.axaml:358 | [08](08-structural-impact.md) |
| 빠른 단추 | ? HelpKeys.Toolbar | — | MainWindow.axaml:364 | [03](03-screen-layout.md) |
| 빠른 단추 | ThemeBtn "테마 전환" (해 · 달) | — | MainWindow.axaml:366, MainWindow.axaml.cs:357 | [03](03-screen-layout.md) |
| 빠른 단추 | HelpBtn "도움말" | F1 | MainWindow.axaml:369 | [13](13-shortcuts.md) |
| 창 단추 | MinBtn "최소화" · MaxBtn "최대화"/"이전 크기로" · CloseBtn "창 닫기" | Alt+F4 | MainWindow.axaml:376–382, MainWindow.Shell.cs:116 | [03](03-screen-layout.md) |
| 상태 표시줄 | JobIcon · JobText "준비" | — | MainWindow.axaml:392–393 | [03](03-screen-layout.md) |
| 상태 표시줄 | JobProgress · JobElapsed | — | MainWindow.axaml:394–395 | [03](03-screen-layout.md) |
| 상태 표시줄 | CancelBtn "취소 (Esc)" | Esc | MainWindow.axaml:396 | [03](03-screen-layout.md) |
| 상태 표시줄 | WarnBadge "경고 — 로그 보기" · ErrBadge "오류 — 로그 보기" | — | MainWindow.axaml:397, :403, MainWindow.axaml.cs:195 | [03](03-screen-layout.md) |
| 상태 표시줄 | GlText (OpenGL 정보) | — | MainWindow.axaml:409 | [03](03-screen-layout.md) |
| 상태 표시줄 | ? HelpKeys.Status | — | MainWindow.axaml:410 | [03](03-screen-layout.md) |
| 진행 표시 | BusyOverlay (BusyTitle "(Esc 취소)" · BusyStage · BusyProgress) | — | MainWindow.axaml:562, MainWindow.axaml.cs:450 | [03](03-screen-layout.md), [02](02-quick-start.md) |

## 5. 왼쪽 레일 · 서랍

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 레일 | RailInput "입력" | — | MainWindow.axaml:983 | [03](03-screen-layout.md) |
| 레일 | RailMc "Monte Carlo" | — | MainWindow.axaml:987 | [03](03-screen-layout.md), [07](07-monte-carlo-credibility.md) |
| 레일 | RailJson "시나리오 JSON" | — | MainWindow.axaml:991 | [03](03-screen-layout.md), [06](06-scenario.md) |
| 레일 | RailFile "결과 · 파일" (묶음) | — | MainWindow.axaml:997, MainWindow.Shell.cs:64 | [03](03-screen-layout.md) |
| 레일 | RailAnalysis "해석" (묶음) | — | MainWindow.axaml:1001, MainWindow.Shell.cs:65 | [03](03-screen-layout.md) |
| 레일 | RailWind "풍동" (묶음) | — | MainWindow.axaml:1005, MainWindow.Shell.cs:66 | [03](03-screen-layout.md), [09](09-flow-wind-tunnel.md) |
| 레일 | RailBrowser "브라우저" | Ctrl+B | MainWindow.axaml:964 | [03](03-screen-layout.md) |
| 레일 | RailDash "대시보드" | — | MainWindow.axaml:968 | [03](03-screen-layout.md) |
| 레일 | RailBottom "그래프 · 로그" | — | MainWindow.axaml:972 | [03](03-screen-layout.md) |
| 레일 | RailHelp "도움말 레일" | F1 | MainWindow.axaml:976 | [03](03-screen-layout.md) |
| 레일 | ? HelpKeys.Rail | — | MainWindow.axaml:980 | [03](03-screen-layout.md) |
| 서랍 머리 | DrawerTitle 입력/Monte Carlo/시나리오 JSON · LeftPinBtn "서랍 고정" · LeftCloseBtn "서랍 닫기" | Esc | MainWindow.axaml:799–804, MainWindow.Shell.cs:152 | [03](03-screen-layout.md) |
| 서랍 | LeftGrip 폭 조절(260–560, 더블클릭 320) | — | MainWindow.axaml:955, MainWindow.Shell.cs:278 | [03](03-screen-layout.md) |
| 서랍 › 입력 | "시나리오" 소제목 · ? HelpKeys.Scenario | — | MainWindow.axaml:822–823 | [06](06-scenario.md) |
| 서랍 › 입력 | ScenarioBox (config/scenarios/*.json 목록) | — | MainWindow.axaml:827, MainWindow.axaml.cs:116 | [06](06-scenario.md), [02](02-quick-start.md) |
| 서랍 › 입력 | ScenarioInfoBtn "설명" | — | MainWindow.axaml:815 | [06](06-scenario.md) |
| 서랍 › 입력 | OpenBtn "열기…" | Ctrl+O | MainWindow.axaml:837 | [06](06-scenario.md) |
| 서랍 › 입력 | ImportBtn "모델 가져오기…" | Ctrl+I | MainWindow.axaml:843 | [10](10-fusion-blender.md) |
| 서랍 › 입력 | "투하 조건 · 환경" 소제목 · ? HelpKeys.Conditions | — | MainWindow.axaml:853–854 | [06](06-scenario.md) |
| 서랍 › 입력 | AltBox "운반체 고도 AGL [m]" | — | MainWindow.axaml:858–859 | [06](06-scenario.md) |
| 서랍 › 입력 | SpeedBox "운반체 속도 [m/s]" | — | MainWindow.axaml:860–861 | [06](06-scenario.md) |
| 서랍 › 입력 | HeadingBox "운반체 방위 [°]" | — | MainWindow.axaml:862–863 | [06](06-scenario.md) |
| 서랍 › 입력 | WindBox "풍속 (10 m) [m/s]" | — | MainWindow.axaml:864–865 | [06](06-scenario.md) |
| 서랍 › 입력 | WindDirBox "풍향 (불어오는) [°]" | — | MainWindow.axaml:866–867 | [06](06-scenario.md) |
| 서랍 › 입력 | TurbBox "난류 (Dryden)" — none · light · moderate · severe | — | MainWindow.axaml:868–869, MainWindow.axaml.cs:103 | [06](06-scenario.md) |
| 서랍 › 입력 | DtBox "적분 dt [s]" | — | MainWindow.axaml:870–871 | [06](06-scenario.md) |
| 서랍 › 입력 | RunBtn "시뮬레이션 실행" | F5 | MainWindow.axaml:875 | [02](02-quick-start.md), [06](06-scenario.md) |
| 서랍 › 입력 | SaveBtn "결과 저장…" | Ctrl+S | MainWindow.axaml:881 | [11](11-results.md), [06](06-scenario.md) |
| 서랍 › 입력 | Expander "모델 가져오기 옵션" · ? HelpKeys.ImportOptions | — | MainWindow.axaml:889–893 | [10](10-fusion-blender.md) |
| 서랍 › 입력 | ImportMassBox "질량 [kg]" (기본 1.0) | — | MainWindow.axaml:897–898 | [10](10-fusion-blender.md) |
| 서랍 › 입력 | NoseAxisBox "기수축" — 자동(형상 제안) · ±X · ±Y · ±Z | — | MainWindow.axaml:899–900, MainWindow.axaml.cs:109 | [10](10-fusion-blender.md) |
| 서랍 › MC | "Monte Carlo" 소제목 · ? HelpKeys.MonteCarlo | — | MainWindow.axaml:911–912 | [07](07-monte-carlo-credibility.md) |
| 서랍 › MC | RunsBox "GPU 실행 수" (100000) | — | MainWindow.axaml:916–917 | [07](07-monte-carlo-credibility.md) |
| 서랍 › MC | FullRunsBox "6DOF 실행 수" (100) | — | MainWindow.axaml:918–919 | [07](07-monte-carlo-credibility.md) |
| 서랍 › MC | GpuMcBtn "GPU MC" | — | MainWindow.axaml:922 | [07](07-monte-carlo-credibility.md) |
| 서랍 › MC | CpuMcBtn "6DOF MC" | Esc 취소 | MainWindow.axaml:928 | [07](07-monte-carlo-credibility.md) |
| 서랍 › MC | 안내 문구 "결과는 3D 착탄점 · CEP 원 · 95 % 타원 …" | — | MainWindow.axaml:935 | [07](07-monte-carlo-credibility.md) |
| 서랍 › JSON | JsonBox (시나리오 전체) | — | MainWindow.axaml:951 | [06](06-scenario.md) |
| 서랍 › JSON | ApplyJsonBtn "JSON 적용 · 검증" · ? HelpKeys.Json | — | MainWindow.axaml:943–949, MainWindow.axaml.cs:850 | [06](06-scenario.md) |

## 6. 브라우저 (어셈블리) · 오른쪽 클릭 메뉴

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 브라우저 | BrowserTitle "BROWSER 어셈블리" · BrowserCollapseBtn 접기/펼치기 | — | MainWindow.axaml:504–508, MainWindow.Browser.cs:30 | [03](03-screen-layout.md) |
| 브라우저 | TreeExpandBtn "모두 펼치기" | — | MainWindow.axaml:511 | [03](03-screen-layout.md) |
| 브라우저 | TreeCollapseBtn "물체만 펼치기" | — | MainWindow.axaml:514 | [03](03-screen-layout.md) |
| 브라우저 | TreeShowAllBtn "모두 보기" | — | MainWindow.axaml:517 | [03](03-screen-layout.md) |
| 브라우저 | ? HelpKeys.Tree | — | MainWindow.axaml:520 | [03](03-screen-layout.md) |
| 브라우저 | AsmTree 행: 눈(TreeEye "보이기") · 종류 아이콘 · 이름 · 상태(수납/비행/착지) | — | MainWindow.axaml:538–551, ViewModels.cs:65 | [03](03-screen-layout.md) |
| 브라우저 | 클릭 = 선택 · 더블클릭 = 확대 | — | MainWindow.axaml.cs:285–290 | [03](03-screen-layout.md) |
| 브라우저 | BrowserGrip 폭 조절(더블클릭 = 280) · 높이 상한 | — | MainWindow.axaml:557, MainWindow.Browser.cs:37, :138 | [03](03-screen-layout.md) |
| 브라우저 | Ctrl+B 숨기기 · 보이기 | Ctrl+B | MainWindow.Browser.cs:19, :122 | [03](03-screen-layout.md), [13](13-shortcuts.md) |
| 브라우저 | 사용자 파라미터 · 조인트 노드(형상 아래, Fusion 내보내기 1.1) | — | src/Sim.Core/Engine/AssemblyTree.cs:78–91 | [10](10-fusion-blender.md) |
| 우클릭 | CmToggleShow "숨기기" / "보이기" | — | MainWindow.axaml:527, MainWindow.Browser.cs:88 | [03](03-screen-layout.md) |
| 우클릭 | CmIsolate "이것만 보기 (격리 — 하위 포함)" | — | MainWindow.axaml:528, MainWindow.Browser.cs:96 | [03](03-screen-layout.md) |
| 우클릭 | CmShowAll "모두 보기" | — | MainWindow.axaml:529 | [03](03-screen-layout.md) |
| 우클릭 | CmFocus "선택 물체로 확대" | C | MainWindow.axaml:531 | [03](03-screen-layout.md) |
| 우클릭 | CmFollow "추적" (체크) | T | MainWindow.axaml:532 | [03](03-screen-layout.md) |
| 우클릭 | CmDashboard "대시보드에서 보기 — <이름>" | — | MainWindow.axaml:534, MainWindow.Browser.cs:92 | [03](03-screen-layout.md) |

## 7. 3D 위 떠 있는 요소 · 재생 막대

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 3D | Overlay HUD "선택: … · t = …" / "카메라: 추적·자유 · 거리" | — | MainWindow.axaml:425, MainWindow.axaml.cs:758 | [03](03-screen-layout.md) |
| 카메라 막대 | FollowToggle "추적" | T | MainWindow.axaml:432 | [04](04-3d-view-camera-playback.md) |
| 카메라 막대 | ZoomInBtn "확대" · ZoomOutBtn "축소" | + · − | MainWindow.axaml:438, :441 | [04](04-3d-view-camera-playback.md) |
| 카메라 막대 | FocusBtn "선택 물체로" | C | MainWindow.axaml:444 | [04](04-3d-view-camera-playback.md) |
| 카메라 막대 | FitBtn "전체 보기" | F | MainWindow.axaml:447 | [04](04-3d-view-camera-playback.md) |
| 카메라 막대 | ResetCamBtn "카메라 초기화" | Home | MainWindow.axaml:450 | [04](04-3d-view-camera-playback.md) |
| 카메라 막대 | ? HelpKeys.View3D | — | MainWindow.axaml:453 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | ColorModeBox "색상" (면 색 표시 모드) | — | MainWindow.axaml:461 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | ColorMapBox "컬러맵" — Turbo · Viridis | — | MainWindow.axaml:462, src/Sim.Core/Structures/ColorScale.cs:18 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | ColorOptsBtn "옵션" 플라이아웃 "색상 옵션" | — | MainWindow.axaml:463–480 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | PeakHoldBox "최대값 유지 (전 구간)" | — | MainWindow.axaml:472, MainWindow.Analysis.cs:57 | [04](04-3d-view-camera-playback.md), [08](08-structural-impact.md) |
| 표시 막대 | SelectedOnlyBox "선택 물체만" | — | MainWindow.axaml:473 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | AutoRangeBox "범위 자동" · RangeMinBox "최소" · RangeMaxBox "최대" | — | MainWindow.axaml:474–477 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | ? HelpKeys.Color | — | MainWindow.axaml:483 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | SplitBox "화면" — 1 화면 · 2 분할 · 4 분할 | — | MainWindow.axaml:488, MainWindow.Split.cs:70 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | CamSyncBox "카메라 동기" (분할일 때만) | — | MainWindow.axaml:489, MainWindow.Split.cs:94 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | SectionBox "단면(내부)" | — | MainWindow.axaml:490, MainWindow.Shock.cs:249 | [04](04-3d-view-camera-playback.md) |
| 표시 막대 | ? HelpKeys.Split | — | MainWindow.axaml:491 | [04](04-3d-view-camera-playback.md) |
| 분할 | 화면별 색 모드 콤보(왼쪽 아래) · 화면별 레전드 | — | MainWindow.Split.cs:27–69 | [04](04-3d-view-camera-playback.md) |
| 레전드 | ColorLegend — 제목 · 단위 · 범위 · 출처 각주 · 보조 줄 | — | Controls/ColorLegend.cs, MainWindow.Analysis.cs:314 | [04](04-3d-view-camera-playback.md) |
| 재생 막대 | PlayBtn "재생 / 일시정지" | Space | MainWindow.axaml:574 | [04](04-3d-view-camera-playback.md), [02](02-quick-start.md) |
| 재생 막대 | PrevEventBtn "이전 이벤트" · NextEventBtn "다음 이벤트" | PageUp · PageDown | MainWindow.axaml:577, :580 | [04](04-3d-view-camera-playback.md) |
| 재생 막대 | RateBox "재생 속도" — 0.001× ~ 50× | — | MainWindow.axaml:583, MainWindow.axaml.cs:105 | [04](04-3d-view-camera-playback.md) |
| 재생 막대 | TimeText "t = … s" | — | MainWindow.axaml:584 | [04](04-3d-view-camera-playback.md) |
| 재생 막대 | TimeSlider | — | MainWindow.axaml:586 | [04](04-3d-view-camera-playback.md) |
| 재생 막대 | ? HelpKeys.Playback | — | MainWindow.axaml:585 | [04](04-3d-view-camera-playback.md) |
| 다시 열기 | BottomFab "그래프 · 로그 열기" · DashFab "대시보드 열기" | — | MainWindow.axaml:592, :595 | [03](03-screen-layout.md) |

## 8. 그래프 · 로그 서랍

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 아래 서랍 | 머리 "그래프 · 로그" · PlotToggle "그래프" · LogToggle "로그" | — | MainWindow.axaml:609–613 | [03](03-screen-layout.md) |
| 아래 서랍 | BottomPinBtn "아래 서랍 고정" · BottomCloseBtn "그래프 · 로그 닫기" | — | MainWindow.axaml:616, :619 | [03](03-screen-layout.md) |
| 아래 서랍 | BottomGrip 높이 조절(더블클릭 = 260) | — | MainWindow.axaml:670 | [03](03-screen-layout.md) |
| 그래프 | PlotSel — 고도·속도 · 자세(롤·피치·요) · 하중(G)·대기속도 · MC 착탄 산포 | — | MainWindow.axaml:632, MainWindow.axaml.cs:107 | [03](03-screen-layout.md), [07](07-monte-carlo-credibility.md) |
| 그래프 | Plot — 클릭 · ←/→(Shift ×10) · 휠 확대 · 오른쪽 드래그 이동 · 더블클릭 전체 | ← → | MainWindow.axaml:634, Controls/PlotView.cs:112, MainWindow.axaml.cs:1403 | [03](03-screen-layout.md), [13](13-shortcuts.md) |
| 그래프 | ? HelpKeys.Plot | — | MainWindow.axaml:631 | [03](03-screen-layout.md) |
| 로그 | LogLevelBox — "전체" / "경고 · 오류" / "오류만" | — | MainWindow.axaml:644, MainWindow.axaml.cs:111 | [03](03-screen-layout.md) |
| 로그 | LogSearch "검색" | — | MainWindow.axaml:645 | [03](03-screen-layout.md) |
| 로그 | LogAutoScroll "자동 스크롤" | — | MainWindow.axaml:646 | [03](03-screen-layout.md) |
| 로그 | LogCopyBtn "복사" | — | MainWindow.axaml:647 | [03](03-screen-layout.md), [11](11-results.md) |
| 로그 | LogSaveBtn "로그 저장" | — | MainWindow.axaml:650 | [11](11-results.md) |
| 로그 | LogClearBtn "지우기" | — | MainWindow.axaml:653 | [03](03-screen-layout.md) |
| 로그 | LogList · LogFileText "파일: results/logs/ui_….log" | — | MainWindow.axaml:658–659, MainWindow.axaml.cs:305 | [03](03-screen-layout.md), [11](11-results.md) |
| 로그 | ? HelpKeys.Log | — | MainWindow.axaml:642 | [03](03-screen-layout.md) |

## 9. 대시보드

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 대시보드 | 머리 "대시보드" · DashPinBtn "대시보드 고정" · DashCloseBtn "대시보드 닫기" | — | MainWindow.axaml:681–686 | [03](03-screen-layout.md) |
| 대시보드 | DashGrip 폭 조절(280–620, 더블클릭 = 340) | — | MainWindow.axaml:788, MainWindow.Shell.cs:279 | [03](03-screen-layout.md) |
| 대시보드 | 머리 카드 DashBody · DashModeChip(비행·착지·수납·운반체) · DashPhase | — | MainWindow.axaml:697–702, MainWindow.Dashboard.cs:129–141 | [03](03-screen-layout.md) |
| 대시보드 | ? HelpKeys.Dashboard | — | MainWindow.axaml:701 | [03](03-screen-layout.md) |
| 대시보드 | TileGrid KPI 12개 — 고도 AGL · 속도 · 마하 · 하강률 · 대기속도 · 동압 · 하중 · 최대 하중(누적) · 각속도 \|ω\| · Roll · Pitch · Yaw | — | MainWindow.axaml:705, MainWindow.Dashboard.cs:24–30 | [03](03-screen-layout.md) |
| 대시보드 | DeviceCard "장치 · 연결" (낙하산 · 로터 · 펄스 · 테더 · 힌지) · ? HelpKeys.Devices | — | MainWindow.axaml:706–712, MainWindow.Dashboard.cs:63 | [03](03-screen-layout.md) |
| 대시보드 | ImpactCard "착지 · 착탄점" · ? HelpKeys.Impact | — | MainWindow.axaml:715–721, MainWindow.Dashboard.cs:171 | [03](03-screen-layout.md) |
| 대시보드 | "구조 · 재질 (근사)" StructText · ? HelpKeys.Structure | — | MainWindow.axaml:724–730, MainWindow.Dashboard.cs:183 | [03](03-screen-layout.md), [08](08-structural-impact.md) |
| 대시보드 | CredCard "결과 신뢰도 (7009A · 90 % 구간)" · ? HelpKeys.Credibility | — | MainWindow.axaml:733–741, MainWindow.Credibility.cs:44 | [07](07-monte-carlo-credibility.md) |
| 대시보드 | Expander "이벤트" EventsList · ? HelpKeys.Events | — | MainWindow.axaml:743–765, MainWindow.axaml.cs:296 | [03](03-screen-layout.md), [04](04-3d-view-camera-playback.md) |
| 대시보드 | Expander "결과 요약" SummaryPanel(실행 · 착지 · 그 밖의 물체 · 낙하산 · 테더 · 힌지 · 충돌 판정 · 지표 · 경고) · ? HelpKeys.Summary | — | MainWindow.axaml:766–784, MainWindow.Summary.cs:77–169 | [03](03-screen-layout.md) |
| 대시보드 | SummaryCopyBtn "요약 텍스트 복사" | — | MainWindow.axaml:775 | [11](11-results.md) |

## 10. 단축키 · 마우스 (KeyDown 처리)

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 키 | 열린 ? 말풍선 닫기 → 작업 취소 → 겹친 서랍 닫기 | Esc | MainWindow.axaml.cs:1371, :1381–1383 | [13](13-shortcuts.md) |
| 키 | 실행 | F5 | MainWindow.axaml.cs:1379 | [13](13-shortcuts.md) |
| 키 | 도움말 | F1 | MainWindow.axaml.cs:1380 | [13](13-shortcuts.md) |
| 키 | 시나리오 열기 · 모델 가져오기 · 결과 저장 · 결과 불러오기 | Ctrl+O · Ctrl+I · Ctrl+S · Ctrl+L | MainWindow.axaml.cs:1384–1387 | [13](13-shortcuts.md) |
| 키 | 구조 분석 · CalculiX FEA | F6 · F7 | MainWindow.axaml.cs:1388–1389 | [13](13-shortcuts.md) |
| 키 | GPU 가상 풍동 · 풍동 보기 | Shift+F8 · F8 | MainWindow.axaml.cs:1390–1391 | [13](13-shortcuts.md), [09](09-flow-wind-tunnel.md) |
| 키 | 보기 기본값 | Ctrl+0 | MainWindow.axaml.cs:1392 | [13](13-shortcuts.md) |
| 키 | 브라우저 숨기기 · 보이기 | Ctrl+B | MainWindow.axaml.cs:1393 | [13](13-shortcuts.md) |
| 키 | 처음으로 | Ctrl+Home | MainWindow.axaml.cs:1394 | [13](13-shortcuts.md) |
| 키 | 다음 · 이전 충격 창 | Ctrl+] · Ctrl+[ | MainWindow.axaml.cs:1395–1396 | [13](13-shortcuts.md), [08](08-structural-impact.md) |
| 키 | 제자리 시점 회전(2°) | Ctrl+화살표 | MainWindow.axaml.cs:1398–1401 | [13](13-shortcuts.md), [04](04-3d-view-camera-playback.md) |
| 키 | 그래프 포커스 시각 이동 | ← → (Shift ×10) | MainWindow.axaml.cs:1403 | [13](13-shortcuts.md) |
| 키 | 추적 · 선택 물체로 · 전체 보기 | T · C · F | MainWindow.axaml.cs:1440–1442 | [13](13-shortcuts.md), [04](04-3d-view-camera-playback.md) |
| 키 | 확대 · 축소 | + · − | MainWindow.axaml.cs:1443–1444 | [13](13-shortcuts.md) |
| 키 | 시점 프리셋 위 · 정면 · 측면 · 등각 | 1 · 2 · 3 · 4 | MainWindow.axaml.cs:1445–1448 | [13](13-shortcuts.md) |
| 키 | 이전 · 다음 프레임 | , · . | MainWindow.axaml.cs:1449–1450 | [13](13-shortcuts.md) |
| 키 | 공간 이동(누르는 동안) 전진 · 후진 · 좌 · 우 · 상승 · 하강 | W · S · A · D · E · Q | MainWindow.axaml.cs:1419–1426, :1453 | [13](13-shortcuts.md), [04](04-3d-view-camera-playback.md) |
| 키 | 재생 / 일시정지 · 카메라 초기화 | Space · Home | MainWindow.axaml.cs:1456–1457 | [13](13-shortcuts.md) |
| 키 | 이전 · 다음 이벤트 | PageUp · PageDown | MainWindow.axaml.cs:1458–1459 | [13](13-shortcuts.md) |
| 키 | 화면 평면 이동 · 궤도 회전 | 화살표 · Shift+화살표 | MainWindow.axaml.cs:1460–1463 | [13](13-shortcuts.md) |
| 마우스 | 왼쪽 드래그 궤도 · Ctrl+드래그 시점 · 오른쪽/가운데/Shift 드래그 이동 · 휠 확대 · Ctrl+휠 전진 · 클릭 선택 · 더블클릭 확대/전체 | — | Rendering/GlView.cs:419–468, HelpContent.cs (Guide.Mouse) | [04](04-3d-view-camera-playback.md), [13](13-shortcuts.md) |
| 끌어다 놓기 | .mnsr → 불러오기 · .json → 시나리오 · .stl/.massprops.json → 가져오기 | — | MainWindow.Analysis.cs:84–93 | [03](03-screen-layout.md) |
| 셸 | 패널 고정/겹침 · 0.2 s 애니메이션 · 3D 클릭 시 겹친 서랍 닫힘 | — | MainWindow.Shell.cs:99–107, :254 | [03](03-screen-layout.md) |
| 셸 | 창 하나만(두 번째 실행은 종료) | — | Program.cs:23, SingleInstance.cs | [01](01-install-update.md) |

## 11. 보조 창 · 대화상자

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 도움말 창 | "도움말 — MnS 시뮬레이터" · 검색 "검색 (예: 휠, F6, 분할, 테마)" · 목차 · 장 카드 | Ctrl+F · Esc | HelpWindow.cs:24–66 | [13](13-shortcuts.md) |
| 도움말 창 | 장(Guide.*) 24개 — 시작하기 … 단축키 전체 · 업데이트 · 설치 · 정보 | — | HelpContent.cs:12–35 | [13](13-shortcuts.md) |
| 섹션 ? | HelpTip 말풍선 · '전체 도움말에서 보기' (App.* 키) | Esc | Help/HelpTip.cs, Help/HelpKeys.cs, Help/AppHelp.cs | [03](03-screen-layout.md) |
| 시나리오 설명 | InfoWindow (이름 · 경로 · description) | Esc | MainWindow.axaml.cs:1513, HelpWindow.cs:148 | [06](06-scenario.md) |
| 재질 | "재질 라이브러리" 창(재질 19종) | Esc | MainWindow.Analysis.cs:575–595, src/Sim.Core/Structures/materials.json | [08](08-structural-impact.md) |
| 신뢰도 | "결과 신뢰도 — NASA-STD-7009A" 창(요인 · 사용 범위 · 시험 우선순위) | Esc | MainWindow.Credibility.cs:90–121 | [07](07-monte-carlo-credibility.md) |
| CalculiX 옵션 | "정적/진동/좌굴 FEA — CalculiX" 창 — 해석 · 하중 순간 · 대표 G · 방향 · 반경 · 요소 크기 · 메셔 · 비선형 · [실행] [취소] | Enter · Esc | MainWindow.Fea.cs:167–229 | [08](08-structural-impact.md) |
| 업데이트 | "업데이트 — Open M&S x.y.z" — [지금 업데이트] [나중에] [이 버전 건너뛰기] · 릴리스 노트 · 진행 | — | MainWindow.Update.cs:112–127 | [01](01-install-update.md) |
| 업데이트 | "업데이트 확인" 결과 상자 · "정보 — Open M&S" | — | MainWindow.Update.cs:79, :200 | [01](01-install-update.md) |
| 업데이트 | 자동 확인(시작 5 s 뒤 · 하루 1회 · 개발 빌드 제외) · SHA-256 검증 · /SILENT 설치 후 재실행 | — | MainWindow.Update.cs:25, :64, Update/UpdateChecker.cs | [01](01-install-update.md) |
| 풍동 보기 | 창 "풍동 보기 — CFD 표면 압력 · 3-D 유선" — 물체 · 조건 · 받음각 · 표면(Cp · 속도비 · Cf) · 표면 색 · 유선(속도 크기 색 · 단색 노랑 · 끔) · 입자 흐름 · 컬러맵 · 전체 보기 · ? | ← → , . Space F Esc | WindTunnelWindow.cs:65–127, src/Sim.Core/Models/WindTunnel.cs:17 | [09](09-flow-wind-tunnel.md) |
| 풍동 보기 | 한계 문구 · 레전드 · 유선 속력 레전드 | — | WindTunnelWindow.cs:29, :43–44 | [09](09-flow-wind-tunnel.md) |
| GPU 가상 풍동 | 창 "GPU 가상 풍동 — XLB 격자 볼츠만 실시간 흐름 (시각화 근사)" — 모드(GPU 실시간 · OpenFOAM 재생) · 시퀀스 · 물체 · 받음각 α · 옆미끄럼 β · 유속 · 계산 Re · 격자(저 · 중 · 고) | ← → , . | AirflowWindow.cs:104–148 | [09](09-flow-wind-tunnel.md) |
| GPU 가상 풍동 | 단면(XZ 피치면 · XY 요잉면) · 위치 · 색(속도 · 와도 · 단면 끔) · 컬러맵 · 연기 입자 · 유선 · 일시정지 · 다시 시작 · 전체 보기 · ? | Space F Esc | AirflowWindow.cs:126–150, :201–208 | [09](09-flow-wind-tunnel.md) |
| GPU 가상 풍동 | 계수 상자 · "신뢰 등급: 시각화(근사)" · 한계 문구 | — | AirflowWindow.cs:155–171, src/Sim.Core/Airflow/AirflowFrame.cs:132–140 | [09](09-flow-wind-tunnel.md) |

## 12. 앱 실행 인자 · 스모크 옵션

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 앱 인자 | `--real-models DIR` · `--synthetic` | — | Program.cs:17–19 | [14](14-headless-smoke.md), [12](12-cli.md) |
| 앱 인자 | `--opening-shock-report <scenario.json>` (+ `--variant` `--runs` `--ccx` `--install` `--no-sweep` `--out` `--raw`) | — | Program.cs:21, HeadlessReports.cs:15–41 | [12](12-cli.md) |
| 앱 인자 | `--theme dark\|light` | — | Theme/ThemeService.cs:31 | [14](14-headless-smoke.md) |
| 스모크 | `--smoke [시나리오\|.mnsr]` | — | MainWindow.axaml.cs:97, :1677 | [14](14-headless-smoke.md) |
| 스모크 | `--size WxH` | — | MainWindow.axaml.cs:1681 | [14](14-headless-smoke.md) |
| 스모크 | `--import 파일` | — | MainWindow.axaml.cs:1694 | [14](14-headless-smoke.md), [10](10-fusion-blender.md) |
| 스모크 | `--unpin dash,bottom` | — | MainWindow.axaml.cs:1702 | [14](14-headless-smoke.md) |
| 스모크 | `--pin-left` | — | MainWindow.axaml.cs:1707 | [14](14-headless-smoke.md) |
| 스모크 | `--dash closed` · `--bottom closed` | — | MainWindow.axaml.cs:1708–1709 | [14](14-headless-smoke.md) |
| 스모크 | `--drawer input\|mc\|json` | — | MainWindow.axaml.cs:1710 | [14](14-headless-smoke.md) |
| 스모크 | `--browser collapsed\|hidden` | — | MainWindow.axaml.cs:1722 | [14](14-headless-smoke.md) |
| 스모크 | `--hide 이름,…` | — | MainWindow.axaml.cs:1728 | [14](14-headless-smoke.md) |
| 스모크 | `--tree-expand` | — | MainWindow.axaml.cs:1731 | [14](14-headless-smoke.md) |
| 스모크 | `--structural` | — | MainWindow.axaml.cs:1734 | [14](14-headless-smoke.md) |
| 스모크 | `--fea 물체` | — | MainWindow.axaml.cs:1741 | [14](14-headless-smoke.md) |
| 스모크 | `--ccx 물체` · `--ccx-analysis` · `--ccx-g` · `--ccx-dir` · `--ccx-mesher` | — | MainWindow.axaml.cs:1747–1753 | [14](14-headless-smoke.md) |
| 스모크 | `--all` · `--peak` · `--color 키` | — | MainWindow.axaml.cs:1757–1759 | [14](14-headless-smoke.md) |
| 스모크 | `--modes 키,…` · `--split 1\|2\|4` · `--unsync` · `--flowlines` | — | MainWindow.axaml.cs:1761–1769 | [14](14-headless-smoke.md) |
| 스모크 | `--save 파일` · `--reload` | — | MainWindow.axaml.cs:1770–1777 | [14](14-headless-smoke.md) |
| 스모크 | `--env` · `--env-file` · `--env-rot` · `--env-bright` · `--env-ev` · `--env-bg` · `--env-refl` · `--env-offset` · `--env-off` | — | MainWindow.axaml.cs:1782–1797 | [14](14-headless-smoke.md), [05](05-scene-environment.md) |
| 스모크 | `--at s` → view.png · window.png · browser_zoom.png | — | MainWindow.axaml.cs:1800–1830 | [14](14-headless-smoke.md) |
| 스모크 | `--select 물체` · `--dist m` · `--section` | — | MainWindow.axaml.cs:1805–1808 | [14](14-headless-smoke.md) |
| 스모크 | `--ccxshock` | — | MainWindow.axaml.cs:1812 | [14](14-headless-smoke.md) |
| 스모크 | `--shot-name 이름` | — | MainWindow.axaml.cs:1825 | [14](14-headless-smoke.md) |
| 스모크 | `--browser-menu` → window_menu.png · browser_menu_zoom.png | — | MainWindow.axaml.cs:1832, MainWindow.Browser.cs:195 | [14](14-headless-smoke.md) |
| 스모크 | `--gpumc` | — | MainWindow.axaml.cs:1839 | [14](14-headless-smoke.md) |
| 스모크 | `--helpshot` → callout_*.png · help*.png · scenario_info.png · dialog_*.png | — | MainWindow.axaml.cs:1858, :1577 | [14](14-headless-smoke.md) |
| 스모크 | `--shellshots` → shell_*.png | — | MainWindow.axaml.cs:1859, MainWindow.Shell.cs:384 | [14](14-headless-smoke.md) |
| 스모크 | `--updateshot` → update_*.png | — | MainWindow.axaml.cs:1860, MainWindow.Update.cs:204 | [14](14-headless-smoke.md), [01](01-install-update.md) |
| 스모크 | `--theme-toggle` → window_toggled.png | — | MainWindow.axaml.cs:1862 | [14](14-headless-smoke.md) |
| 스모크 | `--theme` 사본 → results/smoke/theme_<테마>/ | — | MainWindow.axaml.cs:1873 | [14](14-headless-smoke.md) |
| 스모크 | `--windtunnel α` · `--wt-field cp\|speed\|cf` → windtunnel.png | — | MainWindow.axaml.cs:1882–1884 | [14](14-headless-smoke.md), [09](09-flow-wind-tunnel.md) |
| 스모크 | `--airflow α` · `--airflow-beta` · `--airflow-field` · `--airflow-res` · `--airflow-frames` · `--airflow-wait` → airflow.png | — | MainWindow.axaml.cs:1890–1896 | [14](14-headless-smoke.md), [09](09-flow-wind-tunnel.md) |
| 스모크 | `--shot` (끝 0.5 s 대기) · smoke.log · 종료 코드 3/4 | — | MainWindow.axaml.cs:1916 | [14](14-headless-smoke.md) |

## 13. 명령줄 `mns`

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| CLI | `mns` · `help` · `-h` · `--help` (사용법) | — | src/Sim.Cli/Program.cs:16–70 | [12](12-cli.md) |
| CLI | 공통 `--real-models DIR` · `--synthetic` | — | src/Sim.Cli/Program.cs:68, :94 | [12](12-cli.md) |
| CLI | `run` (`--out` `--dt` `--tend` `--archive` `--structural` `--loadcases`) | — | src/Sim.Cli/Program.cs:21, :141 | [12](12-cli.md) |
| CLI | `open` | — | src/Sim.Cli/Program.cs:25, :185 | [12](12-cli.md), [11](11-results.md) |
| CLI | `fea` (`--body` `--out` `--deck-only` `--install` `--timeout`) | — | src/Sim.Cli/Program.cs:26, :195 | [12](12-cli.md), [08](08-structural-impact.md) |
| CLI | `fea-taylor` (`--material` `--speed` `--out` `--deck-only` `--install`) | — | src/Sim.Cli/Program.cs:28, :216 | [12](12-cli.md) |
| CLI | `ccx` (`--body` `--analysis` `--nonlinear` `--case` `--time` `--loadcases` `--g` `--dir` `--at` `--patch` `--size` `--mesher` `--cad` `--cad-frame` `--converge` `--out` `--archive` `--install` `--deck-only`) | — | src/Sim.Cli/Program.cs:30–34, src/Sim.Cli/CcxCommand.cs:15–135 | [12](12-cli.md), [08](08-structural-impact.md) |
| CLI | `simpkg` (main: `--step-dir` `--out` `--run` `--install`) | — | src/Sim.Cli/Program.cs:35, src/Sim.Cli/SimPkgCommand.cs | [12](12-cli.md), [10](10-fusion-blender.md) |
| CLI | `simpkg` 추가 옵션 `--modal` `--modes` `--converge` `--converge-top` `--converge-global` `--mesh-max` `--mesh-min` `--mesh-thick` `--fusion-report` `--fusion-legend` `--design-results` `--design-case` `--threads` | — | 브랜치 a6b961e: src/Sim.Cli/SimPkgCommand.cs · Program.cs | [10](10-fusion-blender.md), [12](12-cli.md) |
| CLI | `ccx-verify` (`--out` `--install`) | — | src/Sim.Cli/Program.cs:38, :237 | [12](12-cli.md) |
| CLI | `ccx-mesh` (`--size` `--order` `--out`) | — | src/Sim.Cli/Program.cs:39, :253 | [12](12-cli.md) |
| CLI | `materials` | — | src/Sim.Cli/Program.cs:40, :264 | [12](12-cli.md), [08](08-structural-impact.md) |
| CLI | `adversarial` (`--out`) | — | src/Sim.Cli/Program.cs:41, :270 | [12](12-cli.md) |
| CLI | `mc` (`--runs` `--body` `--seed` `--sampling` `--out`) | — | src/Sim.Cli/Program.cs:42, :281 | [12](12-cli.md), [07](07-monte-carlo-credibility.md) |
| CLI | `gpumc` (`--runs` `--cpu` `--seed` `--out`) | — | src/Sim.Cli/Program.cs:44, :302 | [12](12-cli.md), [07](07-monte-carlo-credibility.md) |
| CLI | `massprops` (`--mass` `--density` `--unit` `--fusion` `--out`) | — | src/Sim.Cli/Program.cs:46, :328 | [12](12-cli.md) |
| CLI | `validate` | — | src/Sim.Cli/Program.cs:48, :351 | [12](12-cli.md), [06](06-scenario.md) |
| CLI | `validate-public` (`CASE_ROOT` `--out`) | — | src/Sim.Cli/Program.cs:49, :372 | [12](12-cli.md) |
| CLI | `scenario-report` (`--variant` `--runs` `--ccx` `--install` `--out` `--raw`) | — | src/Sim.Cli/Program.cs:51, :392 | [12](12-cli.md) |
| CLI | `opening-shock` (`--variant` `--runs` `--ccx` `--install` `--no-sweep` `--out` `--raw`) | — | src/Sim.Cli/Program.cs:53, :408 | [12](12-cli.md) |
| CLI | `credibility` (`--runs` `--out` `--raw` `--fea-gci` `--loads`) | — | src/Sim.Cli/Program.cs:56, :426 | [12](12-cli.md), [07](07-monte-carlo-credibility.md) |
| CLI | `tumble-study` (`--runs` `--seed` `--aero-db` `--damping` `--no-sweeps` `--no-maps` `--out`) | — | src/Sim.Cli/Program.cs:59, :448 | [12](12-cli.md) |
| CLI | `tumble-study --fatigue-simpkg summary.json` (진동 피로 근사) | — | 브랜치 a6b961e: src/Sim.Cli/Program.cs · src/Sim.Core/Structures/VibrationFatigue.cs | [08](08-structural-impact.md), [12](12-cli.md) |
| CLI | `container-material` (`--scenario` `--envelope` `--ccx` `--ccx-jobs` `--thickness-mm` `--mesh-theta` `--mesh-length` `--install` `--out`) | — | src/Sim.Cli/Program.cs:61, :469 | [12](12-cli.md) |
| CLI | `loads` (`--runs` `--seed` `--ccx` `--ccx-threads` `--no-aero` `--parts` `--envelope` `--sweep` `--raw` `--out`) | — | src/Sim.Cli/Program.cs:63, :495 | [12](12-cli.md) |
| CLI | `aero-compare` (`--db` `--body` `--out`) — 사용법 문자열에 없음 | — | src/Sim.Cli/Program.cs:523 | [12](12-cli.md) |
| CLI | `gpuinfo` | — | src/Sim.Cli/Program.cs:66, :538 | [12](12-cli.md) |

## 14. 설치 · 배포 · 외부 도구 · 데모

| 영역 | 항목(코드의 실제 이름·헤더) | 단축키 | 원천(파일:줄 또는 브랜치) | 매뉴얼 장 |
| --- | --- | --- | --- | --- |
| 설치 | Inno Setup `OpenMS-Setup-x.y.z.exe` — `%LOCALAPPDATA%\Programs\OpenMS` · 관리자 권한 없음 · 한국어 · 시작 메뉴 'Open M&S' · 바탕 화면 바로 가기(선택) · 'Open M&S 실행' | — | installer/OpenMS.iss | [01](01-install-update.md) |
| 설치 | 제거 — 앱 종료 · results\logs · results\smoke 삭제 | — | installer/OpenMS.iss | [01](01-install-update.md) |
| 배포 | `scripts/build-installer.ps1 -Version x.y.z [-SkipPublish]` | — | scripts/build-installer.ps1 | [01](01-install-update.md) |
| 배포 | CI — build · test · trx 올리기 | — | .github/workflows/ci.yml | [01](01-install-update.md) |
| 배포 | 릴리스 — 태그 v* → 설치 프로그램 · zip · .sha256 · GitHub 릴리스 | — | .github/workflows/release.yml | [01](01-install-update.md) |
| 배포 | 버전 `0.0.0` = 개발 빌드(업데이트 확인 안 함) | — | Directory.Build.props | [01](01-install-update.md) |
| 설정 | `%APPDATA%\MnS.Simulator\settings.json` — 테마 · `scene` · `update` | — | Theme/ThemeStore.cs, Update/UpdateChecker.cs | [01](01-install-update.md), [03](03-screen-layout.md), [05](05-scene-environment.md) |
| Fusion | 스크립트 MnS_ExportForSim 1.1 — 폴더 · 이름 · STEP? · USD? 대화 | — | tools/fusion/MnS_ExportForSim/MnS_ExportForSim.py | [10](10-fusion-blender.md) |
| Fusion | 출력 `.stl` · `.massprops.json`(재료 물성 · 외관 · 메시 · 정확도 · 사용자 파라미터 · 조인트) · `.step` · `.usdz` | — | tools/fusion/MnS_ExportForSim/MnS_ExportForSim.py | [10](10-fusion-blender.md) |
| Fusion | `export_options.json` — `mesh` · `massAccuracy` · `usd` | — | tools/fusion/MnS_ExportForSim/MnS_ExportForSim.py (DEFAULT_OPTIONS) | [10](10-fusion-blender.md) |
| Fusion | MnS_ExportGroups — 묶음 정의 JSON · groups_summary.json | — | tools/fusion/MnS_ExportGroups/MnS_ExportGroups.py | [10](10-fusion-blender.md) |
| Fusion | Fusion 재료 물성 우선 · 외관 색 · 대시보드 "(Fusion 재료 물성)" | — | src/Sim.Core/Structures/Materials.cs, StructureDef.cs, MainWindow.Dashboard.cs | [08](08-structural-impact.md), [10](10-fusion-blender.md) |
| Blender | `import_mns_result.py -- <결과폴더> [--fps] [--scale] [--usd]` | — | tools/blender/import_mns_result.py:4, :212 | [10](10-fusion-blender.md) |
| 결과 | `.mnsr` 구성(result.json · scenario(.resolved).json · frames.bin · summary · events · timeseries · devices · scene · structural · mc · fea) | — | src/Sim.Core/Analysis/ResultArchive.cs:31–60 | [11](11-results.md) |
| GPU 풍동 | 서버 `tools/airflow/server.py` · venv 설치 · `MNS_AIRFLOW_PYTHON` | — | tools/airflow/README.md | [09](09-flow-wind-tunnel.md) |
| CFD | `tools/cfd` — run_cfd.py · foamcase.py · post.py · flowviz.py · flowseq.py · flowseq_replay.py | — | tools/cfd/README.md | [09](09-flow-wind-tunnel.md) |
| CFD | 유동 영역 규칙 `tools/flowdomain.py` — 확장 · 기준 길이 · 최소 셀 · 크기/개수 유지 · 막힘 비율 5 % | — | tools/flowdomain.py:1–60 | [09](09-flow-wind-tunnel.md) |
| 외부 FEA | CalculiX 위치 `CCX_PATH` · Gmsh · `MNS_PYTHON` · OpenCourant `OPENCOURANT_PATH` | — | src/Sim.Core/Fea/CalculiX.cs:11–28, src/Sim.Core/Fea/OpenCourant.cs:15–32 | [08](08-structural-impact.md) |
| 환경 파일 | `mns.environment/1` 형식 · `soilMatch` | — | config/environments/README.md | [05](05-scene-environment.md) |
| 데모 | `demo_01_cargo_airdrop` | — | config/scenarios/demo_01_cargo_airdrop.json | [06](06-scenario.md) |
| 데모 | `demo_02_impact_materials` | — | config/scenarios/demo_02_impact_materials.json | [06](06-scenario.md) |
| 데모 | `demo_03_wind_dispersion` | — | config/scenarios/demo_03_wind_dispersion.json | [06](06-scenario.md) |
| 시나리오 | 스키마 문서 링크 | — | docs/scenario-schema.md | [06](06-scenario.md) |

## 대조 결과

대조 결과: 항목 369개 · 장에 연결 369개 · 누락 0
(표의 모든 행을 세고, '매뉴얼 장' 칸의 링크가 실제 장 파일을 가리키는지 확인한 값입니다.)
