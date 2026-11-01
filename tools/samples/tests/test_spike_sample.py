"""합성 스파이크 센서 샘플 생성기: 메시 질량특성 적분이 해석해와 맞는지, 샘플 파일이 생성기와 일치하는지."""
import importlib.util
import json
import math
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("spike", ROOT / "tools" / "samples" / "make_spike_sensor_sample.py")
spike = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(spike)


def test_box_mass_props_analytic():
    a, b, c = 0.2, 0.1, 0.05
    m, cg, i = spike.solid_mass_props(spike.box((0, 0, 0), (a, b, c)), 1000.0)
    assert m == pytest.approx(1000 * a * b * c, rel=1e-12)
    assert cg == pytest.approx([a / 2, b / 2, c / 2], rel=1e-12)
    assert i[0][0] == pytest.approx(m * (b * b + c * c) / 12, rel=1e-9)
    assert i[2][2] == pytest.approx(m * (a * a + b * b) / 12, rel=1e-9)
    assert abs(i[0][1]) < 1e-15


def test_cylinder_and_cone_volume():
    v_cyl = spike.mesh_props(spike.cylinder(0.04, 0.0, 0.2, seg=256))[0]
    assert v_cyl == pytest.approx(math.pi * 0.04 ** 2 * 0.2, rel=1e-3)
    v_cone = spike.mesh_props(spike.cone(0.008, -0.03, 0.0, seg=256))[0]
    assert v_cone == pytest.approx(math.pi * 0.008 ** 2 * 0.03 / 3, rel=1e-3)


def test_sample_matches_generator():
    parts, props = spike.build()
    mass, cg, _ = spike.mns.combine(props)
    assert mass == pytest.approx(spike.TOTAL_MASS, rel=1e-12)
    # 스파이크가 −Z, 무거운 쪽(CG)이 날개 끝보다 스파이크 쪽에 가깝다
    assert 0.1 < cg[2] < 0.2
    doc = json.loads((ROOT / "samples" / "fusion_export" / "spike_sensor_sample.massprops.json").read_text(encoding="utf-8"))
    assert doc["mass"] == pytest.approx(mass, rel=1e-9)
    assert doc["cg"] == pytest.approx(cg, abs=1e-9)
    assert doc["noseAxisHint"] == "-Z"
