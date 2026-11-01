"""내보내기 1.1 추가 정보(재료 물성·외관·메시 정밀도·질량 정확도·사용자 파라미터·조인트·USD) 검증.

Fusion 없이 adsk 가짜 객체로 export_bodies 전체를 돌린다: python -m pytest tools/fusion/tests
"""
import importlib.util
import json
import math
import sys
import types
from pathlib import Path
from types import SimpleNamespace as NS

import pytest

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("mns_export_x", ROOT / "tools" / "fusion" / "MnS_ExportForSim" / "MnS_ExportForSim.py")
mns = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mns)


# ---------------------------------------------------------------- 순수 함수

@pytest.mark.parametrize("units,factor", [
    ("", 1.0), ("kg / m^3", 1.0), ("g / cm^3", 1000.0), ("kg/cm^3", 1e6), ("MPa", 1e6), ("GPa", 1e9),
    ("N / mm^2", 1e6), ("psi", 6894.757293168), ("1 / C", 1.0), ("1/F", 1.8), ("C^-1", 1.0),
    ("W / (m * C)", 1.0), ("W / (m K)", 1.0), ("J / (kg * C)", 1.0), ("cal / (g * C)", 4184.0),
    ("µm / (m * C)", 1e-6), ("kg/m³", 1.0), ("lbm / in^3", 0.45359237 / 0.0254 ** 3),
])
def test_unit_factor_si(units, factor):
    assert mns.unit_factor_si(units) == pytest.approx(factor, rel=1e-9)


@pytest.mark.parametrize("bad", ["furlong", "kg / / m", "m^", "(m", "kg $"])
def test_unit_factor_si_rejects_unknown(bad):
    assert mns.unit_factor_si(bad) is None


def _fusion_al_props():
    # Fusion 물리 재료(Aluminum 6061)와 같은 모양의 id·이름·단위
    return [
        {"id": "structural_Density", "name": "Density", "value": 2.7, "units": "g / cm^3"},
        {"id": "structural_Young_modulus", "name": "Young's Modulus", "value": 68.9, "units": "GPa"},
        {"id": "structural_Poisson_ratio", "name": "Poisson's Ratio", "value": 0.33, "units": ""},
        {"id": "structural_Shear_modulus", "name": "Shear Modulus", "value": 26.0, "units": "GPa"},
        {"id": "structural_Minimum_yield_stress", "name": "Yield Strength", "value": 275.0, "units": "MPa"},
        {"id": "structural_Minimum_tensile_strength", "name": "Tensile Strength", "value": 310.0, "units": "MPa"},
        {"id": "structural_Thermal_expansion_coefficient", "name": "Thermal Expansion Coefficient", "value": 23.6, "units": "µm / (m * C)"},
        {"id": "structural_Thermal_conductivity", "name": "Thermal Conductivity", "value": 167.0, "units": "W / (m * C)"},
        {"id": "structural_Specific_heat", "name": "Specific Heat", "value": 0.896, "units": "J / (g * C)"},
    ]


def test_material_properties_si_from_fusion_like_props():
    si, bad = mns.material_properties_si(_fusion_al_props())
    assert bad == []
    assert si["density"] == pytest.approx(2700)
    assert si["E"] == pytest.approx(68.9e9)
    assert si["nu"] == pytest.approx(0.33)
    assert si["yield"] == pytest.approx(275e6)
    assert si["ultimate"] == pytest.approx(310e6)
    assert si["thermalExpansion"] == pytest.approx(23.6e-6)
    assert si["thermalConductivity"] == pytest.approx(167)
    assert si["specificHeat"] == pytest.approx(896)
    assert set(si) <= set(mns.MATERIAL_UNITS)


