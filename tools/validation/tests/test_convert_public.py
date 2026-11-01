import json
import math
import os
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

import convert_public as cp  # noqa: E402
import fit_rotor_vrs as fv  # noqa: E402


def test_angle_mean_across_wrap():
    assert abs(abs(cp.angle_mean([179.0, -179.0])) - 180.0) < 1e-9
    assert abs(cp.angle_mean([10.0, 20.0]) - 15.0) < 1e-12


def test_nasa_exclusions_follow_authors():
    assert cp.nasa_excluded(3, 2, "eulerAngle_deg_Roll")
    assert not cp.nasa_excluded(3, 2, "altitudeMsl_ft")
    assert cp.nasa_excluded(4, 2, "altitudeMsl_ft")
    assert cp.nasa_excluded(7, 3, "feVelocity_ft_s_Y")
    assert not cp.nasa_excluded(1, 3, "feVelocity_ft_s_Y")


def _write_csv(path, rows):
    with open(path, "w", newline="") as f:
        f.write("time,altitudeMsl_ft,eulerAngle_deg_Roll\n")
        for r in rows:
            f.write(",".join(str(x) for x in r) + "\n")


def test_nasa_reference_mean_band_and_tolerance(tmp_path):
    a = tmp_path / "Atmos_02_sim_01.csv"
    b = tmp_path / "Atmos_02_sim_02.csv"
    _write_csv(a, [(0.0, 30000.0, 0.0), (0.1, 29999.0, 1.0), (1.0, 29990.0, 2.0)])
    _write_csv(b, [(0.0, 30000.0, 0.0), (0.1, 29999.0, 9.0), (1.0, 29992.0, 40.0)])
    ref = cp.nasa_reference(2, [str(a), str(b)], [0.0, 1.0])
    alt = ref["altitudeMsl_ft"]
    assert alt["mean"] == [30000.0, 29991.0] and alt["lo"][1] == 29990.0 and alt["hi"][1] == 29992.0
    assert alt["n"] == 2 and abs(alt["tol"] - (3.0 + 1.0)) < 1e-9   # base 3 ft + half of the 2 ft spread
    roll = ref["eulerAngle_deg_Roll"]
    assert roll["n"] == 1 and roll["sims"] == [1]                   # SIM 2 Euler angles excluded
    assert roll["mean"] == [0.0, 2.0]


def test_flight_path_from_apogee():
    g = cp.flight_path_deg(492.3, 127500, 134800)
    vz = math.sqrt(2 * 9.75 * 7300 * cp.FT)
    assert abs(math.sin(math.radians(g)) * 492.3 - vz) < 1e-6
    assert 20 < g < 30


def test_young_roles_and_point_grades(tmp_path):
    d = tmp_path / "penetration_young"
    d.mkdir()
    def shot(test, target, v, star=False, sharp=180.0):
        return {"test": test, "target": target,
                "soil": {"degree_of_saturation_pct": 80},
                "projectile": {"total_mass_kg": 2.505, "diameter_m": 0.0397, "nose_sharpness_angle_deg": sharp},
                "impact": {"velocity_m_s": v, "velocity_estimated_from_gas_pressure": star},
                "measured_total_depth": {"m": 0.3}}
    case = {"sources": [{"title": "t", "report_number": "r", "year": 1, "url": "u", "role": "x"}],
            "measured_tests_thompson_1969": [shot(1, 2, 61.9), shot(8, 2, 51.8, sharp=22.6), shot(13, 2, 53.3, star=True), shot(15, 1, 51.2, star=True)]}
    (d / "case.json").write_text(json.dumps(case), encoding="utf-8")
    cal, pri = cp.convert_young(str(tmp_path))
    pts = {p["label"].split(" (")[0]: p for p in cal["points"]}
    assert pts["시험 1"]["role"] == "calibration" and "grade" not in pts["시험 1"]
    assert pts["시험 8"]["role"] == "validation" and pts["시험 8"]["grade"] == "B" and pts["시험 8"]["inputs"]["nose"] == "ogive"
    assert pts["시험 13"]["grade"] == "B" and pts["시험 13"]["group"] == "T2"
    assert pts["시험 15"]["role"] == "calibration"            # single-shot target: calibration only
    assert cal["calibration"]["parameter"] == "soilS"
    assert all(p["inputs"]["soilS"] == 15.0 for p in pri["points"])
    assert cal["origin"]["sha256"] == cp.sha256_of(str(d / "case.json"))


