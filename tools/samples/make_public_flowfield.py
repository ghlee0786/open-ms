"""공개 데모 컨테이너(Ø0.5 × 1.2 m 원통, 데모 1)의 **합성 예시** 표면 유동장(mns.surface-flow/1) — 풍동 보기 시연용.

CFD 결과가 아니다. 해석적 근사로 만든 그림용 값이다:
  - 측면 Cp = cos²α · Cp_축(x) + sin²α · Cp_횡류(θ)
    Cp_횡류 = 아임계 원기둥 꼴(정체점 1 → 75° 에서 최저 −1.2 → 뒤쪽 기저압 −1.0), Cp_축 = 앞 모서리 박리 흡입(지수 감쇠)
  - 끝면 Cp = 앞: cos²α · (1 − 0.45 (r/R)²) + 횡류 기저 · 뒤: 기저압 −0.25 cos²α + 횡류 기저
  - Cf = 평판 난류 크기(0.002–0.004) 근사
  - 유선 = 균일 흐름 + 횡류면 2-D 원기둥 퍼텐셜 흐름(끝 밖에서는 가우스로 줄임)을 RK4 로 적분
받음각 0–90°(15° 간격), 앞뒤 대칭. 시뮬레이터는 이 장을 색 · 유선 표시에만 쓰고 힘 계산에는 쓰지 않는다.

    python tools/samples/make_public_flowfield.py
    → samples/flow/demo_container_flow.json
"""
from __future__ import annotations

import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "samples" / "flow" / "demo_container_flow.json"
R, X0, X1 = 0.25, -0.6, 0.6
NX, NT, NR = 41, 37, 9
ALPHAS = [0.0, 15.0, 30.0, 45.0, 60.0, 75.0, 90.0]
MACH, SPEED = 0.15, 51.0
GROUP = "M0.15"


def cp_cross(th_deg: float) -> float:
    """횡류 Cp(θ) — θ = 바람 맞는 쪽에서 잰 각."""
    if th_deg <= 75.0:
        return 1.0 - 2.2 * math.sin(math.radians(th_deg * 90.0 / 75.0)) ** 2
    return -1.2 + 0.2 * (th_deg - 75.0) / 105.0


def cp_axial(x: float) -> float:
    """축 흐름 측면 Cp — 앞(+x) 모서리 박리 흡입이 뒤로 갈수록 줄어든다."""
    return -0.05 - 0.5 * math.exp(-(X1 - x) / (0.6 * R))


def grids(alpha: float) -> dict:
    ca2, sa2 = math.cos(math.radians(alpha)) ** 2, math.sin(math.radians(alpha)) ** 2
    ths = [180.0 * j / (NT - 1) for j in range(NT)]
    xs = [X0 + (X1 - X0) * i / (NX - 1) for i in range(NX)]
    rs = [i / (NR - 1) for i in range(NR)]
    side_cp = [ca2 * cp_axial(x) + sa2 * cp_cross(t) for x in xs for t in ths]
    side_cf = [0.0035 - 0.0015 * sa2 * abs(math.cos(math.radians(t))) for _ in xs for t in ths]
    base = [sa2 * (-0.6 + 0.3 * math.cos(math.radians(t)) * r) for r in rs for t in ths]
    fwd_cp = [ca2 * (1.0 - 0.45 * r * r) + b for r, b in zip([r for r in rs for _ in ths], base)]
    aft_cp = [-0.25 * ca2 + b for b in base]
    fwd_cf = [0.002 * r for r in rs for _ in ths]
    aft_cf = [0.0005 for _ in rs for _ in ths]
    rd = lambda v, n=2: [round(a, n) for a in v]  # noqa: E731 — 그림용 값이라 소수 둘째 자리(cf 는 다섯째)면 충분
    return {"cp": {"side": rd(side_cp), "capFwd": rd(fwd_cp), "capAft": rd(aft_cp)},
            "cf": {"side": rd(side_cf, 5), "capFwd": rd(fwd_cf, 5), "capAft": rd(aft_cf, 5)}}


