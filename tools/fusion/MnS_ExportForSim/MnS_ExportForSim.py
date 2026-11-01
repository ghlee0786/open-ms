"""MnS_ExportForSim — Fusion 360 → 공중투하 M&S 시뮬레이터 내보내기 스크립트 (FR-GEO-01/02/05, PRD T절).

사용법 (Fusion 360):
  1) 유틸리티 > 추가 기능(Scripts and Add-Ins, Shift+S) > 스크립트 탭 > '+' 로 이 폴더(MnS_ExportForSim) 추가
  2) 내보낼 바디/컴포넌트(Occurrence)를 선택(없으면 보이는 모든 솔리드 바디) 후 '실행'
  3) 폴더를 고르면 <이름>.stl (m 단위, Fusion 모델 좌표) + <이름>.massprops.json 생성
  4) 시뮬레이터(Sim.App) '모델 가져오기' 에서 .stl 또는 .massprops.json 선택
  5) (선택) FEA 용 <이름>.step — 마지막 질문에서 '예'. 바디가 한 occurrence 에 있으면 그것만, 아니면 루트 전체.
     `mns ccx <시나리오> --body <이름> --cad <이름>.step` 이 Gmsh(OCC fragment 로 바디 접촉면 결합)로 C3D10 메시를 만든다(PRD Z, FR-FEA-03).
  6) (선택) USD(재질·외관 포함) <이름>.usdz — 마지막 질문에서 '예'. Blender `import_mns_result.py --usd` 가 STL 대신 쓴다.
  7) (선택) 이 폴더에 export_options.json — {"mesh": "medium"|"fine"|{"surfaceDeviation_mm":…, "normalDeviation_deg":…,
     "maxEdgeLength_mm":…, "aspectRatio":…}, "massAccuracy": "high"|"veryHigh"}. 없으면 1.0 과 같은 출력.

추가 필드(1.1, 1.0 필드는 그대로): parts[].materialId·materialLibrary·materialProperties(SI: density kg/m³, E·yield·ultimate Pa,
  nu, thermalExpansion 1/K, thermalConductivity W/(m·K), specificHeat J/(kg·K) — 없는 값 생략)·appearance(baseColor 0–1,
  opacity, metallic, roughness)·occurrence, userParameters, joints(이 바디들과 관계있는 것), mesh(사용한 정밀도), usd.

출력 JSON (schema mns.massprops/1):
  SI 단위(m, kg, kg·m²), Fusion 모델 좌표(Y-up), 관성 = CG 기준 텐서(비대각 = −곱관성).
  Fusion API 의 getXYZMomentsOfInertia 기준점(원점/CG)·곱관성 부호는 문서만으로 확정하기 어려우므로,
  4가지 해석 후보 중 getPrincipalMomentsOfInertia(CG 기준 주관성모멘트)와 고유값이 일치하는 것을 자동 선택하고
  그 근거(inertiaCheck)를 JSON 에 기록한다(VV-07 대조용).
  SECTION_VIEW_* 바디·숨김 바디·비솔리드는 제외(06c 규칙).

이 파일의 '순수 함수' 부분은 Fusion 없이도 import 가능하며 pytest 로 검증한다(tools/fusion/tests).
"""
from __future__ import annotations

import datetime
import json
import math
import os
import re
import struct
import tempfile
import traceback

try:  # Fusion 360 안에서만 존재
    import adsk.core
    import adsk.fusion
except ImportError:  # pragma: no cover - 테스트 환경
    adsk = None

SCHEMA = "mns.massprops/1"
EXPORTER = "MnS_ExportForSim 1.1"
EXCLUDE_PREFIX = "SECTION_VIEW"
# 동률 후보 텐서를 '같다'고 볼 상대 허용오차(관성 최대 성분 대비). VV-07 기준(1e-6)과 같다.
SAME_TENSOR_TOL = 1e-6

# 내보내기 선택 사항(export_options.json · groups_*.json 의 같은 키). 기본값 = 1.0 과 같은 출력.
#   mesh: "medium"(기본 — STL 내보내기 MeshRefinementMedium, 1.0 과 같음) | "fine" | {사용자 값}
#   massAccuracy: "high"(기본) | "veryHigh"
#   usd: false(기본) | true — 선택 바디를 재질·외관과 함께 <이름>.usdz 로
DEFAULT_OPTIONS = {"mesh": "medium", "massAccuracy": "high", "usd": False}
# 메시 정밀도 프리셋. mesh-calculator = BRepBody.meshManager.createMeshCalculator()(TriangleMeshCalculator).
# fine: 표면 편차 0.02 mm · 법선 편차 5° — CFD·FEA 표면 메시용(파일이 수 배 커진다).
MESH_PRESETS = {
    "medium": {"method": "stl-export", "refinement": "MeshRefinementMedium"},
    "fine": {"method": "mesh-calculator", "surfaceDeviation_mm": 0.02, "normalDeviation_deg": 5.0,
             "maxEdgeLength_mm": 0.0, "aspectRatio": 0.0},
}
MESH_KEYS = ("surfaceDeviation_mm", "normalDeviation_deg", "maxEdgeLength_mm", "aspectRatio")
MASS_ACCURACY = {"high": "HighCalculationAccuracy", "veryhigh": "VeryHighCalculationAccuracy"}

# ---------------------------------------------------------------------------
# 순수 함수 (Fusion 불필요)
# ---------------------------------------------------------------------------


def sym_eigvals(m):
    """3×3 대칭행렬 고유값(오름차순)."""
    a, b, c = m[0][0], m[1][1], m[2][2]
    d, e, f = m[0][1], m[0][2], m[1][2]
    p1 = d * d + e * e + f * f
    if p1 <= 1e-30 * (abs(a) + abs(b) + abs(c) + 1e-300):
        return sorted([a, b, c])
    q = (a + b + c) / 3.0
    p2 = (a - q) ** 2 + (b - q) ** 2 + (c - q) ** 2 + 2 * p1
    p = math.sqrt(p2 / 6.0)
    bm = [[(m[i][j] - (q if i == j else 0.0)) / p for j in range(3)] for i in range(3)]
    det = (bm[0][0] * (bm[1][1] * bm[2][2] - bm[1][2] * bm[2][1])
           - bm[0][1] * (bm[1][0] * bm[2][2] - bm[1][2] * bm[2][0])
           + bm[0][2] * (bm[1][0] * bm[2][1] - bm[1][1] * bm[2][0]))
    r = max(-1.0, min(1.0, det / 2.0))
    phi = math.acos(r) / 3.0
    e1 = q + 2 * p * math.cos(phi)
    e3 = q + 2 * p * math.cos(phi + 2 * math.pi / 3)
    e2 = 3 * q - e1 - e3
    return sorted([e1, e2, e3])


