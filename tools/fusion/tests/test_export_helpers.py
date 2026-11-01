"""Fusion 내보내기 스크립트의 순수 함수 검증(VV-07 의 오프라인 부분). Fusion 없이 실행: python -m pytest tools/fusion/tests"""
import importlib.util
import math
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("mns_export", ROOT / "tools" / "fusion" / "MnS_ExportForSim" / "MnS_ExportForSim.py")
mns = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mns)


def test_sym_eigvals_diagonal_and_rotated():
    assert mns.sym_eigvals([[3, 0, 0], [0, 1, 0], [0, 0, 2]]) == [1, 2, 3]
    c, s = math.cos(0.3), math.sin(0.3)
    r = [[c, -s, 0], [s, c, 0], [0, 0, 1]]
    d = [[1, 0, 0], [0, 2, 0], [0, 0, 5]]
    rd = [[sum(r[i][k] * d[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    m = [[sum(rd[i][k] * r[j][k] for k in range(3)) for j in range(3)] for i in range(3)]
    ev = mns.sym_eigvals(m)
    assert ev == pytest.approx([1, 2, 5], rel=1e-12)


@pytest.mark.parametrize("origin", ["world-origin", "center-of-mass"])
@pytest.mark.parametrize("sign", [-1, 1])
def test_choose_inertia_identifies_convention(origin, sign):
    mass = 2.0
    cg = [3.0, -2.0, 5.0]  # cm
    # CG 기준 실제 텐서(비대각 있음): 회전된 대각 텐서
    c, s = math.cos(0.4), math.sin(0.4)
    r = [[1, 0, 0], [0, c, -s], [0, s, c]]
    d = [[10.0, 0, 0], [0, 20.0, 0], [0, 0, 26.0]]
    rd = [[sum(r[i][k] * d[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    i_cg = [[sum(rd[i][k] * r[j][k] for k in range(3)) for j in range(3)] for i in range(3)]
    t = i_cg if origin == "center-of-mass" else mns.mat_add(i_cg, mns.parallel_axis(mass, cg))
    # Fusion 이 돌려줄 성분: sign=-1 → 곱관성 = −텐서 비대각, sign=+1 → 텐서 비대각 그대로
    xy, yz, xz = sign * t[0][1], sign * t[1][2], sign * t[0][2]
    axes = [tuple(r[i][k] for i in range(3)) for k in range(3)]  # r 의 열 = 주축
    inertia, info = mns.choose_inertia_at_cg((t[0][0], t[1][1], t[2][2], xy, yz, xz), mass, cg, (10.0, 20.0, 26.0), axes)
    assert info["reference"] == origin
    assert info["ok"]
    for i in range(3):
        for j in range(3):
            assert inertia[i][j] == pytest.approx(i_cg[i][j] * 1e-4, abs=1e-12)


def test_ambiguity_flagged_without_axes():
    # CG 기준 값 + 비대각 하나뿐 → 부호를 고유값으로 구분 불가 → ambiguous 경고
    t = [[10.0, 0, 0], [0, 20.0, 3.0], [0, 3.0, 26.0]]
    ev = mns.sym_eigvals(t)
    _, info = mns.choose_inertia_at_cg((10.0, 20.0, 26.0, 0.0, -3.0, 0.0), 1.0, [0, 0, 0], tuple(ev))
    assert info["ambiguous"] and not info["ok"]


def test_axisymmetric_noise_is_not_ambiguous():
    # FX-04: 축대칭 부품(CG 가 축 위) — 곱관성이 수치잡음(1e-8 kg·cm²)뿐이면 부호 후보 텐서가 사실상 같다 → ok
    mass, cg = 1.0, [0.0, 0.0, 10.0]
    i_cg = [[5.0, 0, 0], [0, 5.0, 0], [0, 0, 1.0]]
    t = mns.mat_add(i_cg, mns.parallel_axis(mass, cg))
    _, info = mns.choose_inertia_at_cg((t[0][0], t[1][1], t[2][2], 1e-8, 0.0, 0.0), mass, cg, (1.0, 5.0, 5.0))
    assert info["tiedCandidates"] >= 1
    assert not info["ambiguous"] and info["ok"]
    assert info["maxTiedTensorDifference"] < mns.SAME_TENSOR_TOL


@pytest.mark.parametrize("doc,names,comps,expect", [
    ("Untitled", ["DISPLAY_Original_Actual_Test_Weight_1_SCALE_085", "DISPLAY_Original_Actual_Test_Weight_2_SCALE_085"], [],
     "DISPLAY_Original_Actual_Test_Weight"),
    ("REV7_FoldingArm v3", ["SensorBody"], [], "SensorBody_REV7"),
    ("Untitled", [], ["Sensor_Dummy"], "Sensor_Dummy"),
    ("Untitled", ["ab", "cd"], [], "model"),
    ("REV8 TYPE-B", [], [], "REV8_TYPE-B"),
])
def test_default_export_name(doc, names, comps, expect):
    # FX-05: 미저장 문서에서 'Untitled' 대신 선택 이름 공통 접두 / 컴포넌트 이름 + REV
    assert mns.default_export_name(doc, names, comps) == expect


def test_combine_two_point_masses():
    z = [[0.0] * 3 for _ in range(3)]
    m, cg, i = mns.combine([(1.0, [1, 0, 0], z), (1.0, [-1, 0, 0], z)])
    assert m == 2 and cg == [0, 0, 0]
    assert i[1][1] == pytest.approx(2.0) and i[2][2] == pytest.approx(2.0) and i[0][0] == pytest.approx(0.0)


def test_stl_roundtrip_and_unit_detection(tmp_path):
    tris = [((0, 0, 0), (100, 0, 0), (0, 200, 0)), ((0, 0, 0), (0, 200, 0), (0, 0, 300))]
    p = tmp_path / "a.stl"
    mns.write_binary_stl(str(p), tris)
    back = mns.read_stl(str(p))
    assert len(back) == 2
    assert back[1][2] == pytest.approx((0, 0, 300))
    assert mns.stl_unit_scale(back, [0, 0, 0], [0.1, 0.2, 0.3]) == 0.001
    assert mns.stl_unit_scale(back, [0, 0, 0], [1.0, 2.0, 3.0]) == 0.01
    assert mns.safe_name("REV8 TYPE-B / 드론") == "REV8_TYPE-B_드론"


def test_plan_step_export_single_occurrence_vs_root():
    one = mns.plan_step_export("REV7 sensor", [("asm:1+sensor:1", "sensor:1"), ("asm:1+sensor:1", "sensor:1")])
    assert one["scope"] == "occurrence" and one["key"] == "asm:1+sensor:1" and one["warning"] is None
    assert one["file"] == mns.safe_name("REV7 sensor") + ".step"
    many = mns.plan_step_export("x", [("a:1", "a:1"), ("b:1", "b:1")])
    assert many["scope"] == "root" and "루트" in many["warning"]
    root = mns.plan_step_export("x", [(None, ""), (None, "")])
    assert root["scope"] == "root" and root["warning"] is None
