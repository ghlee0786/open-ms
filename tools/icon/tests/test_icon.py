"""앱 아이콘: 생성기 크기 집합, 커밋된 app.ico 가 모든 크기를 담고 있는지."""
import importlib.util
from pathlib import Path

import pytest

PIL = pytest.importorskip("PIL")
from PIL import Image  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location("icon", ROOT / "tools" / "icon" / "make_icon.py")
icon = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(icon)


def test_render_sizes_are_square_rgba_with_transparent_corners():
    for s in (16, 32, 256):
        im = icon.render(s, simple=s <= 32)
        assert im.size == (s, s) and im.mode == "RGBA"
        assert im.getpixel((0, 0))[3] < 40           # 둥근 모서리 = 투명
        assert im.getpixel((s // 2, s // 2))[3] >= 250   # 배경 불투명(LANCZOS 축소 반올림 허용)


def test_committed_ico_contains_all_sizes():
    ico = ROOT / "src" / "Sim.App" / "Assets" / "app.ico"
    assert ico.exists(), "tools/icon/make_icon.py 를 실행해 app.ico 를 만드세요"
    sizes = Image.open(ico).info.get("sizes")
    assert {(s, s) for s in icon.SIZES} <= set(sizes)