def tensor(xx, yy, zz, xy, yz, xz, offdiag_sign):
    """성분 → 텐서. offdiag_sign=-1 이면 곱관성(∫xy dm)으로 보고 비대각 = −값, +1 이면 값 그대로."""
    s = offdiag_sign
    return [[xx, s * xy, s * xz], [s * xy, yy, s * yz], [s * xz, s * yz, zz]]


def parallel_axis(mass, d):
    """m(|d|²E − d dᵀ)."""
    dd = d[0] ** 2 + d[1] ** 2 + d[2] ** 2
    return [[mass * ((dd if i == j else 0.0) - d[i] * d[j]) for j in range(3)] for i in range(3)]


def mat_add(a, b, sb=1.0):
    return [[a[i][j] + sb * b[i][j] for j in range(3)] for i in range(3)]


def mat_scale(a, s):
    return [[a[i][j] * s for j in range(3)] for i in range(3)]


def choose_inertia_at_cg(xyz, mass, cg_cm, principal, axes=None):
    """Fusion 원시값(kg·cm², cm) → CG 기준 텐서(kg·m²) + 선택 근거.

    xyz = (xx, yy, zz, xy, yz, xz) [kg·cm²], principal = (i1, i2, i3) [kg·cm²] (CG 기준 주관성모멘트),
    axes = 주축 단위벡터 3개(principal 과 같은 순서, 선택). 고유값만으로는 곱관성 부호가 구분되지 않는
    경우(비대각 3성분의 곱 = 0, 예: 대칭 부품)가 있어 주축이 주어지면 aᵢᵀ T aᵢ = Iᵢ 도 함께 대조한다.
    """
    xx, yy, zz, xy, yz, xz = xyz
    target = sorted(principal)
    scale = max(abs(v) for v in target) or 1.0
    cands = []
    for origin in ("world-origin", "center-of-mass"):
        for sign in (-1, 1):
            t = tensor(xx, yy, zz, xy, yz, xz, sign)
            if origin == "world-origin":
                t = mat_add(t, parallel_axis(mass, cg_cm), -1.0)
            ev = sym_eigvals(t)
            err = max(abs(ev[i] - target[i]) for i in range(3)) / scale
            if axes is not None:
                for a, p in zip(axes, principal):
                    ta = [sum(t[i][j] * a[j] for j in range(3)) for i in range(3)]
                    err = max(err, abs(sum(a[i] * ta[i] for i in range(3)) - p) / scale)
            cands.append((err, origin, sign, t))
    cands.sort(key=lambda c: c[0])
    err, origin, sign, t = cands[0]
    # 오차가 같은(동률) 후보들. 동률이어도 텐서가 허용오차(SAME_TENSOR_TOL·scale) 안에서 같으면 어느 쪽을 골라도
    # 결과가 같으므로 정상(FX-04: 축대칭 부품은 곱관성≈0 이라 부호·기준점 후보가 수치잡음 수준으로만 다르다).
    ties = [c for c in cands[1:] if c[0] - err < 1e-9]
    max_diff = max((max(abs(c[3][i][j] - t[i][j]) for i in range(3) for j in range(3)) for c in ties), default=0.0) / scale
    ambiguous = max_diff > SAME_TENSOR_TOL
    info = {
        "reference": origin,
        "productSign": "tensor=-product" if sign == -1 else "tensor=+value",
        "principalMomentsFusion_kgm2": [v * 1e-4 for v in target],
        "maxRelativeEigenError": err,
        "usedPrincipalAxes": axes is not None,
        "tiedCandidates": len(ties),
        "maxTiedTensorDifference": max_diff,
        "ambiguous": ambiguous,
        "ok": err < 1e-3 and not ambiguous,
    }
    if ties and not ambiguous:
        info["note"] = "후보가 여럿이나 텐서가 허용오차 안에서 같음(축대칭 등) — 정보만 기록"
    return mat_scale(t, 1e-4), info


def combine(parts):
    """[(mass, cg[m], I_cg[kg·m²])] → (총질량, 합성 CG, 합성 CG 기준 관성)."""
    m = sum(p[0] for p in parts)
    if m <= 0:
        raise ValueError("총 질량이 0 입니다")
    cg = [sum(p[0] * p[1][i] for p in parts) / m for i in range(3)]
    total = [[0.0] * 3 for _ in range(3)]
    for pm, pcg, pi in parts:
        d = [pcg[i] - cg[i] for i in range(3)]
        total = mat_add(mat_add(total, pi), parallel_axis(pm, d))
    return m, cg, total


def read_stl(path):
    """STL(바이너리/ASCII) → 삼각형 리스트 [(v0, v1, v2)], 각 v=(x,y,z)."""
    with open(path, "rb") as fh:
        data = fh.read()
    if len(data) >= 84:
        n = struct.unpack_from("<I", data, 80)[0]
        if 84 + n * 50 == len(data):
            tris = []
            for i in range(n):
                vals = struct.unpack_from("<12f", data, 84 + i * 50)
                tris.append((vals[3:6], vals[6:9], vals[9:12]))
            return tris
    text = data.decode("ascii", errors="ignore")
    verts = [tuple(float(x) for x in line.split()[1:4]) for line in text.splitlines() if line.strip().startswith("vertex")]
    return [tuple(verts[i:i + 3]) for i in range(0, len(verts) - 2, 3)]


def write_binary_stl(path, tris, header=b"MnS_ExportForSim (m)"):
    with open(path, "wb") as fh:
        fh.write(header.ljust(80, b"\0")[:80])
        fh.write(struct.pack("<I", len(tris)))
        for a, b, c in tris:
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            ln = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
            fh.write(struct.pack("<12fH", nx / ln, ny / ln, nz / ln, *a, *b, *c, 0))


def bounds(tris):
    xs = [v[0] for t in tris for v in t]
    ys = [v[1] for t in tris for v in t]
    zs = [v[2] for t in tris for v in t]
    return [min(xs), min(ys), min(zs)], [max(xs), max(ys), max(zs)]


def stl_unit_scale(tris, bbox_min_m, bbox_max_m):
    """STL 바운딩박스를 기준(m)과 비교해 m 배율(1, 0.1, 0.01, 0.001, 0.0254) 추정."""
    mn, mx = bounds(tris)
    diag = math.dist(mn, mx)
    ref = math.dist(bbox_min_m, bbox_max_m)
    if diag <= 0 or ref <= 0:
        return 0.001
    return min((1.0, 0.1, 0.01, 0.001, 0.0254), key=lambda s: abs(math.log(diag * s / ref)))


