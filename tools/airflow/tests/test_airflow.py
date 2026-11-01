"""GPU 가상 풍동 파이썬 쪽 순수 함수 · IPC 시험. GPU·XLB 없이 통과한다(실제 XLB 실행 시험은 설치돼 있을 때만)."""

import math
import os
import socket
import struct
import sys
import tempfile
import threading

import numpy as np
import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import lattice as lt  # noqa: E402
import protocol as pr  # noqa: E402
import server as sv  # noqa: E402


def box_tris(lo, hi):
    """축 정렬 상자 삼각형 수프(바깥 법선)."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    v = np.array([[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0], [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]], float)
    f = [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4), (2, 3, 7), (2, 7, 6), (1, 2, 6), (1, 6, 5), (0, 4, 7), (0, 7, 3)]
    return v[np.array(f)].reshape(-1, 3)


def cylinder_tris(r=0.15, x0=-1.52, x1=-0.02, n=24):
    """x 축 원통(끝면 포함) — 컨테이너 대용."""
    th = np.linspace(0, 2 * np.pi, n + 1)[:-1]
    tris = []
    for i in range(n):
        a, b = th[i], th[(i + 1) % n]
        pa0, pb0 = [x0, r * np.cos(a), r * np.sin(a)], [x0, r * np.cos(b), r * np.sin(b)]
        pa1, pb1 = [x1, r * np.cos(a), r * np.sin(a)], [x1, r * np.cos(b), r * np.sin(b)]
        tris += [pa0, pb0, pb1, pa0, pb1, pa1, [x0, 0, 0], pb0, pa0, [x1, 0, 0], pa1, pb1]
    return np.array(tris, float)


# ---------------------------------------------------------------- 좌표

def test_rotation_maps_travel_direction_to_plus_x():
    for a, b in [(0, 0), (30, 0), (90, 0), (45, 20), (-15, -35), (170, 10)]:
        r = lt.rotation_body_to_tunnel(a, b)
        assert np.allclose(r @ lt.travel_direction(a, b), [1, 0, 0], atol=1e-12)
        assert np.allclose(r @ r.T, np.eye(3), atol=1e-12) and abs(np.linalg.det(r) - 1) < 1e-12


def test_rotation_matches_windtunnel_totunnel_at_zero_sideslip():
    # Sim.Core WindTunnel.ToTunnel: (x c + z s, y, −x s + z c)
    a = 37.0
    c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
    p = np.array([0.3, -0.2, 0.7])
    assert np.allclose(lt.rotation_body_to_tunnel(a, 0) @ p, [p[0] * c + p[2] * s, p[1], -p[0] * s + p[2] * c])


def test_shared_vector_with_csharp():
    # C# AirflowFramesTests 와 같은 값(두 구현이 같은 회전을 쓰는지 고정)
    r = lt.rotation_body_to_tunnel(30, 10)
    assert np.allclose(r @ np.array([1.0, 2.0, 3.0]), [2.67737652, 1.55875951, 2.09807621], atol=1e-6)


def test_plan_grid_dx_independent_of_alpha_and_body_fits():
    v = cylinder_tris()
    p = lt.plan_grid(v, "low")
    assert p.shape == lt.RESOLUTIONS["low"]
    for a in (0, 45, 90):
        vl = lt.body_to_lattice(v, p, a, 15)
        assert vl.min() > 1 and np.all(vl.max(axis=0) < np.array(p.shape) - 1)
    # 외접구 지름 = 측면의 SPHERE_FRACTION
    assert abs(p.sphere_diameter / p.dx - min(p.shape[1:]) * lt.SPHERE_FRACTION) < 1e-9


def test_lattice_tunnel_roundtrip_and_flow_axis_flip():
    p = lt.plan_grid(cylinder_tris(), "low")
    t = np.array([[0.2, -0.3, 0.1], [0, 0, 0]])
    assert np.allclose(lt.lattice_to_tunnel(lt.tunnel_to_lattice(t, p), p), t)
    # 풍동 −X(하류) = 격자 +x
    d = lt.tunnel_to_lattice(np.array([[-1.0, 0, 0]]), p) - lt.tunnel_to_lattice(np.array([[0.0, 0, 0]]), p)
    assert d[0, 0] > 0 and abs(d[0, 1]) < 1e-12


# ---------------------------------------------------------------- 영역 규칙(tools/flowdomain.py)

def test_plan_grid_presets_reproduce_previous_layout():
    # 이 레인 전 plan_grid: dx = D / (min(ny, nz)·0.42), 물체 중심 = (0.28·nx, ny/2, nz/2)
    for v in (cylinder_tris(), cylinder_tris(0.2, -1.0, 1.0), box_tris((-0.14, -0.14, -0.195), (0.14, 0.14, 0.195))):
        c = (v.min(axis=0) + v.max(axis=0)) / 2
        diam = 2.0 * float(np.max(np.linalg.norm(v - c, axis=1)))
        for name, sh in lt.RESOLUTIONS.items():
            for a in (0.0, 45.0, 90.0):
                p = lt.plan_grid(v, name, alpha_deg=a)
                assert p.shape == sh and p.resolution == name
                assert abs(p.dx / (diam / (min(sh[1:]) * lt.SPHERE_FRACTION)) - 1) < 1e-12
                assert np.allclose(p.origin_cells, [sh[0] * lt.UPSTREAM_FRACTION, sh[1] / 2, sh[2] / 2], atol=1e-9)
                assert p.origin_cells[1] == sh[1] / 2 and p.origin_cells[2] == sh[2] / 2     # 단면 지수(floor)가 그대로
    # 지정 shape(이전 'custom')도 같은 배치
    p = lt.plan_grid(cylinder_tris(), "low", shape=(200, 80, 64))
    assert p.shape == (200, 80, 64) and p.resolution == "custom"
    assert abs(p.dx * 64 * lt.SPHERE_FRACTION / p.sphere_diameter - 1) < 1e-12


def test_plan_grid_domain_options_size_and_count():
    v = cylinder_tris()
    # 크기 유지: D 당 30 셀, 상류 2 · 하류 5 · 측면 1.5 D → (1 + 2 + 5)·30 × (1 + 3)·30
    p = lt.plan_grid(v, "low", domain={"mode": "size", "cellsPerLength": 30, "upstream": 2, "downstream": 5, "lateral": 1.5})
    assert p.shape == (240, 120, 120) and p.dx == pytest.approx(p.sphere_diameter / 30) and p.resolution == "custom"
    assert np.allclose(p.origin_cells, [75, 60, 60])
    # 개수 유지(프리셋 shape) + 측면만 넓힘 → shape 그대로, dx 가 커진다
    q = lt.plan_grid(v, "low", domain={"lateral": 1.5})
    assert q.shape == lt.RESOLUTIONS["low"] and q.dx == pytest.approx(4 * p.sphere_diameter / 96)
    # 총 셀 예산
    r = lt.plan_grid(v, "low", domain={"cellBudget": 1_000_000})
    assert abs(r.cells / 1e6 - 1) < 0.05
    for pl in (p, q, r):
        for a in (0, 45, 90):
            vl = lt.body_to_lattice(v, pl, a, 15)
            assert vl.min() > 1 and np.all(vl.max(axis=0) < np.array(pl.shape) - 1)
    assert p.extra["domain"]["reasons"][0].startswith("물체 = 외접구")


def test_blockage_per_attitude_and_session_warning(tmp_path):
    v = cylinder_tris()
    p = lt.plan_grid(v, "low")
    d = p.dx * 96
    assert lt.blockage(p, v, 0) == pytest.approx(0.30 ** 2 / d ** 2, rel=0.01)
    assert lt.blockage(p, v, 90) == pytest.approx(0.30 * 1.50 / d ** 2, rel=0.01)
    stl = tmp_path / "c.stl"
    _write_stl(stl, v)
    s = sv.Session(sv.SelfTestBackend())
    s.apply({"mesh": str(stl), "alpha": 90})
    assert s.blockage == pytest.approx(lt.blockage(p, v, 90)) and not s.warnings
    # 측면을 좁히면(0.2 D, 크기 유지 D 당 20 셀) 막힘 경고 · 영역 설정 변경 = 격자 다시
    assert s.apply({"domain": {"mode": "size", "cellsPerLength": 20, "lateral": 0.2}}) == ["rebuild"]
    assert s.blockage > 0.05 and any("막힘 비율" in w for w in s.warnings)
    h, _ = s.frame(s.advance())
    assert h["blockage"] == pytest.approx(s.blockage)


# ---------------------------------------------------------------- 무차원 수

def test_lb_parameters_clamps_re_and_tau_above_half():
    p = lt.plan_grid(cylinder_tris(), "low")
    lb = lt.lb_parameters(1e7, 0.30, p)
    assert lb["re_clamped"] and abs(lb["re"] - lb["re_max"]) < 1e-9 and lb["tau"] > 0.5
    assert abs(lb["nu_lb"] - lt.NU_LB_MIN) < 1e-15
    lb2 = lt.lb_parameters(200, 0.30, p)
    assert not lb2["re_clamped"] and abs(lb2["re"] - 200) < 1e-9
    assert abs(lb2["u_lb"] * lb2["ref_cells"] / lb2["nu_lb"] - 200) < 1e-6


def test_physical_numbers_mach_half_at_1km():
    ph = lt.physical_numbers(168.2, 0.30, 1000)
    assert abs(ph["mach"] - 0.5) < 0.005
    assert 3e6 < ph["re"] < 4e6


def test_coefficients_unit_force():
    p = lt.plan_grid(cylinder_tris(), "low")
    area = 0.0835
    q = 0.5 * lt.U_LB ** 2 * area / p.dx ** 2
    c = lt.coefficients(np.array([q, -2 * q, 3 * q]), p, area)
    assert abs(c["CD"] - 1) < 1e-12 and abs(c["CY"] + 2) < 1e-12 and abs(c["CL"] - 3) < 1e-12


# ---------------------------------------------------------------- 복셀화

def test_voxelize_box_volume():
    tris = box_tris((5.0, 6.0, 7.0), (15.0, 12.0, 11.0))
    s = lt.voxelize(tris, (24, 20, 20))
    inner = s[6:15, 7:12, 8:11]
    assert inner.all()
    assert 10 * 6 * 4 <= s.sum() <= 12 * 8 * 6
    assert not s[0].any() and not s[:, :, 0].any()


def test_voxelize_hollow_shell_is_filled_and_winding_independent():
    outer = box_tris((4.3, 4.3, 4.3), (15.7, 15.7, 15.7))               # 면이 셀 경계(정수)에 걸리지 않게
    inner = box_tris((6.3, 6.3, 6.3), (13.7, 13.7, 13.7))[::-1]         # 안쪽 면 감김 반대(속 빈 외피 STL)
    s = lt.voxelize(np.vstack([outer, inner]), (20, 20, 20))
    assert s[10, 10, 10]                                                 # 속 빈 공간도 고체(외형 기준)
    flipped = outer.reshape(-1, 3, 3)[:, [0, 2, 1]].reshape(-1, 3)
    assert np.array_equal(lt.voxelize(flipped, (20, 20, 20)), lt.voxelize(outer, (20, 20, 20)))


def test_fill_enclosed_numpy_fallback_matches_expected():
    s = np.zeros((9, 9, 9), bool)
    s[2:7, 2:7, 2] = s[2:7, 2:7, 6] = True
    s[2:7, 2, 2:7] = s[2:7, 6, 2:7] = True
    s[2, 2:7, 2:7] = s[6, 2:7, 2:7] = True
    f = lt.fill_enclosed(s)
    assert f[4, 4, 4] and f[3:6, 3:6, 3:6].all() and not f[0, 0, 0] and f.sum() == 125


def test_surface_points_spacing_covers_long_thin_triangle():
    tri = np.array([[0, 0, 0], [40, 0, 0], [0, 0.3, 0]], float)
    p = lt.surface_points(tri, 0.45)
    cells = {int(x) for x in np.floor(p[:, 0])}
    assert cells == set(range(0, 40)) | {40} or cells == set(range(0, 40))


# ---------------------------------------------------------------- 후처리

def uniform_field(shape, u=(0.06, 0, 0)):
    a = np.zeros((3,) + shape)
    for k in range(3):
        a[k] = u[k]
    return a


def test_trilinear_exact_for_linear_field():
    nx, ny, nz = 8, 7, 6
    i, j, k = np.meshgrid(np.arange(nx) + 0.5, np.arange(ny) + 0.5, np.arange(nz) + 0.5, indexing="ij")
    u = np.stack([2 * i + j, 3 * k, i - k])
    pts = np.array([[2.2, 3.7, 1.9], [5.5, 1.5, 4.25]])
    v = lt.trilinear(u, pts)
    x, y, z = pts.T
    assert np.allclose(v, np.column_stack([2 * x + y, 3 * z, x - z]))


def test_particles_move_with_uniform_flow_and_leave_domain():
    shape = (40, 10, 10)
    u = uniform_field(shape)
    ps = lt.ParticleSystem(np.array([[2.0, 5.0, 5.0]]), jitter=0.0)
    ps.emit()
    ps.advect(lt.CpuField(u), 100)
    assert np.allclose(ps.pos, [[8.0, 5.0, 5.0]])
    ps.advect(lt.CpuField(u), 10000)
    assert len(ps.pos) == 0


def test_particles_removed_in_solid():
    shape = (40, 10, 10)
    solid = np.zeros(shape, bool)
    solid[10:14] = True
    ps = lt.ParticleSystem(np.array([[2.0, 5.0, 5.0]]), jitter=0.0)
    ps.emit()
    ps.advect(lt.CpuField(uniform_field(shape), solid), 150)
    assert len(ps.pos) == 0


def test_streamline_straight_in_uniform_flow_and_stops_at_solid():
    shape = (40, 10, 10)
    solid = np.zeros(shape, bool)
    solid[25:] = True
    ls = lt.streamlines(uniform_field(shape), np.array([[2.0, 5.0, 5.0], [2.0, 3.0, 3.0]]), solid, h=0.5, max_points=200)
    assert len(ls) == 2
    for l in ls:
        assert np.allclose(l[:, 1:3], l[0, 1:3]) and np.all(np.diff(l[:, 0]) > 0)
        assert l[-1, 0] < 25 and np.allclose(l[:, 3], 0.06)


def test_slice_vorticity_of_solid_body_rotation():
    nx, ny, nz = 12, 5, 12
    i, _, k = np.meshgrid(np.arange(nx) + 0.5, np.arange(ny) + 0.5, np.arange(nz) + 0.5, indexing="ij")
    w = 0.01
    u = np.stack([w * (k - 6), np.zeros_like(i), -w * (i - 6)])     # ω_y = ∂u/∂z − ∂w/∂x = 2w
    s = lt.slice_plane(u, None, "xz", 2)
    assert s["speed"].shape == (nx, nz)
    assert np.allclose(s["vort"][2:-2, 2:-2], 2 * w)


def test_slice_geometry_spans_domain():
    p = lt.plan_grid(cylinder_tris(), "low")
    g = lt.slice_geometry(p, "xz", int(p.origin_cells[1]))
    assert abs(np.linalg.norm(g["axisA"]) - p.shape[0] * p.dx) < 1e-9
    assert abs(np.linalg.norm(g["axisB"]) - p.shape[2] * p.dx) < 1e-9
    assert g["axisA"][0] < 0          # 격자 +x = 풍동 −X


# ---------------------------------------------------------------- IPC

def test_pack_unpack_roundtrip():
    a = np.arange(12, dtype=np.float32).reshape(3, 4)
    b = np.array([3, 1, 2], dtype=np.int32)
    c = np.array([1, 0, 1], dtype=bool)
    buf = pr.pack({"type": "frame", "x": 1.5, "s": "한글"}, {"a": a, "b": b, "c": c})
    h, arr = pr.unpack(buf)
    assert h["type"] == "frame" and h["s"] == "한글"
    assert np.array_equal(arr["a"], a) and np.array_equal(arr["b"], b) and arr["c"].tolist() == [1, 0, 1]
    hl, pl = struct.unpack_from("<II", buf)
    assert pl == 48 + 12 + 3 and hl + pl + 8 == len(buf)


def test_unpack_rejects_out_of_range_array():
    buf = pr.pack({"type": "x"}, {"a": np.zeros(4, np.float32)})
    with pytest.raises(ValueError):
        pr.unpack(buf[:-1])


def test_read_stl_binary_and_ascii(tmp_path):
    tris = box_tris((0, 0, 0), (1, 2, 3))
    n = len(tris) // 3
    rec = np.zeros(n, dtype=np.dtype([("n", "<f4", 3), ("v", "<f4", (3, 3)), ("a", "<u2")]))
    rec["v"] = tris.reshape(-1, 3, 3)
    p = tmp_path / "b.stl"
    p.write_bytes(b"\0" * 80 + struct.pack("<I", n) + rec.tobytes())
    assert np.allclose(pr.read_stl(str(p)), tris)
    q = tmp_path / "a.stl"
    lines = ["solid x"]
    for t in tris.reshape(-1, 3, 3):
        lines += ["facet normal 0 0 0", "outer loop"] + [f"vertex {x} {y} {z}" for x, y, z in t] + ["endloop", "endfacet"]
    q.write_text("\n".join(lines + ["endsolid x"]))
    assert np.allclose(pr.read_stl(str(q)), tris)


def _write_stl(path, tris):
    n = len(tris) // 3
    rec = np.zeros(n, dtype=np.dtype([("n", "<f4", 3), ("v", "<f4", (3, 3)), ("a", "<u2")]))
    rec["v"] = tris.reshape(-1, 3, 3)
    with open(path, "wb") as f:
        f.write(b"\0" * 80 + struct.pack("<I", n) + rec.tobytes())


def test_session_rebuild_remask_omega_decisions(tmp_path):
    stl = tmp_path / "c.stl"
    _write_stl(stl, cylinder_tris())
    s = sv.Session(sv.SelfTestBackend())
    assert s.apply({"mesh": str(stl), "alpha": 30}) == ["rebuild"]
    assert s.apply({"alpha": 60}) == ["remask"]
    assert s.apply({"re": 300}) == ["omega"]
    assert s.apply({"speed": 50}) == []                     # 속력 = 시간 축척·표시만
    assert "slice" in s.apply({"plane": "xy"})
    assert s.apply({"resolution": "medium"}) == ["rebuild"]
    assert abs(s.ref_length - 0.30) < 1e-6                 # 원통 지름(기본 기준 길이)


def test_session_frame_contents(tmp_path):
    stl = tmp_path / "c.stl"
    _write_stl(stl, cylinder_tris())
    s = sv.Session(sv.SelfTestBackend())
    s.apply({"mesh": str(stl), "alpha": 45, "re": 500})
    h, a = s.frame(s.advance())
    nA, nB = h["slice"]["nA"], h["slice"]["nB"]
    assert a["slice_speed"].shape == (nA, nB) and a["slice_vort"].shape == (nA, nB)
    assert np.isnan(a["slice_speed"]).any()                  # 물체 단면(고체) = NaN
    assert a["particles"].shape[1] == 4 and len(a["particles"]) > 0
    assert a["line_counts"].sum() == len(a["lines"])
    assert h["re"] == pytest.approx(500) and h["mach"] > 0.4 and h["coeffs"] and h["backend"] == "selftest"
    pr.unpack(pr.pack(h, a))                                  # NaN 은 배열 안에만 — 머리 JSON 에는 없어야 한다


def test_socket_roundtrip_with_selftest_backend(tmp_path):
    stl = tmp_path / "c.stl"
    _write_stl(stl, cylinder_tris())
    a, b = socket.socketpair()
    t = threading.Thread(target=sv.run_connection, args=(b, sv.SelfTestBackend()), daemon=True)
    t.start()
    a.settimeout(20)
    h, _ = pr.recv(a)
    assert h["type"] == "hello" and h["schema"] == pr.SCHEMA
    pr.send(a, {"cmd": "config", "mesh": str(stl), "alpha": 20, "resolution": "low"})
    frames, errors = 0, []
    while frames < 2:
        h, arr = pr.recv(a)
        if h["type"] == "frame":
            frames += 1
            assert h["alpha"] == 20 and "slice_speed" in arr
        elif h["type"] == "error":
            errors.append(h["message"])
            break
    assert not errors
    pr.send(a, {"cmd": "config", "mesh": str(tmp_path / "없음.stl")})
    while True:
        h, _ = pr.recv(a)
        if h["type"] == "error":
            break
    pr.send(a, {"cmd": "quit"})
    t.join(10)
    assert not t.is_alive()
    a.close()


@pytest.mark.skipif(os.environ.get("MNS_AIRFLOW_GPU_TEST") != "1", reason="GPU XLB 실행 시험은 MNS_AIRFLOW_GPU_TEST=1 일 때만")
def test_xlb_backend_runs_on_gpu():
    pytest.importorskip("xlb")
    from xlb_backend import XlbBackend

    tris = cylinder_tris()
    plan = lt.plan_grid(tris, "low")
    be = XlbBackend(log=lambda m: None)
    be.build(plan, lt.body_to_lattice(tris, plan, 90))
    be.set_omega(lt.lb_parameters(300, 0.30, plan)["omega"])
    be.run(2000)
    u = be.velocity()
    assert np.isfinite(u).all() and be.solid.sum() > 500
    c = lt.coefficients(be.force(), plan, math.pi * 0.30 ** 2 / 4)
    assert c["CD"] > 0
    # GPU 후처리(Warp) = numpy 기준 구현
    g, cpu = be.field(), lt.CpuField(u, be.solid)
    assert abs(g.max_speed() - cpu.max_speed()) < 1e-6
    pts = lt.rake(plan, 5, 5, x_frac=0.5)
    assert np.allclose(g.sample(pts), cpu.sample(pts), atol=1e-6)
    assert np.allclose(g.advect(pts, 80), cpu.advect(pts, 80), atol=1e-3)
    gs, cs = g.slice("xz", int(plan.origin_cells[1])), cpu.slice("xz", int(plan.origin_cells[1]))
    assert np.allclose(gs["speed"], cs["speed"], atol=1e-6) and np.allclose(gs["vort"], cs["vort"], atol=1e-6)
    gl, cl = g.streamlines(pts[:4], 0.8, 100), cpu.streamlines(pts[:4], 0.8, 100)
    assert len(gl) == len(cl) and np.allclose(gl[0][:20], cl[0][:20], atol=1e-3)


def test_check_diverged():
    lt.check_diverged(0.1)
    for bad in (float("nan"), 0.5, float("inf")):
        with pytest.raises(FloatingPointError):
            lt.check_diverged(bad)