def test_material_properties_partial_and_unconverted():
    si, bad = mns.material_properties_si([
        {"id": "structural_Density", "name": "Density", "value": 1.05, "units": "g / cm^3"},
        {"id": "structural_Young_modulus", "name": "Young's Modulus", "value": 2.2, "units": "furlong"},   # 단위 모름
        {"id": "structural_Minimum_yield_stress", "name": "Yield", "value": 40.0, "units": ""},          # 차원 있는데 단위 없음
        {"id": "opaque_albedo", "name": "Color", "value": "x"},
    ])
    assert si == {"density": pytest.approx(1050)}
    assert [b["key"] for b in bad] == ["E", "yield"]


def test_appearance_from_color_and_from_properties():
    a = mns.appearance_info("Red Anodized", "A1", (255, 0, 51, 255), 0.25, [{"id": "metal_f0", "kind": "color", "value": (1, 2, 3, 255)}])
    assert a["baseColor"] == pytest.approx([1.0, 0.0, 0.2])
    assert a["opacity"] == 1.0 and a["metallic"] == 1.0 and a["roughness"] == 0.25 and a["id"] == "A1"
    # 미리보기 color 가 없으면 스키마 색 속성에서
    b = mns.appearance_info("ABS White", props=[
        {"id": "opaque_albedo", "kind": "color", "value": (255, 255, 255, 128)},
        {"id": "surface_roughness", "kind": "float", "value": 0.6},
    ])
    assert b["baseColor"] == [1.0, 1.0, 1.0] and b["opacity"] == pytest.approx(128 / 255)
    assert b["metallic"] == 0.0 and b["roughness"] == 0.6
    # 아무 것도 없으면 이름만
    assert mns.appearance_info("x") == {"name": "x"}


def test_mesh_settings_defaults_presets_and_custom():
    d = mns.mesh_settings(None)
    assert d["preset"] == "medium" and d["method"] == "stl-export" and d["refinement"] == "MeshRefinementMedium"
    f = mns.mesh_settings("fine")
    assert f["method"] == "mesh-calculator" and f["surfaceDeviation_mm"] == 0.02
    c = mns.mesh_settings({"surfaceDeviation_mm": 0.1, "maxEdgeLength_mm": 5, "normalDeviation_deg": 10})
    assert c["preset"] == "custom" and c["maxEdgeLength_mm"] == 5.0
    v = mns.mesh_calculator_values(c)
    assert v["surfaceTolerance"] == pytest.approx(0.01) and v["maxSideLength"] == pytest.approx(0.5)
    assert v["maxNormalDeviation"] == pytest.approx(math.radians(10)) and "maxAspectRatio" not in v
    assert "maxSideLength" not in mns.mesh_calculator_values(f)   # 0 = 제한 없음 → 넣지 않음
    for bad in ("coarse", {"surfaceDeviation_mm": 0}, {"surfaceDeviation_mm": -1}, {"tolerance": 1}):
        with pytest.raises(ValueError):
            mns.mesh_settings(bad)


def test_resolve_options_defaults_equal_1_0_behaviour():
    o = mns.resolve_options(None)
    assert o == {"mesh": mns.mesh_settings("medium"), "massAccuracy": "high", "usd": False}
    assert mns.resolve_options({"massAccuracy": "VeryHigh", "usd": True})["massAccuracy"] == "veryHigh"
    with pytest.raises(ValueError):
        mns.resolve_options({"massAccuracy": "ultra"})


def test_tris_from_indexed_and_bbox_match():
    coords = [0, 0, 0, 10, 0, 0, 0, 10, 0, 0, 0, 10]
    tris = mns.tris_from_indexed(coords, [0, 1, 2, 0, 2, 3])
    assert tris[1] == ((0, 0, 0), (0, 10, 0), (0, 0, 10))
    m = mns.scale_tris(tris, 0.01)
    assert mns.bbox_matches(m, [0, 0, 0], [0.1, 0.1, 0.1])
    assert not mns.bbox_matches(m, [1, 0, 0], [1.1, 0.1, 0.1])   # 다른 좌표계(proxy 로컬 등)
    assert not mns.bbox_matches([], [0, 0, 0], [1, 1, 1])