def scale_tris(tris, s):
    return [tuple(tuple(c * s for c in v) for v in t) for t in tris]


def safe_name(name):
    n = re.sub(r"[^\w\-.가-힣]+", "_", name).strip("_")
    return n or "model"


def default_export_name(doc_name, entity_names, component_names=()):
    """FX-05: 기본 내보내기 이름.

    1) 선택 이름들의 공통 접두(끝의 _-숫자 제거, 3자 이상) 2) 첫 컴포넌트 이름 3) 문서 이름(미저장 'Untitled' 제외) 4) 'model'.
    문서 이름에 REVnn 이 있고 결과에 없으면 뒤에 붙인다.
    """
    names = [n for n in entity_names if n]
    base = ""
    if names:
        prefix = os.path.commonprefix(names) if len(names) > 1 else names[0]
        prefix = re.sub(r"[\s_\-]*\d*$", "", prefix) if len(names) > 1 else prefix
        if len(prefix) >= 3:
            base = prefix
    if not base:
        base = next((c for c in component_names if c), "")
    doc = doc_name or ""
    if not base and doc and not doc.lower().startswith("untitled"):
        base = doc
    base = base or "model"
    rev = re.search(r"REV\d+", doc, re.IGNORECASE)
    if rev and rev.group(0).upper() not in base.upper():
        base = f"{base}_{rev.group(0).upper()}"
    return safe_name(base)


def plan_step_export(name, groups):
    """FEA(CalculiX) 용 STEP 내보내기 범위(FR-FEA-03).

    groups: 선택 바디마다 (소속 occurrence/component 키, 이름) — 키가 None 이면 루트 컴포넌트 소속.
    바디가 모두 한 occurrence 에 있으면 그 occurrence 만, 아니면 루트 전체(다른 바디도 포함될 수 있음 → 경고).
    반환: {"file": "<name>.step", "scope": "occurrence"|"root", "key": 키, "warning": str|None}
    """
    keys = {k for k, _ in groups}
    file = f"{safe_name(name)}.step"
    if len(keys) == 1 and None not in keys:
        key = next(iter(keys))
        label = next(n for k, n in groups if k == key)
        return {"file": file, "scope": "occurrence", "key": key, "label": label, "warning": None}
    warn = None
    if len(keys) > 1:
        warn = "선택 바디가 여러 컴포넌트에 걸쳐 있어 루트 전체를 STEP 으로 내보냈습니다 — 선택하지 않은 바디도 포함될 수 있음"
    return {"file": file, "scope": "root", "key": None, "label": "root", "warning": warn}


def build_massprops_json(name, mass, cg_m, inertia_m, bbox_min_m, bbox_max_m, stl_name, parts_info, extra=None):
    doc = {
        "schema": SCHEMA,
        "name": name,
        "source": "fusion",
        "frame": "fusion",
        "frameNote": "Fusion 360 모델 좌표(Y-up). 시뮬레이터는 config/frames.json 의 fusionToBody 로 동체 FRD 로 변환.",
        "units": {"length": "m", "mass": "kg", "inertia": "kg*m^2"},
        "mass": mass,
        "cg": list(cg_m),
        "inertia": [list(r) for r in inertia_m],
        "principalMoments": sym_eigvals(inertia_m),
        "bboxMin": list(bbox_min_m),
        "bboxMax": list(bbox_max_m),
        "stl": stl_name,
        "stlUnit": "m",
        "parts": parts_info,
        "exportedAt": datetime.datetime.now().isoformat(timespec="seconds"),
        "exporter": EXPORTER,
    }
    if extra:
        doc.update(extra)
    return doc


def resolve_options(opts=None):
    """내보내기 선택 사항 + 기본값 → {"mesh": 메시 설정, "massAccuracy": "high"|"veryHigh", "usd": bool}."""
    o = dict(DEFAULT_OPTIONS)
    o.update({k: v for k, v in (opts or {}).items() if v is not None})
    acc = str(o["massAccuracy"]).replace("_", "").lower()
    if acc not in MASS_ACCURACY:
        raise ValueError(f"massAccuracy: high | veryHigh ('{o['massAccuracy']}')")
    return {"mesh": mesh_settings(o["mesh"]), "massAccuracy": "veryHigh" if acc == "veryhigh" else "high",
            "usd": bool(o["usd"])}


def mesh_settings(spec=None):
    """메시 정밀도: 프리셋 이름 또는 {preset?, surfaceDeviation_mm, normalDeviation_deg, maxEdgeLength_mm, aspectRatio}.

    사용자 값(dict)은 mesh-calculator 로 계산한다(preset 을 주면 그 값 위에 덮어씀). 0 = 제한 없음.
    """
    if spec is None or isinstance(spec, str):
        name = spec or "medium"
        if name not in MESH_PRESETS:
            raise ValueError(f"mesh: {' | '.join(MESH_PRESETS)} | {{사용자 값}} ('{name}')")
        return {"preset": name, **MESH_PRESETS[name]}
    base_name = spec.get("preset", "fine")
    base = dict(MESH_PRESETS.get(base_name) or {})
    if base.get("method") != "mesh-calculator":
        base = dict(MESH_PRESETS["fine"])
    unknown = set(spec) - set(MESH_KEYS) - {"preset"}
    if unknown:
        raise ValueError(f"mesh: 알 수 없는 키 {sorted(unknown)} (가능: {', '.join(MESH_KEYS)})")
    for k in MESH_KEYS:
        if k in spec:
            v = float(spec[k])
            if not (v >= 0 and math.isfinite(v)):
                raise ValueError(f"mesh.{k} 는 0 이상이어야 합니다 ({spec[k]})")
            base[k] = v
    if not base["surfaceDeviation_mm"] > 0:
        raise ValueError("mesh.surfaceDeviation_mm 는 양수여야 합니다")
    base["preset"] = "custom" if any(k in spec for k in MESH_KEYS) else base_name
    return base


def mesh_calculator_values(settings):
    """메시 설정(mm·deg) → TriangleMeshCalculator 속성값(cm·rad). 0 인 항목은 넣지 않는다(= Fusion 기본 '제한 없음')."""
    out = {"surfaceTolerance": settings["surfaceDeviation_mm"] * 0.1}
    if settings.get("maxEdgeLength_mm"):
        out["maxSideLength"] = settings["maxEdgeLength_mm"] * 0.1
    if settings.get("normalDeviation_deg"):
        out["maxNormalDeviation"] = math.radians(settings["normalDeviation_deg"])
    if settings.get("aspectRatio"):
        out["maxAspectRatio"] = settings["aspectRatio"]
    return out


