"""Convert the source-data validation cases (main checkout `validation/sources/<case>/`, git 밖 원자료, PRD O-4)
into repository cases `validation/public/<id>/case.json` (schema mns.validation-case/1) that
`mns validate-public` reproduces with the simulator (PRD FX-08, VV-49).

Only derived inputs/measurements go into the repository (no PDFs, no raw CSV copies): every
generated case records the source case.json path and its sha256 in "origin".

Usage:  python tools/validation/convert_public.py [--src <main validation/sources>] [--dst validation/public]
Standard library only.
"""
import argparse
import csv
import hashlib
import json
import math
import os
import statistics

FT, LB, IN = 0.3048, 0.45359237, 0.0254
# 원자료(git 밖) 기본 위치 = 이 저장소의 validation/sources — 다른 곳(예: 작업 worktree 에서 메인 체크아웃)이면 MNS_VALIDATION_SOURCES 또는 --src
DEFAULT_SRC = os.environ.get("MNS_VALIDATION_SOURCES") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "validation", "sources")
SCHEMA = "mns.validation-case/1"

# Canopy constants used for parachute predictions (see parachute_dgb_opening_cruz2003):
FILL_CONSTANT_DGB = 8.2   # inflation distance / D0 measured in WTMD 11/12 (calibration tests)
FILL_EXPONENT = 2.0       # POC default power-law fill curve (Sr = (t/tf)^2)


# ---------------------------------------------------------------- helpers

def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def source_ref(p):
    """원자료 경로 기록: PC 마다 다른 절대 경로 대신 저장소 기준 'validation/sources/…'(그 폴더 밖이면 그대로)."""
    s = p.replace("\\", "/")
    i = s.find("validation/sources/")
    return s[i:] if i >= 0 else s


def origin(src_case_dir):
    p = os.path.join(src_case_dir, "case.json")
    return {"source_case": source_ref(p), "sha256": sha256_of(p),
            "converter": "tools/validation/convert_public.py"}


def base_case(cid, title, vv, grade, kind, model, tol, sources, envelope="", use="validation", notes=None):
    return {"schema": SCHEMA, "id": cid, "title": title, "vv": vv, "grade": grade, "use": use, "kind": kind,
            "model": model, "tolerance": {"relative": tol}, "envelope": envelope, "sources": sources,
            "notes": notes or [], "points": []}


def r6(x):
    return float(f"{x:.6g}")


# ---------------------------------------------------------------- NASA check-cases 1-8

NASA_EARTH = {
    1: {"gravity": "wgs84-j2", "rotation": True}, 2: {"gravity": "wgs84-j2", "rotation": True},
    3: {"gravity": "wgs84-j2", "rotation": True},
    4: {"gravity": "inverse-square", "rotation": False, "radius": 6371007.181},
    5: {"gravity": "inverse-square", "rotation": True, "radius": 6371007.181},
    6: {"gravity": "wgs84-j2", "rotation": True}, 7: {"gravity": "wgs84-j2", "rotation": True},
    8: {"gravity": "wgs84-j2", "rotation": True},
}
# Inertial initial body rates [deg/s] (case tables give Earth-relative values; inertial = Earth-relative + Earth rate on body x)
NASA_OMEGA = {1: [0, 0, 0], 2: [10, 20, 30], 3: [10, 20, 30], 4: [10, 20, 30], 5: [10, 20, 30],
              6: [0, 0, 0], 7: [0, 0, 0], 8: [0, 0, 0]}
NASA_VARS = ["altitudeMsl_ft", "feVelocity_ft_s_Z", "feVelocity_ft_s_Y",
             "eulerAngle_deg_Roll", "eulerAngle_deg_Pitch", "eulerAngle_deg_Yaw",
             "bodyAngularRateWrtEi_deg_s_Roll", "bodyAngularRateWrtEi_deg_s_Pitch", "bodyAngularRateWrtEi_deg_s_Yaw"]


def nasa_base_tol(case_no, var):
    """Tolerances from the source README (altitude, angles, rates) and chosen for velocities."""
    if var == "altitudeMsl_ft":
        return 20.0 if case_no in (4, 5) else 3.0
    if var == "feVelocity_ft_s_Z":
        return 2.0 if case_no in (4, 5) else 0.3
    if var == "feVelocity_ft_s_Y":
        return 0.1
    if var.startswith("eulerAngle"):
        return 0.1
    return 0.05


def nasa_excluded(case_no, sim_no, var):
    """Exclusions stated by the authors (Vol.II Table 78 notes, recorded in the source case.json)."""
    if sim_no == 2 and var.startswith("eulerAngle"):
        return True                      # SIM 2 Euler angles: integration errors
    if sim_no == 2 and case_no in (4, 5):
        return True                      # SIM 2 unexplained X-body force/gravity differences
    if sim_no == 3 and case_no in (6, 7, 8):
        return True                      # SIM 3 non-zero Y-body force
    return False


def wrap180(a):
    return (a + 180.0) % 360.0 - 180.0


def angle_mean(values):
    ref = values[0]
    return wrap180(ref + statistics.mean(wrap180(v - ref) for v in values))


def sample_csv(path, times):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    tcol = "time" if "time" in rows[0] else "Time"
    out = []
    for t in times:
        row = min(rows, key=lambda r: abs(float(r[tcol]) - t))
        if abs(float(row[tcol]) - t) > 1e-3:
            raise ValueError(f"{path}: no sample at t={t}")
        out.append(row)
    return out


def nasa_reference(case_no, files, times):
    """Mean / min / max of each variable across the included reference simulations at the sample times."""
    per_sim = {}
    for f in files:
        sim_no = int(os.path.splitext(os.path.basename(f))[0].split("_")[-1])
        per_sim[sim_no] = sample_csv(f, times)
    out = {}
    for var in NASA_VARS:
        sims = [s for s, rows in per_sim.items()
                if not nasa_excluded(case_no, s, var) and var in rows[0] and rows[0][var] not in ("", "nan")]
        if not sims:
            continue
        mean, lo, hi = [], [], []
        for k in range(len(times)):
            vals = [float(per_sim[s][k][var]) for s in sims]
            if var.startswith("eulerAngle"):
                m = angle_mean(vals)
                dev = [wrap180(v - m) for v in vals]
                mean.append(r6(m)); lo.append(r6(m + min(dev))); hi.append(r6(m + max(dev)))
            else:
                mean.append(r6(statistics.mean(vals))); lo.append(r6(min(vals))); hi.append(r6(max(vals)))
        spread = max(h - l for h, l in zip(hi, lo))
        out[var] = {"mean": mean, "lo": lo, "hi": hi, "n": len(sims),
                    "tol": r6(nasa_base_tol(case_no, var) + spread / 2),
                    "sims": sorted(sims)}
    return out


