"""MnS_ExportGroups — 활성 Fusion 디자인을 '분리 단위' 그룹별로 대화상자 없이 일괄 내보낸다(애니메이션 기반 시나리오, PRD AA).

그룹 정의: groups_<이름>.json (기본 같은 폴더의 groups.json, 환경변수 MNS_GROUPS_PARAMS 로 바꿈).
  scope "root" = 루트 컴포넌트 바디, "box" = boxOccurrence 의 바디(어셈블리 좌표 proxy),
  "occ:<occurrence 이름>" = 그 루트 occurrence 의 바디(어셈블리 좌표 proxy). document 를 주면 활성 문서 이름을 확인한다.
  include = 바디 이름 부분 일치 패턴 목록("" = 남은 바디 전부). 위에서부터 먼저 맞는 그룹이 가져간다.
  숨김 바디 포함(전시용 단면 모델은 외피가 숨김 상태), SECTION_VIEW_* 제외.
  선택 사항(파일 최상위 또는 그룹별, 그룹 값 우선): "mesh"("medium"|"fine"|{…}) · "massAccuracy"("high"|"veryHigh") · "usd"(true/false).
  massprops 의 joints 는 그 그룹 바디의 occurrence 와 관계있는 것만, userParameters 는 디자인 전체.
그룹마다 MnS_ExportForSim.export_bodies 로 <name>.stl + .massprops.json (+ .step · .usdz) 을 out 폴더에 쓰고,
groups_summary.json 에 그룹별 질량·CG·바디 수를 남긴다.

실행: python ~/.claude/skills/fusion360/scripts/fusion_cli.py run tools/fusion/MnS_ExportGroups/MnS_ExportGroups.py
(가져오기와 같은 호출에서 돌리지 않는다 — fusion_scripts 의 크래시 회피 규칙.)
"""
import importlib.util
import json
import os

import adsk.core
import adsk.fusion

PARAMS = os.environ.get("MNS_GROUPS_PARAMS",
                        os.path.join(os.path.dirname(os.path.abspath(__file__)), "groups.json"))
EXCLUDE_PREFIX = "SECTION_VIEW"


OPTION_KEYS = ("mesh", "massAccuracy", "usd")


def group_options(prm, group):
    """내보내기 선택 사항(MnS_ExportForSim.resolve_options 키): 파일 전체 값 위에 그룹 값이 덮어쓴다. 없으면 기본(1.0 과 같음)."""
    out = {k: prm[k] for k in OPTION_KEYS if k in prm}
    out.update({k: group[k] for k in OPTION_KEYS if k in group})
    return out


def _load_exporter(path):
    spec = importlib.util.spec_from_file_location("mns_export_for_sim", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def run(_context: str):
    prm = json.load(open(PARAMS, encoding="utf-8"))
    mns = _load_exporter(prm["exporter"])
    app = adsk.core.Application.get()
    design = adsk.fusion.Design.cast(app.activeProduct)
    root = design.rootComponent
    if prm.get("document") and app.activeDocument.name != prm["document"]:
        raise RuntimeError(f"활성 문서 {app.activeDocument.name} ≠ {prm['document']}")

    def occ_bodies(name):
        occ = root.occurrences.itemByName(name)
        if occ is None:
            raise RuntimeError(f"occurrence {name} 없음")
        return [b.createForAssemblyContext(occ) for b in occ.component.bRepBodies]

    pools = {"root": [b for b in root.bRepBodies]}
    if prm.get("boxOccurrence"):
        pools["box"] = occ_bodies(prm["boxOccurrence"])
    for g in prm["groups"]:
        if g["scope"].startswith("occ:") and g["scope"] not in pools:
            pools[g["scope"]] = occ_bodies(g["scope"][4:])
    for k in pools:
        pools[k] = [b for b in pools[k] if b.isSolid and not b.name.upper().startswith(EXCLUDE_PREFIX)]
    taken = set()
    os.makedirs(prm["out"], exist_ok=True)
    summary = {"source": prm.get("source"), "document": app.activeDocument.name, "groups": []}
    for g in prm["groups"]:
        bodies = [b for b in pools[g["scope"]]
                  if b.entityToken not in taken and any(p in b.name for p in g["include"])]
        for b in bodies:
            taken.add(b.entityToken)
        if not bodies:
            summary["groups"].append({"name": g["name"], "error": "맞는 바디 없음"})
            print("SKIP", g["name"])
            continue
        # 숨김 바디는 STL 내보내기가 파일을 만들지 않는다 → 내보내는 동안만 켜고 되돌린다(문서는 저장하지 않음)
        hidden = [b for b in bodies if not b.isLightBulbOn]
        for b in hidden:
            b.isLightBulbOn = True
        try:
            json_path, mass, cg, warns = mns.export_bodies(
                app, design, bodies, prm["out"], g["name"], prm.get("step", True),
                extra={"group": g["name"], "groupScope": g["scope"], "groupPatterns": g["include"],
                       "hiddenBodiesIncluded": [b.name for b in hidden]},
                options=group_options(prm, g))
        finally:
            for b in hidden:
                b.isLightBulbOn = False
        summary["groups"].append({"name": g["name"], "bodies": len(bodies), "mass": mass, "cg": cg,
                                  "massprops": os.path.basename(json_path), "warnings": warns})
        print(f"GROUP {g['name']}: {len(bodies)} bodies, {mass:.3f} kg, cg={[round(c, 4) for c in cg]}, warnings={len(warns)}")
    with open(os.path.join(prm["out"], "groups_summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2)