def tris_from_indexed(coords, indices):
    """TriangleMesh.nodeCoordinatesAsDouble(x,y,z 나열) + nodeIndices(삼각형마다 3개) → 삼각형 리스트."""
    pts = [tuple(coords[i:i + 3]) for i in range(0, len(coords) - 2, 3)]
    return [(pts[indices[i]], pts[indices[i + 1]], pts[indices[i + 2]]) for i in range(0, len(indices) - 2, 3)]


def bbox_matches(tris, bmin, bmax, rel_tol=0.05):
    """메시 경계상자가 기준(바디 boundingBox, m)과 대각선의 rel_tol 안에서 맞는지(좌표계·단위 확인)."""
    if not tris:
        return False
    mn, mx = bounds(tris)
    tol = rel_tol * (math.dist(bmin, bmax) or 1.0)
    return all(abs(mn[i] - bmin[i]) <= tol and abs(mx[i] - bmax[i]) <= tol for i in range(3))


# --- 단위 → SI 배율 (Fusion FloatProperty.units 문자열, 예: "g / cm^3", "W / (m * C)") ---
_UNITS = {
    "m": 1.0, "cm": 0.01, "mm": 1e-3, "um": 1e-6, "µm": 1e-6, "μm": 1e-6, "micron": 1e-6, "in": 0.0254, "ft": 0.3048,
    "kg": 1.0, "g": 1e-3, "lbm": 0.45359237, "lb": 0.45359237, "slug": 14.593902937,
    "s": 1.0, "sec": 1.0, "min": 60.0, "h": 3600.0, "hr": 3600.0,
    "n": 1.0, "kn": 1e3, "lbf": 4.4482216152605,
    "pa": 1.0, "kpa": 1e3, "mpa": 1e6, "gpa": 1e9, "bar": 1e5, "psi": 6894.757293168, "ksi": 6894757.293168,
    "j": 1.0, "kj": 1e3, "cal": 4.184, "kcal": 4184.0, "btu": 1055.05585262,
    "w": 1.0, "kw": 1e3,
    # 온도는 물성의 '차이' 단위(1/K · W/(m·K) · J/(kg·K)) — 섭씨 1도 = 1 K, 화씨 1도 = 5/9 K
    "k": 1.0, "c": 1.0, "degc": 1.0, "°c": 1.0, "celsius": 1.0, "f": 5 / 9, "degf": 5 / 9, "°f": 5 / 9, "fahrenheit": 5 / 9,
}
_UNIT_TOKEN = re.compile(r"\s*(?:(\d+(?:\.\d*)?(?:[eE][-+]?\d+)?)|([A-Za-zµμ°]+)|(\^|\*|/|\(|\)|·|-|\+))")


def unit_factor_si(units):
    """단위 문자열 → SI 배율(값 × 배율 = SI). 빈 문자열 = 무차원(1). 해석하지 못하면 None."""
    s = (units or "").strip().replace("²", "^2").replace("³", "^3").replace("⁻¹", "^-1")
    if not s:
        return 1.0
    toks, pos = [], 0
    while pos < len(s):
        m = _UNIT_TOKEN.match(s, pos)
        if not m or m.end() == pos:
            if s[pos:].strip() == "":
                break
            return None
        pos = m.end()
        toks.append(("num", float(m.group(1))) if m.group(1) else ("id", m.group(2)) if m.group(2) else ("op", m.group(3)))
    i = 0

    def peek():
        return toks[i] if i < len(toks) else ("end", None)

    def factor():
        nonlocal i
        kind, v = peek()
        if kind == "num":
            i += 1
            base = v
        elif kind == "id":
            i += 1
            base = _UNITS.get(v, _UNITS.get(v.lower()))
            if base is None:
                raise ValueError(v)
        elif (kind, v) == ("op", "("):
            i += 1
            base = expr()
            if peek() != ("op", ")"):
                raise ValueError("(")
            i += 1
        else:
            raise ValueError(v)
        if peek() == ("op", "^"):
            i += 1
            sign = 1
            if peek() in (("op", "-"), ("op", "+")):
                sign = -1 if peek()[1] == "-" else 1
                i += 1
            kind, e = peek()
            if kind != "num":
                raise ValueError("^")
            i += 1
            base = base ** (sign * e)
        return base

    def expr():
        nonlocal i
        val = factor()
        while True:
            kind, v = peek()
            if (kind, v) in (("op", "*"), ("op", "·")):
                i += 1
                val *= factor()
            elif (kind, v) == ("op", "/"):
                i += 1
                val /= factor()
            elif kind in ("num", "id") or (kind, v) == ("op", "("):
                val *= factor()  # 공백 곱(예: "N m")
            else:
                return val

    try:
        val = expr()
    except (ValueError, ZeroDivisionError):
        return None
    return val if i == len(toks) else None


# 물성 id/이름(소문자·기호 제거) 안의 열쇠말 → massprops 키. 위에서부터 먼저 맞는 것.
_MATERIAL_KEYS = (
    ("thermalExpansion", ("thermalexpansion", "expansioncoefficient")),
    ("thermalConductivity", ("thermalconductivity",)),
    ("specificHeat", ("specificheat",)),
    ("density", ("density",)),
    ("E", ("youngmodulus", "youngsmodulus", "modulusofelasticity", "elasticmodulus")),
    ("nu", ("poisson",)),
    ("yield", ("yieldstrength", "yieldstress", "minimumyield")),
    ("ultimate", ("tensilestrength", "ultimatetensile", "minimumtensile", "ultimatestrength")),
)
MATERIAL_UNITS = {"density": "kg/m^3", "E": "Pa", "nu": "-", "yield": "Pa", "ultimate": "Pa",
                  "thermalExpansion": "1/K", "thermalConductivity": "W/(m*K)", "specificHeat": "J/(kg*K)"}


def _norm(s):
    return re.sub(r"[^0-9a-z]", "", (s or "").lower())