def convert_nasa(src):
    d = os.path.join(src, "nasa_6dof_checkcases")
    c = load_json(os.path.join(d, "case.json"))
    s = c["source"]
    out = base_case(
        "nasa_6dof_checkcases",
        "NASA/NESC 6-DOF check-cases 1-8 (dropped sphere, tumbling brick, winds) — code-to-code verification",
        "VV-05/VV-48", "A", "nasa-checkcase",
        "6DOF 엔진(RK4 운동방정식) + 지구 모델(평면 NED · 역제곱/WGS-84 J2 중력 · 자전) + US1976 + 평균풍 + 감쇠 미계수",
        0.01,
        [{"title": f"{s['title']} ({s['report_number']})", "url": s["urls"][0],
          "note": "Vol.II App. B/C/D; reference time histories from NESC Academy (6 independent NASA tools)"}],
        envelope="평면 NED(수평 이동 수 km 이내, 곡률·수송률 무시) + 선택 지구 모델, 대기권 30 s 낙하(9.1→4.7 km)",
        notes=["실험이 아니라 코드 대 코드 검증(VV-05). 기준 = 포함된 NASA 도구들의 평균 시간이력(1 s 간격 표본).",
               "허용치 = 원 README 제안값(고도 ±3 ft, 4·5 는 ±20 ft, 오일러 ±0.1°, 각속도 ±0.05°/s; 속도는 Z ±0.3/±2 ft/s, Y ±0.1 ft/s) + 기준 도구 간 최대 폭의 절반.",
               "저자 제외 사항 적용: SIM 2 오일러각, SIM 2(케이스 4·5 전체), SIM 3(케이스 6–8 전체).",
               "초기 각속도는 관성 기준(표의 지구 상대값 + 지구 자전). 케이스 1 은 항력 0, 2 는 공력 0, 3 은 CD 0 + 감쇠."])
    out["origin"] = origin(d)
    times = [float(t) for t in range(0, 31)]
    for cs in c["cases"]:
        n = cs["case"]
        v = cs["vehicle"]
        if "Ixx=Iyy=Izz" in v:
            i = v["Ixx=Iyy=Izz"]["si"]
            veh = {"mass": v["mass"]["si"], "inertia": [i, i, i], "area": v["reference_area_S"]["si"],
                   "cd": 0.0 if n == 1 else v["CD"]}
        else:
            veh = {"mass": v["mass"]["si"], "inertia": [v["Ixx"]["si"], v["Iyy"]["si"], v["Izz"]["si"]],
                   "area": v["S"]["si"], "cd": 0.0}
            if n == 3:
                veh["damping"] = [v["Clp"], v["Cmq"], v["Cnr"]]
                veh["dampingLengths"] = [v["b"]["si"], v["cbar"]["si"], v["b"]["si"]]
        earth = dict(NASA_EARTH[n]); earth["latitude"] = 0.0
        inputs = {"case": n, "vehicle": veh, "earth": earth, "altitude_m": 9144.0,
                  "omegaInertial_deg_s": NASA_OMEGA[n], "duration_s": 30.0, "dt": 0.005}
        if n == 7:   # steady 20 ft/s from due west
            inputs["windProfile"] = [[0.0, 20 * FT, 270.0], [20000.0, 20 * FT, 270.0]]
        if n == 8:   # Vwind = (0.003 h - 20) ft/s from west, h ft MSL -> linear in h, vector interpolation exact
            def wv(h_ft):
                w = 0.003 * h_ft - 20.0
                return [h_ft * FT, abs(w) * FT, 270.0 if w >= 0 else 90.0]
            inputs["windProfile"] = [wv(0.0), wv(20000.0 / 3.0), wv(30000.0), wv(40000.0)]
        files = [os.path.join(d, f.replace("\\", "/")) for f in cs["reference_data_files"]]
        inputs["reference"] = {"t": times, "vars": nasa_reference(n, files, times)}
        out["points"].append({"label": f"case {n}", "inputs": inputs, "provenance": "simulation time histories (NASA tools)"})
    return [out]


# ---------------------------------------------------------------- Young / Thompson 1969

CALIB_SHOT = {7: 4, 8: 5, 2: 1, 3: 2, 6: 3}   # first primary shot of each multi-shot target


def young_nose(proj):
    if proj["nose_sharpness_angle_deg"] >= 179:
        return {"nose": "flat"}
    # Ln/d = 2.5 with tip half-angle 22.6 deg = tangent ogive (CRH 6.5); README: N ~ 1.01
    return {"nose": "ogive", "lOverD": 2.5}


def a_priori_s(soil):
    """Young Table 4-2 class from the moisture description: low moisture stiff clay 5-10, moist to wet 10-20 (mid-points)."""
    return 7.5 if soil["degree_of_saturation_pct"] <= 30 else 15.0


def convert_young(src):
    d = os.path.join(src, "penetration_young")
    c = load_json(os.path.join(d, "case.json"))
    srcs = [{"title": f"{s['title']} ({s['report_number']}, {s['year']})", "url": s["url"], "note": s["role"]} for s in c["sources"]]
    tests = c["measured_tests_thompson_1969"]
    targets = {}
    for t in tests:
        targets.setdefault(t["target"], []).append(t["test"])
    cal = base_case(
        "penetration_young_thompson1969",
        "Young penetration depth vs Thompson 1969 lab shots (2.5 kg, 40 mm, 51-110 m/s into compacted kaolin): S calibrated on one shot per target, other shots predicted",
        "VV-45", "A", "young-penetration", "Young 관입식(SAND97-2426, FR-GND-02)", 0.30, srcs,
        envelope="토양, 관입체 ≥ 5 lb(2.27 kg), 수직·받음각 0, 깊이 > 3 구경",
        notes=["S-number 는 측정값이 아니므로 타깃별로 첫 1차 시험 한 발로 S 를 보정하고(보정), 같은 타깃의 나머지 시험을 예측한다(검증).",
               "단일 시험 타깃(1, 4, 5)은 보정만 가능 — 보정된 S 를 참고로 보고.",
               "허용치 ±30 %(PRD VV-45, 원식의 통계적 산포 수준).",
               "속도 * 표시는 가스 압력에서 추정한 값(크로노그래프 없음, 원 보고서)."])
    cal["calibration"] = {"parameter": "soilS", "lo": 0.5, "hi": 200}
    cal["origin"] = origin(d)
    pri = base_case(
        "penetration_young_thompson1969_apriori",
        "Young penetration depth with S chosen a priori from Young Table 4-2 by moisture class (no calibration)",
        "VV-45", "B", "young-penetration", "Young 관입식(SAND97-2426, FR-GND-02)", 0.50, srcs,
        envelope="토양, 관입체 ≥ 5 lb(2.27 kg), 수직·받음각 0",
        notes=["S 를 표 4-2 범위의 중앙값으로 고른 사전 예측: 포화도 ≤ 30 % → '저·중 함수 실트·점토 5–10'(7.5), 그 외 → '습윤 실트·점토 10–20'(15).",
               "S 선택 자체가 판단(범위 2배 폭)이라 등급 B·허용 ±50 %(원 README 제안)."])
    pri["origin"] = origin(d)
    for t in tests:
        proj, imp, soil = t["projectile"], t["impact"], t["soil"]
        star = "*" if imp["velocity_estimated_from_gas_pressure"] else ""
        label = f"시험 {t['test']} (타깃 {t['target']}, {proj['total_mass_kg']} kg, {imp['velocity_m_s']}{star} m/s)"
        inputs = {"mass_kg": proj["total_mass_kg"], "diameter_m": proj["diameter_m"], "speed_mps": imp["velocity_m_s"]}
        inputs.update(young_nose(proj))
        single = len(targets[t["target"]]) == 1
        role = "calibration" if single or CALIB_SHOT.get(t["target"]) == t["test"] else "validation"
        pt = {"label": label, "group": f"T{t['target']}", "role": role, "inputs": inputs,
              "measured": t["measured_total_depth"]["m"], "unit": "m", "provenance": "table (Thompson 1969 Tables 3-5)"}
        # Point grades (VV-50): inputs that are estimates, not measurements, lower the point to B (warning only).
        notes = []
        if imp["velocity_estimated_from_gas_pressure"]:
            notes.append("충돌 속도가 가스 압력 추정값(크로노그래프 없음, 원 README: 1순위 검증점은 시험 1–8) → 점 등급 B")
        if proj["nose_sharpness_angle_deg"] < 179:
            notes.append("노즈 종류가 원표에 없음: Ln/d 2.5·첨예각 22.6° 에서 탄젠트 오자이브(CRH 6.5)로 추정해 N = 1.01 → 점 등급 B")
        if notes:
            pt["grade"] = "B"; pt["note"] = "; ".join(notes)
        cal["points"].append(pt)
        pi = dict(inputs); pi["soilS"] = a_priori_s(soil)
        pri["points"].append({"label": label, "inputs": pi, "measured": t["measured_total_depth"]["m"], "unit": "m",
                              "provenance": "table (Thompson 1969 Tables 3-5)"})
    return [cal, pri]


