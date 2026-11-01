"""README · 매뉴얼이 가리키는 로컬 파일(이미지 · 문서)이 있는지 검사한다.

    python scripts/check_doc_links.py [--strict] [파일.md ...]

- 기본 대상: README.md · docs/public/README.md · docs/manual/**/*.md
- 마크다운 링크/이미지 `[..](경로)` · `![..](경로)` 와 HTML `<img src="경로">` 를 본다. http(s) · mailto · `#앵커` 는 건너뛴다.
- 상대 경로는 그 md 파일 폴더 기준이다. 단 docs/public/README.md 는 공개판에서 저장소 루트 README 가 되므로 루트 기준이다.
- 파일이 없지만 docs/manual/_shots.json 의 `shots[].file` 에 있으면 '촬영 대기'(아직 찍지 않은 스크린샷)로 보고하고 통과시킨다.
  --strict 면 촬영 대기도 실패로 친다(스크린샷을 다 찍은 뒤의 검사).
- 종료 코드: 0 = 통과, 1 = 없는 파일이 있음.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parent.parent
SHOTS = Path("docs/manual/_shots.json")
# 공개판에서 루트로 옮겨지는 파일 — 상대 경로를 저장소 루트 기준으로 푼다.
ROOT_BASED = {Path("docs/public/README.md")}

_MD_LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
_HTML_SRC = re.compile(r"<(?:img|source)\b[^>]*?\bsrc\s*=\s*[\"']([^\"']+)[\"']", re.IGNORECASE)
_FENCE = re.compile(r"^\s*(```|~~~)")
_CODE_SPAN = re.compile(r"`[^`]*`")
_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)


@dataclass
class Report:
    ok: list[tuple[str, str]] = field(default_factory=list)
    pending: list[tuple[str, str]] = field(default_factory=list)
    missing: list[tuple[str, str]] = field(default_factory=list)

    def failed(self, strict: bool) -> bool:
        return bool(self.missing or (strict and self.pending))


def links_in(text: str) -> list[str]:
    """md 본문의 로컬 링크 후보(코드 블록 · 코드 조각 · 주석 안은 뺀다)."""
    text = _COMMENT.sub("", text)
    out: list[str] = []
    fenced = False
    for line in text.splitlines():
        if _FENCE.match(line):
            fenced = not fenced
            continue
        if fenced:
            continue
        line = _CODE_SPAN.sub("", line)
        out += _MD_LINK.findall(line)
        out += _HTML_SRC.findall(line)
    return out


def is_local(target: str) -> bool:
    t = target.strip()
    return bool(t) and not t.startswith("#") and not re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", t)


def pending_files(root: Path) -> set[str]:
    p = root / SHOTS
    if not p.is_file():
        return set()
    data = json.loads(p.read_text(encoding="utf-8"))
    return {Path(s["file"]).as_posix() for s in data.get("shots", []) if s.get("file")}


def default_targets(root: Path) -> list[Path]:
    cands = [root / "README.md", root / "docs/public/README.md"]
    cands += sorted((root / "docs/manual").rglob("*.md")) if (root / "docs/manual").is_dir() else []
    return [p for p in cands if p.is_file()]


def check(files: list[Path], root: Path = ROOT) -> Report:
    rep = Report()
    pend = pending_files(root)
    for md in files:
        md = md.resolve()
        rel_md = md.relative_to(root)
        base = root if rel_md in ROOT_BASED else md.parent
        for raw in links_in(md.read_text(encoding="utf-8")):
            if not is_local(raw):
                continue
            path = unquote(raw.split("#", 1)[0].split("?", 1)[0])
            if not path:
                continue
            target = (root / path.lstrip("/")) if path.startswith("/") else (base / path)
            target = target.resolve()
            try:
                rel = target.relative_to(root).as_posix()
            except ValueError:
                rel = str(target)
            item = (rel_md.as_posix(), rel)
            if target.exists():
                rep.ok.append(item)
            elif rel in pend:
                rep.pending.append(item)
            else:
                rep.missing.append(item)
    return rep


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("files", nargs="*", type=Path, help="검사할 md (기본: README · 공개 README · 매뉴얼)")
    ap.add_argument("--strict", action="store_true", help="촬영 대기도 실패로 친다")
    ap.add_argument("--root", type=Path, default=ROOT, help="저장소 루트 (기본: 이 스크립트의 상위 폴더)")
    a = ap.parse_args(argv)
    root = a.root.resolve()
    files = [f if f.is_absolute() else (Path.cwd() / f) for f in a.files] or default_targets(root)
    rep = check(files, root)
    for md, rel in rep.missing:
        print(f"없음      {md} -> {rel}")
    for md, rel in rep.pending:
        print(f"촬영 대기 {md} -> {rel}")
    uniq_pend = len({r for _, r in rep.pending})
    print(f"\n대상 {len(files)}개 · 링크 확인 {len(rep.ok)} · 촬영 대기 {len(rep.pending)}(파일 {uniq_pend}) · 없음 {len(rep.missing)}")
    return 1 if rep.failed(a.strict) else 0


if __name__ == "__main__":
    sys.exit(main())
