"""컨테이너 → 박스 → 드론(테더) → 센서 간이 시나리오 생성기 (PRD T절 템플릿 ③).

수치는 일반 예시값(가정)이다.
    python tools/scenario_gen/make_container_poc.py  →  tests/Sim.Core.Tests/scenarios/03_container_drone_sensor_poc.json
"""
from __future__ import annotations

import json
import math
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "tests" / "Sim.Core.Tests" / "scenarios" / "03_container_drone_sensor_poc.json"

N_BOX = 4
BOX_X = [0.585, 0.195, -0.195, -0.585]          # 컨테이너 축(+X=노즈) 방향 박스 중심
TARGETS = [(100, -150), (100, -50), (100, 50), (100, 150)]   # 드론 목표: 테더 해제 위치 기준 (N, E) 오프셋 [m] (가정)
RELEASE_AGL = 200.0
CRUISE_AGL = 120.0


def body(name, **kw):
    d = {"name": name}
    d.update(kw)
    return d


def build() -> dict:
    bodies = []
    events = []
    connectors = []

    # 컨테이너: Ø420×2,000, 구조 23.5 kg 중 뚜껑 1.5 kg (가정)
    bodies.append(body(
        "container", parent="projectile", mount=[0, 0, 0.5],
        mass=22.0, cg=[0.05, 0, 0],
        shape={"type": "cylinder", "radius": 0.21, "length": 2.0, "axis": "x"},
        aero={"cp": [-0.25, 0, 0], "damping": [-2, -6, -6]},
        color="#7a8794",
        devices=[
            {"type": "parachute", "name": "drogue", "d0": 1.25, "cd0": 0.55, "attach": [-1.0, 0, 0],
             "riser": 6, "fillConstant": 8, "fillExponent": 2, "lineStretchTime": 0.25},
            {"type": "parachute", "name": "main", "d0": 3.66, "cd0": 2.2, "attach": [-1.0, 0, 0],
             "riser": 8, "fillConstant": 8, "fillExponent": 2, "lineStretchTime": 0.5},
        ],
    ))
    bodies.append(body(
        "lid", parent="container", mount=[-0.985, 0, 0], mass=1.5,
        shape={"type": "cylinder", "radius": 0.21, "length": 0.03, "axis": "x"},
        color="#c06060",
    ))

    for k in range(1, N_BOX + 1):
        bx, sh, dr, se, ch, tt = f"box{k}", f"shell{k}", f"drone{k}", f"sensor{k}", f"box{k}.chute", f"tether{k}"
        # 박스 상부(낙하산 결속, 2.0 kg 가정) — 박스 +Z(아래) 를 컨테이너 +X(노즈) 로 정렬: mountAttitude pitch 90°
        bodies.append(body(
            bx, parent="container", mount=[BOX_X[k - 1], 0, 0], mountAttitude=[0, 90, 0],
            mass=2.0, cg=[0, 0, -0.05],
            shape={"type": "box", "size": [0.28, 0.28, 0.20], "center": [0, 0, -0.095]},
            aero={"cp": [0, 0, -0.05], "damping": [-3, -3, -3]},
            color="#4f8fd8",
            devices=[{"type": "parachute", "name": "chute", "d0": 1.22, "cd0": 2.2, "attach": [0, 0, -0.195],
                      "riser": 2.5, "fillConstant": 8, "fillExponent": 2, "lineStretchTime": 0.35}],
        ))
        # 하부 박스 쉘(레일 340 mm 하강 후 이탈, 1.1 kg 가정)
        bodies.append(body(
            sh, parent=bx, mount=[0, 0, 0.10], mass=1.1,
            shape={"type": "box", "size": [0.28, 0.28, 0.19]},
            aero={"cp": [0, 0, 0], "damping": [-3, -3, -3]},
            color="#8fb3e0",
        ))
        # 드론(초기 개념 1.5 kg, 접힘) — 전개 구성으로 관성 증가
        bodies.append(body(
            dr, parent=sh, mount=[0, 0, -0.01], mass=1.5,
            cg=[0, 0, 0],
            inertia=[0.012, 0.012, 0.02],
            shape={"type": "box", "size": [0.25, 0.25, 0.12]},
            aero={"cd": 1.0, "area": 0.06, "cp": [0, 0, -0.01], "damping": [-4, -4, -4], "refLength": 0.35},
            configs={"deployed": {"mass": 1.5, "cg": [0, 0, 0], "inertia": [0.025, 0.025, 0.045]}},
            color="#40b070",
            devices=[{"type": "multirotor", "name": "rotor", "maxThrust": 85, "tau": 0.15, "maxTiltDeg": 35,
                      "cruiseSpeed": 12, "landSpeed": 2.5, "idleRatio": 0.3}],
        ))
        # 센서 더미(1.0 kg) — 노즈(+X) 아래로 탑재
        bodies.append(body(
            se, parent=dr, mount=[0, 0, 0.09], mountAttitude=[0, -90, 0], mass=1.0, cg=[0.04, 0, 0],
            shape={"type": "cylinder", "radius": 0.04, "length": 0.30, "axis": "x"},
            aero={"cp": [-0.07, 0, 0], "damping": [-2, -12, -12], "refLength": 0.30},
            noseCoefficient=0.8, color="#e0a030",
        ))
        connectors.append({"type": "rail", "name": f"rail{k}", "parent": bx, "child": sh,
                           "axis": [0, 0, 1], "length": 0.34, "friction": 0.1})
        connectors.append({"type": "tether", "name": tt, "a": bx, "pointA": [0, 0, 0.0],
                           "b": dr, "pointB": [0, 0, -0.06], "length": 1.5, "ea": 1.5e4, "damping": 60,
                           "breakLoad": 4020})

        eject_when = ({"type": "altitude", "body": "container", "below": 800} if k == 1
                      else {"type": "after", "event": f"eject{k - 1}", "delay": 2.0})
        tn, te = TARGETS[k - 1]
        events += [
            {"name": f"eject{k}", "when": eject_when,
             "do": [{"type": "separate", "body": bx, "relVel": [4, 0, 0]}]},
            {"name": f"box{k}_chute", "when": {"type": "after", "event": f"{bx}.separated", "delay": 1.0},
             "do": [{"type": "deploy", "device": ch}]},
            {"name": f"rail{k}_start", "when": {"type": "after", "event": f"box{k}_chute", "delay": 6.0},
             "do": [{"type": "startRail", "connector": f"rail{k}"}]},
            {"name": f"drone{k}_out", "when": {"type": "after", "event": f"rail{k}.exit", "delay": 0.05},
             "do": [{"type": "separate", "body": dr, "relVel": [0, 0, 1.0]}]},
            {"name": f"drone{k}_arms", "when": {"type": "after", "event": f"drone{k}_out", "delay": 1.0},
             "do": [{"type": "switchConfig", "body": dr, "config": "deployed"},
                    {"type": "set", "device": f"{dr}.rotor", "mode": "stabilize"}]},
            {"name": f"drone{k}_release",
             "when": {"type": "and", "all": [{"type": "after", "event": f"drone{k}_arms", "delay": 0.5},
                                             {"type": "altitude", "body": dr, "below": RELEASE_AGL}]},
             "do": [{"type": "release", "connector": tt},
                    {"type": "set", "device": f"{dr}.rotor", "mode": "goto", "targetRelative": [tn, te],
                     "targetAgl": CRUISE_AGL}]},
            {"name": f"sensor{k}_drop",
             "when": {"type": "and", "all": [{"type": "after", "event": f"drone{k}_release", "delay": 3.0},
                                             {"type": "arrived", "device": f"{dr}.rotor", "radius": 3.0}]},
             "do": [{"type": "separate", "body": se, "relVel": [0, 0, 0.5]}]},
            {"name": f"drone{k}_land", "when": {"type": "after", "event": f"sensor{k}_drop", "delay": 2.0},
             "do": [{"type": "set", "device": f"{dr}.rotor", "mode": "land"}]},
        ]

    events = [
        {"name": "container_release", "when": {"type": "time", "t": 0.5},
         "do": [{"type": "separate", "body": "container", "relVel": [0, 0, 2.0]}]},
        {"name": "lid_drogue", "when": {"type": "after", "event": "container.separated", "delay": 1.2},
         "do": [{"type": "separate", "body": "lid", "relVel": [-5, 0, 0]},
                {"type": "deploy", "device": "container.drogue"}]},
        {"name": "main_deploy",
         "when": {"type": "or", "any": [{"type": "and", "all": [{"type": "after", "event": "lid_drogue", "delay": 1.0},
                                                                {"type": "speed", "body": "container", "below": 45}]},
                                        {"type": "after", "event": "lid_drogue", "delay": 12.0}]},
         "do": [{"type": "release", "device": "container.drogue"}, {"type": "deploy", "device": "container.main"}]},
    ] + events

    return {
        "schema": "mns.scenario/1",
        "name": "03_container_drone_sensor_poc",
        "description": ("운반 탄체(139 m/s, 1500 m AGL) → 컨테이너 분리 → 뚜껑 분리·드로그 → 주낙하산 → 박스 4개 순차 사출(2 s) → "
                        "박스 낙하산 → 하부 쉘 레일 340 mm 이탈 → 드론 테더 1.5 m 매달림 → AGL 200 m 해제 → 목표 비행 → 센서 투하 → 착륙. "
                        "생성: tools/scenario_gen/make_container_poc.py (간이·가정 수치 포함)"),
        "sim": {"dt": 0.001, "tEnd": 600, "outputRate": 25, "seed": 3},
        "environment": {
            "wind": {"speed": 5, "directionFrom": 270, "refHeight": 10, "shear": "log", "roughness": 0.03},
            "turbulence": "light", "soil": "farmland",
        },
        "carrier": {"name": "projectile", "altitude": 1500, "speed": 139, "heading": 0,
                    "shape": {"type": "cylinder", "radius": 0.25, "length": 4.0, "axis": "x"}},
        "bodies": bodies,
        "connectors": connectors,
        "events": events,
        "metrics": [
            {"type": "landingSeparation", "name": "boxLandingSeparation", "bodies": [f"box{k}" for k in range(1, N_BOX + 1)], "target": 70},
            {"type": "landingSeparation", "name": "sensorSeparation", "bodies": [f"sensor{k}" for k in range(1, N_BOX + 1)]},
            {"type": "minSeparation", "name": "droneDebrisClearance",
             "bodies": [f"drone{k}" for k in range(1, N_BOX + 1)] + [f"shell{k}" for k in range(1, N_BOX + 1)] + ["lid"]},
        ],
        "dispersions": [
            {"path": "environment.wind.speed", "dist": "truncnormal", "sd": 1.5, "min": -5, "max": 6},
            {"path": "environment.wind.directionFrom", "dist": "normal", "sd": 20},
            {"path": "bodies.container.devices.main.cd0", "dist": "normal", "mode": "scale", "sd": 0.08},
            {"path": "events.eject2.when.delay", "dist": "uniform", "min": -0.2, "max": 0.2},
        ],
        "monteCarlo": {"runs": 32, "impactBody": "sensor1", "sampling": "lhs", "seed": 31},
        "gpuMonteCarlo": {
            "runs": 100000, "seed": 7, "sampling": "lhs",
            "model": {
                "mass": 45.9, "cdA": 0.2, "dt": 0.01, "tMax": 900,
                "chute": {"d0": 3.66, "cd0": 2.2, "delay": 4.0, "lineStretchTime": 0.5, "fillConstant": 8, "fillExponent": 2},
                "release": {"altitude": 1500, "speed": 139, "heading": 0, "vd": 2.0},
            },
            "dispersions": [
                {"path": "environment.wind.speed", "dist": "truncnormal", "sd": 1.5, "min": -5, "max": 6},
                {"path": "environment.wind.directionFrom", "dist": "normal", "sd": 20},
                {"path": "model.chute.cd0", "dist": "normal", "mode": "scale", "sd": 0.08},
            ],
        },
    }


if __name__ == "__main__":
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(build(), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {OUT}")