# ---------------------------------------------------------------- DGB opening loads (Cruz 2003)

def convert_cruz(src):
    d = os.path.join(src, "parachute_dgb_opening_loads")
    c = load_json(os.path.join(d, "case.json"))
    s = c["source"]
    out = base_case(
        "parachute_dgb_opening_cruz2003",
        "Peak opening loads of full-scale DGB parachutes: 11 NFAC wind-tunnel tests (infinite mass) + 4 heavy (8000 lb) drop tests",
        "VV-41", "A", "parachute-opening-infinite-mass",
        "낙하산 충만·개방하중(F절: 충만 곡선 + 겉보기 질량 유입 항 k0)", 0.25,
        [{"title": f"{s['title']} ({s['report_number']})", "url": s["url"], "note": "Tables 1-4"}],
        envelope="비압축(M ≤ 0.5), 무한 질량(풍동·중량 투하체), DGB D0 13–15 m",
        notes=["k0(겉보기 질량 계수)를 WTMD 11·12(같은 설계 반복시험)로 보정하고 나머지 시험의 최대 하중을 예측한다.",
               f"충만 거리 n = {FILL_CONSTANT_DGB} D0(WTMD 11·12 측정 8.18·8.19), 충만 곡선 Sr = (t/tf)^{FILL_EXPONENT:g} — 모든 시험에 같은 캐노피 상수로 적용.",
               "CD0 는 원 논문의 가정값(측정 아님). 풍동 속도는 막힘 보정 V_eff(식 8), 낙하시험은 가속도 적분으로 얻은 q(저자: 불확도 큼)에서 V=√(2q/ρ), ρ=1 (무한 질량 근사에서 하중은 ρ 와 무관).",
               "WTMD 2(완전 충만 실패), DTST 037/038(3000 lb, 최대 하중이 충만 66 % 에서 발생 — 유한 질량 효과)는 제외.",
               "허용 ±25 %(PRD VV-41)."])
    out["calibration"] = {"parameter": "apparentMassK0", "lo": 0.0, "hi": 10.0}
    out["origin"] = origin(d)
    for t in c["wind_tunnel_tests"]:
        if t["test_id"] == "WTMD 2":
            continue
        p, r, si = t["parachute"], t["results_original_units"], t["results_si"]
        out["points"].append({
            "label": f"{t['test_id']} ({p['type']}, q_eff {si['qeff_at_peak_Pa']:.0f} Pa)",
            "role": "calibration" if t["test_id"] in ("WTMD 11", "WTMD 12") else "validation",
            "inputs": {"d0_m": p["D0_m"], "cd0": r["CD0_assumed"], "rho_kg_m3": si["rho_kg_m3"], "v_m_s": si["Veff_at_peak_m_s"],
                       "fillConstant": FILL_CONSTANT_DGB, "fillExponent": FILL_EXPONENT, "measuredCx": r["Cx"]},
            "measured": si["peak_load_N"], "unit": "N", "provenance": "table (Cruz 2003 Tables 3-4)"})
    for t in c["drop_tests"]:
        r, si, p = t["results_original_units"], t["results_si"], t["parachute"]
        if r["pct_infl_PL"] != 100:
            continue
        q = si["q_at_peak_Pa"]
        out["points"].append({
            "label": f"{t['test_id']} (낙하 8000 lb, q {q:.0f} Pa)", "role": "validation",
            "inputs": {"d0_m": p["D0_m"], "cd0": r["CD0_assumed"], "rho_kg_m3": 1.0, "v_m_s": r6(math.sqrt(2 * q / 1.0)),
                       "fillConstant": FILL_CONSTANT_DGB, "fillExponent": FILL_EXPONENT, "measuredCx": r["Cx"]},
            "measured": si["peak_load_N"], "unit": "N", "provenance": "table (Cruz 2003 Table 2; q derived from accelerometers)"})
    return [out]


# ---------------------------------------------------------------- PEPP flight tests (TM X-1451, TM X-1414)

def flight_path_deg(speed, alt_ft, apogee_ft):
    """Ascending flight path angle implied by the apogee (ballistic, drag-free estimate)."""
    vz = math.sqrt(2 * 9.75 * (apogee_ft - alt_ft) * FT)
    return math.degrees(math.asin(min(vz / speed, 1.0)))