def test_user_parameter_info_units():
    p = mns.user_parameter_info("arm_len", "250 mm", 25.0, "mm", "암 길이")
    assert p["valueSI"] == pytest.approx(0.25) and p["siUnit"] == "m" and p["comment"] == "암 길이"
    assert mns.user_parameter_info("a", "30 deg", math.radians(30), "deg")["valueSI"] == pytest.approx(math.radians(30))
    assert mns.user_parameter_info("n", "4", 4.0, "")["siUnit"] == ""
    assert "valueSI" not in mns.user_parameter_info("p", "1 MPa", 1.0, "MPa")


def test_filter_joints_keeps_group_related_only():
    js = [mns.joint_info("hinge", "Revolute", "box:1", "drone:1+arm:1"),
          mns.joint_info("slide", "Slider", "rail:1", "frame:1"),
          mns.joint_info("ground", "Rigid", "", "frame:1"),
          mns.joint_info("asm", "Rigid", "drone:1", "lid:1")]
    names = lambda r: [j["name"] for j in r]  # noqa: E731
    assert names(mns.filter_joints(js, {"drone:1+arm:1"})) == ["hinge", "asm"]   # 같은 것 · 조상
    assert names(mns.filter_joints(js, {"drone:1"})) == ["hinge", "asm"]         # 자손
    assert names(mns.filter_joints(js, {""})) == ["ground"]                       # 루트 바디 → 루트에 붙은 것
    assert names(mns.filter_joints(js, {"rail:1", ""})) == ["slide", "ground"]
    j = mns.joint_info("x", "Ball", "a:1", None, (1.0, 2.0, 3.0))
    assert j["origin"] == pytest.approx([0.01, 0.02, 0.03]) and j["occurrenceTwo"] == ""


# ---------------------------------------------------------------- adsk 가짜 객체로 export_bodies 전체

class Coll:
    def __init__(self, items):
        self._items = list(items)
        self.count = len(self._items)

    def item(self, i):
        return self._items[i]


def cube_tris(size):
    s = size
    v = [(x, y, z) for x in (0, s) for y in (0, s) for z in (0, s)]
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    out = []
    for a, b, c, d in faces:
        out += [(v[a], v[b], v[c]), (v[a], v[c], v[d])]
    return out


