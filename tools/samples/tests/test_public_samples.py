"""공개 합성 샘플: 데모 컨테이너 유동장(samples/flow) · Fusion 내보내기 1.1 형식 샘플이 생성기와 일치하고 형식이 맞는지."""
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def _load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / "samples" / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_flowfield_matches_generator_and_shape():
    flow = _load("make_public_flowfield")
    doc = json.loads((ROOT / "samples" / "flow" / "demo_container_flow.json").read_text(encoding="utf-8"))
    assert doc == json.loads(json.dumps(flow.build(), ensure_ascii=False)), "생성기를 다시 돌려 파일을 맞추세요"
    s = doc["surface"]
    assert doc["schema"] == "mns.surface-flow/1" and s["kind"] == "cylinder"
    assert "합성 예시" in doc["warning"]
    for c in doc["cases"]:
        assert c["method"] == "synthetic"
        for f in c["fields"].values():
            assert len(f["side"]) == s["nx"] * s["ntheta"]
            assert len(f["capFwd"]) == len(f["capAft"]) == s["nr"] * s["ntheta"]
        for ln in c["streamlines"]:
            assert len(ln["p"]) == 3 * len(ln["s"]) and len(ln["s"]) >= 2
    # 정체점 Cp ≈ 1(받음각 90° 측면 θ = 0)
    c90 = next(c for c in doc["cases"] if c["alpha"] == 90)
    assert abs(c90["fields"]["cp"]["side"][0] - 1.0) < 0.02


def test_demo_scenario_points_to_flowfield():
    scen = json.loads((ROOT / "config" / "scenarios" / "demo_01_cargo_airdrop.json").read_text(encoding="utf-8"))
    ff = next(b for b in scen["bodies"] if b["name"] == "container")["aero"]["flowField"]
    assert (ROOT / "config" / "scenarios" / ff).resolve() == (ROOT / "samples" / "flow" / "demo_container_flow.json").resolve()


def test_fusion11_sample_fields():
    doc = json.loads((ROOT / "samples" / "fusion_export" / "spike_sensor_v11_sample.massprops.json").read_text(encoding="utf-8"))
    assert doc["exporter"].endswith("1.1")
    assert abs(doc["mass"] - 1.0) < 1e-9
    assert abs(sum(p["mass"] for p in doc["parts"]) - doc["mass"]) < 1e-9
    assert (ROOT / "samples" / "fusion_export" / doc["stl"]).is_file()
    for p in doc["parts"]:
        assert p["materialProperties"]["E"] > 0 and len(p["appearance"]["baseColor"]) == 3
    assert {u["name"] for u in doc["userParameters"]} >= {"spike_length", "body_dia"}
    assert len(doc["joints"]) >= 2 and all(j["type"] for j in doc["joints"])