def convert_pepp(src, k0):
    out = []
    d = os.path.join(src, "parachute_dgb_200lb")
    c = load_json(os.path.join(d, "case.json"))
    s, i, m = c["source"], c["inputs"], c["measured_outputs"]["flight"]
    dep = i["deployment_condition_flight"]
    gam = flight_path_deg(dep["true_airspeed"]["si"], 127500, 134800)
    fl = base_case(
        "parachute_dgb_flight_tmx1451",
        "30-ft DGB supersonic mortar deployment (M 1.56, 38.9 km): peak opening load and time",
        "VV-40", "A", "parachute-deployment-flight", "낙하산 충만·개방하중(F절: 충만 곡선 + 겉보기 질량 유입 항 k0)", 0.20,
        [{"title": f"{s['title']} ({s['report_number']})", "url": s["url"]}],
        envelope="비압축(M ≤ 0.5) — 이 시험은 M 1.56 이라 외삽",
        notes=[f"충만 n = {FILL_CONSTANT_DGB}, k0 = {k0:g} 은 Cruz 2003 풍동 보정값(독립 예측). 경로각 {gam:.1f}° 는 정점 고도에서 역산한 값(계산).",
               "측정 최대 하중은 텐시오미터(리저) 값 — 시뮬레이터 하중도 탑재체에 걸리는 낙하산 하중.",
               "쉬라우드 간섭으로 팽창이 0.60–0.73 s 정체(원 보고서) — 시각 비교는 참고 수준."])
    fl["origin"] = origin(d)
    fl["points"].append({"label": "TM X-1451 DGB", "inputs": {
        "mass_kg": i["payload"]["total_descent_mass_incl_parachute"]["si"], "d0_m": i["parachute"]["nominal_diameter_D0"]["si"],
        "cd0": m["mean_CD0_deceleration_phase"]["si"], "altitude_m": dep["altitude_msl"]["si"], "speed_mps": dep["true_airspeed"]["si"],
        "flightPathDeg": round(gam, 1), "lineStretchTime_s": m["time_mortar_fire_to_line_stretch"]["si"],
        "fillConstant": FILL_CONSTANT_DGB, "fillExponent": FILL_EXPONENT, "apparentMassK0": k0,
        "peakTime_s": m["peak_opening_load"]["at_time_s"]},
        "measured": m["peak_opening_load"]["si"], "unit": "N", "provenance": "table (TM X-1451 Summary, p.7)"})
    out.append(fl)

    # steady descent vs altitude (Fig.18 digitized) — consistency: CD0eff 0.47 comes from the same data
    desc = base_case(
        "parachute_dgb_descent_tmx1451",
        "30-ft DGB steady descent velocity vs altitude 24-39 km (Fig.18 digitized) with the reported effective CD0 0.47",
        "VV-40", "B", "parachute-steady-descent", "낙하산 정상 하강(CdS 평형) + US1976 고고도 밀도", 0.08,
        [{"title": f"{s['title']} ({s['report_number']})", "url": s["url"], "note": "Fig.18 digitized (+-300 ft, +-5 ft/s)"}],
        envelope="정상 하강, M < 0.5, 24–39 km", use="consistency",
        notes=["유효 CD0 0.47 은 같은 하강 자료에서 저자가 구한 값 → 독립 검증이 아니라 일관성 확인(대기 모델 37 km 까지 + 평형 하강 계산).",
               "허용 ±8 %(원 README: 디지타이즈 ±5 ft/s + 밀도 ±3 % → 속도 ±1.5 % + 잔여 감속)."])
    desc["origin"] = origin(d)
    with open(os.path.join(d, "digitized", "fig18_descent_points.csv"), newline="") as f:
        rows = list(csv.DictReader(line for line in f if not line.startswith("#")))
    for r in rows:
        if "post-reef" in r["series_note"] and "before" not in r["series_note"]:
            continue   # after post-reefing: not the test configuration
        alt_ft, v = float(r["altitude_ft"]), float(r["descent_velocity_ftps"]) * FT
        p = {"label": f"{alt_ft * FT / 1000:.1f} km", "inputs": {"altitude_m": round(alt_ft * FT, 1)},
             "measured": round(v, 2), "unit": "m/s", "provenance": "digitized"}
        if alt_ft > 110000:
            # source README: residual deceleration above ~110 kft (not yet at equilibrium descent) -> reference only
            p["role"] = "info"; p["note"] = "110 kft 위는 잔여 감속(평형 전, 원 README) → 참고"
        desc["points"].append(p)
    desc["inputs"] = {"mass_kg": i["payload"]["total_descent_mass_incl_parachute"]["si"], "d0_m": i["parachute"]["nominal_diameter_D0"]["si"],
                      "cd0": m["mean_effective_CD0_steady_descent"]["si"], "geopotential": True}
    out.append(desc)

    d2 = os.path.join(src, "parachute_ringsail_tmx1414")
    c2 = load_json(os.path.join(d2, "case.json"))
    s2, i2, m2 = c2["source"], c2["inputs"], c2["measured_outputs"]
    gam2 = flight_path_deg(i2["deployment_true_airspeed"]["si"], 122500, 126600)
    rs = base_case(
        "parachute_ringsail_flight_tmx1414",
        "31.2-ft ringsail supersonic mortar deployment (M 1.39, 37.3 km): peak opening load and time",
        "VV-40", "A", "parachute-deployment-flight", "낙하산 충만·개방하중(F절: 충만 곡선 + 겉보기 질량 유입 항 k0)", 0.20,
        [{"title": f"{s2['title']} ({s2['report_number']})", "url": s2["url"], "note": "tensiometer +-50 lb"}],
        envelope="비압축(M ≤ 0.5) — 이 시험은 M 1.39 라 외삽",
        notes=[f"충만 n = {FILL_CONSTANT_DGB}, k0 = {k0:g}(DGB 풍동 보정값을 링세일에 그대로 — 캐노피 종류 차이는 모델에 없음). 경로각 {gam2:.1f}° 는 정점에서 역산."])
    rs["origin"] = origin(d2)
    rs["points"].append({"label": "TM X-1414 ringsail", "inputs": {
        "mass_kg": i2["total_descent_mass"]["si"], "d0_m": i2["nominal_diameter_D0"]["si"], "cd0": m2["mean_CD0_deceleration"]["si"],
        "altitude_m": i2["deployment_altitude"]["si"], "speed_mps": i2["deployment_true_airspeed"]["si"], "flightPathDeg": round(gam2, 1),
        "lineStretchTime_s": m2["snatch_force_line_stretch"]["at_time_s"], "fillConstant": FILL_CONSTANT_DGB,
        "fillExponent": FILL_EXPONENT, "apparentMassK0": k0, "peakTime_s": m2["peak_opening_load"]["at_time_s"]},
        "measured": m2["peak_opening_load"]["si"], "unit": "N", "provenance": "table (TM X-1414)"})
    out.append(rs)
    return out


# ---------------------------------------------------------------- UIUC VRS / static

VRS_CALIBRATION = ("TE10x5", "SF9x4.7", "SP10x5")


def convert_vrs(src):
    d = os.path.join(src, "rotor_vrs_uiuc")
    c = load_json(os.path.join(d, "case.json"))
    s = c["source"]
    t1 = {p["id"]: p for p in c["propellers_table1"]}
    out = base_case(
        "rotor_vrs_uiuc",
        "Small propellers (9-11 in, 4000 RPM) in axial descent through the vortex ring state: CT/CT0 vs J (Shetty 2010 Appendix B)",
        "VV-43", "A", "rotor-vrs-ct-ratio", "로터 하강 추력(FR-UAV-02 v2: BET+운동량+경험 VRS, RotorModel)", 0.1,
        [{"title": f"{s['title']} ({s['report_number']})", "url": s["url"]},
         {"title": s["thesis"]["title"] + " (M.S. thesis, UIUC 2010)", "url": s["thesis"]["url"], "note": "Appendix B tables"}],
        envelope="축방향 하강 0 ≤ V/vh ≤ 3.3, 9–11 in 프로펠러, 4000 RPM",
        notes=[f"RotorModel 계수(a_eff, D, xc, w)는 {', '.join(VRS_CALIBRATION)} 세 곡선으로 보정(tools/validation/fit_rotor_vrs.py) — 이 셋은 보정, 나머지는 검증.",
               "판정(PRD VV-43): 곡선 RMS ≤ 0.1, 최저점 J ±0.1. 측정 최저점 깊이 < 5 % 인 곡선은 최저점이 정의되지 않아 위치 판정 제외.",
               "CT0 는 UIUC propDB 정적 시험(다른 시험 캠페인)을 4000 RPM 으로 보간한 값.",
               "SF10x4.7 의 마지막 점(J −0.735)은 원 자료에서 이상치로 분리됨."])
    out["origin"] = origin(d)
    curves = c["measured_outputs"]["CT_vs_J_tabulated_thesis_appendixB"]["curves"]
    for e in curves:
        if not e.get("CT0_static_propDB_4000rpm") or e["id"] not in t1:
            continue
        data = [[row[0], row[2]] for row in e["data"]]
        out["points"].append({"label": e["id"], "role": "calibration" if e["id"] in VRS_CALIBRATION else "validation",
                              "inputs": {"solidity": t1[e["id"]]["solidity"], "ct0": e["CT0_static_propDB_4000rpm"], "data": data},
                              "provenance": "table (thesis App. B; CT0 propDB)"})
    return [out]