def make_fake(with_extras=True, mesh_bbox_ok=True):
    """10 cm 정육면체 바디 하나(알루미늄, 빨강 외관) + 사용자 파라미터·조인트가 있는 가짜 Fusion."""
    calls = NS(accuracy=[], stl=[], calc=[], step=[], usd=[], assigned=[])
    fusion = NS(
        CalculationAccuracy=NS(LowCalculationAccuracy=0, MediumCalculationAccuracy=1, HighCalculationAccuracy=2, VeryHighCalculationAccuracy=3),
        MeshRefinementSettings=NS(MeshRefinementHigh=0, MeshRefinementMedium=1, MeshRefinementLow=2, MeshRefinementCustom=3),
        JointTypes=NS(RigidJointType=0, RevoluteJointType=1, SliderJointType=2, CylindricalJointType=3, BallJointType=6),
        DesignTypes=NS(DirectDesignType=1),
        Design=NS(cast=lambda x: x),
        TemporaryBRepManager=NS(get=lambda: NS(copy=lambda b: NS(copyOf=b.name))),
    )
    adsk = NS(fusion=fusion, core=NS(DocumentTypes=NS(FusionDesignDocumentType=0)))

    class Props:
        def __init__(self, acc):
            calls.accuracy.append(acc)
            self.mass, self.volume = 2.7, 1000.0
            self.centerOfMass = NS(x=5.0, y=5.0, z=5.0)

        def getXYZMomentsOfInertia(self):
            return True, 45.0, 45.0, 45.0, 0.0, 0.0, 0.0   # CG 기준 m a²/6 [kg·cm²]

        def getPrincipalMomentsOfInertia(self):
            return True, 45.0, 45.0, 45.0

        def getPrincipalAxes(self):
            return True, NS(x=1, y=0, z=0), NS(x=0, y=1, z=0), NS(x=0, y=0, z=1)

    class Calc:
        def calculate(self):
            calls.calc.append(dict(self.__dict__))
            off = 0.0 if mesh_bbox_ok else 50.0   # 경계상자와 안 맞음(proxy 로컬 좌표 흉내)
            tris = cube_tris(10.0)
            pts = [p for t in tris for p in t]
            return NS(nodeCoordinatesAsDouble=[c + off for p in pts for c in p], nodeIndices=list(range(len(pts))))

    def write_stl(opts):
        calls.stl.append(dict(opts.__dict__))
        mns.write_binary_stl(opts.filename, cube_tris(100.0))   # Fusion STL 은 mm
        return True

    if with_extras:
        material = NS(name="Aluminum 6061", id="PrismMaterial-002", parent=NS(name="Fusion Material Library"),
                      materialProperties=Coll([NS(id=p["id"], name=p["name"], value=p["value"], units=p["units"]) for p in _fusion_al_props()]))
        appearance = NS(name="Anodized Red", id="Prism-123", color=NS(red=204, green=0, blue=0, opacity=255), roughness=0.3,
                        appearanceProperties=Coll([NS(id="metal_f0", name="Color", value=NS(red=204, green=0, blue=0, opacity=255))]))
    else:
        material = NS(name="Steel", id=None, parent=None, materialProperties=Coll([]))
        appearance = None
    body = NS(name="Cube", material=material, appearance=appearance, parentComponent=NS(name="CubeComp"),
              assemblyContext=NS(fullPathName="cube:1"), isSolid=True, isVisible=True, entityToken="t1",
              boundingBox=NS(minPoint=NS(x=0.0, y=0.0, z=0.0), maxPoint=NS(x=10.0, y=10.0, z=10.0)),
              getPhysicalProperties=Props, meshManager=NS(createMeshCalculator=Calc))

    joints = [NS(name="Hinge", jointMotion=NS(jointType=1), occurrenceOne=NS(fullPathName="cube:1"), occurrenceTwo=None,
                 geometryOrOriginOne=NS(origin=NS(x=10.0, y=0.0, z=5.0)), isSuppressed=False),
              NS(name="Other", jointMotion=NS(jointType=2), occurrenceOne=NS(fullPathName="far:1"), occurrenceTwo=NS(fullPathName="away:1"),
                 geometryOrOriginOne=NS(geometry=NS(origin=NS(x=0.0, y=0.0, z=0.0))), isSuppressed=True)]
    asbuilt = [NS(name="AB", jointMotion=NS(jointType=0), occurrenceOne=NS(fullPathName="cube:1+pin:1"),
                  occurrenceTwo=NS(fullPathName="base:1"), geometry=NS(origin=NS(x=1.0, y=1.0, z=1.0)), isSuppressed=False)]
    params = [NS(name="edge", expression="100 mm", value=10.0, unit="mm", comment="정육면체 변")]

    stl_mgr = NS(createSTLExportOptions=lambda b, path: NS(filename=path, body=b.name), execute=write_stl)
    design = NS(exportManager=stl_mgr, unitsManager=NS(defaultLengthUnits="mm"),
                userParameters=Coll(params if with_extras else []),
                rootComponent=NS(allJoints=joints if with_extras else [], allAsBuiltJoints=asbuilt if with_extras else []))

    def tmp_execute(opts):
        getattr(calls, opts.kind).append(opts.filename)
        Path(opts.filename).write_text("stub", encoding="utf-8")
        return True

    class TmpBodies:
        def add(self, c):
            return NS(name="Body1", material=None, appearance=None)

    def add_copy(kind):
        return lambda src, name: calls.assigned.append((kind, name)) or NS(name=name)

    tmp_design = NS(designType=0, rootComponent=NS(bRepBodies=TmpBodies()),
                    materials=NS(itemByName=lambda n: None, addByCopy=add_copy("material")),
                    appearances=NS(itemByName=lambda n: None, addByCopy=add_copy("appearance")),
                    exportManager=NS(createSTEPExportOptions=lambda f, g: NS(kind="step", filename=f),
                                     createUSDExportOptions=lambda f, g: NS(kind="usd", filename=f), execute=tmp_execute))
    app = NS(activeProduct=design, activeDocument=NS(name="Cube REV7", activate=lambda: None))

    def add_doc(_t):
        app.activeProduct = tmp_design
        return NS(close=lambda save: None)

    app.documents = NS(add=add_doc)
    return adsk, app, design, body, calls