def material_properties_si(props):
    """재료 물성 [{id, name, value, units}] → ({키: SI 값}, [변환 못 한 항목]). 없는 값은 생략.

    Fusion 물리 재료(materialProperties)의 FloatProperty 를 키(density·E·nu·yield·ultimate·열팽창·열전도·비열)로 고르고
    units 문자열로 SI 변환한다. 같은 키가 여럿이면(이방성 X/Y/Z 등) 첫 값.
    """
    out, unconverted = {}, []
    for p in props:
        key = next((k for k, words in _MATERIAL_KEYS
                    if any(w in _norm(p.get("id")) or w in _norm(p.get("name")) for w in words)), None)
        if key is None or key in out:
            continue
        v = p.get("value")
        if not isinstance(v, (int, float)) or isinstance(v, bool) or not math.isfinite(v):
            continue
        f = unit_factor_si(p.get("units", ""))
        if f is None or (key != "nu" and not (p.get("units") or "").strip()):
            unconverted.append({"key": key, "id": p.get("id"), "value": v, "units": p.get("units", "")})
            continue
        out[key] = v * f
    return out, unconverted


def appearance_info(name, aid=None, color=None, roughness=None, props=()):
    """외관 → {name, id, baseColor[r,g,b 0–1], opacity, metallic, roughness} (얻을 수 있는 것만).

    color = (r, g, b, a) 0–255(Appearance.color, 미리보기 API) — 없으면 props [{id, kind, value}] 의 색 속성
    (opaque_albedo · metal_f0 · transparent_color · generic_diffuse …). metallic 은 스키마로 판정(metal_* = 1, 그 밖 0).
    """
    info = {"name": name}
    if aid:
        info["id"] = aid
    ids = [_norm(p.get("id")) for p in props]
    src = None
    if color is None:
        prefer = ("opaquealbedo", "metalf0", "transparentcolor", "genericdiffuse", "layereddiffuse")
        cands = [p for p in props if p.get("kind") == "color" and p.get("value") is not None]
        pick = next((p for w in prefer for p in cands if _norm(p.get("id")) == w), None)
        pick = pick or next((p for p in cands if _norm(p.get("id")).endswith(("color", "diffuse", "albedo"))), None)
        if pick is not None:
            color, src = pick["value"], _norm(pick.get("id"))
    if color is not None:
        r, g, b = (max(0.0, min(1.0, c / 255.0)) for c in color[:3])
        info["baseColor"] = [r, g, b]
        if len(color) > 3 and color[3] is not None:
            info["opacity"] = max(0.0, min(1.0, color[3] / 255.0))
    if any(i.startswith("metal") for i in ids) or (src or "").startswith("metal"):
        info["metallic"] = 1.0
    elif any(i.startswith(("opaque", "generic", "transparent", "plastic", "layered")) for i in ids):
        info["metallic"] = 0.0
    if roughness is None:
        rp = next((p for p in props if _norm(p.get("id")) in ("surfaceroughness", "genericglossiness")
                   and isinstance(p.get("value"), (int, float))), None)
        if rp is not None:
            roughness = rp["value"] if _norm(rp["id"]) == "surfaceroughness" else 1.0 - rp["value"]
    if roughness is not None:
        info["roughness"] = max(0.0, min(1.0, float(roughness)))
    return info


def user_parameter_info(name, expression, value, unit, comment=""):
    """사용자 파라미터 → {name, expression, unit, value(Fusion 내부 단위), valueSI?, siUnit?, comment?}.

    Fusion 의 value 는 내부(데이터베이스) 단위 — 길이 cm, 각도 rad, 질량 kg. 길이·각도·무차원은 SI 값도 쓴다.
    """
    p = {"name": name, "expression": expression, "unit": unit or "", "value": value}
    u = (unit or "").strip().lower()
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if u in ("mm", "cm", "m", "in", "ft", "um", "µm", "km", "mil", "yd"):
            p["valueSI"], p["siUnit"] = value * 0.01, "m"
        elif u in ("deg", "rad", "°"):
            p["valueSI"], p["siUnit"] = value, "rad"
        elif u in ("kg", "g", "lbm", "lb"):
            p["valueSI"], p["siUnit"] = value, "kg"
        elif u == "":
            p["valueSI"], p["siUnit"] = value, ""
    if comment:
        p["comment"] = comment
    return p


def joint_info(name, kind, occ_one, occ_two, origin_cm=None, source="joint", suppressed=False):
    """조인트 → {name, type, source(joint|asBuiltJoint), occurrenceOne/Two(occurrence 경로 · 루트 = ""), origin(m)?}."""
    j = {"name": name, "type": kind, "source": source, "occurrenceOne": occ_one or "", "occurrenceTwo": occ_two or ""}
    if origin_cm is not None:
        j["origin"] = [c * 0.01 for c in origin_cm]
    if suppressed:
        j["suppressed"] = True
    return j


def filter_joints(joints, body_paths):
    """그룹(바디 묶음)과 관계있는 조인트만: 두 occurrence 중 하나가 바디의 occurrence 와 같거나 그 조상·자손.

    body_paths = 바디의 occurrence 경로 집합(루트 컴포넌트 바디 = ""). 루트 바디가 있으면 루트에 붙은 조인트("")도 포함.
    """
    paths = set(body_paths)

    def related(p):
        if p == "":
            return "" in paths
        return any(b and (p == b or p.startswith(b + "+") or b.startswith(p + "+")) for b in paths)

    return [j for j in joints if related(j.get("occurrenceOne", "")) or related(j.get("occurrenceTwo", ""))]


# ---------------------------------------------------------------------------
# Fusion 360 부분
# ---------------------------------------------------------------------------


def _occurrence_bodies(occ):
    out = list(occ.bRepBodies)
    for i in range(occ.childOccurrences.count):
        out += _occurrence_bodies(occ.childOccurrences.item(i))
    return out


def _collect_bodies(design, selections):
    bodies = []
    if selections.count > 0:
        for i in range(selections.count):
            ent = selections.item(i).entity
            body = adsk.fusion.BRepBody.cast(ent)
            if body:
                bodies.append(body)
                continue
            occ = adsk.fusion.Occurrence.cast(ent)
            if occ:
                bodies += _occurrence_bodies(occ)
                continue
            comp = adsk.fusion.Component.cast(ent)
            if comp:
                bodies += list(comp.bRepBodies)
    else:
        root = design.rootComponent
        bodies += list(root.bRepBodies)
        for i in range(root.allOccurrences.count):
            bodies += list(root.allOccurrences.item(i).bRepBodies)
    out, seen = [], set()
    for b in bodies:
        if b is None or not b.isSolid or not b.isVisible:
            continue
        if b.name.upper().startswith(EXCLUDE_PREFIX):
            continue
        key = b.entityToken
        if key in seen:
            continue
        seen.add(key)
        out.append(b)
    return out


