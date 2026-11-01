"""운반 탄체 3분할 시나리오 생성기(PRD V절): M 0.5 @ 1 km, 조각·힌지 기하, 커밋된 템플릿과 생성기 일치."""
import importlib.util
import json
import math
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("mms", ROOT / "tools" / "scenario_gen" / "make_munition_split.py")
mms = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mms)


def test_speed_is_mach_05_at_1km_isa():
    a = math.sqrt(1.4 * 287.05287 * (288.15 - 6.5))
    assert mms.SPEED == pytest.approx(0.5 * a, abs=0.01)
    assert mms.SPEED == pytest.approx(168.2, abs=0.1)


def test_three_petals_at_120_deg_with_consistent_hinge_kinematics():
    doc = mms.build_split()
    petals = [b for b in doc["bodies"] if b["name"].startswith("petal")]
    assert [p["shape"]["centerAngle"] for p in petals] == [0.0, 120.0, 240.0]
    assert all(p["shape"]["span"] == 120 for p in petals)
    assert petals[0]["mass"] == pytest.approx(mms.petal_mass())
    hinges = [c for c in doc["connectors"] if c["type"] == "hinge"]
    assert len(hinges) == 3 and all(h["a"] == "tail" for h in hinges)
    for p, h in zip(petals, hinges):
        a = math.radians(p["shape"]["centerAngle"])
        # 힌지 축 = 조각 중앙의 접선 방향(축 x 와 방사 방향에 수직)
        ax = h["axisA"]
        radial = [0, math.cos(a), math.sin(a)]
        assert sum(x * y for x, y in zip(ax, radial)) == pytest.approx(0, abs=1e-6)
        assert ax[0] == 0
    # 초기 운동: 앞끝 방사 속도 PETAL_DV, CG 는 절반, ω 는 힌지 축 방향
    cut = next(e for e in doc["events"] if e["name"] == "petal1_cut")["do"][0]
    assert math.hypot(*cut["relVel"]) == pytest.approx(mms.PETAL_DV / 2, rel=1e-6)
    assert math.hypot(*cut["relOmega"]) == pytest.approx(mms.PETAL_DV / mms.SHELL_L, rel=1e-6)


def test_containers_match_box_spec_and_fit_inside_shell():
    doc = mms.build_split()
    boxes = [b for b in doc["bodies"] if b["name"].startswith("c")]
    assert len(boxes) == 4
    assert all(b["shape"]["size"] == [0.4, 0.27, 0.27] for b in boxes)
    half_diag = math.hypot(0.27, 0.27) / 2
    assert half_diag < mms.R_OUT - mms.SHELL_T          # 대각선이 외피 안쪽 반경 안
    xs = sorted(b["mount"][0] for b in boxes)
    assert xs[-1] + 0.4 / 2 <= mms.SHELL_L / 2 + 1e-9  # 원통부 길이 안에 직렬 배치


def test_committed_templates_match_generator():
    for path, doc in ((mms.OUT06, mms.build_split()), (mms.OUT07, mms.build_tail_cut())):
        committed = json.loads(Path(path).read_text(encoding="utf-8"))
        assert committed == json.loads(json.dumps(doc)), f"{path.name} 이 생성기와 다릅니다 — make_munition_split.py 를 다시 실행"


def test_tail_cut_exits_aft_first():
    doc = mms.build_tail_cut()
    exits = [e for e in doc["events"] if e["name"].endswith("_exit")]
    assert [e["name"] for e in exits] == ["c4_exit", "c3_exit", "c2_exit", "c1_exit"]
    delays = [e["when"]["delay"] for e in exits]
    assert delays == sorted(delays)
