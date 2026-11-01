"""Blender 가져오기 스크립트의 순수 함수 검증(bpy 불필요)."""
import importlib.util
import json
import math
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("mns_blender", ROOT / "tools" / "blender" / "import_mns_result.py")
mb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mb)


def rotate(q, v):
    m = mb.quat_to_mat(q)
    return tuple(sum(m[i][j] * v[j] for j in range(3)) for i in range(3))


def test_ned_to_enu():
    assert mb.ned_to_enu((1, 2, 3)) == (2, 1, -3)


def test_body_axes_in_blender():
    # 동체 정렬 = NED (기수 북쪽, 아래=+D) → Blender: 기수 +Y(북), 동체 아래 −Z
    q = mb.body_quat_enu((1, 0, 0, 0))
    assert rotate(q, (1, 0, 0)) == pytest.approx((0, 1, 0), abs=1e-12)
    assert rotate(q, (0, 0, 1)) == pytest.approx((0, 0, -1), abs=1e-12)
    # 요 90°(기수 동쪽) → Blender +X
    c = math.cos(math.pi / 4)
    q90 = mb.body_quat_enu((c, 0, 0, c))
    assert rotate(q90, (1, 0, 0)) == pytest.approx((1, 0, 0), abs=1e-12)


def test_mat_quat_roundtrip():
    for q in [(0.9, 0.1, -0.3, 0.2), (0.1, 0.9, 0.2, -0.3), (0.05, -0.2, 0.95, 0.1), (0.1, 0.1, -0.2, 0.95)]:
        n = math.sqrt(sum(c * c for c in q))
        q = tuple(c / n for c in q)
        back = mb.mat_to_quat(mb.quat_to_mat(q))
        d = abs(sum(a * b for a, b in zip(q, back)))
        assert d == pytest.approx(1, abs=1e-12)


def _bbox(pts):
    return [min(p[i] for p in pts) for i in range(3)], [max(p[i] for p in pts) for i in range(3)]


@pytest.mark.parametrize("axes,scale", [("yup-to-zup", 100.0), ("none", 1.0), ("zup-to-yup", 1000.0)])
def test_fit_usd_transform_recovers_body_frame(axes, scale):
    # 동체 = modelToBody · Fusion 모델(m) (noseAxis −Z: x_b = −z_f, y_b = x_f, z_b = −y_f)
    m2b = ((0, 0, -1), (1, 0, 0), (0, -1, 0))
    model_pts = [(x, y, z) for x in (-0.02, 0.06) for y in (-0.03, 0.05) for z in (0.0, 0.3)]
    body_min, body_max = _bbox([mb.mat_vec(m2b, p) for p in model_pts])
    c = mb.USD_AXES[axes]
    imp = [tuple(scale * v for v in mb.mat_vec(c, p)) for p in model_pts]   # Blender 가 놓은 USD
    imp_min, imp_max = _bbox(imp)
    t, err, how = mb.fit_usd_transform(imp_min, imp_max, body_min, body_max, m2b)
    assert err < 1e-9 and how == f"{axes}·s={scale:g}"
    probe = (0.01, -0.02, 0.2)
    assert mb.mat_vec(t, tuple(scale * v for v in mb.mat_vec(c, probe))) == pytest.approx(mb.mat_vec(m2b, probe), abs=1e-12)


def test_fit_usd_transform_reports_mismatch():
    t, err, _ = mb.fit_usd_transform((0, 0, 0), (5, 1, 1), (0, 0, 0), (1, 1, 1), mb.USD_AXES["none"])
    assert err > mb.USD_FIT_TOL   # 어떤 후보로도 맞지 않음 → STL 로 대체


def test_usd_path_only_with_flag_and_file(tmp_path):
    (tmp_path / "meshes").mkdir()
    (tmp_path / "meshes" / "b.usdz").write_bytes(b"x")
    body = {"name": "b", "mesh": "meshes/b.stl", "usd": "meshes/b.usdz", "center": [0, 0, 0.1], "size": [0.2, 0.2, 0.2]}
    assert Path(mb.usd_path(body, str(tmp_path), True)) == tmp_path / "meshes" / "b.usdz"
    assert mb.usd_path(body, str(tmp_path), False) is None                      # 기본 = 기존 STL 경로
    assert mb.usd_path({**body, "usd": "meshes/x.usdz"}, str(tmp_path), True) is None
    assert mb.usd_path({"name": "b"}, str(tmp_path), True) is None
    assert mb.body_bbox(body) == (pytest.approx((-0.1, -0.1, 0.0)), pytest.approx((0.1, 0.1, 0.2)))
    assert mb.body_bbox({"name": "b"}) is None


def test_load_and_resample(tmp_path):
    (tmp_path / "scene.json").write_text(json.dumps({"bodies": [{"name": "b", "mesh": "meshes/b.stl"}]}), encoding="utf-8")
    hdr = "t_s,body,mode,n_m,e_m,d_m,alt_agl_m,origin_n_m,origin_e_m,origin_d_m,qw,qx,qy,qz\n"
    rows = "".join(f"{t / 10},b,Free,0,0,0,0,{t},0,-{100 - t},1,0,0,0\n" for t in range(11))
    (tmp_path / "timeseries.csv").write_text(hdr + rows, encoding="utf-8")
    scene, series, devices, events = mb.load_result(str(tmp_path))
    assert len(series["b"]) == 11
    rs = mb.resample(series["b"], 20)
    assert rs[0][0] == 1 and len(rs) == 21
    assert rs[-1][1][0] == pytest.approx(1.0)
