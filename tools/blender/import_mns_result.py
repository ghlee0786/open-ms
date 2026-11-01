"""MnS 시뮬레이션 결과 → Blender 물리 기반 애니메이션 (FR-EXP-02).

결과 폴더(mns run / 시뮬레이터 '결과 저장')에는 scene.json, timeseries.csv, devices.csv, meshes/*.stl 이 있다.
  blender --python tools/blender/import_mns_result.py -- <결과폴더> [--fps 25] [--scale 1.0] [--usd]
또는 Blender 텍스트 편집기에서 RESULT_DIR 를 지정해 실행.

--usd: Fusion 이 재질 포함 USD 를 내보낸 물체(scene.json bodies[].usd = meshes/*.usd*)는 STL 대신 USD 를 가져와
재질·외관을 유지한다. USD 는 Fusion 모델 좌표라 scene.json 의 modelToBody 와, Blender USD 가져오기의 축(Y-up→Z-up)·단위
배율 후보 중 동체 메시 경계상자에 맞는 것으로 동체 좌표에 놓는다(맞는 후보가 없으면 그 물체는 STL 로 대체).

좌표: 엔진 NED(m) → Blender ENU(x=동, y=북, z=위). 물체 자세 q(동체→NED) → R_enu = C_ned→enu · R(q).
메시는 동체 기준좌표(FRD, m)로 저장되어 있어 그대로 쓰고 매 프레임 위치·쿼터니언 키를 넣는다.
낙하산은 반구 오브젝트(충만비 → 스케일 키, 미전개 → 숨김 키), 테더는 양 끝 Empty 에 훅으로 연결된 커브.
이벤트(events.json)는 타임라인 마커로 넣는다.
"""
from __future__ import annotations

import csv
import json
import math
import os
import sys
from collections import defaultdict

RESULT_DIR = ""  # Blender 텍스트 편집기에서 실행할 때 지정

NED_TO_ENU = ((0, 1, 0), (1, 0, 0), (0, 0, -1))


# ---------------- 순수 함수(테스트 가능) ----------------

def ned_to_enu(v):
    return (v[1], v[0], -v[2])


def quat_to_mat(q):
    w, x, y, z = q
    n = math.sqrt(w * w + x * x + y * y + z * z) or 1.0
    w, x, y, z = w / n, x / n, y / n, z / n
    return ((1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)),
            (2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)),
            (2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)))


def mat_mul(a, b):
    return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))


