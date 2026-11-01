"""scripts/check_doc_links.py 시험 + README 인포그래픽(SVG) 형식 검사."""
import importlib.util
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("check_doc_links", REPO / "scripts" / "check_doc_links.py")
cdl = importlib.util.module_from_spec(_spec)
sys.modules["check_doc_links"] = cdl  # dataclass 가 모듈을 찾는다
_spec.loader.exec_module(cdl)


def _write(p: Path, text: str) -> Path:
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    return p


def test_classifies_ok_pending_missing(tmp_path):
    _write(tmp_path / "assets/infographics/a.svg", "<svg/>")
    _write(tmp_path / "docs/manual/02-x.md", "# x")
    _write(tmp_path / "docs/manual/_shots.json", json.dumps(
        {"schema": "mns.shots/1", "shots": [{"file": "docs/manual/images/wait.png"}, {"file": "assets/demo.gif"}]}))
    man = _write(tmp_path / "docs/manual/01-a.md", "\n".join([
        "![있음](../../assets/infographics/a.svg)",
        "[다음 장](02-x.md#2-1-제목)",
        "![대기](images/wait.png)",
        "![없음](images/nope.png)",
        "[외부](https://example.com/x.png) [앵커](#위) [메일](mailto:a)",
        "`![코드 조각](images/code.png)`",
        "```",
        "![코드 블록](images/block.png)",
        "```",
        "<!-- ![주석](images/comment.png) -->",
        '<p><img src="images/html-missing.png" width="10"></p>',
    ]))
    # 공개 README 초안은 저장소 루트 기준으로 푼다
    pub = _write(tmp_path / "docs/public/README.md", "![히어로](assets/demo.gif)\n![그림](assets/infographics/a.svg)")

    rep = cdl.check([man, pub], tmp_path)

    assert sorted(r for _, r in rep.ok) == ["assets/infographics/a.svg", "assets/infographics/a.svg", "docs/manual/02-x.md"]
    assert sorted(r for _, r in rep.pending) == ["assets/demo.gif", "docs/manual/images/wait.png"]
    assert sorted(r for _, r in rep.missing) == ["docs/manual/images/html-missing.png", "docs/manual/images/nope.png"]
    assert rep.failed(strict=False)


def test_exit_codes(tmp_path):
    _write(tmp_path / "docs/manual/_shots.json", json.dumps({"shots": [{"file": "docs/manual/images/wait.png"}]}))
    _write(tmp_path / "docs/manual/README.md", "![대기](images/wait.png)")
    assert cdl.main(["--root", str(tmp_path)]) == 0
    assert cdl.main(["--root", str(tmp_path), "--strict"]) == 1
    _write(tmp_path / "README.md", "![없음](assets/none.png)")
    assert cdl.main(["--root", str(tmp_path)]) == 1


def test_repository_docs_have_no_missing_files():
    """저장소의 README · 공개 README · 매뉴얼이 가리키는 파일은 모두 있거나 촬영 대기(_shots.json)다."""
    rep = cdl.check(cdl.default_targets(REPO), REPO)
    assert rep.missing == []


def test_shots_json_shape():
    p = REPO / "docs/manual/_shots.json"
    if not p.is_file():
        return
    data = json.loads(p.read_text(encoding="utf-8"))
    assert data["schema"] == "mns.shots/1"
    files = [s["file"] for s in data["shots"]]
    assert len(files) == len(set(files)), "같은 파일 이름이 두 번 있다"
    for s in data["shots"]:
        assert re.fullmatch(r"(assets|docs/manual/images)/[a-z0-9/_.-]+\.(png|gif)", s["file"]), s["file"]
        assert s["scenario"].startswith("config/scenarios/demo_"), "공개 데모 시나리오만 쓴다"
        assert s.get("description")


def test_infographics_are_static_animated_svg():
    """README 인포그래픽: XML 이 바르고, 자바스크립트 · 외부 자원 없이 CSS/SMIL 로만 움직인다(GitHub 이 그대로 보여 준다)."""
    svgs = sorted((REPO / "assets/infographics").glob("*.svg"))
    assert len(svgs) >= 5
    for f in svgs:
        text = f.read_text(encoding="utf-8")
        root = ET.fromstring(text)
        assert root.tag == "{http://www.w3.org/2000/svg}svg", f.name
        assert root.get("viewBox"), f.name
        assert not re.search(r"<script|<foreignObject|\bon[a-z]+\s*=|javascript:|@import|@font-face", text, re.IGNORECASE), f.name
        assert not re.search(r"(?:href|src)\s*=\s*[\"'](?:https?:)?//", text), f.name
        assert "@keyframes" in text or "<animate" in text, f"{f.name}: 애니메이션 없음"
        assert root.find("{http://www.w3.org/2000/svg}title") is not None, f"{f.name}: <title> 없음"