def read_static(path):
    rows = []
    with open(path) as f:
        for line in f:
            parts = line.split()
            if parts and parts[0][0].isdigit():
                rows.append((float(parts[0]), float(parts[1]), float(parts[2])))
    return rows


def convert_static(src):
    d = os.path.join(src, "propeller_static_uiuc")
    out = base_case(
        "propeller_static_uiuc",
        "Static thrust coefficient vs RPM of APC 8x4 Thin Electric and 8x3.8 Slow Flyer (UIUC propDB vol.1)",
        "VV-44", "A", "propeller-static-ct", "프로펠러 정적 추력계수 CT(RPM) (RotorModel.StaticCt)", 0.05,
        [{"title": "UIUC Propeller Data Site, vol.1 (Brandt & Selig)", "url": "https://m-selig.ae.illinois.edu/props/propDB.html"}],
        envelope="정지(J = 0), 3000–7000 RPM, 8 in APC", use="calibration",
        notes=["RPM 지수 p(CT ∝ n^p)는 이 두 프로펠러로 정한 값이라 보정용 — 독립 검증 자료 없음.",
               "기준 CT 는 각 프로펠러의 ~5000 RPM 점. '일정 k_T' 참고 줄은 RPM 의존을 무시한 경우의 오차(최대 ±10 %)."])
    out["origin"] = {"source_case": source_ref(os.path.join(d, "case.json")), "sha256": sha256_of(os.path.join(d, "case.json")),
                     "converter": "tools/validation/convert_public.py"}
    for name, fn in (("APC TE 8x4", "apce_8x4_static_2791rd.txt"), ("APC SF 8x3.8", "apcsf_8x3.8_static_2777rd.txt")):
        rows = read_static(os.path.join(d, "source", fn))
        ref = min(rows, key=lambda r: abs(r[0] - 5000))
        for rpm, ct, _ in rows:
            out["points"].append({"label": f"{name} {rpm:.0f} RPM", "inputs": {"ctRef": ref[1], "rpmRef": ref[0], "rpm": rpm},
                                  "measured": ct, "unit": "-", "provenance": f"table ({fn})"})
    return [out]


# ---------------------------------------------------------------- reference drag data

def convert_drag(src):
    d = os.path.join(src, "reference_drag_curves")
    c = load_json(os.path.join(d, "case.json"))
    srcs = {s["key"]: s for s in c["sources"]}
    out = []
    br = srcs["bacon_reid_1924"]
    sph = base_case(
        "sphere_free_fall_bacon_reid1924",
        "Spheres dropped from an airplane (NACA Rep.185): maximum (terminal) speed, Re 3.5e5-1.5e6",
        "VV-47", "A", "sphere-free-fall", "L1 형상 항력(구 Cd 0.47) + 6DOF 낙하", 0.05,
        [{"title": f"{br['title']} ({br['report_number']})", "url": br["url"], "note": "Table VI"}],
        envelope=f"L1 형상 Cd: Re ≤ 2×10⁵(아임계)",
        notes=["모든 낙하는 Re 3.5×10⁵ 이상(항력 위기·초임계)이라 L1 상수 Cd(0.47) 의 적용 범위 밖 → 외삽으로 오차만 보고.",
               "저자가 신뢰 불가로 표시한 (?) 낙하(덧댄 고무구)는 제외, 종단에 이르지 않은 1회(j = 0.5 m/s²)는 참고.",
               "허용 ±5 %(속도; Cd ±10 % 에 해당). 낙하 고도 = 측정 고도 근사(측정 고도의 밀도는 원 표에 없음)."])
    sph["origin"] = origin(d)
    cols = c["sphere_free_fall_drops_in_air"]["columns"]
    for row in c["sphere_free_fall_drops_in_air"]["rows"]:
        r = dict(zip(cols, row))
        if r["flag"].startswith("(?)"):
            continue
        p = {"label": f"{r['sphere']} {r['weight_kg']} kg, Ø{r['diameter_m']} m, {r['drop_altitude_ft']} ft",
             "inputs": {"mass_kg": r["weight_kg"], "diameter_m": r["diameter_m"], "dropHeight_m": r["drop_altitude_ft"] * FT},
             "measured": r["V_max_m_s"], "unit": "m/s", "provenance": "table (Table VI)"}
        if r["flag"]:
            p["role"] = "info"
        sph["points"].append(p)
    out.append(sph)

    gp = srcs["gowen_perkins_1953"]
    cyl = base_case(
        "drag_cylinder_cd_re_gowen1953",
        "2-D circular cylinder crossflow CD vs Re 1e4-1e6 (NACA TN 2960 Fig.6, digitized band centre)",
        "VV-47", "B", "drag-coefficient", "L1 형상 항력(원통 횡류 Cd, L/D→∞ 1.2)", 0.05,
        [{"title": f"{gp['title']} ({gp['report_number']})", "url": gp["url"], "note": "Fig.6 digitized"}],
        envelope="L1 형상 Cd: Re ≤ 2×10⁵(아임계)",
        notes=["2-D(무한 길이) 비교라 L/D = 1000 원통으로 계산. 항력 위기(Re > 2×10⁵) 구간은 L1 적용 범위 밖 → 외삽.",
               "중요: 컨테이너(Ø0.41 m, 최대 139 m/s)는 Re ~4×10⁶ — 매끈한 원통이면 초임계(CD ~0.3–0.6)라 L1 횡류 Cd 가 과대일 수 있음(CFD/풍동 필요)."])
    cyl["origin"] = origin(d)
    cyl["inputs"] = {"shape": {"type": "cylinder", "radius": 0.5, "length": 1000.0, "axis": "x"}, "flowDirection": [0, 0, 1]}
    for re_, cd, lo, hi in c["circular_cylinder_crossflow_CD_vs_Re"]["rows"]:
        cyl["points"].append({"label": f"Re {re_:.2g}", "inputs": {"re": re_}, "measured": cd, "unit": "-",
                              "provenance": f"digitized (band {lo}-{hi})"})
    out.append(cyl)
    return out