def mat_to_quat(m):
    tr = m[0][0] + m[1][1] + m[2][2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        q = (0.25 * s, (m[2][1] - m[1][2]) / s, (m[0][2] - m[2][0]) / s, (m[1][0] - m[0][1]) / s)
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2
        q = ((m[2][1] - m[1][2]) / s, 0.25 * s, (m[0][1] + m[1][0]) / s, (m[0][2] + m[2][0]) / s)
    elif m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2
        q = ((m[0][2] - m[2][0]) / s, (m[0][1] + m[1][0]) / s, 0.25 * s, (m[1][2] + m[2][1]) / s)
    else:
        s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2
        q = ((m[1][0] - m[0][1]) / s, (m[0][2] + m[2][0]) / s, (m[1][2] + m[2][1]) / s, 0.25 * s)
    return q if q[0] >= 0 else tuple(-c for c in q)


def body_quat_enu(q_body_to_ned):
    """동체→ENU 쿼터니언(w,x,y,z)."""
    return mat_to_quat(mat_mul(NED_TO_ENU, quat_to_mat(q_body_to_ned)))


def load_result(result_dir):
    with open(os.path.join(result_dir, "scene.json"), encoding="utf-8") as fh:
        scene = json.load(fh)
    series = defaultdict(list)
    with open(os.path.join(result_dir, "timeseries.csv"), encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            series[row["body"]].append((
                float(row["t_s"]),
                (float(row["origin_n_m"]), float(row["origin_e_m"]), float(row["origin_d_m"])),
                (float(row["qw"]), float(row["qx"]), float(row["qy"]), float(row["qz"])),
                row["mode"],
            ))
    devices = defaultdict(list)
    dev_path = os.path.join(result_dir, "devices.csv")
    if os.path.exists(dev_path):
        with open(dev_path, encoding="utf-8") as fh:
            for row in csv.DictReader(fh):
                devices[(row["element"], row["type"])].append((
                    float(row["t_s"]), row["visible"] == "1", float(row["fill"]),
                    (float(row["anchor_n"]), float(row["anchor_e"]), float(row["anchor_d"])),
                    (float(row["tip_n"]), float(row["tip_e"]), float(row["tip_d"])),
                    float(row["size_m"]),
                ))
    events = []
    ev_path = os.path.join(result_dir, "events.json")
    if os.path.exists(ev_path):
        with open(ev_path, encoding="utf-8") as fh:
            events = json.load(fh)
    return scene, series, devices, events


def mat_transpose(a):
    return tuple(tuple(a[j][i] for j in range(3)) for i in range(3))


def mat_vec(a, v):
    return tuple(sum(a[i][k] * v[k] for k in range(3)) for i in range(3))


# Blender USD 가져오기가 Fusion 모델 좌표(m) p 를 놓는 방식 후보: p_imp = s · C · p
#   C: 축 변환(그대로 · Y-up→Z-up = X 축 +90° · −90°), s: 단위 배율(m · cm · mm · in 를 변환하지 않은 경우)
USD_AXES = {
    "none": ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
    "yup-to-zup": ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
    "zup-to-yup": ((1, 0, 0), (0, 0, 1), (0, -1, 0)),
}
USD_SCALES = (1.0, 100.0, 1000.0, 1 / 0.0254)
USD_FIT_TOL = 0.05


def usd_path(body, result_dir, prefer_usd):
    """--usd 이고 scene.json 물체에 usd 가 있으며 파일이 있으면 그 경로, 아니면 None(STL 사용)."""
    rel = body.get("usd")
    if not prefer_usd or not rel:
        return None
    path = os.path.join(result_dir, rel)
    return path if os.path.exists(path) else None


def body_bbox(body):
    """scene.json 물체의 동체 좌표 경계상자(center ± size/2) — 없으면 None."""
    if "center" not in body or "size" not in body:
        return None
    c, s = body["center"], body["size"]
    return tuple(c[i] - s[i] / 2 for i in range(3)), tuple(c[i] + s[i] / 2 for i in range(3))


def fit_usd_transform(imp_min, imp_max, body_min, body_max, model_to_body):
    """가져온 USD 의 Blender 월드 경계상자 → 동체 좌표 변환 T(3×3, 동체 = T · p_imp) 를 후보 중에서 고른다.

    T = modelToBody · Cᵀ / s. 각 후보로 경계상자 꼭짓점 8개를 옮겨 동체 메시 경계상자와 비교(대각선 대비 최대 오차).
    반환 (T, 오차, 후보 이름) — 오차가 USD_FIT_TOL 보다 크면 호출자가 STL 로 대체한다.
    """
    corners = [(x, y, z) for x in (imp_min[0], imp_max[0]) for y in (imp_min[1], imp_max[1]) for z in (imp_min[2], imp_max[2])]
    diag = math.dist(body_min, body_max) or 1.0
    best = None
    for name, c in USD_AXES.items():
        for s in USD_SCALES:
            t = tuple(tuple(v / s for v in row) for row in mat_mul(model_to_body, mat_transpose(c)))
            pts = [mat_vec(t, p) for p in corners]
            mn = [min(p[i] for p in pts) for i in range(3)]
            mx = [max(p[i] for p in pts) for i in range(3)]
            err = max(max(abs(mn[i] - body_min[i]), abs(mx[i] - body_max[i])) for i in range(3)) / diag
            if best is None or err < best[1] - 1e-12:
                best = (t, err, f"{name}·s={s:g}")
    return best


def resample(samples, fps):
    """시간 정렬 샘플 → 프레임 번호별 최근접 샘플 [(frame, sample)]."""
    if not samples:
        return []
    t_end = samples[-1][0]
    out, j = [], 0
    for f in range(int(math.floor(t_end * fps)) + 1):
        t = f / fps
        while j + 1 < len(samples) and abs(samples[j + 1][0] - t) <= abs(samples[j][0] - t):
            j += 1
        out.append((f + 1, samples[j]))
    return out


# ---------------- Blender 부분 ----------------

def _import_stl(path):  # pragma: no cover - Blender 전용
    import bpy
    before = set(bpy.data.objects)
    if hasattr(bpy.ops.wm, "stl_import"):
        bpy.ops.wm.stl_import(filepath=path, forward_axis="Y", up_axis="Z")
    else:
        bpy.ops.import_mesh.stl(filepath=path, axis_forward="Y", axis_up="Z")
    new = [o for o in bpy.data.objects if o not in before]
    return new[0] if new else None


def _import_usd_body(name, path, body):  # pragma: no cover - Blender 전용
    """USD 를 가져와 동체 좌표 Empty(이름 = 물체) 아래에 놓는다. 맞는 배치 후보가 없으면 가져온 것을 지우고 None."""
    import bpy
    from mathutils import Matrix, Vector
    bb = body_bbox(body)
    before = set(bpy.data.objects)
    bpy.ops.wm.usd_import(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == "MESH"]
    if not meshes or bb is None:
        print(f"MnS: {name} USD 에 메시가 없거나 동체 경계상자가 없음 → STL")
        bpy.data.batch_remove(new)
        return None, []
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    imp_min = [min(p[i] for p in pts) for i in range(3)]
    imp_max = [max(p[i] for p in pts) for i in range(3)]
    t, err, how = fit_usd_transform(imp_min, imp_max, bb[0], bb[1], body.get("modelToBody") or USD_AXES["none"])
    if err > USD_FIT_TOL:
        print(f"MnS: {name} USD 배치 후보 오차 {err:.3f} > {USD_FIT_TOL} → STL")
        bpy.data.batch_remove(new)
        return None, []
    holder = bpy.data.objects.new(name, None)
    t4 = Matrix(((*t[0], 0), (*t[1], 0), (*t[2], 0), (0, 0, 0, 1)))
    for o in new:
        if o.parent is None or o.parent not in new:
            m = t4 @ o.matrix_world.copy()
            o.parent = holder
            o.matrix_parent_inverse = Matrix.Identity(4)
            o.matrix_basis = m
    print(f"MnS: {name} USD 가져옴({how}, 경계상자 오차 {err:.4f}) — 재질 유지")
    return holder, new


def build(result_dir, fps=25, scale=1.0, prefer_usd=False):  # pragma: no cover - Blender 전용
    import bpy

    scene_def, series, devices, events = load_result(result_dir)
    sc = bpy.context.scene
    sc.render.fps = fps
    coll = bpy.data.collections.new(f"MnS_{os.path.basename(os.path.normpath(result_dir))}")
    sc.collection.children.link(coll)

    def link(obj):
        for c in obj.users_collection:
            c.objects.unlink(obj)
        coll.objects.link(obj)

    last_frame = 1
    for body in scene_def["bodies"]:
        name = body["name"]
        obj, usd_objs = None, []
        if usd_path(body, result_dir, prefer_usd) is not None:
            obj, usd_objs = _import_usd_body(name, usd_path(body, result_dir, prefer_usd), body)
        mesh_path = os.path.join(result_dir, body.get("mesh", ""))
        if obj is None:
            obj = _import_stl(mesh_path) if body.get("mesh") and os.path.exists(mesh_path) else None
        if obj is None:
            obj = bpy.data.objects.new(name, None)
        obj.name = name
        link(obj)
        for o in usd_objs:
            link(o)
        color = body.get("color", "#9aa4b1").lstrip("#")
        rgb = tuple(int(color[i:i + 2], 16) / 255 for i in (0, 2, 4))
        if obj.type == "MESH":
            mat = bpy.data.materials.new(f"{name}_mat")
            mat.diffuse_color = (*rgb, 1.0)
            obj.data.materials.append(mat)
        obj.rotation_mode = "QUATERNION"
        obj.scale = (scale, scale, scale)
        for frame, (t, origin, q, mode) in resample(series.get(name, []), fps):
            obj.location = ned_to_enu(origin)
            obj.rotation_quaternion = body_quat_enu(q)
            obj.keyframe_insert("location", frame=frame)
            obj.keyframe_insert("rotation_quaternion", frame=frame)
            last_frame = max(last_frame, frame)

    for (elem, typ), samples in devices.items():
        rs = resample([(s[0],) + s[1:] for s in samples], fps)
        if typ == "parachute":
            bpy.ops.mesh.primitive_uv_sphere_add(segments=24, ring_count=8, radius=1.0)
            canopy = bpy.context.active_object
            canopy.name = elem
            link(canopy)
            for frame, (t, vis, fill, anchor, tip, size) in rs:
                a, b = ned_to_enu(anchor), ned_to_enu(tip)
                r = max(size / 2 * (0.3 + 0.7 * fill), 0.01)
                canopy.location = b
                canopy.scale = (r, r, r * 0.6)
                canopy.hide_viewport = canopy.hide_render = not vis
                canopy.keyframe_insert("location", frame=frame)
                canopy.keyframe_insert("scale", frame=frame)
                canopy.keyframe_insert("hide_viewport", frame=frame)
                canopy.keyframe_insert("hide_render", frame=frame)
        elif typ in ("tether", "rail"):
            ea = bpy.data.objects.new(f"{elem}_A", None)
            eb = bpy.data.objects.new(f"{elem}_B", None)
            coll.objects.link(ea)
            coll.objects.link(eb)
            curve = bpy.data.curves.new(elem, "CURVE")
            curve.dimensions = "3D"
            curve.bevel_depth = 0.01
            spl = curve.splines.new("POLY")
            spl.points.add(1)
            cobj = bpy.data.objects.new(elem, curve)
            coll.objects.link(cobj)
            for idx, empty in enumerate((ea, eb)):
                hook = cobj.modifiers.new(f"hook{idx}", "HOOK")
                hook.object = empty
                hook.vertex_indices_set([idx])
            for frame, (t, vis, fill, anchor, tip, size) in rs:
                ea.location, eb.location = ned_to_enu(anchor), ned_to_enu(tip)
                ea.keyframe_insert("location", frame=frame)
                eb.keyframe_insert("location", frame=frame)
                cobj.hide_viewport = cobj.hide_render = not vis
                cobj.keyframe_insert("hide_viewport", frame=frame)
                cobj.keyframe_insert("hide_render", frame=frame)

    for ev in events:
        m = sc.timeline_markers.new(ev["name"], frame=int(round(ev["t"] * fps)) + 1)
        m.select = False
    sc.frame_start, sc.frame_end = 1, last_frame
    print(f"MnS: {len(scene_def['bodies'])} 물체, {last_frame} 프레임 @ {fps} fps")


def _main():  # pragma: no cover - Blender 전용
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    result_dir = argv[0] if argv else RESULT_DIR
    fps = int(argv[argv.index("--fps") + 1]) if "--fps" in argv else 25
    scale = float(argv[argv.index("--scale") + 1]) if "--scale" in argv else 1.0
    if not result_dir:
        raise SystemExit("결과 폴더를 지정하세요: blender --python import_mns_result.py -- <결과폴더>")
    build(result_dir, fps, scale, prefer_usd="--usd" in argv)


if __name__ == "__main__":
    try:
        import bpy  # noqa: F401
        _main()
    except ImportError:
        print("Blender(bpy) 안에서 실행하세요. 순수 함수는 tools/blender/tests 에서 검증합니다.")