def run_export(monkeypatch, tmp_path, options=None, want_step=False, **fake):
    adsk, app, design, body, calls = make_fake(**fake)
    monkeypatch.setattr(mns, "adsk", adsk)
    path, mass, cg, warns = mns.export_bodies(app, design, [body], str(tmp_path), "cube", want_step, options=options)
    return json.loads(Path(path).read_text(encoding="utf-8")), calls, warns


V1_FIELDS = ("schema", "name", "source", "frame", "frameNote", "units", "mass", "cg", "inertia", "principalMoments",
             "bboxMin", "bboxMax", "stl", "stlUnit", "parts", "exportedAt", "exporter", "document", "fusionUnits", "accuracy", "warnings")
V1_PART_FIELDS = ("body", "component", "material", "mass", "volume_m3", "cg", "inertiaCheck", "stlScaleDetected")


def test_export_default_keeps_1_0_fields_and_path(monkeypatch, tmp_path):
    doc, calls, warns = run_export(monkeypatch, tmp_path)
    for k in V1_FIELDS:
        assert k in doc, k
    for k in V1_PART_FIELDS:
        assert k in doc["parts"][0], k
    # 기본 = 1.0 과 같은 경로: High 정확도 · STL 내보내기 MeshRefinementMedium · mm 감지
    assert calls.accuracy == [2] and doc["accuracy"] == "HighCalculationAccuracy"
    assert len(calls.stl) == 1 and calls.stl[0]["meshRefinement"] == 1 and calls.stl[0]["isBinaryFormat"]
    assert "surfaceDeviation" not in calls.stl[0] and calls.calc == []
    assert doc["parts"][0]["stlScaleDetected"] == 0.001
    assert doc["mesh"]["preset"] == "medium" and doc["mesh"]["methodUsed"] == ["stl-export(MeshRefinementMedium)"]
    assert doc["mass"] == pytest.approx(2.7) and doc["cg"] == pytest.approx([0.05, 0.05, 0.05])
    assert doc["bboxMax"] == pytest.approx([0.1, 0.1, 0.1], abs=1e-6)
    assert "usd" not in doc and "step" not in doc and calls.usd == [] and warns == []


def test_export_writes_material_appearance_params_joints(monkeypatch, tmp_path):
    doc, _, _ = run_export(monkeypatch, tmp_path)
    p = doc["parts"][0]
    assert p["materialId"] == "PrismMaterial-002" and p["materialLibrary"] == "Fusion Material Library"
    mp = p["materialProperties"]
    assert mp["density"] == pytest.approx(2700) and mp["E"] == pytest.approx(68.9e9) and mp["yield"] == pytest.approx(275e6)
    assert p["appearance"]["baseColor"] == pytest.approx([0.8, 0.0, 0.0]) and p["appearance"]["metallic"] == 1.0
    assert p["appearance"]["roughness"] == 0.3 and p["occurrence"] == "cube:1"
    assert doc["userParameters"] == [{"name": "edge", "expression": "100 mm", "unit": "mm", "value": 10.0,
                                      "valueSI": pytest.approx(0.1), "siUnit": "m", "comment": "정육면체 변"}]
    # 이 바디(cube:1)와 관계있는 조인트만: Hinge(같은 occurrence) · AB(자손) — Other 는 제외
    assert [j["name"] for j in doc["joints"]] == ["Hinge", "AB"]
    hinge = doc["joints"][0]
    assert hinge["type"] == "Revolute" and hinge["origin"] == pytest.approx([0.1, 0.0, 0.05]) and hinge["occurrenceTwo"] == ""
    assert doc["joints"][1]["source"] == "asBuiltJoint" and doc["joints"][1]["type"] == "Rigid"
    assert "2/3" in doc["jointsNote"]