def convert_tumbling(src):
    d = os.path.join(src, "freefall_cylinder")
    c = load_json(os.path.join(d, "case.json"))
    ds = next(x for x in c["datasets"] if x["key"] == "britcher_tumbling_air")
    s = ds["source"]
    out = base_case(
        "tumbling_cylinder_britcher",
        "Time-averaged drag of free-to-tumble L/D = 1 cylinders in air (magnetic suspension wind tunnel, 6-22 m/s)",
        "VV-46", "B", "tumbling-mean-drag", "L1 형상 항력(원통) — 텀블링 회전각 평균", 0.10,
        [{"title": s["title"] + " (NTRS " + s["ntrs_id"] + ")", "url": s["url"], "note": "Fig.6 vector-digitized"}],
        envelope="L1 형상 Cd: Re ≤ 2×10⁵",
        notes=["L1 은 회전각별 투영 항력의 평균만 계산(마그누스·자동회전·회전 유발 항력 없음). 기준면적 = 원형 단면 πD²/4.",
               "저주파 고정(lock-in, 회전비 0.02) 모드는 참고.",
               "공기 중 원통·상자 자유낙하 6-DOF 자세 실측은 공개 자료 없음(원 INDEX) — 물속 MIDEX 자료는 매질이 달라 변환하지 않음."])
    out["origin"] = origin(d)
    for r in ds["measured_outputs"]["digitized_points"]["summary_1to1_cylinders_6_to_22_m_s"]:
        dm = r["D_mm"] / 1000.0
        re_ = 1.2 * 14.0 * dm / 1.81e-5   # mid tunnel speed 14 m/s, sea-level air (computed)
        p = {"label": f"L/D 1, Ø{r['D_mm']} mm" + (" (lock-in)" if r.get("mode") else ""),
             "inputs": {"diameter_m": dm, "length_m": dm, "re": round(re_, -2)},
             "measured": r["CD_tumbling_mean"], "unit": "-", "provenance": f"digitized (range {r['CD_range'][0]}-{r['CD_range'][1]})"}
        if r.get("mode"):
            p["role"] = "info"
        out["points"].append(p)
    return [out]


# ---------------------------------------------------------------- 2차 수집(PRD FR-REL-06)

RE_SUBCRITICAL = 2.0e5      # Shape.MaxValidReynolds — 아임계 상수 CD 구간 상한(항력 위기 시작)
CD_SUBCRITICAL_LOW_M = 1.20  # 2-D 원통 아임계 CD (M ≤ 0.2, Gowen–Perkins Fig.6 대역 중앙 / White 표 7.3 무한 길이)


def bin_points(pts, rel=0.01):
    """(Re, CD) 점을 Re 순으로 정렬하고 Re 가 rel(1 %) 이내로 붙은 점끼리 평균(같은 조건 반복 측정) — 나머지는 저자 점을 그대로 잇는다.
    측정 산포는 표 I(a) 원점과의 잔차로 따로 보고(UQ 모델 형태 산포)."""
    pts = sorted(pts)
    out, cur = [], [pts[0]]
    for p in pts[1:]:
        if p[0] <= cur[0][0] * (1 + rel):
            cur.append(p)
        else:
            out.append(cur)
            cur = [p]
    out.append(cur)
    return [[r6(math.exp(statistics.fmean(math.log(x[0]) for x in g))), r6(statistics.fmean(x[1] for x in g)), len(g)] for g in out]


def cylinder_model_data(src):
    """원통 횡류 2-D CD(Re, M) 모델 표(TR R-300 표 II 일정 마하 행) + 아임계 CD–M(TN 2960 그림 7) + 유한 길이 η(그림 8)."""
    d = os.path.join(src, "cylinder_crossflow_mach")
    c = load_json(os.path.join(d, "case.json"))
    rows = []
    for m, groups in sorted(c["tr_r300_table_II_constant_Mach"]["rows"].items(), key=lambda kv: float(kv[0])):
        pts = [(re_, cd) for g in groups.values() for cd, re_ in g]
        rows.append({"mach": float(m), "points": bin_points(pts), "gases": sorted(groups)})
    fig7 = c["tn2960_fig7_mach_effect"]["symbols_subcritical_Re"]["circle_0.5in_Ames_1x3.5ft"]
    sub = [[0.0, CD_SUBCRITICAL_LOW_M], [0.2, CD_SUBCRITICAL_LOW_M]] + [[p["M"], p["CD"]] for p in fig7]
    eta = [[p["L_over_D"], p["eta"]] for p in c["tn2960_fig8_finite_length"]["curve_ref11_M_lt_0.1_Re_88000"]]
    return {
        "grade": "A (TR R-300 Table II, cross-plotted by the authors from Table I(a)) / B (TN 2960 Fig.7 subcritical CD-M, Fig.8 eta(L/D))",
        "sources": [s.get("report_number", s.get("key")) for s in c["sources"]],
        "reSubcritical": RE_SUBCRITICAL,
        "machRows": rows,
        "subcriticalCdVsMach": sub,
        "eta": eta,
        "notes": [
            "machRows: TR R-300 표 II 일정 마하 행(공기 + Freon-12) — Re 가 1 % 이내인 반복점만 평균(마지막 수 = 점 수), 나머지는 저자 점을 그대로 잇는다.",
            "아임계(Re ≤ 2×10⁵) CD: M ≤ 0.2 는 1.20(Gowen Fig.6·White 표 7.3), M ≥ 0.3 은 TN 2960 그림 7(0.5 in 원통, Re 0.85–1.57×10⁵, B).",
            "Re_sub ~ 행 최소 Re 사이는 log Re 선형(항력 위기 구간은 표 II 의 저 Re Freon 점이 있으면 그 점이 형태를 정한다).",
            "η(L/D): TN 2960 그림 8 아임계(Re 8.8×10⁴, M < 0.1) 곡선 — 초임계·M 0.5 의 L/D < 15 η 공개 자료 없음(±0.08 판단, README).",
        ],
        "origin": origin(d),
    }


def convert_cylinder_mach(src):
    d = os.path.join(src, "cylinder_crossflow_mach")
    c = load_json(os.path.join(d, "case.json"))
    s = c["sources"][0]
    out = base_case(
        "drag_cylinder_crossflow_mach_tr_r300",
        "2-D circular cylinder crossflow CD vs Re (0.59e6-18.3e6) and Mach (0.09-0.60): 133 measured points (NASA TR R-300 Table I(a))",
        "FR-REL-06", "A", "cylinder-crossflow-2d", "원통 횡류 2-D CD(Re, M) 표(TR R-300·TN 2960, aero.crossflow tr-r300)", 0.1,
        [{"title": f"{s['title']} ({s['report_number']}, {s['year']})", "url": s["url"], "note": "Table I(a) gaps sealed"}],
        envelope="2-D CD: 0 ≤ M ≤ 0.6, Re ≤ 1.9×10⁷", use="consistency",
        notes=["모델 표(행)는 같은 보고서 표 II(저자가 표 I(a) 를 일정 마하로 다시 그린 값)라 독립 검증이 아니라 일관성 확인 — 표 보간 오차를 잰다.",
               "허용(README 제안): |ΔCD| ≤ 0.05 (M ≤ 0.4), 0.10 (M > 0.4, 기울기 급함). 반복점 차 0.03–0.04 가 측정 재현성.",
               "Freon-12(γ ≈ 1.14) 점은 원문과 같이 같은 M 으로 취급. 표 I(b)(틈 열림)는 옮기지 않음.",
               "독립 비교: OpenFOAM 2-D 원통 기준 사례(validation/reports/cfd) — FR-REL-01."])
    out["origin"] = origin(d)
    out["notes"][0] = ("모델 표는 같은 보고서 표 II(저자가 표 I(a) 를 일정 마하로 다시 그린 값) → 판정 줄 = 표 II 점(구현 일관성, 반복점 평균과의 차). "
                       "표 I(a) 원점 133개는 '참고' 줄 — 표 II 재작도·측정 산포·항력 위기 기울기에 따른 차이를 그대로 보여 주고, "
                       "그 잔차(RMS)를 불확도 정량화(FR-REL-03)의 모델 형태 산포로 쓴다. 독립 검증 아님.")
    for m, groups in sorted(c["tr_r300_table_II_constant_Mach"]["rows"].items(), key=lambda kv: float(kv[0])):
        for gas, pts in groups.items():
            for cd, re_ in pts:
                out["points"].append({"label": f"표 II M {float(m):.2f} {gas} Re {re_ / 1e6:.2f}e6", "inputs": {"re": re_, "mach": float(m)},
                                      "measured": cd, "unit": "-", "toleranceAbs": 0.05 if float(m) <= 0.4 else 0.10,
                                      "provenance": "table (TR R-300 Table II)"})
    for gas, pts in c["tr_r300_table_I_a"]["groups"].items():
        for cd, m, re_ in pts:
            out["points"].append({"label": f"표 I(a) {gas} M {m:.3f} Re {re_ / 1e6:.2f}e6", "inputs": {"re": re_, "mach": m},
                                  "measured": cd, "unit": "-", "toleranceAbs": 0.05 if m <= 0.4 else 0.10, "role": "info",
                                  "provenance": "table (TR R-300 Table I(a))"})
    return [out]


