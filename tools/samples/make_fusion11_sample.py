"""Fusion 내보내기 1.1 형식 공개 합성 샘플 — 스파이크 센서 샘플 형상 + 외관 · 재료 물성 · 사용자 파라미터 · 조인트 예시값.

형상(STL)은 make_spike_sensor_sample.py 의 spike_sensor_sample.stl 을 그대로 가리킨다(실제 설계 형상 아님).
1.1 필드(parts[].materialProperties · appearance · occurrence, userParameters, joints, mesh)는 Fusion 없이
내보내기 스크립트의 순수 함수(user_parameter_info · joint_info · appearance_info)로 만든다. 물성은 교과서 수준의 예시값이다.
총질량은 공개 예시값 1.0 kg 으로 둔다(몸통 등가 밀도로 맞춤).

    python tools/samples/make_fusion11_sample.py
    → samples/fusion_export/spike_sensor_v11_sample.massprops.json
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


spike = _load("mns_spike_sample", ROOT / "tools" / "samples" / "make_spike_sensor_sample.py")
mns = spike.mns
OUT = ROOT / "samples" / "fusion_export"
NAME = "spike_sensor_v11_sample"
TOTAL_MASS = 1.0

# 예시 물성(SI) — 일반 문헌 대표값
PROPS = {
    "Steel": {"density": 7850.0, "E": 200e9, "nu": 0.29, "yield": 250e6, "ultimate": 400e6,
              "thermalExpansion": 1.2e-5, "thermalConductivity": 50.0, "specificHeat": 470.0},
    "ABS": {"density": 1050.0, "E": 2.2e9, "nu": 0.35, "yield": 40e6, "ultimate": 44e6,
            "thermalExpansion": 9.0e-5, "thermalConductivity": 0.17, "specificHeat": 1400.0},
    "Aluminum 6061": {"density": 2700.0, "E": 68.9e9, "nu": 0.33, "yield": 276e6, "ultimate": 310e6,
                      "thermalExpansion": 2.36e-5, "thermalConductivity": 167.0, "specificHeat": 896.0},
}
# 외관(0–255 RGBA) · 메탈 여부
LOOKS = {
    "Steel": ("Steel - Satin", (150, 152, 156, 255), [{"id": "metal_f0", "kind": "color"}], 0.35),
    "ABS": ("Plastic - Matte (Orange)", (232, 120, 40, 255), [{"id": "opaque_albedo", "kind": "color"}], 0.6),
    "Aluminum 6061": ("Aluminum - Anodized (Gray)", (96, 104, 112, 255), [{"id": "metal_f0", "kind": "color"}], 0.4),
}
# 바디 → (재료, occurrence 경로)
BODY_MAP = {"SpikeTip": ("Steel", "Spike:1"), "Spike": ("Steel", "Spike:1"), "Body": ("Aluminum 6061", "Body:1"),
            "Fin1": ("ABS", "Fins:1"), "Fin2": ("ABS", "Fins:1"), "Fin3": ("ABS", "Fins:1"), "Fin4": ("ABS", "Fins:1")}


def main():
    spike.TOTAL_MASS = TOTAL_MASS
    parts, props = spike.build()
    mass, cg, inertia = mns.combine(props)
    all_tris = [tri for _, t, _, _ in parts for tri in t]
    mn, mx = mns.bounds(all_tris)
    info = []
    for (n, _, d, _), p in zip(parts, props):
        mat, occ = BODY_MAP[n]
        look, rgba, aprops, rough = LOOKS[mat]
        info.append({"body": n, "material": mat, "density_kg_m3": d, "mass": p[0], "cg": p[1],
                     "inertiaCheck": {"reference": "synthetic-mesh-integration", "ok": True},
                     "materialId": "PrismMaterial-" + mat.replace(" ", ""), "materialLibrary": "Fusion Material Library",
                     "materialProperties": dict(PROPS[mat], **({"density": d} if n == "Body" else {})),
                     "appearance": mns.appearance_info(look, None, rgba, rough, aprops), "occurrence": occ})
    params = [mns.user_parameter_info("spike_length", "100 mm", 10.0, "mm", "스파이크 길이"),
              mns.user_parameter_info("body_dia", "80 mm", 8.0, "mm", "몸통 지름"),
              mns.user_parameter_info("body_length", "200 mm", 20.0, "mm"),
              mns.user_parameter_info("fin_span", "60 mm", 6.0, "mm", "날개 폭(몸통 밖)"),
              mns.user_parameter_info("fin_thickness", "3 mm", 0.3, "mm"),
              mns.user_parameter_info("fin_count", "4", 4.0, "", "날개 수")]
    joints = [mns.joint_info("Rigid1", "Rigid", "Spike:1", "Body:1", (0.0, 0.0, 10.0)),
              mns.joint_info("Rigid2", "Rigid", "Body:1", "Fins:1", (0.0, 0.0, 20.0)),
              mns.joint_info("Spike:1 고정", "Rigid", "Spike:1", "", None, "asBuiltJoint")]
    doc = mns.build_massprops_json(NAME, mass, cg, inertia, mn, mx, "spike_sensor_sample.stl", info, {
        "document": "SYNTHETIC SAMPLE (not a real design) — export 1.1 fields example",
        "fusionUnits": "mm", "noseAxisHint": "-Z", "warnings": [],
        "userParameters": params, "joints": joints,
        "mesh": {"method": "stl-export", "refinement": "medium"}})
    doc["exportedAt"] = "2026-01-01T00:00:00"   # 다시 만들어도 같은 파일
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / f"{NAME}.massprops.json").write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {OUT / NAME}.massprops.json  mass={mass:.4f} kg")


if __name__ == "__main__":
    main()
