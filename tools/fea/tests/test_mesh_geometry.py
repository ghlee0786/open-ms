import json
import os
import struct
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import mesh_geometry as mg  # noqa: E402

gmsh = pytest.importorskip("gmsh")


def _cube_tris(o=(0.0, 0.0, 0.0), s=0.1):
    x0, y0, z0 = o
    p = [(x0 + (i & 1) * s, y0 + ((i >> 1) & 1) * s, z0 + ((i >> 2) & 1) * s) for i in range(8)]
    quads = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]  # 바깥 법선
    tris = []
    for a, b, c, d in quads:
        tris += [(p[a], p[b], p[c]), (p[a], p[c], p[d])]
    return tris


def _write_stl(path, tris):
    with open(path, "wb") as f:
        f.write(b"\0" * 80 + struct.pack("<I", len(tris)))
        for t in tris:
            f.write(struct.pack("<12fH", 0, 0, 0, *t[0], *t[1], *t[2], 0))


def _read_inp(path):
    nodes, elems, etype, mode = {}, [], None, None
    for line in open(path):
        s = line.strip()
        if s.startswith("*NODE"):
            mode = "n"
            continue
        if s.startswith("*ELEMENT"):
            mode, etype = "e", s.split("TYPE=")[1]
            continue
        if not s or s.startswith("*"):
            continue
        v = [x.strip() for x in s.split(",")]
        if mode == "n":
            nodes[int(v[0])] = tuple(float(x) for x in v[1:4])
        else:
            elems.append([int(x) for x in v[1:]])
    return nodes, elems, etype


def test_stl_cube_tet10_volume_and_abaqus_node_order(tmp_path):
    stl = tmp_path / "cube.stl"
    _write_stl(stl, _cube_tris())
    out = tmp_path / "m.inp"
    summ = tmp_path / "s.json"
    assert mg.main([str(stl), "--out", str(out), "--size", "0.03", "--summary", str(summ)]) == 0
    info = json.loads(summ.read_text(encoding="utf-8"))
    assert info["ok"] and abs(info["volume_m3"] - 1e-3) < 1e-6
    nodes, elems, etype = _read_inp(out)
    assert etype == "C3D10" and len(elems) == info["elements"]
    # Abaqus C3D10: 5:(1,2) 6:(2,3) 7:(3,1) 8:(1,4) 9:(2,4) 10:(3,4) — 직선 변이므로 중점
    pairs = [(0, 1), (1, 2), (2, 0), (0, 3), (1, 3), (2, 3)]
    for e in elems[:50]:
        p = [nodes[n] for n in e]
        for k, (a, b) in enumerate(pairs):
            mid = tuple((p[a][i] + p[b][i]) / 2 for i in range(3))
            assert max(abs(mid[i] - p[4 + k][i]) for i in range(3)) < 1e-9
        assert mg.tet_volume(*p[:4]) > 0