FILL_CONSTANT_CROSS = 11.7    # Knacke 표 5-6 십자형 비리핑 n (A)


def convert_cross40(src, k0):
    d = os.path.join(src, "parachute_cross_40in_opening_shock")
    c = load_json(os.path.join(d, "case.json"))
    s = c["source"]
    s0 = c["test_conditions"]["reference_area_S0_m2"]
    d0 = math.sqrt(4 * s0 / math.pi)
    src_ref = [{"title": f"{s['title']} ({s['report_number']}, {s['date']})", "url": s["url"], "note": "Tables 3-4"}]
    common_notes = [f"D0 = √(4 S0/π) = {d0:.3f} m(S0 = 2LW − W², 원문), CD0 = 표 4 정상 항력계수(같은 구성), 해면 ρ 1.225(무한 질량 Cx 는 ρ 무관).",
                    f"충만 상수 n = {FILL_CONSTANT_CROSS}(Knacke 표 5-6 십자형, A), 충만 곡선 지수 2(POC 기본).",
                    "측정 최대 하중 = Cx(표 3, 3회 평균) × q × CD0 × S0 (Cx 정의: 최대 충격력/정상 항력, 원문).",
                    "허용 ±25 %(VV-41 과 같음). 저자: 투과도가 지배, 줄 수·길이 영향 작음 → 구성 간 차 ±0.05–0.15."]
    cal = base_case(
        "parachute_cross40in_opening_noltr73157",
        "Small (40-in, S0 0.473 m2) cross parachutes in a wind tunnel: infinite-mass opening-shock factor Cx 1.07-1.41 (27 configurations, NOLTR 73-157) — k0 per cloth permeability",
        "FR-REL-06", "A", "parachute-opening-infinite-mass",
        "낙하산 충만·개방하중(F절: 충만 곡선 + 겉보기 질량 유입 항 k0)", 0.25, src_ref,
        envelope="비압축(M ≤ 0.5), 무한 질량(풍동), 소형 캐노피 S0 0.47 m²",
        notes=["모델에 천 투과도 항이 없으므로 k0(겉보기 질량 계수)를 투과도 그룹(8/80/208 cfm)마다 한 구성(16 줄·줄 1.4D)으로 보정하고 "
               "같은 그룹의 나머지 8 구성을 예측한다(보정/검증 분리, VV-50). 보정된 k0 의 투과도 경향이 결과다."] + common_notes)
    cal["calibration"] = {"parameter": "apparentMassK0", "lo": 0.0, "hi": 10.0}
    cal["origin"] = origin(d)
    pri = base_case(
        "parachute_cross40in_opening_noltr73157_apriori",
        "Small (40-in) cross parachutes: infinite-mass opening loads predicted a priori with k0 calibrated on large DGB canopies (Cruz 2003)",
        "FR-REL-06", "B", "parachute-opening-infinite-mass",
        "낙하산 충만·개방하중(F절: 충만 곡선 + 겉보기 질량 유입 항 k0)", 0.25, src_ref,
        envelope="비압축(M ≤ 0.5), 무한 질량(풍동), 소형 캐노피 S0 0.47 m²",
        notes=[f"사전 예측: k0 = {k0:g}(Cruz 2003 대형 DGB 풍동 보정값)을 십자형 소형 캐노피에 그대로 — 캐노피 종류를 넘는 계수 전이는 판단이라 "
               "점 등급 B(경고만, Young 사전 예측 사례와 같은 규칙). 이 오차가 소형 낙하산 개방하중 모델 형태 불확도(FR-REL-03)의 근거."] + common_notes)
    pri["origin"] = origin(d)
    cds = {(r["cloth_permeability_cfm_ft2"], r["n_lines"], r["line_length_over_D"]): r for r in c["table4_steady_CD"]["rows"]}
    for r in c["table3_shock_factor"]["rows"]:
        key = (r["cloth_permeability_cfm_ft2"], r["n_lines"], r["line_length_over_D"])
        t4 = cds[key]
        v, rho = t4["V_m_s"], 1.225
        q = 0.5 * rho * v * v
        cx = r["shock_factor_avg_3_tests"]
        inputs = {"d0_m": r6(d0), "cd0": t4["CD_on_S0"], "rho_kg_m3": rho, "v_m_s": v, "fillConstant": FILL_CONSTANT_CROSS,
                  "fillExponent": FILL_EXPONENT, "measuredCx": cx}
        pt = {"label": f"{key[0]} cfm, {key[1]} 줄, 줄 {key[2]}D", "inputs": inputs,
              "measured": r6(cx * q * t4["CD_on_S0"] * s0), "unit": "N",
              "provenance": "table (NOLTR 73-157 Tables 3-4; force = Cx·q·CD0·S0 computed)"}
        cal["points"].append(dict(pt, group=f"{key[0]}cfm", role="calibration" if key[1:] == (16, 1.4) else "validation"))
        pri["points"].append(dict(pt, inputs=dict(inputs, apparentMassK0=k0)))
    return [cal, pri]


def knacke_data(src):
    d = os.path.join(src, "parachute_opening_force_knacke")
    c = load_json(os.path.join(d, "case.json"))
    rows = []
    for tbl in ("table_5_1_solid_textile", "table_5_2_slotted"):
        for r in c["cx_tables_knacke"][tbl]:
            if isinstance(r.get("Cx_inf_mass"), (int, float)):
                rows.append({"type": r["type"], "cx": r["Cx_inf_mass"], "cd0": r.get("CD0_range"), "source": r.get("source", tbl)})
    n = {r["type"]: r["n_unreefed"] for r in c["fill_constant_table_5_6"]["rows"]}
    x1 = [[p["W_over_CDS_Pa"], p["X1"]] for p in c["fig5_48_X1_vs_canopy_loading"]["points"]]
    ck = [[p.get("Rm"), p.get("Ck")] for p in c["fig5_50_Ck_vs_Rm"]["points"]]
    return {"grade": "A (Knacke Tables 5-1, 5-2, 5-6) / B (Fig.5-48, 5-50)", "cxTable": rows, "fillConstant": n,
            "x1VsCanopyLoadingPa": x1, "ckVsRm": ck, "origin": origin(d),
            "notes": ["표 값은 여러 시험의 핸드북 대표값(불확도 없음). 그림 5-50 산포 ±0.2(눈 판독).",
                      "X1 곡선은 17 Pa(0.36 psf) 아래·Rm 0.067 위는 없음 — 박스 낙하산(약 30 Pa, Rm ≈ 0.6)은 곡선 범위 밖."]}


