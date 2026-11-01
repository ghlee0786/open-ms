"""Fusion 360 없이 '내보내기 결과' 샘플을 만든다(합성 데이터 — 실제 설계 형상 아님, NFR-06).

Fusion 스크립트(MnS_ExportForSim)의 순수 함수를 그대로 써서, Fusion API 가 돌려줄 원시값
(cm, kg·cm², 원점 기준 XYZ 관성 + 곱관성 양수 적분 관례로 가정)을 흉내 내고 같은 경로로 JSON/STL 을 만든다.

    python tools/samples/make_sample_fusion_export.py
    → samples/fusion_export/sensor_dummy_sample.stl, sensor_dummy_sample.massprops.json
"""
from __future__ import annotations

import importlib.util
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("mns_export", ROOT / "tools" / "fusion" / "MnS_ExportForSim" / "MnS_ExportForSim.py")
mns = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mns)

OUT = ROOT / "samples" / "fusion_export"


def cylinder_tris_mm(r, length, cx, cy, seg=48):
    """Fusion 모델 좌표: 길이축 = +Z (0 → length), 축 중심 (cx, cy). 단위 mm."""
    tris = []
    for i in range(seg):
        t0, t1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
        a0 = (cx + r * math.cos(t0), cy + r * math.sin(t0), 0.0)
        a1 = (cx + r * math.cos(t1), cy + r * math.sin(t1), 0.0)
        b0 = (a0[0], a0[1], length)
        b1 = (a1[0], a1[1], length)
        tris += [(a0, a1, b1), (a0, b1, b0), ((cx, cy, length), b0, b1), ((cx, cy, 0.0), a1, a0)]
    return tris


def main():
    mass = 1.0
    r_cm, l_cm, cx, cy = 4.0, 30.0, 2.0, 1.0
    cg_cm = [cx, cy, l_cm / 2]
    # CG 기준 (cm, kg·cm²)
    i_ax = 0.5 * mass * r_cm ** 2
    i_tr = mass * (3 * r_cm ** 2 + l_cm ** 2) / 12
    # Fusion 이 돌려준다고 가정한 '원점 기준' 값: 대각 = I_cg + m(...), 곱관성 = +∫xy dm (양수 적분 관례)
    xx = i_tr + mass * (cg_cm[1] ** 2 + cg_cm[2] ** 2)
    yy = i_tr + mass * (cg_cm[0] ** 2 + cg_cm[2] ** 2)
    zz = i_ax + mass * (cg_cm[0] ** 2 + cg_cm[1] ** 2)
    xy = mass * cg_cm[0] * cg_cm[1]
    yz = mass * cg_cm[1] * cg_cm[2]
    xz = mass * cg_cm[0] * cg_cm[2]
    principal = (i_ax, i_tr, i_tr)
    axes = [(0.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)]
    inertia_m, info = mns.choose_inertia_at_cg((xx, yy, zz, xy, yz, xz), mass, cg_cm, principal, axes)

    tris_mm = cylinder_tris_mm(r_cm * 10, l_cm * 10, cx * 10, cy * 10)
    bmin = [(cx - r_cm) * 0.01, (cy - r_cm) * 0.01, 0.0]
    bmax = [(cx + r_cm) * 0.01, (cy + r_cm) * 0.01, l_cm * 0.01]
    s = mns.stl_unit_scale(tris_mm, bmin, bmax)
    tris_m = mns.scale_tris(tris_mm, s)

    OUT.mkdir(parents=True, exist_ok=True)
    name = "sensor_dummy_sample"
    mns.write_binary_stl(str(OUT / f"{name}.stl"), tris_m)
    mn, mx = mns.bounds(tris_m)
    doc = mns.build_massprops_json(name, mass, [c * 0.01 for c in cg_cm], inertia_m, mn, mx, f"{name}.stl", [{
        "body": "SensorBody", "material": "합성 샘플(균일)", "mass": mass, "cg": [c * 0.01 for c in cg_cm],
        "inertiaCheck": info, "stlScaleDetected": s,
    }], {"document": "SYNTHETIC SAMPLE (not a real design)", "fusionUnits": "mm", "warnings": []})
    (OUT / f"{name}.massprops.json").write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {OUT / name}.*  inertiaCheck={info['reference']}/{info['productSign']} err={info['maxRelativeEigenError']:.2e}")


if __name__ == "__main__":
    main()
