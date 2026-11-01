"""스파이크 선행형 센서 합성 샘플(실제 설계 형상 아님, NFR-06) — FX-01/02/03 재현용.

실모델 시험에서 드러난 상황을 합성 데이터로 재현한다: Fusion 모델 좌표에서 길이축 Z,
**스파이크가 −Z 쪽**(z<0.1 m), 몸통·날개가 +Z 쪽. 기수축을 지정하지 않으면(+Z 기본) 날개 쪽이 기수로 계산된다.

형상(Fusion 모델 좌표, m):
  원뿔 팁  z −0.03 → 0   (꼭짓점 z=−0.03, 밑면 r 8 mm)      강철 7850 kg/m³
  스파이크 z 0 → 0.10    r 8 mm                              강철
  몸통     z 0.10 → 0.30 r 40 mm                             등가 밀도(총 1.0 kg 맞춤)
  날개 4장 z 0.20 → 0.30, r 40 → 100 mm, 두께 3 mm           ABS 1050 kg/m³
질량특성은 물체별 밀도로 메시 적분(사면체 분해)해 CG 기준 텐서로 기록한다.

    python tools/samples/make_spike_sensor_sample.py
    → samples/fusion_export/spike_sensor_sample.stl, spike_sensor_sample.massprops.json
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
NAME = "spike_sensor_sample"
TOTAL_MASS = 1.0
STEEL, ABS = 7850.0, 1050.0


def _orient(tris):
    """닫힌 메시의 부호 부피가 양수(바깥 법선)가 되도록 삼각형 방향을 맞춘다."""
    vol, _, _ = mesh_props(tris)
    return tris if vol > 0 else [(a, c, b) for a, b, c in tris]


def cylinder(r, z0, z1, seg=32):
    tris = []
    for i in range(seg):
        t0, t1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
        a0, a1 = (r * math.cos(t0), r * math.sin(t0), z0), (r * math.cos(t1), r * math.sin(t1), z0)
        b0, b1 = (a0[0], a0[1], z1), (a1[0], a1[1], z1)
        tris += [(a0, a1, b1), (a0, b1, b0), ((0.0, 0.0, z1), b0, b1), ((0.0, 0.0, z0), a1, a0)]
    return _orient(tris)


def cone(r, z_apex, z_base, seg=32):
    tris = []
    apex, cb = (0.0, 0.0, z_apex), (0.0, 0.0, z_base)
    for i in range(seg):
        t0, t1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
        b0, b1 = (r * math.cos(t0), r * math.sin(t0), z_base), (r * math.cos(t1), r * math.sin(t1), z_base)
        tris += [(apex, b1, b0), (cb, b0, b1)]
    return _orient(tris)


def box(lo, hi):
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    faces = [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5), (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)]
    return _orient([(v[a], v[b], v[c]) for a, b, c in faces])


def mesh_props(tris):
    """닫힌 메시(단위 밀도): 부피, 1차 모멘트, 원점 기준 2차 모멘트 S=∫x xᵀ dV (사면체 0,a,b,c 분해)."""
    vol = 0.0
    first = [0.0, 0.0, 0.0]
    s = [[0.0] * 3 for _ in range(3)]
    for a, b, c in tris:
        det = (a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * c[1] - b[1] * c[0]))
        vol += det / 6
        tot = [a[i] + b[i] + c[i] for i in range(3)]
        for i in range(3):
            first[i] += det / 24 * tot[i]
            for j in range(3):
                s[i][j] += det / 120 * (a[i] * a[j] + b[i] * b[j] + c[i] * c[j] + tot[i] * tot[j])
    return vol, first, s


def solid_mass_props(tris, density):
    vol, first, s = mesh_props(tris)
    m = density * vol
    cg = [f / vol for f in first]
    tr = s[0][0] + s[1][1] + s[2][2]
    i_origin = [[density * ((tr if i == j else 0.0) - s[i][j]) for j in range(3)] for i in range(3)]
    i_cg = mns.mat_add(i_origin, mns.parallel_axis(m, cg), -1.0)
    return m, cg, i_cg


def build():
    tip = cone(0.008, -0.03, 0.0)
    shaft = cylinder(0.008, 0.0, 0.10)
    body = cylinder(0.040, 0.10, 0.30)
    fins = [box((0.040, -0.0015, 0.20), (0.100, 0.0015, 0.30)),
            box((-0.100, -0.0015, 0.20), (-0.040, 0.0015, 0.30)),
            box((-0.0015, 0.040, 0.20), (0.0015, 0.100, 0.30)),
            box((-0.0015, -0.100, 0.20), (0.0015, -0.040, 0.30))]
    parts = [("SpikeTip", tip, STEEL, "Steel"), ("Spike", shaft, STEEL, "Steel")]
    parts += [(f"Fin{i + 1}", f, ABS, "ABS") for i, f in enumerate(fins)]
    fixed = [solid_mass_props(t, d) for _, t, d, _ in parts]
    m_fixed = sum(p[0] for p in fixed)
    v_body = mesh_props(body)[0]
    rho_body = (TOTAL_MASS - m_fixed) / v_body
    parts.insert(2, ("Body", body, rho_body, "등가(전장·배터리 포함)"))
    props = [solid_mass_props(t, d) for _, t, d, _ in parts]
    return parts, props


def main():
    parts, props = build()
    mass, cg, inertia = mns.combine(props)
    all_tris = [tri for _, t, _, _ in parts for tri in t]
    OUT.mkdir(parents=True, exist_ok=True)
    mns.write_binary_stl(str(OUT / f"{NAME}.stl"), all_tris, header=b"MnS synthetic spike sensor (m)")
    mn, mx = mns.bounds(all_tris)
    info = [{"body": n, "material": mat, "density_kg_m3": d, "mass": p[0], "cg": p[1],
             "inertiaCheck": {"reference": "synthetic-mesh-integration", "ok": True}}
            for (n, _, d, mat), p in zip(parts, props)]
    doc = mns.build_massprops_json(NAME, mass, cg, inertia, mn, mx, f"{NAME}.stl", info, {
        "document": "SYNTHETIC SAMPLE (not a real design) — spike toward Fusion -Z",
        "fusionUnits": "mm", "noseAxisHint": "-Z", "warnings": []})
    (OUT / f"{NAME}.massprops.json").write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {OUT / NAME}.*  mass={mass:.4f} kg  cg={[round(c, 4) for c in cg]}")


if __name__ == "__main__":
    main()