def criteria_data(src):
    d = os.path.join(src, "shock_tolerance_uav")
    return {"grade": "A (MIL-STD-810H 516.8 tables) / C (BMI088 datasheet, UN 38.3 report quote)",
            "functionalShockFlight_g": 20.0, "functionalShockDuration_ms": 11.0,
            "launchEject_g": 30.0, "crashHazardFlight_g": 40.0,
            "lipoUn383_g": 150.0, "lipoUn383_ms": 6.0, "imuBmi088_g_1ms": 2000.0,
            "airdropLandingLow_mps": 8.7,
            "notes": ["810H g 값은 시험 입력(규격)이지 고장 한계가 아니다 — '규격 시험 수준을 넘는가'로 판정.",
                      "Pixhawk 보드 단위 충격 정격은 공개 자료 없음(IMU 부품 정격만)."],
            "origin": origin(d)}


def components_data(src):
    """브레이크 코드 · 테더 · 폼 패드 입력값(출처 등급 포함) — 시나리오 생성기와 입력 출처 레지스트리가 쓴다."""
    tx = load_json(os.path.join(src, "textile_cords_tapes", "case.json"))
    t9 = next(r for r in tx["v_t_295_nylon_threads"]["rows"] if r["type"] == "I" and r["size"] == "9")
    te = load_json(os.path.join(src, "tether_dyneema_nylon", "case.json"))
    li = te["stiffness_computed"]["liros_2mm"]
    mw = te["stiffness_computed"]["marlow_2p5mm_min_linear"]
    fo = load_json(os.path.join(src, "foam_epp_eva", "case.json"))
    epp45 = next(r for r in fo["epp_arpro"]["rows"] if r["moulded_density_g_l"] == 45)
    return {
        "brakeCord": {"spec": "V-T-295 nylon thread Type I size 9 (AFFDL Table 4.3)", "breakLoad_N": t9["min_break_N"],
                      "maxElongation": [0.22, 0.33], "ratedElongationFig46": 0.20,
                      "grade": "A", "note": "규격 최소값(평균은 더 높음) — 분산 −0/+25 % 제안(README). 신율: Class I ≤ 22 %·II ≤ 33 %(A), "
                                            "AFFDL 그림 4.6 평균 곡선 정격 20 %(B). 실 굵기 선택은 설계 가정.",
                      "origin": origin(os.path.join(src, "textile_cords_tapes"))},
        "tether": {"spec": "LIROS 2 mm SK78 12-strand (Marlow D12 곡선 모양 가정)", "breakLoad_N": li["MBL_N"],
                   "ea_N": li["EA_secant_new_0_20pct_N"], "eaRange_N": [li["EA_secant_new_0_20pct_N"], mw["EA_secant_new_0_20pct_N"]],
                   "breakRange_N": [li["MBL_N"], mw["MBL_N"]], "grade": "C",
                   "note": "제조사·유통사 값(공인 시험 없음). 첫 충격 = 새 로프 0–20 % 할선 EA.",
                   "origin": origin(os.path.join(src, "tether_dyneema_nylon"))},
        "eppPad": {"spec": "ARPRO 5135 45 g/l", "stress25_kPa": epp45["stress_25pct_kPa"], "stress50_kPa": epp45["stress_50pct_kPa"],
                   "stress75_kPa": epp45["stress_75pct_kPa"], "grade": "C",
                   "note": "준정적 ISO 844 전형값 ±20 %. 동적 배율 1.0–1.5 는 출처 없음(D, 민감도용).",
                   "origin": origin(os.path.join(src, "foam_epp_eva"))},
    }


def credibility_data(src):
    """NASA-STD-7009A 부록 E 8요인 × 수준 0–4 정의(원문 그대로) + 평가 규칙(합산 금지) — FR-REL-04 채점 기준."""
    d = os.path.join(src, "credibility_framework")
    c = load_json(os.path.join(d, "case.json"))
    return {"grade": "A (NASA-STD-7009A, 2016-07-13, public release, verbatim extraction)",
            "source": c["source"]["title"] + " — " + c["source"]["url"],
            "terminologyNote": c["terminology_note"], "categories": c["categories"],
            "rules": c["assessment_rules_verbatim"]["E.4"], "requirementMS31": c["requirement_MS31"]["text"],
            "factors": [{"factor": f["factor"], "category": f["category"], "levels": f["level_definitions_verbatim"],
                         "source": f["level_definitions_source"]} for f in c["factors"]],
            "origin": origin(d)}


def reference_data(src):
    return {"schema": "mns.reference-data/1", "generator": "tools/validation/convert_public.py",
            "cylinderCrossflow": cylinder_model_data(src), "parachuteKnacke": knacke_data(src),
            "shockCriteria": criteria_data(src), "components": components_data(src),
            "credibility7009A": credibility_data(src)}


# ---------------------------------------------------------------- driver

def convert_all(src, k0):
    cases = []
    cases += convert_nasa(src)
    cases += convert_young(src)
    cases += convert_cruz(src)
    cases += convert_pepp(src, k0)
    cases += convert_vrs(src)
    cases += convert_static(src)
    cases += convert_drag(src)
    cases += convert_tumbling(src)
    cases += convert_cylinder_mach(src)
    cases += convert_cross40(src, k0)
    return cases


def write_cases(cases, dst):
    for c in cases:
        d = os.path.join(dst, c["id"])
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "case.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(c, f, ensure_ascii=False, indent=1)
            f.write("\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC)
    ap.add_argument("--dst", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "validation", "public"))
    ap.add_argument("--reference-data", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Sim.Core",
                                                          "Models", "reference_data.json"),
                    help="모델 기준 자료(원통 Cd(Re,M)·Knacke·충격 기준·부품 값) — Sim.Core 내장 리소스")
    ap.add_argument("--k0", type=float, default=None,
                    help="apparent-mass k0 calibrated on the Cruz 2003 tunnel tests (from `mns validate-public` report)")
    a = ap.parse_args()
    k0 = a.k0 if a.k0 is not None else K0_CRUZ_CALIBRATED
    cases = convert_all(a.src, k0)
    write_cases(cases, os.path.abspath(a.dst))
    for c in cases:
        print(f"{c['id']:<44} {c['kind']:<34} points {len(c['points'])}")
    ref = os.path.abspath(a.reference_data)
    with open(ref, "w", encoding="utf-8", newline="\n") as f:
        json.dump(reference_data(a.src), f, ensure_ascii=False, indent=1)
        f.write("\n")
    print(f"모델 기준 자료 → {ref}")


# k0 calibrated by `mns validate-public` on WTMD 11/12 (parachute_dgb_opening_cruz2003 report) — used for the flight cases.
K0_CRUZ_CALIBRATED = 1.2623

if __name__ == "__main__":
    main()