def _items(coll):
    """Fusion 컬렉션(count/item) 또는 파이썬 리스트 → 리스트."""
    if coll is None:
        return []
    if isinstance(coll, (list, tuple)):
        return list(coll)
    return [coll.item(i) for i in range(coll.count)]


def _try(fn, default=None):
    try:
        return fn()
    except Exception:  # noqa: BLE001 — Fusion API 는 속성마다 RuntimeError 등을 던질 수 있다
        return default


def _property_rows(props):
    """adsk.core.Properties → [{id, name, kind(color|float|other), value, units}] (읽을 수 있는 것만)."""
    rows = []
    for p in _items(props):
        pid, name = _try(lambda: p.id, ""), _try(lambda: p.name, "")
        val = _try(lambda: p.value)
        if hasattr(val, "red"):
            rows.append({"id": pid, "name": name, "kind": "color",
                         "value": (val.red, val.green, val.blue, _try(lambda: val.opacity, 255))})
        elif isinstance(val, (int, float)) and not isinstance(val, bool):
            rows.append({"id": pid, "name": name, "kind": "float", "value": float(val), "units": _try(lambda: p.units, "") or ""})
        else:
            rows.append({"id": pid, "name": name, "kind": "other", "value": val})
    return rows


def _material_fields(body, warnings):
    """바디 재료 → parts[] 추가 필드 {materialId, materialLibrary, materialProperties(SI)}."""
    mat = _try(lambda: body.material)
    if mat is None:
        return {}
    out = {}
    mid = _try(lambda: mat.id)
    if mid:
        out["materialId"] = mid
    parent = _try(lambda: mat.parent)
    lib = _try(lambda: parent.name) if parent is not None else None
    out["materialLibrary"] = lib or (_try(lambda: parent.objectType, "") or "").split("::")[-1] or "unknown"
    si, unconverted = material_properties_si(_property_rows(_try(lambda: mat.materialProperties)))
    if si:
        out["materialProperties"] = si
    if unconverted:
        out["materialPropertiesUnconverted"] = unconverted
        warnings.append(f"{body.name}: 재료 물성 {len(unconverted)}개 단위 변환 실패 — materialPropertiesUnconverted 참조")
    return out


def _appearance_fields(body):
    """바디 외관 → {appearance: {...}} (없으면 빈 dict)."""
    app_ = _try(lambda: body.appearance)
    if app_ is None:
        return {}
    c = _try(lambda: app_.color)  # 미리보기 API — 없거나 실패하면 appearanceProperties 에서
    color = (c.red, c.green, c.blue, _try(lambda: c.opacity, 255)) if c is not None and hasattr(c, "red") else None
    return {"appearance": appearance_info(_try(lambda: app_.name, ""), _try(lambda: app_.id), color,
                                          _try(lambda: app_.roughness), _property_rows(_try(lambda: app_.appearanceProperties)))}


def _user_parameters(design):
    out = []
    for p in _items(_try(lambda: design.userParameters)):
        out.append(user_parameter_info(_try(lambda: p.name, ""), _try(lambda: p.expression, ""), _try(lambda: p.value),
                                       _try(lambda: p.unit, ""), _try(lambda: p.comment, "")))
    return out


def _enum_name(enum_cls, value, default="unknown"):
    if enum_cls is None or not isinstance(value, int):
        return default
    for k in dir(enum_cls):
        if not k.startswith("_") and getattr(enum_cls, k, None) == value:
            return k
    return default


def _occ_path(occ):
    return (_try(lambda: occ.fullPathName, "") or "") if occ is not None else ""


def _joint_origin(j):
    """Joint.geometryOrOriginOne(JointGeometry | JointOrigin) 또는 AsBuiltJoint.geometry 의 원점 [cm]."""
    g = _try(lambda: j.geometryOrOriginOne)
    if g is None:
        g = _try(lambda: j.geometry)
    if g is not None and _try(lambda: g.origin) is None:
        g = _try(lambda: g.geometry)  # JointOrigin → JointGeometry
    o = _try(lambda: g.origin) if g is not None else None
    return (o.x, o.y, o.z) if o is not None else None


def _joints(design):
    """루트 기준 모든 조인트·as-built 조인트 → joint_info 목록."""
    root = design.rootComponent
    types = getattr(getattr(adsk, "fusion", None), "JointTypes", None) if adsk is not None else None
    out = []
    for source, coll in (("joint", _try(lambda: root.allJoints, [])), ("asBuiltJoint", _try(lambda: root.allAsBuiltJoints, []))):
        for j in _items(coll):
            kind = _enum_name(types, _try(lambda: j.jointMotion.jointType))
            out.append(joint_info(_try(lambda: j.name, ""), kind.replace("JointType", "") or "unknown",
                                  _occ_path(_try(lambda: j.occurrenceOne)), _occ_path(_try(lambda: j.occurrenceTwo)),
                                  _joint_origin(j), source, bool(_try(lambda: j.isSuppressed, False))))
    return out


def _mesh_tris(body, settings, export_mgr, stl_tmp, bmin, bmax, warnings):
    """바디 → (삼각형[m], 감지 배율, 사용한 방법). 기본(stl-export Medium)은 1.0 과 같은 경로.

    mesh-calculator: TriangleMeshCalculator(cm) — 결과가 바디 경계상자와 맞지 않으면(조립 좌표가 아닌 proxy 등)
    같은 정밀도의 STL 내보내기(MeshRefinementCustom)로 대체하고 경고한다.
    """
    if settings["method"] == "mesh-calculator":
        try:
            calc = body.meshManager.createMeshCalculator()
            for k, v in mesh_calculator_values(settings).items():
                setattr(calc, k, v)
            mesh = calc.calculate()
            tris = scale_tris(tris_from_indexed(list(mesh.nodeCoordinatesAsDouble), list(mesh.nodeIndices)), 0.01)
            if bbox_matches(tris, bmin, bmax):
                return tris, 0.01, "mesh-calculator"
            warnings.append(f"{body.name}: TriangleMeshCalculator 결과가 바디 경계상자와 맞지 않아 STL 내보내기(같은 정밀도)로 대체")
        except Exception as ex:  # noqa: BLE001
            warnings.append(f"{body.name}: TriangleMeshCalculator 실패({ex}) — STL 내보내기(같은 정밀도)로 대체")
    opts = export_mgr.createSTLExportOptions(body, stl_tmp)
    opts.isBinaryFormat = True
    if settings["method"] == "mesh-calculator":
        opts.meshRefinement = adsk.fusion.MeshRefinementSettings.MeshRefinementCustom
        opts.surfaceDeviation = settings["surfaceDeviation_mm"] * 0.1
        if settings.get("normalDeviation_deg"):
            opts.normalDeviation = math.radians(settings["normalDeviation_deg"])
        if settings.get("maxEdgeLength_mm"):
            opts.maximumEdgeLength = settings["maxEdgeLength_mm"] * 0.1
        if settings.get("aspectRatio"):
            opts.aspectRatio = settings["aspectRatio"]
        method = "stl-export(custom)"
    else:
        opts.meshRefinement = adsk.fusion.MeshRefinementSettings.MeshRefinementMedium
        method = "stl-export(MeshRefinementMedium)"
    export_mgr.execute(opts)
    tris = read_stl(stl_tmp)
    s = stl_unit_scale(tris, bmin, bmax)
    return scale_tris(tris, s), s, method