def test_export_without_extras_omits_missing_values(monkeypatch, tmp_path):
    doc, _, _ = run_export(monkeypatch, tmp_path, with_extras=False)
    p = doc["parts"][0]
    assert p["material"] == "Steel" and "materialProperties" not in p and "appearance" not in p and "materialId" not in p
    assert doc["userParameters"] == [] and doc["joints"] == []


def test_export_fine_mesh_uses_triangle_mesh_calculator(monkeypatch, tmp_path):
    doc, calls, warns = run_export(monkeypatch, tmp_path, options={"mesh": "fine", "massAccuracy": "veryHigh"})
    assert calls.accuracy == [3] and doc["accuracy"] == "VeryHighCalculationAccuracy"
    assert calls.stl == [] and len(calls.calc) == 1
    assert calls.calc[0]["surfaceTolerance"] == pytest.approx(0.002) and calls.calc[0]["maxNormalDeviation"] == pytest.approx(math.radians(5))
    assert doc["mesh"]["methodUsed"] == ["mesh-calculator"] and doc["parts"][0]["stlScaleDetected"] == 0.01
    tris = mns.read_stl(str(tmp_path / "cube.stl"))
    assert mns.bounds(tris)[1] == pytest.approx([0.1, 0.1, 0.1], abs=1e-6) and warns == []


def test_export_mesh_calculator_falls_back_when_bbox_mismatch(monkeypatch, tmp_path):
    doc, calls, warns = run_export(monkeypatch, tmp_path, options={"mesh": {"surfaceDeviation_mm": 0.05, "maxEdgeLength_mm": 2}},
                                   mesh_bbox_ok=False)
    assert len(calls.calc) == 1 and len(calls.stl) == 1
    s = calls.stl[0]
    assert s["meshRefinement"] == 3 and s["surfaceDeviation"] == pytest.approx(0.005) and s["maximumEdgeLength"] == pytest.approx(0.2)
    assert doc["mesh"]["preset"] == "custom" and doc["mesh"]["methodUsed"] == ["stl-export(custom)"]
    assert any("경계상자" in w for w in warns)


def test_export_usd_and_step_share_temp_document(monkeypatch, tmp_path):
    doc, calls, warns = run_export(monkeypatch, tmp_path, options={"usd": True}, want_step=True)
    assert calls.step == [str(tmp_path / "cube.step")] and calls.usd == [str(tmp_path / "cube.usdz")]
    assert doc["step"]["file"] == "cube.step" and doc["usd"]["file"] == "cube.usdz" and doc["usd"]["frame"] == "fusion"
    assert ("material", "Aluminum 6061") in calls.assigned and ("appearance", "Anodized Red") in calls.assigned
    assert warns == []


def test_groups_options_merge_file_then_group():
    fake = types.ModuleType("adsk")
    fake.core, fake.fusion = types.ModuleType("adsk.core"), types.ModuleType("adsk.fusion")
    saved = {k: sys.modules.get(k) for k in ("adsk", "adsk.core", "adsk.fusion")}
    sys.modules.update({"adsk": fake, "adsk.core": fake.core, "adsk.fusion": fake.fusion})
    try:
        spec = importlib.util.spec_from_file_location("mns_groups", ROOT / "tools" / "fusion" / "MnS_ExportGroups" / "MnS_ExportGroups.py")
        grp = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(grp)
    finally:
        for k, v in saved.items():
            if v is None:
                sys.modules.pop(k, None)
            else:
                sys.modules[k] = v
    assert grp.group_options({"out": "x"}, {"name": "g"}) == {}
    assert grp.group_options({"mesh": "fine", "usd": True}, {"name": "g", "usd": False}) == {"mesh": "fine", "usd": False}
    assert mns.resolve_options(grp.group_options({"massAccuracy": "veryHigh"}, {}))["massAccuracy"] == "veryHigh"