def velocity(p, alpha: float):
    """CFD 좌표(동체 x 축, z = 바람 맞는 쪽) 속도 / V∞. 흐름 = −d, d = (cos α, 0, sin α)."""
    a = math.radians(alpha)
    ux, uc = -math.cos(a), math.sin(a)
    x, y, z = p
    r2 = y * y + z * z
    vy, vz = 0.0, -uc
    if r2 > 1e-12 and uc > 0:
        r = math.sqrt(r2)
        cphi, sphi = z / r, y / r
        k = R * R / r2
        vr, vphi = -uc * cphi * (1 - k), uc * sphi * (1 + k)
        py, pz = vr * sphi + vphi * cphi, vr * cphi - vphi * sphi   # 전체 횡류 속도
        ex = max(0.0, abs(x) - X1)
        g = math.exp(-(ex / (0.7 * R)) ** 2)
        vy, vz = g * py, -uc + g * (pz + uc)
    return ux, vy, vz


def inside(p) -> bool:
    return X0 <= p[0] <= X1 and p[1] ** 2 + p[2] ** 2 < (0.98 * R) ** 2


def streamline(seed, alpha: float, h: float = 0.03, steps: int = 140, keep: int = 36):
    pts, sp = [seed], []
    p = seed
    for _ in range(steps):
        k1 = velocity(p, alpha)
        k2 = velocity([p[i] + 0.5 * h * k1[i] for i in range(3)], alpha)
        k3 = velocity([p[i] + 0.5 * h * k2[i] for i in range(3)], alpha)
        k4 = velocity([p[i] + h * k3[i] for i in range(3)], alpha)
        q = [p[i] + h / 6 * (k1[i] + 2 * k2[i] + 2 * k3[i] + k4[i]) for i in range(3)]
        if inside(q):
            break
        pts.append(q)
        p = q
    sp = [math.sqrt(sum(c * c for c in velocity(q, alpha))) for q in pts]
    idx = sorted({round(i * (len(pts) - 1) / (keep - 1)) for i in range(keep)}) if len(pts) > keep else list(range(len(pts)))
    return {"p": [round(c, 2) for i in idx for c in pts[i]], "s": [round(sp[i], 2) for i in idx]}


def seeds(alpha: float):
    """흐름 위쪽(중심에서 d 방향 2 m) 평면의 격자 — 가로(y) 5 × 세로(d 에 수직) 6."""
    a = math.radians(alpha)
    d, n = (math.cos(a), 0.0, math.sin(a)), (-math.sin(a), 0.0, math.cos(a))
    out = []
    for yy in (-0.45, -0.2, 0.0, 0.2, 0.45):
        for nn in (-0.75, -0.45, -0.15, 0.15, 0.45, 0.75):
            out.append([2.0 * d[i] + nn * n[i] + (yy if i == 1 else 0.0) for i in range(3)])
    return out


def build() -> dict:
    cases = []
    for a in ALPHAS:
        cases.append({"group": GROUP, "label": f"M{MACH:.2f} α {a:g}° 합성 예시", "alpha": a, "mach": MACH, "speed": SPEED,
                      "method": "synthetic", "valid": True, "fields": grids(a),
                      "streamlines": [streamline(s, a) for s in seeds(a)]})
    return {
        "schema": "mns.surface-flow/1",
        "name": "demo_container_flow",
        "body": "container",
        "description": "공개 데모 1 컨테이너 Ø0.5 × 1.2 m 평평한 끝 원통 — 합성 예시(해석적 근사, CFD 결과 아님)",
        "frame": "동체 FRD(m, x = 기수). 진행 방향 d = (cos α, 0, sin α), 유동 −d. θ = +z(바람 맞는 쪽)에서 잰 각, y 대칭",
        "surface": {"kind": "cylinder", "radius": R, "x0": X0, "x1": X1, "nx": NX, "ntheta": NT, "nr": NR},
        "foreAftSymmetric": True,
        "fields": {"cp": "압력 계수 (p − p∞)/q∞", "cf": "벽 마찰 계수 |τ_w|/q∞"},
        "provenance": {"solver": "합성 예시 — 해석적 근사(아임계 원기둥 Cp · 2-D 퍼텐셜 유선)", "generator": "tools/samples/make_public_flowfield.py"},
        "warning": "합성 예시 — CFD 결과가 아닌 해석적 근사로 만든 시연용 값입니다. 설계 판단에 쓰지 마세요.",
        "cases": cases,
    }


def main():
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(build(), ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"wrote {OUT}  ({OUT.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
