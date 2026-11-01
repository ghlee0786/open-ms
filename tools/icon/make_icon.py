"""MnS 시뮬레이터 앱 아이콘 생성기 (Pillow).

    python tools/icon/make_icon.py  →  src/Sim.App/Assets/app.ico (16~256 px) + app.png (256 px)

디자인: 짙은 남색 둥근 사각형 위에 주황 낙하산 캐노피 · 라이저 4줄 · 하늘색 캡슐(센서 박스),
왼쪽 위에서 내려오는 점선 궤적과 바닥의 착탄 표적 링(초록). 4배 해상도로 그린 뒤 축소(안티앨리어싱).
작은 크기(≤ 32 px)는 궤적·표적을 빼고 선을 굵게 한 단순판을 따로 그린다.
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "Sim.App" / "Assets"
SIZES = [16, 24, 32, 48, 64, 128, 256]

NAVY_TOP = (16, 30, 52)
NAVY_BOT = (31, 58, 96)
CANOPY_A = (255, 140, 66)
CANOPY_B = (244, 162, 97)
RISER = (230, 236, 244)
PAYLOAD = (76, 201, 240)
TRAJ = (142, 202, 255)
TARGET = (124, 217, 139)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def render(px: int, simple: bool) -> Image.Image:
    s = 4  # 초과 표본
    n = px * s
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    # 배경: 세로 그라데이션 둥근 사각형
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        gd.line([(0, y), (n, y)], fill=lerp(NAVY_TOP, NAVY_BOT, y / max(n - 1, 1)) + (255,))
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=int(n * 0.2), fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    u = n / 256.0  # 256 기준 좌표

    def P(x, y):
        return (x * u, y * u)

    w = (1.0 if not simple else 1.8)
    if not simple:
        # 착탄 표적 링
        cx, cy = 128, 214
        for r, a in ((46, 90), (30, 150), (14, 230)):
            d.ellipse([P(cx - r, cy - r * 0.32), P(cx + r, cy + r * 0.32)], outline=TARGET + (a,), width=max(1, int(5 * u)))
        # 점선 궤적(왼쪽 위 → 캐노피)
        pts = []
        for i in range(0, 40):
            t = i / 39
            x = 26 + t * 70
            y = 30 + 50 * t * t
            pts.append((x, y))
        for i in range(0, len(pts) - 1, 2):
            d.line([P(*pts[i]), P(*pts[i + 1])], fill=TRAJ + (220,), width=max(1, int(6 * u)))
    # 캐노피(반원 돔 + 주름 줄무늬)
    cx, cy, rx, ry = 128, 104, 78 if not simple else 92, 52 if not simple else 62
    top = cy - ry
    canopy = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    cd = ImageDraw.Draw(canopy)
    cd.pieslice([P(cx - rx, top), P(cx + rx, cy + ry)], 180, 360, fill=CANOPY_A + (255,))
    # 아래 가장자리 물결(조각 사이 패임)
    gores = 6
    for k in range(gores):
        x0 = cx - rx + 2 * rx * k / gores
        x1 = cx - rx + 2 * rx * (k + 1) / gores
        cd.ellipse([P(x0, cy - 9), P(x1, cy + 9)], fill=(0, 0, 0, 0))
    # 줄무늬(밝은 조각)
    for k in range(gores):
        if k % 2 == 1:
            continue
        a0 = 180 + 180 * k / gores
        a1 = 180 + 180 * (k + 1) / gores
        cd.pieslice([P(cx - rx, top), P(cx + rx, cy + ry)], a0, a1, fill=CANOPY_B + (255,))
    for k in range(gores):
        x0 = cx - rx + 2 * rx * k / gores
        x1 = cx - rx + 2 * rx * (k + 1) / gores
        cd.ellipse([P(x0, cy - 9), P(x1, cy + 9)], fill=(0, 0, 0, 0))
    img.alpha_composite(canopy)
    # 라이저 4줄 → 캡슐
    py = 176 if not simple else 184
    for xr in (-rx + 6, -rx * 0.35, rx * 0.35, rx - 6):
        d.line([P(cx + xr, cy + 2), P(cx, py - 14)], fill=RISER + (235,), width=max(1, int(4 * w * u)))
    # 캡슐(둥근 사각) + 하이라이트
    bw, bh = (34, 30) if not simple else (44, 38)
    d.rounded_rectangle([P(cx - bw / 2, py - 16), P(cx + bw / 2, py - 16 + bh)], radius=int(7 * u), fill=PAYLOAD + (255,))
    d.rounded_rectangle([P(cx - bw / 2 + 5, py - 11), P(cx - bw / 2 + 12, py - 16 + bh - 6)], radius=int(3 * u), fill=(220, 245, 255, 200))
    out = img.resize((px, px), Image.LANCZOS)
    if px <= 32:
        out = out.filter(ImageFilter.UnsharpMask(radius=1, percent=60, threshold=2))
    return out


def build() -> list[Image.Image]:
    return [render(sz, simple=sz <= 32) for sz in SIZES]


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    imgs = build()
    big = imgs[-1]
    big.save(ASSETS / "app.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
    big.save(ASSETS / "app.png")
    print(f"→ {(ASSETS / 'app.ico').relative_to(ROOT)} ({', '.join(map(str, SIZES))} px), app.png")


if __name__ == "__main__":
    main()