def test_step_with_two_touching_boxes_is_fragmented_and_bonded(tmp_path):
    step = tmp_path / "two.step"
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Terminal", 0)
        gmsh.model.add("t")
        gmsh.model.occ.addBox(0, 0, 0, 0.1, 0.05, 0.05)
        gmsh.model.occ.addBox(0.1, 0, 0, 0.1, 0.05, 0.05)
        gmsh.model.occ.synchronize()
        gmsh.option.setString("Geometry.OCCTargetUnit", "M")
        gmsh.write(str(step))
    finally:
        gmsh.finalize()
    nodes, elems, etype, info = mg.mesh_file(str(step), 0.02, order=1)
    assert etype == "C3D4" and info["volumes"] == 2
    assert abs(info["volume_m3"] - 5e-4) < 5e-6
    # 결합: 두 상자가 경계면 절점을 공유 → 연결 요소 하나
    parent = list(range(len(nodes)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for e in elems:
        for n in e[1:]:
            parent[find(n)] = find(e[0])
    assert len({find(e[0]) for e in elems}) == 1


def test_step_with_overlapping_parts_is_cut_then_bonded(tmp_path):
    # 실모델 센서처럼 부품이 겹친 STEP(2026-10-07): 작은 부품에서 겹친 부분을 잘라 내고 결합 → 체적 2개, 부피 = 합집합
    step = tmp_path / "overlap.step"
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Terminal", 0)
        gmsh.model.add("o")
        gmsh.model.occ.addBox(0, 0, 0, 0.1, 0.05, 0.05)
        gmsh.model.occ.addBox(0.09, 0, 0, 0.05, 0.05, 0.05)  # 10 mm 겹침
        gmsh.model.occ.synchronize()
        gmsh.option.setString("Geometry.OCCTargetUnit", "M")
        gmsh.write(str(step))
    finally:
        gmsh.finalize()
    nodes, elems, etype, info = mg.mesh_file(str(step), 0.02, order=1)
    assert info["volumes"] == 2
    assert abs(info["overlapVolume_m3"] - 0.01 * 0.05 * 0.05) < 1e-8
    assert abs(info["volume_m3"] - 0.14 * 0.05 * 0.05) < 5e-6
    assert info["size_used_m"] == 0.02


def test_multi_shell_stl_reports_exit_3(tmp_path):
    stl = tmp_path / "two.stl"
    _write_stl(stl, _cube_tris() + _cube_tris((0.3, 0, 0)))
    assert len(mg.connected_components(mg.read_stl(str(stl)))) == 2
    summ = tmp_path / "s.json"
    assert mg.main([str(stl), "--out", str(tmp_path / "x.inp"), "--size", "0.05", "--summary", str(summ)]) == mg.EXIT_MULTI
    assert "껍질" in json.loads(summ.read_text(encoding="utf-8"))["error"]


def test_bad_input_exit_2(tmp_path):
    assert mg.main([str(tmp_path / "none.stl"), "--out", str(tmp_path / "x.inp"), "--size", "0.01"]) == mg.EXIT_INPUT
    assert mg.main([str(tmp_path / "a.obj"), "--out", str(tmp_path / "x.inp"), "--size", "0.01"]) == mg.EXIT_INPUT


def test_step_body_option_meshes_only_the_selected_solid(tmp_path):
    # 조립 STEP 에서 부품 하나(--body N, 가져온 순서 1부터): 작은 상자만 메시하고 그 부피·경계 상자를 요약에 남긴다
    step = tmp_path / "asm.step"
    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Terminal", 0)
        gmsh.model.add("t")
        gmsh.model.occ.addBox(0, 0, 0, 0.1, 0.1, 0.1)
        gmsh.model.occ.addBox(0.3, 0, 0, 0.02, 0.01, 0.04)
        gmsh.model.occ.synchronize()
        gmsh.option.setString("Geometry.OCCTargetUnit", "M")
        gmsh.write(str(step))
    finally:
        gmsh.finalize()
    out = tmp_path / "m.inp"
    summ = tmp_path / "s.json"
    assert mg.main([str(step), "--out", str(out), "--size", "0.005", "--order", "1", "--body", "2", "--summary", str(summ)]) == 0
    info = json.loads(summ.read_text(encoding="utf-8"))
    assert info["body"] == 2 and abs(info["bodyVolume_m3"] - 8e-6) < 1e-9
    assert abs(info["volume_m3"] - 8e-6) < 1e-8
    nodes, _, _ = _read_inp(out)
    assert min(p[0] for p in nodes.values()) > 0.29          # 큰 상자(x 0–0.1) 절점 없음
    assert mg.main([str(step), "--out", str(out), "--size", "0.005", "--body", "3"]) == mg.EXIT_INPUT
    # --list-bodies: 메시 없이 솔리드별 부피·경계 상자(가져온 순서)
    lst = tmp_path / "bodies.json"
    assert mg.main([str(step), "--list-bodies", str(lst)]) == 0
    bodies = json.loads(lst.read_text(encoding="utf-8"))
    assert [b["body"] for b in bodies] == [1, 2]
    assert abs(bodies[0]["volume_m3"] - 1e-3) < 1e-9 and abs(bodies[1]["volume_m3"] - 8e-6) < 1e-9
    assert abs(bodies[1]["bbox_m"][0] - 0.3) < 1e-6
    assert abs(bodies[1]["area_m2"] - 28e-4) < 1e-9          # 20×10×40 mm 상자 겉넓이 2(2+8+4) cm²