def _assign_look(src, dst, tmp_design, warnings):
    """임시 문서의 바디 사본(dst)에 원본 재료·외관을 복사해 지정(USD 에 재질이 들어가도록)."""
    try:
        m = src.material
        if m is not None:
            dst.material = tmp_design.materials.itemByName(m.name) or tmp_design.materials.addByCopy(m, m.name)
    except Exception as ex:  # noqa: BLE001
        warnings.append(f"{src.name}: USD 재료 복사 실패({ex})")
    try:
        a = src.appearance
        if a is not None:
            dst.appearance = tmp_design.appearances.itemByName(a.name) or tmp_design.appearances.addByCopy(a, a.name)
    except Exception as ex:  # noqa: BLE001
        warnings.append(f"{src.name}: USD 외관 복사 실패({ex})")


def load_options_file(path):
    """선택 사항 JSON(없으면 {})."""
    if not path or not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def _export_name(app, selections, bodies):
    names = []
    for i in range(selections.count):
        ent = selections.item(i).entity
        for t in (adsk.fusion.Occurrence, adsk.fusion.BRepBody, adsk.fusion.Component):
            obj = t.cast(ent)
            if obj:
                names.append(obj.name)
                break
    comps = [b.parentComponent.name for b in bodies if b.parentComponent]
    return default_export_name(app.activeDocument.name, names, comps)


def run(context):  # pragma: no cover - Fusion 360 안에서만 실행
    ui = None
    try:
        app = adsk.core.Application.get()
        ui = app.userInterface
        design = adsk.fusion.Design.cast(app.activeProduct)
        if not design:
            ui.messageBox("활성 디자인(Design)이 없습니다. 디자인 작업공간에서 실행하세요.")
            return
        selections = ui.activeSelections
        bodies = _collect_bodies(design, selections)
        if not bodies:
            ui.messageBox("내보낼 솔리드 바디가 없습니다(SECTION_VIEW_*·숨김 바디 제외).")
            return

        dlg = ui.createFolderDialog()
        dlg.title = "시뮬레이터용 내보내기 폴더 선택"
        if dlg.showDialog() != adsk.core.DialogResults.DialogOK:
            return
        folder = dlg.folder
        name = _export_name(app, selections, bodies)
        entered, cancelled = ui.inputBox("내보내기 이름 (파일 이름)", "MnS Export", name)
        if cancelled:
            return
        name = safe_name(entered) if entered.strip() else name
        ans = ui.messageBox(
            "FEA(CalculiX) 용 STEP 도 내보낼까요?\n(여러 바디가 맞닿은 부품은 STEP 이어야 결합된 메시가 됩니다 — mns ccx --cad)",
            "MnS Export", adsk.core.MessageBoxButtonTypes.YesNoButtonType)
        # 메시 정밀도·질량특성 정확도 등: 이 스크립트 폴더의 export_options.json(없으면 기본 = 1.0 과 같음)
        options = load_options_file(os.path.join(os.path.dirname(os.path.abspath(__file__)), "export_options.json"))
        ans_usd = ui.messageBox(
            "USD(재질·외관 포함)도 내보낼까요?\n(Blender: import_mns_result.py --usd 로 STL 대신 가져옴)",
            "MnS Export", adsk.core.MessageBoxButtonTypes.YesNoButtonType)
        options["usd"] = ans_usd == adsk.core.DialogResults.DialogYes
        json_path, mass, cg, warnings = export_bodies(app, design, bodies, folder, name,
                                                      ans == adsk.core.DialogResults.DialogYes, options=options)
        ui.messageBox(
            f"내보내기 완료: {len(bodies)} 바디\n"
            f"질량 {mass:.4f} kg, CG = ({cg[0]:.4f}, {cg[1]:.4f}, {cg[2]:.4f}) m\n"
            f"{json_path}\n" + ("\n경고:\n" + "\n".join(warnings) if warnings else ""),
            "MnS Export")
    except Exception:
        if ui:
            ui.messageBox("실패:\n{}".format(traceback.format_exc()))


