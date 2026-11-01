"""운반 탄체 3분할 외피 분리 시나리오 생성기 (PRD V절, FR-SHL-01).

    python tools/scenario_gen/make_munition_split.py
      → tests/Sim.Core.Tests/scenarios/06_munition_3petal_split.json   (120° 절단 폭약 3분할 → 컨테이너 4개 노출)
      → tests/Sim.Core.Tests/scenarios/07_munition_tail_cut_aft_exit.json (대안: 꼬리날개만 절개, 1개씩 후방 이탈)

조건: 3분할 폭약 절개와 꼬리 절개 대안, 컨테이너 4개 직렬(□270×270×400 mm, 합계 50 kg 이하, 설계하중 50 G),
고도 1 km, 분할 시 약 M 0.5 — 모두 일반 예시값.
탄체 3D 모델이 없어 파라미터 모델이며 치수·질량·두께·폭약 Δv·충격 펄스는 모두 (가정) — 시나리오 description 에 표시.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT06 = ROOT / "tests" / "Sim.Core.Tests" / "scenarios" / "06_munition_3petal_split.json"
OUT07 = ROOT / "tests" / "Sim.Core.Tests" / "scenarios" / "07_munition_tail_cut_aft_exit.json"

# ---- ISA 1 km 에서 M 0.5 ----
ALT = 1000.0
MACH = 0.5
R_AIR, GAMMA = 287.05287, 1.4


def isa_speed_of_sound(h: float) -> float:
    t = 288.15 - 0.0065 * h
    return math.sqrt(GAMMA * R_AIR * t)


SPEED = round(MACH * isa_speed_of_sound(ALT), 2)  # ≈ 168.2 m/s

# ---- 탄체 파라미터 (가정) ----
R_OUT = 0.20          # 외경 0.40 m (□270 대각 382 mm + 벽)
SHELL_T = 0.003       # 외피 두께 3 mm (Al 7075-T6, 가정)
SHELL_L = 1.70        # 원통부 길이
RHO_7075 = 2810.0
NOSE_L, NOSE_M = 0.35, 6.0   # 신관·절개장치 연결부 (가정)
TAIL_L, TAIL_M = 0.30, 7.0   # 꼬리날개 조립체 (가정)
PETAL_ANGLES = [0.0, 120.0, 240.0]   # 조각 중앙 각(축 x 기준 +Y→+Z)
PETAL_DV = 12.0       # 절단 폭약이 조각 앞끝에 주는 방사 방향 속도 [m/s] (가정) — 후방 힌지 둘레 회전으로 부여
PETAL_JITTER = 0.0003  # 조각별 기폭 시각 편차 [s] (가정)
HINGE_RELEASE_DEG = 40.0
T_SPLIT = 0.5

# 컨테이너: □270×270×400 mm, 5.5 kg (가정)
BOX = [0.40, 0.27, 0.27]
BOX_M = 5.5
BOX_X = [0.6, 0.2, -0.2, -0.6]
DROGUE_DELAYS = [0.30, 0.50, 0.70, 0.90]   # 노출 후 드로그 전개 지연 (가정)
SHOCK_G, SHOCK_MS = 15.0, 10.0             # 컨테이너에 전달되는 절단 충격 펄스 (가정, FR-SEP-01 범위 10~20 G)

COLORS = {"nose": "#8c939c", "tail": "#6b7480", "petal": "#b8a27a", "box": "#4f8fd8"}


def petal_mass() -> float:
    ri = R_OUT - SHELL_T
    return round(RHO_7075 * (120 / 360) * math.pi * (R_OUT ** 2 - ri ** 2) * SHELL_L, 3)


def environment() -> dict:
    return {
        "wind": {"speed": 5, "directionFrom": 270, "refHeight": 10, "shear": "log", "roughness": 0.03},
        "turbulence": "light",
        "soil": "farmland",
    }


def container(k: int, parent: str, x: float, drogue_d0=0.5) -> dict:
    return {
        "name": f"c{k}", "parent": parent, "mount": [x, 0, 0],
        "mass": BOX_M,
        "shape": {"type": "box", "size": BOX},
        "aero": {"damping": [-3, -3, -3], "machCorrection": True},
        "structure": {"material": "al6061-t6", "thickness": 0.002},
        "color": COLORS["box"],
        "devices": [
            {"type": "parachute", "name": "drogue", "d0": drogue_d0, "cd0": 0.5, "attach": [-BOX[0] / 2, 0, 0],
             "riser": 3.0, "fillConstant": 6, "fillExponent": 2, "lineStretchTime": 0.15},
            {"type": "parachute", "name": "main", "d0": 1.22, "cd0": 2.2, "attach": [-BOX[0] / 2, 0, 0],
             "riser": 2.5, "fillConstant": 8, "fillExponent": 2, "lineStretchTime": 0.35},
            {"type": "pulse", "name": "shock", "peakG": SHOCK_G, "duration": SHOCK_MS / 1000, "direction": [0, 0, 1]},
        ],
    }


def chute_events(k: int, after: str, drogue_delay: float) -> list:
    return [
        {"name": f"c{k}_drogue", "when": {"type": "after", "event": after, "delay": drogue_delay},
         "do": [{"type": "deploy", "device": f"c{k}.drogue"}]},
        {"name": f"c{k}_main", "when": {"type": "or", "any": [
            {"type": "and", "all": [{"type": "after", "event": f"c{k}_drogue", "delay": 1.0},
                                    {"type": "speed", "body": f"c{k}", "below": 40}]},
            {"type": "after", "event": f"c{k}_drogue", "delay": 10.0}]},
         "do": [{"type": "release", "device": f"c{k}.drogue"}, {"type": "deploy", "device": f"c{k}.main"}]},
    ]


def common_metrics(debris: list) -> list:
    boxes = [f"c{k}" for k in range(1, 5)]
    return [
        {"type": "maxLoad", "name": "containerPeakG", "bodies": boxes, "limitG": 50},
        {"type": "debrisNearDeploy", "name": "debrisAtDrogue", "devices": [f"{b}.drogue" for b in boxes],
         "debris": debris, "window": 3.0, "riskDistance": 5.0},
        {"type": "landingSeparation", "name": "containerLandingSeparation", "bodies": boxes, "target": 70},
        {"type": "minSeparation", "name": "debrisContainerClearance", "bodies": boxes + debris, "ignoreAfterSeparation": 0.2},
    ]


def build_split() -> dict:
    pm = petal_mass()
    bodies = [
        {"name": "nose", "parent": "munition", "mount": [SHELL_L / 2 + NOSE_L / 2, 0, 0], "mass": NOSE_M,
         "shape": {"type": "cylinder", "radius": R_OUT, "length": NOSE_L, "axis": "x"},
         "aero": {"damping": [-1, -4, -4], "machCorrection": True},
         "structure": {"material": "al7075-t6", "thickness": 0.004}, "color": COLORS["nose"]},
        {"name": "tail", "parent": "munition", "mount": [-SHELL_L / 2 - TAIL_L / 2, 0, 0], "mass": TAIL_M,
         "shape": {"type": "cylinder", "radius": R_OUT, "length": TAIL_L, "axis": "x"},
         "aero": {"damping": [-1, -4, -4], "machCorrection": True},
         "structure": {"material": "al7075-t6", "thickness": 0.004}, "color": COLORS["tail"]},
    ]
    connectors, events = [], []
    r_mid = R_OUT - SHELL_T / 2
    for i, ang in enumerate(PETAL_ANGLES, start=1):
        a = math.radians(ang)
        bodies.append({
            "name": f"petal{i}", "parent": "munition", "mount": [0, 0, 0], "mass": pm,
            "shape": {"type": "shellSegment", "radius": R_OUT, "thickness": SHELL_T, "length": SHELL_L,
                      "span": 120, "centerAngle": ang, "axis": "x"},
            "aero": {"damping": [-1, -4, -4], "refLength": SHELL_L, "machCorrection": True},
            "structure": {"material": "al7075-t6"},
            "color": COLORS["petal"],
        })
        # 후방 힌지: 꼬리 조립체 앞면, 조각 중앙 방사 위치, 축 = 접선 방향
        connectors.append({
            "type": "hinge", "name": f"hinge{i}", "a": "tail", "b": f"petal{i}",
            "pointA": [round(TAIL_L / 2, 6), round(r_mid * math.cos(a), 6), round(r_mid * math.sin(a), 6)],
            "axisA": [0, round(-math.sin(a), 6), round(math.cos(a), 6)],
            "stiffness": 1.0e6, "rotStiffness": 5.0e4, "releaseAngle": HINGE_RELEASE_DEG, "breakLoad": 60000,
        })
        # 힌지(후방 끝)와 맞는 초기 운동: 앞끝 방사 속도 PETAL_DV, ω = (PETAL_DV/L)·접선축, CG(길이 중앙) 속도 = PETAL_DV/2
        w = PETAL_DV / SHELL_L
        events.append({
            "name": f"petal{i}_cut", "when": {"type": "time", "t": round(T_SPLIT + (i - 1) * PETAL_JITTER, 6)},
            "do": [{"type": "separate", "body": f"petal{i}",
                    "relVel": [0, round(PETAL_DV / 2 * math.cos(a), 6), round(PETAL_DV / 2 * math.sin(a), 6)],
                    "relOmega": [0, round(-w * math.sin(a), 6), round(w * math.cos(a), 6)]}],
        })
    events.insert(0, {
        "name": "split_detonation", "when": {"type": "time", "t": T_SPLIT},
        "do": [{"type": "log", "message": "3분할 절단 폭약 기폭 (120° 간격)"},
               {"type": "separate", "body": "nose", "relVel": [1.0, 0, 0]}],
    })
    events.append({"name": "tail_free", "when": {"type": "after", "event": "split_detonation", "delay": 0.001},
                   "do": [{"type": "separate", "body": "tail", "relVel": [0, 0, 0]}]})
    expose = []
    for k, x in enumerate(BOX_X, start=1):
        bodies.append(container(k, "munition", x))
        expose += [{"type": "separate", "body": f"c{k}", "relVel": [0, 0, 0]}, {"type": "fire", "device": f"c{k}.shock"}]
    events.append({"name": "containers_exposed", "when": {"type": "after", "event": "split_detonation", "delay": 0.002},
                   "do": expose})
    for k in range(1, 5):
        events += chute_events(k, "containers_exposed", DROGUE_DELAYS[k - 1])

    debris = ["nose", "tail", "petal1", "petal2", "petal3"]
    return {
        "schema": "mns.scenario/1",
        "name": "06_munition_3petal_split",
        "description": (
            f"운반 탄체 3분할 외피 분리(PRD V절 FR-SHL-01): 고도 {ALT:.0f} m, M {MACH} (ISA {SPEED} m/s)에서 t={T_SPLIT} s 120° 절단 폭약 기폭 → "
            f"외피 조각 3개(Al7075 t{SHELL_T * 1000:.0f} mm, {pm} kg)가 꼬리 힌지에 잡힌 채 벌어지다 {HINGE_RELEASE_DEG:.0f}° 에서 이탈, "
            f"컨테이너 4개(□270×270×400, {BOX_M} kg) 노출·충격 펄스 {SHOCK_G:.0f} G/{SHOCK_MS:.0f} ms → 드로그(지연 {DROGUE_DELAYS}) → 주낙하산. "
            "충돌 판정·잔해 간섭·컨테이너 최대 G(설계 50 G)·착지 이격 산출. 탄체 형상·질량·폭약 Δv 는 모두 (가정), "
            "생성: tools/scenario_gen/make_munition_split.py"),
        "sim": {"dt": 0.001, "tEnd": 400, "outputRate": 50, "seed": 6},
        "environment": environment(),
        "carrier": {"name": "munition", "altitude": ALT, "speed": SPEED, "heading": 0},
        "bodies": bodies,
        "connectors": connectors,
        "events": events,
        "collisions": {"restitution": 0.3, "friction": 0.3, "parachutes": True, "until": 40,
                       "bodies": debris + [f"c{k}" for k in range(1, 5)]},
        "metrics": common_metrics(debris),
        "dispersions": [
            {"path": "environment.wind.speed", "dist": "truncnormal", "sd": 1.5, "min": 0, "max": 10},
            {"path": "environment.wind.directionFrom", "dist": "normal", "sd": 20},
            {"path": "events.petal2_cut.when.t", "dist": "uniform", "min": -0.0003, "max": 0.0003},
            {"path": "events.petal3_cut.when.t", "dist": "uniform", "min": -0.0003, "max": 0.0003},
            {"path": "events.petal1_cut.do.0.relOmega.2", "dist": "normal", "mode": "scale", "sd": 0.25},
            {"path": "events.petal2_cut.do.0.relOmega.1", "dist": "normal", "mode": "scale", "sd": 0.25},
            {"path": "events.petal3_cut.do.0.relOmega.1", "dist": "normal", "mode": "scale", "sd": 0.25},
            {"path": "bodies.c1.devices.shock.peakG", "dist": "uniform", "mode": "scale", "min": 0.67, "max": 1.33},
        ],
        "monteCarlo": {"runs": 32, "impactBody": "c1", "sampling": "lhs", "seed": 61},
    }


def build_tail_cut(exit_interval=0.5) -> dict:
    bodies = [
        {"name": "tail", "parent": "munition", "mount": [-SHELL_L / 2 - TAIL_L / 2, 0, 0], "mass": TAIL_M,
         "shape": {"type": "cylinder", "radius": R_OUT, "length": TAIL_L, "axis": "x"},
         "aero": {"damping": [-1, -4, -4], "machCorrection": True},
         "structure": {"material": "al7075-t6", "thickness": 0.004}, "color": COLORS["tail"]},
    ]
    events = [{"name": "tail_cut", "when": {"type": "time", "t": T_SPLIT},
               "do": [{"type": "log", "message": "꼬리날개 조립체 절개 (후방 개방)"},
                      {"type": "separate", "body": "tail", "relVel": [-2.0, 0, 0]}]}]
    # 후방(c4)부터 1개씩: 이탈 직후 드로그가 끌어냄
    for n, k in enumerate([4, 3, 2, 1]):
        bodies.append(container(k, "munition", BOX_X[k - 1]))
        events.append({"name": f"c{k}_exit", "when": {"type": "after", "event": "tail_cut", "delay": round(0.05 + n * exit_interval, 3)},
                       "do": [{"type": "separate", "body": f"c{k}", "relVel": [-3.0, 0, 0]},
                              {"type": "fire", "device": f"c{k}.shock"},
                              {"type": "deploy", "device": f"c{k}.drogue"}]})
        events.append({"name": f"c{k}_main", "when": {"type": "or", "any": [
            {"type": "and", "all": [{"type": "after", "event": f"c{k}_exit", "delay": 1.0},
                                    {"type": "speed", "body": f"c{k}", "below": 40}]},
            {"type": "after", "event": f"c{k}_exit", "delay": 10.0}]},
            "do": [{"type": "release", "device": f"c{k}.drogue"}, {"type": "deploy", "device": f"c{k}.main"}]})
    debris = ["tail"]
    return {
        "schema": "mns.scenario/1",
        "name": "07_munition_tail_cut_aft_exit",
        "description": (
            f"대안 비교(PRD FR-SHL-01): 고도 {ALT:.0f} m, M {MACH}({SPEED} m/s)에서 꼬리날개만 절개 → 컨테이너를 후방부터 "
            f"{exit_interval} s 간격으로 1개씩 이탈(드로그가 끌어냄). 폼팩 실린더 압력차에 의한 이탈 방해는 미모델(가정 목록), "
            "탄체 내부 차폐 무시(FR-AER-09). 생성: tools/scenario_gen/make_munition_split.py"),
        "sim": {"dt": 0.001, "tEnd": 400, "outputRate": 50, "seed": 7},
        "environment": environment(),
        "carrier": {"name": "munition", "altitude": ALT, "speed": SPEED, "heading": 0,
                    "shape": {"type": "cylinder", "radius": R_OUT, "length": SHELL_L + NOSE_L, "axis": "x", "center": [NOSE_L / 2, 0, 0]}},
        "bodies": bodies,
        "events": events,
        "collisions": {"restitution": 0.3, "friction": 0.3, "parachutes": True, "until": 40},
        "metrics": common_metrics(debris),
        "dispersions": [
            {"path": "environment.wind.speed", "dist": "truncnormal", "sd": 1.5, "min": 0, "max": 10},
            {"path": "environment.wind.directionFrom", "dist": "normal", "sd": 20},
        ],
        "monteCarlo": {"runs": 32, "impactBody": "c1", "sampling": "lhs", "seed": 71},
    }


def main() -> None:
    for path, doc in ((OUT06, build_split()), (OUT07, build_tail_cut())):
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"→ {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