def test_read_static_skips_header(tmp_path):
    f = tmp_path / "s.txt"
    f.write_text("RPM    CT       CP\n3037   0.0892   0.0392\n6989   0.0978   0.0401\n")
    assert cp.read_static(str(f)) == [(3037.0, 0.0892, 0.0392), (6989.0, 0.0978, 0.0401)]


def test_rotor_model_matches_csharp_reference_values():
    # same numbers are asserted in tests/Sim.Core.Tests/SourceDataValidationTests.cs (RotorVrsModel)
    a, d, xc, w = 2.5, 0.4, 1.1, 0.95
    js = [-0.136, -0.176, -0.223, -0.262, -0.285]
    expect = [0.92039, 0.88593, 0.85884, 0.85786, 0.86954]
    for j, e in zip(js, expect):
        assert abs(fv.ct_ratio(j, 0.0811, 0.1047, a, d, xc, w) - e) < 1e-3
    assert abs(fv.ct_ratio(0.0, 0.09, 0.1, a, d, xc, w) - 1.0) < 1e-9


def test_bin_points_averages_only_true_repeats():
    pts = [(1.00e6, 0.5), (1.005e6, 0.6), (1.2e6, 0.7)]
    b = cp.bin_points(pts)
    assert len(b) == 2 and b[0][2] == 2 and abs(b[0][1] - 0.55) < 1e-12 and b[1][1] == 0.7


def test_reference_data_in_repository_is_traceable():
    with open(os.path.join(REPO, "src", "Sim.Core", "Models", "reference_data.json"), encoding="utf-8") as f:
        d = json.load(f)
    cyl = d["cylinderCrossflow"]
    assert len(cyl["origin"]["sha256"]) == 64 and cyl["reSubcritical"] == cp.RE_SUBCRITICAL
    machs = [r["mach"] for r in cyl["machRows"]]
    assert machs == sorted(machs) and machs[0] == 0.1 and machs[-1] == 0.6
    for r in cyl["machRows"]:
        res = [p[0] for p in r["points"]]
        assert res == sorted(res) and len(set(res)) == len(res)
    assert [e[0] for e in cyl["eta"]] == sorted(e[0] for e in cyl["eta"])
    assert d["components"]["brakeCord"]["breakLoad_N"] == 355.9 and d["components"]["brakeCord"]["grade"] == "A"
    assert len(d["credibility7009A"]["factors"]) == 8
    for f in d["credibility7009A"]["factors"]:
        assert set(f["levels"]) == {"0", "1", "2", "3", "4"}


def test_new_fr_rel_06_cases_follow_grading_rules():
    root = os.path.join(REPO, "validation", "public")
    with open(os.path.join(root, "parachute_cross40in_opening_noltr73157", "case.json"), encoding="utf-8") as f:
        cal = json.load(f)
    with open(os.path.join(root, "parachute_cross40in_opening_noltr73157_apriori", "case.json"), encoding="utf-8") as f:
        pri = json.load(f)
    assert cal["grade"] == "A" and cal["calibration"]["parameter"] == "apparentMassK0"
    groups = Counter(p["group"] for p in cal["points"] if p["role"] == "calibration")
    assert groups == {"8cfm": 1, "80cfm": 1, "208cfm": 1}     # 투과도 그룹마다 보정점 1개
    assert pri["grade"] == "B" and all(p["inputs"]["apparentMassK0"] == cp.K0_CRUZ_CALIBRATED for p in pri["points"])
    with open(os.path.join(root, "drag_cylinder_crossflow_mach_tr_r300", "case.json"), encoding="utf-8") as f:
        cyl = json.load(f)
    assert cyl["use"] == "consistency"
    assert sum(1 for p in cyl["points"] if p.get("role") == "info") == 133       # 표 I(a) 원점 = 참고


def test_repository_cases_are_converted_and_well_formed():
    root = os.path.join(REPO, "validation", "public")
    expected = ["nasa_6dof_checkcases", "penetration_young_thompson1969", "parachute_dgb_opening_cruz2003",
                "rotor_vrs_uiuc", "propeller_static_uiuc", "sphere_free_fall_bacon_reid1924"]
    for cid in expected:
        with open(os.path.join(root, cid, "case.json"), encoding="utf-8") as f:
            c = json.load(f)
        assert c["schema"] == cp.SCHEMA and c["id"] == cid and c["points"]
        assert len(c["origin"]["sha256"]) == 64
        assert not any(name.lower().endswith((".pdf", ".csv")) for name in os.listdir(os.path.join(root, cid)))