def export_bodies(app, design, bodies, folder, name, want_step, extra=None, options=None):
    """바디 목록 → <name>.stl + <name>.massprops.json (+ want_step 이면 <name>.step, options.usd 면 <name>.usdz). 대화상자 없음 —
    run() 과 그룹 일괄 내보내기(tools/fusion/MnS_ExportGroups)가 같이 쓴다. 반환 (json 경로, 질량, CG, 경고).

    options(resolve_options): mesh(메시 정밀도) · massAccuracy(high|veryHigh) · usd. 기본값이면 1.0 과 같은 STL·질량특성.
    massprops 에 1.0 필드 그대로 + parts[].materialId/materialLibrary/materialProperties(SI)/appearance/occurrence,
    userParameters, joints(이 바디들과 관계있는 것), mesh, usd 를 더한다(시뮬레이터는 모르는 필드를 무시)."""
    opt = resolve_options(options)
    accuracy_name = MASS_ACCURACY[opt["massAccuracy"].lower()]
    accuracy = getattr(adsk.fusion.CalculationAccuracy, accuracy_name)
    mesh_cfg = opt["mesh"]
    export_mgr = design.exportManager
    parts, parts_info, all_tris, warnings = [], [], [], []
    tmpdir = tempfile.mkdtemp(prefix="mns_")
    methods = set()
    for idx, body in enumerate(bodies):
        props = body.getPhysicalProperties(accuracy)
        mass = props.mass
        com = props.centerOfMass
        cg_cm = [com.x, com.y, com.z]
        ok1, xx, yy, zz, xy, yz, xz = props.getXYZMomentsOfInertia()
        ok2, p1, p2, p3 = props.getPrincipalMomentsOfInertia()
        axes = None
        try:
            ok3, ax, ay, az = props.getPrincipalAxes()
            if ok3:
                axes = [(v.x, v.y, v.z) for v in (ax, ay, az)]
        except Exception:
            axes = None
        inertia_m, info = choose_inertia_at_cg((xx, yy, zz, xy, yz, xz), mass, cg_cm, (p1, p2, p3), axes)
        if not info["ok"]:
            warnings.append(f"{body.name}: 관성 해석 후보 불일치(오차 {info['maxRelativeEigenError']:.2e})")
        cg_m = [c * 0.01 for c in cg_cm]
        bb = body.boundingBox
        bmin = [bb.minPoint.x * 0.01, bb.minPoint.y * 0.01, bb.minPoint.z * 0.01]
        bmax = [bb.maxPoint.x * 0.01, bb.maxPoint.y * 0.01, bb.maxPoint.z * 0.01]

        stl_tmp = os.path.join(tmpdir, f"b{idx}.stl")
        tris, s, method = _mesh_tris(body, mesh_cfg, export_mgr, stl_tmp, bmin, bmax, warnings)
        methods.add(method)
        all_tris += tris

        material = body.material.name if body.material else ""
        parts.append((mass, cg_m, inertia_m))
        part = {
            "body": body.name,
            "component": body.parentComponent.name if body.parentComponent else "",
            "material": material,
            "mass": mass,
            "volume_m3": props.volume * 1e-6,
            "cg": cg_m,
            "inertiaCheck": info,
            "stlScaleDetected": s,
        }
        part.update(_material_fields(body, warnings))
        part.update(_appearance_fields(body))
        occ = _occ_path(_try(lambda: body.assemblyContext))
        if occ:
            part["occurrence"] = occ
        parts_info.append(part)

    mass, cg, inertia = combine(parts)
    mn, mx = bounds(all_tris)
    stl_name = f"{name}.stl"
    write_binary_stl(os.path.join(folder, stl_name), all_tris)

    # FEA(CalculiX) 용 STEP — 바디 간 접촉면을 Gmsh 가 공유 메시로 결합할 수 있게(STL 은 결합 정보가 없다)
    # USD — 같은 임시 문서의 사본에 원본 재료·외관을 복사해 지정한 뒤 루트를 USD 로(재질 포함, Fusion 모델 좌표)
    step_info = usd_info = None
    if want_step or opt["usd"]:
        # 선택한 바디만 월드 좌표 사본으로 내보낸다(2026-10-07 실모델 시험):
        # - createSTEPExportOptions 는 컴포넌트/occurrence 전체만 받아 선택하지 않은 바디까지 들어가고,
        #   바디 proxy 의 occurrence 는 'invalid argument geometry' 로 거부됐다.
        # - TemporaryBRepManager.exportToFile 은 .sat/.smt 만 지원(Gmsh 는 SAT 를 못 읽음).
        # → 사본을 임시 새 문서(직접 모델링)의 루트에 넣고 그 루트를 STEP 으로 내보낸 뒤 저장하지 않고 닫는다.
        # STEP·USD 실패가 massprops.json 저장을 막지 않게 한다.
        step_file = f"{safe_name(name)}.step"
        usd_file = f"{safe_name(name)}.usdz"
        src_doc = app.activeDocument
        tmp_doc = None
        try:
            tbm = adsk.fusion.TemporaryBRepManager.get()
            copies = [tbm.copy(b) for b in bodies]
            tmp_doc = app.documents.add(adsk.core.DocumentTypes.FusionDesignDocumentType)
            tmp_design = adsk.fusion.Design.cast(app.activeProduct)
            tmp_design.designType = adsk.fusion.DesignTypes.DirectDesignType
            added = [tmp_design.rootComponent.bRepBodies.add(c) for c in copies]
            tmp_mgr = tmp_design.exportManager
            if want_step:
                try:
                    if not tmp_mgr.execute(tmp_mgr.createSTEPExportOptions(os.path.join(folder, step_file), tmp_design.rootComponent)):
                        raise RuntimeError("STEP 내보내기 실행이 False 를 반환")
                    step_info = {"file": step_file, "scope": "bodies", "target": f"선택 바디 {len(bodies)}개",
                                 "unit": "STEP 파일에 기록된 단위(Gmsh 가 m 로 변환)"}
                except Exception as ex:  # noqa: BLE001 — Fusion API 예외 종류가 다양하다
                    warnings.append(f"STEP 내보내기 실패(STL·질량특성은 정상 저장): {ex}")
            if opt["usd"]:
                try:
                    for src, dst in zip(bodies, added):
                        if dst is not None:
                            _try(lambda: setattr(dst, "name", src.name))
                            _assign_look(src, dst, tmp_design, warnings)
                    if not tmp_mgr.execute(tmp_mgr.createUSDExportOptions(os.path.join(folder, usd_file), tmp_design.rootComponent)):
                        raise RuntimeError("USD 내보내기 실행이 False 를 반환")
                    usd_info = {"file": usd_file, "scope": "bodies", "target": f"선택 바디 {len(bodies)}개", "frame": "fusion",
                                "unit": "USD 파일의 metersPerUnit", "materials": "바디 재료·외관 복사(임시 문서)"}
                except Exception as ex:  # noqa: BLE001
                    warnings.append(f"USD 내보내기 실패(STL·질량특성은 정상 저장): {ex}")
        except Exception as ex:  # noqa: BLE001
            warnings.append(f"STEP/USD 임시 문서 준비 실패(STL·질량특성은 정상 저장): {ex}")
        finally:
            if tmp_doc is not None:
                tmp_doc.close(False)
            src_doc.activate()

    body_paths = {_occ_path(_try(lambda: b.assemblyContext)) for b in bodies}
    all_joints = _try(lambda: _joints(design), []) or []
    joints = filter_joints(all_joints, body_paths)
    doc = build_massprops_json(name, mass, cg, inertia, mn, mx, stl_name, parts_info, {
        "document": app.activeDocument.name,
        "fusionUnits": design.unitsManager.defaultLengthUnits,
        "accuracy": accuracy_name,
        "warnings": warnings,
        "mesh": dict(mesh_cfg, methodUsed=sorted(methods)),
        "userParameters": _try(lambda: _user_parameters(design), []) or [],
        "joints": joints,
        **({"jointsNote": f"이 바디들과 관계있는 조인트 {len(joints)}/{len(all_joints)}개"} if len(joints) != len(all_joints) else {}),
        **({"step": step_info} if step_info else {}),
        **({"usd": usd_info} if usd_info else {}),
        **(extra or {}),
    })
    json_path = os.path.join(folder, f"{name}.massprops.json")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2)
    return json_path, mass, cg, warnings
