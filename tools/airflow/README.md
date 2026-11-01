# tools/airflow — GPU 가상 풍동 서버 (XLB 격자 볼츠만)

앱의 해석 → GPU 가상 풍동(Shift+F8)이 이 서버를 별도 프로세스로 띄워 로컬 TCP 로 흐름 프레임을 받는다.
솔버 선택 근거·실측: `validation/reports/airflow/solver_selection.md`.

## 설치 (처음 한 번, 저장소 루트)
```
python -m venv tools/airflow/.venv
PYTHONUTF8=1 tools/airflow/.venv/Scripts/pip.exe install -r tools/airflow/requirements.txt
```
한국어 Windows(cp949)에서는 `PYTHONUTF8=1`(PowerShell: `$env:PYTHONUTF8=1`)이 필요하다 — requirements.txt 의 한국어 주석을 pip 가 cp949 로 읽다 `UnicodeDecodeError` 로 멈춘다.
가상환경은 git 밖(.gitignore `.venv/`). 다른 파이썬을 쓰려면 환경변수 `MNS_AIRFLOW_PYTHON`. NVIDIA GPU·드라이버 필요.

## 파일
| 파일 | 내용 |
|---|---|
| `lattice.py` | 순수 함수(numpy): 좌표(동체→풍동→격자), 격자 배치, Re·ω·마하, 복셀화, 입자·유선·단면(CPU 기준 구현), 계수 |
| `xlb_backend.py` | XLB(D3Q27 KBC, Warp CUDA) 설정·재복셀화·힘 + 후처리 Warp 커널(`GpuField`, `CpuField` 와 같은 결과 — GPU 시험) |
| `protocol.py` | IPC 형식 `mns.airflow-ipc/1`: `[u32 머리][u32 본문][JSON][배열]` (C# `Sim.Core/Airflow/AirflowProtocol.cs`) |
| `server.py` | TCP 서버 · 설정 반영(격자 재생성/재복셀화/ω) · 프레임 · 발산 시 Re 60 % 로 자동 재시작. `--selftest` = 해석적 가짜 장(시험 전용) |
| `tests/test_airflow.py` | GPU 없이 도는 시험. XLB 실행·GPU/CPU 일치 시험은 `MNS_AIRFLOW_GPU_TEST=1` 일 때만 |

## 영역 · 격자 규칙 (`tools/flowdomain.py`, OpenFOAM 과 공통)
물체 대신 외접구(지름 D, 경계 상자 중심)로 영역을 잡는다 — 받음각이 바뀌어도 dx 가 같아 다시 복셀화만 한다. 확장 단위 = D.
- 기본(`resolution` 프리셋 low · medium · high) = **개수 유지**: 셀 수 고정(`RESOLUTIONS`), 측면 0.69 D(외접구 = 측면의 42 %),
  상류 ≈ 1.3 D(물체 중심 x = 0.28·nx), 하류 = 나머지 — 이 규칙 도입 전과 같은 dx · 물체 위치.
- 설정 JSON 의 `domain`(선택, 앱은 보내지 않음 → 프리셋): `mode` size | count · `upstream` · `downstream` · `lateral` · `minCells`
  (확장마다 최소 셀) · size 는 `cellsPerLength`(D 당 셀) 또는 `cell`[m] · count 는 `shape` [nx,ny,nz] 또는 `cellBudget`(총 셀).
  빠진 키는 프리셋 값. 바꾸면 격자를 다시 만든다. 16 M 셀이 넘으면 경고.
- 막힘 비율 = 그 자세의 정면 투영(경계 상자, 실루엣 상한) / 풍동 단면 — 5 % 를 넘으면 'ready' 상태 문장에 경고, 프레임 `blockage`.

## 수동 실행
```
tools/airflow/.venv/Scripts/python.exe tools/airflow/server.py      # 표준출력 "AIRFLOW_PORT <n>"
```
앱 스모크: `MnS.Simulator.exe --smoke <시나리오> --airflow 45 [--airflow-field vort] [--airflow-res medium] --shot` → `results/smoke/airflow.png`.

## OpenFOAM 재생 시퀀스 (같은 화면의 'OpenFOAM 재생' 모드)
앱은 `results/cfd/**/sequence.json` · `config/aero_db/**/sequence.json`(schema `mns.flow-sequence/1`)을 찾는다.
`{"schema","name","solver","method","alpha","beta","speed","mach","frames":[{"t":초,"file":"frame_0001.mnsf"}]}` +
프레임 파일 = 위 IPC 와 같은 이진 메시지(type `frame`, 배열 `slice_speed`·`slice_vort`·`particles`·`lines`·`line_counts`,
머리 `slice{plane,origin,axisA,axisB}`, 좌표 = 풍동 좌표 m, 흐름 −X) — `protocol.pack(header, arrays)` 로 쓰면 된다.
형식이 다르면 C# `Sim.Core/Airflow/FlowSequence.cs` 한 곳(어댑터)만 고친다.
