"""GPU 가상 풍동(격자 볼츠만) — 솔버와 무관한 순수 함수(numpy 만, GPU·XLB 없이 시험 가능).

좌표계
- 동체(body): 앱의 동체 좌표(m, x = 기수). 메시는 앱이 이 좌표로 넘긴다(noseAxis·modelFrame 반영 끝).
- 풍동(tunnel): 앱 풍동 보기(Sim.Core WindTunnel)와 같다 — 진행 방향 d 가 +X, 흐름(자유류)은 −X, 물체 중심 = 원점.
  d(동체) = (cos α cos β, sin β, sin α cos β). 동체→풍동 R = Rz(−β)·Ry(α) (β = 0 이면 WindTunnel.ToTunnel 과 같다).
- 격자(lattice): 셀 지수(i, j, k). 흐름 +x — x_l = −X_t, y_l = Y_t, z_l = Z_t, 간격 dx[m].

공력 계수 축(OpenFOAM DB 와 같은 정의): CD = 흐름 방향(−X_t = +x_l) 힘, CL = +Z_t(= +z_l) 힘, CY = +Y_t 힘, 모두 /(q∞ S_ref).
"""

from __future__ import annotations

import math
import os
import sys
from dataclasses import dataclass, field

import numpy as np

_TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))     # tools/ — 공통 영역 규칙 flowdomain.py
if _TOOLS not in sys.path:
    sys.path.append(_TOOLS)
import flowdomain as fd  # noqa: E402

# 해상도 프리셋: 격자 셀 수(nx, ny, nz). 물체 외접구 지름 = ny·SPHERE_FRACTION 셀(받음각이 바뀌어도 dx 고정 → 다시 복셀화만).
RESOLUTIONS: dict[str, tuple[int, int, int]] = {
    "low": (256, 96, 96),
    "medium": (352, 128, 128),
    "high": (480, 176, 176),
}
SPHERE_FRACTION = 0.42
UPSTREAM_FRACTION = 0.28      # 물체 중심의 x 위치(입구에서 nx 비율)
U_LB = 0.06                   # 격자 단위 자유류 속력(격자 마하 ≈ 0.10)
NU_LB_MIN = 8e-4              # 안정 하한(KBC, τ ≈ 0.5024) — RTX 4090 시험(컨테이너, low): α 90° ν 3.3e-4 발산·6.1e-4 안정,
                              # α 45°→70° 자세 변경 직후 ν 5e-4 발산 → 여유를 두고 8e-4
CS = 1.0 / math.sqrt(3.0)


def rotation_body_to_tunnel(alpha_deg: float, beta_deg: float = 0.0) -> np.ndarray:
    """동체 → 풍동 회전 행렬(3×3). R·d = +X."""
    a, b = math.radians(alpha_deg), math.radians(beta_deg)
    ca, sa, cb, sb = math.cos(a), math.sin(a), math.cos(b), math.sin(b)
    ry = np.array([[ca, 0.0, sa], [0.0, 1.0, 0.0], [-sa, 0.0, ca]])
    rz = np.array([[cb, sb, 0.0], [-sb, cb, 0.0], [0.0, 0.0, 1.0]])
    return rz @ ry


def travel_direction(alpha_deg: float, beta_deg: float = 0.0) -> np.ndarray:
    a, b = math.radians(alpha_deg), math.radians(beta_deg)
    return np.array([math.cos(a) * math.cos(b), math.sin(b), math.sin(a) * math.cos(b)])


# 풍동 ↔ 격자 축 부호(x 만 뒤집힘)
_FLIP = np.array([-1.0, 1.0, 1.0])


@dataclass
class GridPlan:
    """격자 배치: 모양 · 셀 크기 · 물체 중심(동체) · 물체 중심의 격자 위치(셀 좌표, 셀 중심 = 지수 + 0.5)."""

    shape: tuple[int, int, int]
    dx: float
    body_center: np.ndarray
    origin_cells: np.ndarray
    sphere_diameter: float
    resolution: str = "custom"
    extra: dict = field(default_factory=dict)

    @property
    def cells(self) -> int:
        return int(self.shape[0] * self.shape[1] * self.shape[2])


MAX_CELLS = 16_000_000         # 사용자 영역 설정의 셀 상한 경고(high 프리셋 14.9 M 위 — GPU 메모리)


def preset_rule(shape: tuple[int, int, int]) -> fd.DomainRule:
    """기존 배치(외접구 지름 = 측면의 SPHERE_FRACTION, 물체 중심 x = UPSTREAM_FRACTION·nx)를 공통 규칙으로 쓴 것.

    단위 = 외접구 지름 D. 측면 (1/0.42 − 1)/2 = 0.69 D, 상류 0.28·nx·dx/D − 0.5(low 1.28 · medium 1.33 · high 1.32 D),
    하류 = 나머지(규칙은 상류와 같은 식의 0.72 쪽 — 모자라지 않으므로 남는 칸이 하류로 간다).
    """
    nx, lat = shape[0], min(shape[1], shape[2])
    cells_per_d = lat * SPHERE_FRACTION
    return fd.DomainRule(upstream=UPSTREAM_FRACTION * nx / cells_per_d - 0.5,
                         downstream=(1.0 - UPSTREAM_FRACTION) * nx / cells_per_d - 0.5,
                         lateral=(1.0 / SPHERE_FRACTION - 1.0) / 2.0, ref="max", min_cells=0)


# 앱 · JSON 설정의 영역 키(server DEFAULTS "domain") → 공통 규칙 필드
_DOMAIN_KEYS = {"upstream": "upstream", "downstream": "downstream", "lateral": "lateral", "minCells": "min_cells"}


def domain_settings(resolution: str = "low", shape: tuple[int, int, int] | None = None, domain: dict | None = None
                    ) -> tuple[fd.DomainRule, fd.Resolution]:
    """해상도 프리셋(+ 선택 영역 설정) → (규칙, 해상도). domain 키(모두 선택, 단위 = 외접구 지름 D):
    mode size|count · upstream · downstream · lateral · minCells · cell[m] · cellsPerLength(D 당 셀) · shape [nx,ny,nz] · cellBudget.
    빠진 키는 프리셋 값. mode size 는 cell 또는 cellsPerLength 가 있어야 한다."""
    shape = tuple(int(n) for n in (shape or RESOLUTIONS[resolution]))
    rule = preset_rule(shape)
    dom = dict(domain or {})
    for k, f in _DOMAIN_KEYS.items():
        if dom.get(k) is not None:
            setattr(rule, f, int(dom[k]) if f == "min_cells" else float(dom[k]))
    mode = dom.get("mode") or "count"
    if mode == "size":
        res = fd.Resolution(mode="size", cell=float(dom.get("cell") or 0.0), cells_per_length=float(dom.get("cellsPerLength") or 0.0),
                            max_cells=MAX_CELLS)
    elif dom.get("cellBudget"):
        res = fd.Resolution(mode="count", cell_budget=int(dom["cellBudget"]), max_cells=MAX_CELLS)
    else:
        res = fd.Resolution(mode="count", shape=tuple(int(n) for n in (dom.get("shape") or shape)), max_cells=MAX_CELLS)
    return rule, res


def plan_grid(verts_body: np.ndarray, resolution: str = "low", shape: tuple[int, int, int] | None = None,
              domain: dict | None = None, alpha_deg: float = 0.0, beta_deg: float = 0.0) -> GridPlan:
    """격자 배치(공통 규칙 tools/flowdomain.py). 물체 대신 외접구(중심 = 경계 상자 중심, 지름 D)를 쓴다 → 받음각과 무관한 dx
    (자세가 바뀌면 다시 복셀화만). 기본(domain 없음) = 해상도 프리셋의 개수 유지 — 기존 배치와 같은 값.
    alpha/beta 는 막힘 비율(그 자세의 정면 투영)에만 쓴다."""
    v = np.asarray(verts_body, dtype=float).reshape(-1, 3)
    if v.shape[0] < 3:
        raise ValueError("메시 정점이 3개 미만입니다")
    lo, hi = v.min(axis=0), v.max(axis=0)
    center = (lo + hi) / 2
    diam = 2.0 * float(np.max(np.linalg.norm(v - center, axis=1)))
    if not diam > 0:
        raise ValueError("메시 크기가 0 입니다")
    rule, res = domain_settings(resolution, shape, domain)
    half = diam / 2
    dp = fd.plan_domain((-half,) * 3, (half,) * 3, (1.0, 0.0, 0.0), rule, res,
                        frontal_area=frontal_area(v, center, alpha_deg, beta_deg))
    dp.reasons.insert(0, f"물체 = 외접구(지름 D {diam:.4g} m, 경계 상자 중심) — 받음각과 무관한 격자. 확장 배수 단위 = D")
    shape_out = tuple(int(n) for n in dp.counts)
    custom = bool(domain) or shape_out != RESOLUTIONS.get(resolution)
    return GridPlan(shape=shape_out, dx=dp.cell, body_center=center, origin_cells=np.array(dp.center_cells, dtype=float),
                    sphere_diameter=diam, resolution="custom" if custom else resolution, extra={"domain": dp.to_dict()})


def frontal_area(verts_body: np.ndarray, center: np.ndarray, alpha_deg: float, beta_deg: float = 0.0) -> float:
    """그 자세에서 흐름(풍동 −X)에 수직인 면(y·z)에 투영한 물체 경계 상자 넓이[m²] — 실제 실루엣의 상한."""
    t = (np.asarray(verts_body, dtype=float).reshape(-1, 3) - center) @ rotation_body_to_tunnel(alpha_deg, beta_deg).T
    ext = t.max(axis=0) - t.min(axis=0)
    return float(ext[1] * ext[2])


def blockage(plan: GridPlan, verts_body: np.ndarray, alpha_deg: float, beta_deg: float = 0.0) -> float:
    """막힘 비율 = 자세별 정면 투영(경계 상자) / 풍동 단면(ny·nz·dx²)."""
    return fd.blockage_ratio(frontal_area(verts_body, plan.body_center, alpha_deg, beta_deg), plan.shape[1] * plan.shape[2] * plan.dx ** 2)


def tunnel_to_lattice(p_tunnel: np.ndarray, plan: GridPlan) -> np.ndarray:
    """풍동 좌표[m] → 격자 연속 좌표[셀](셀 i 의 중심 = i + 0.5)."""
    return np.asarray(p_tunnel, dtype=float) * _FLIP / plan.dx + plan.origin_cells


def lattice_to_tunnel(p_cells: np.ndarray, plan: GridPlan) -> np.ndarray:
    return (np.asarray(p_cells, dtype=float) - plan.origin_cells) * plan.dx * _FLIP


def body_to_lattice(verts_body: np.ndarray, plan: GridPlan, alpha_deg: float, beta_deg: float = 0.0) -> np.ndarray:
    """동체 메시 정점 → 격자 연속 좌표(XLB MeshBoundaryMasker 입력 — 삼각형 수프 (3T, 3))."""
    r = rotation_body_to_tunnel(alpha_deg, beta_deg)
    t = (np.asarray(verts_body, dtype=float).reshape(-1, 3) - plan.body_center) @ r.T
    return tunnel_to_lattice(t, plan)


# ---------------------------------------------------------------- 무차원 수

def lb_parameters(re_target: float, ref_length_m: float, plan: GridPlan, u_lb: float = U_LB) -> dict:
    """설정 Re → 격자 점성·완화 계수. 격자가 못 푸는 Re 는 상한(re_max)으로 자른다."""
    l_cells = ref_length_m / plan.dx
    re_max = u_lb * l_cells / NU_LB_MIN
    re = float(min(max(re_target, 1.0), re_max))
    nu = u_lb * l_cells / re
    tau = 3.0 * nu + 0.5
    return {"re": re, "re_max": re_max, "re_clamped": re_target > re_max * (1 + 1e-9), "nu_lb": nu,
            "tau": tau, "omega": 1.0 / tau, "u_lb": u_lb, "ma_lb": u_lb / CS, "ref_cells": l_cells}


def physical_numbers(speed_ms: float, ref_length_m: float, altitude_m: float = 0.0) -> dict:
    """실제 조건(ISA): 마하 · Re(참고 — 격자 Re 와 다름을 화면에 함께 표시)."""
    t = 288.15 - 0.0065 * min(max(altitude_m, 0.0), 11000.0)
    p = 101325.0 * (t / 288.15) ** 5.2559
    rho = p / (287.05 * t)
    mu = 1.458e-6 * t ** 1.5 / (t + 110.4)
    a = math.sqrt(1.4 * 287.05 * t)
    return {"mach": speed_ms / a, "re": rho * speed_ms * ref_length_m / mu, "rho": rho, "a": a}


def step_seconds(plan: GridPlan, speed_ms: float, u_lb: float = U_LB) -> float:
    """격자 1 스텝 = 실제 몇 초인가(자유류가 같은 셀 수를 지나는 시간)."""
    return plan.dx * u_lb / max(speed_ms, 1e-9)


def coefficients(force_lattice: np.ndarray, plan: GridPlan, ref_area_m2: float, u_lb: float = U_LB) -> dict:
    """격자 운동량 교환 힘(ρ = 1 격자 단위) → CD · CY · CL (OpenFOAM DB 와 같은 축)."""
    f = np.asarray(force_lattice, dtype=float).reshape(3)
    area_cells = ref_area_m2 / plan.dx ** 2
    q = 0.5 * u_lb * u_lb * area_cells
    return {"CD": f[0] / q, "CY": f[1] / q, "CL": f[2] / q}


# ---------------------------------------------------------------- 장 후처리

U_DIVERGED = 0.35             # 격자 속력이 이보다 크면(격자 마하 ≈ 0.6) 발산으로 본다(자유류 0.06 의 약 6 배)


def check_diverged(max_abs_u: float):
    """최대 |속도 성분| 이 NaN 이거나 > U_DIVERGED 면 FloatingPointError(발산 — Re 를 낮춰 다시 시작해야 함)."""
    m = float(max_abs_u)
    if not math.isfinite(m) or m > U_DIVERGED:
        raise FloatingPointError(f"격자 볼츠만 계산이 발산했습니다(최대 격자 속력 {m:.3g}) — Re 를 낮추거나 해상도를 올리세요.")


def trilinear(u: np.ndarray, pts: np.ndarray) -> np.ndarray:
    """격자 셀 중심 장 u (C, nx, ny, nz) 를 연속 좌표 pts (N, 3)[셀] 에서 삼선형 보간 → (N, C). 바깥은 경계값."""
    c, nx, ny, nz = u.shape
    uf = u.reshape(c, -1)
    p = np.asarray(pts, dtype=float) - 0.5
    i0 = np.floor(np.nan_to_num(p, nan=-1.0)).astype(np.int64)
    f = p - i0
    out = np.zeros((p.shape[0], c), dtype=np.float64)
    ix = [np.clip(i0[:, 0] + d, 0, nx - 1) * (ny * nz) for d in (0, 1)]
    iy = [np.clip(i0[:, 1] + d, 0, ny - 1) * nz for d in (0, 1)]
    iz = [np.clip(i0[:, 2] + d, 0, nz - 1) for d in (0, 1)]
    wx = (1 - f[:, 0], f[:, 0])
    wy = (1 - f[:, 1], f[:, 1])
    wz = (1 - f[:, 2], f[:, 2])
    for a in (0, 1):
        for b in (0, 1):
            ixy, wxy = ix[a] + iy[b], wx[a] * wy[b]
            for d in (0, 1):
                out += (wxy * wz[d])[:, None] * np.take(uf, ixy + iz[d], axis=1).T
    return out


def inside_domain(pts: np.ndarray, shape: tuple[int, int, int], margin: float = 1.0) -> np.ndarray:
    p = np.asarray(pts)
    s = np.asarray(shape, dtype=float)
    return np.all((p >= margin) & (p <= s - margin), axis=1)


def solid_at(solid: np.ndarray | None, pts: np.ndarray) -> np.ndarray:
    if solid is None:
        return np.zeros(len(pts), dtype=bool)
    idx = np.floor(np.asarray(pts)).astype(np.int64)
    for a in range(3):
        idx[:, a] = np.clip(idx[:, a], 0, solid.shape[a] - 1)
    return solid[idx[:, 0], idx[:, 1], idx[:, 2]]


def rake(plan: GridPlan, n_y: int, n_z: int, x_frac: float = 0.06, span: float = 0.62, plane: str = "grid") -> np.ndarray:
    """입구 근처 x = x_frac·nx 평면의 씨앗점(격자 좌표). plane: grid(y·z 격자) | xz(y = 중앙 한 줄) | xy(z = 중앙 한 줄)."""
    nx, ny, nz = plan.shape
    x = nx * x_frac
    ys = plan.origin_cells[1] + np.linspace(-0.5, 0.5, n_y) * ny * span if n_y > 1 else np.array([plan.origin_cells[1]])
    zs = plan.origin_cells[2] + np.linspace(-0.5, 0.5, n_z) * nz * span if n_z > 1 else np.array([plan.origin_cells[2]])
    if plane == "xz":
        ys = np.array([plan.origin_cells[1]])
    elif plane == "xy":
        zs = np.array([plan.origin_cells[2]])
    yy, zz = np.meshgrid(ys, zs, indexing="ij")
    return np.column_stack([np.full(yy.size, x), yy.ravel(), zz.ravel()])


class ParticleSystem:
    """연기 입자(유적선 streakline): 입구 갈퀴(rake)에서 매 프레임 방출, 순간 속도장으로 이류(RK2), 나가거나 물체에 박히면 제거.

    입자 위치는 격자 좌표[셀], 속도장 u 는 격자 단위(셀/스텝). 경과 스텝 n 만큼 한 번에 이동(부분 단계로 나눔). 나이 단위 = 스텝.
    """

    def __init__(self, seeds: np.ndarray, max_particles: int = 24000, max_age: int = 20000, jitter: float = 0.35, seed: int = 1):
        self.seeds = np.asarray(seeds, dtype=float)
        self.max_particles = max_particles
        self.max_age = max_age
        self.jitter = jitter
        self.rng = np.random.default_rng(seed)
        self.pos = np.zeros((0, 3))
        self.age = np.zeros(0, dtype=np.int32)
        self.seed_id = np.zeros(0, dtype=np.int32)

    def emit(self):
        n = len(self.seeds)
        room = self.max_particles - len(self.pos)
        if n == 0 or room <= 0:
            return
        k = min(n, room)
        ids = np.arange(n)[:k]
        p = self.seeds[ids] + self.rng.uniform(-self.jitter, self.jitter, size=(k, 3)) * np.array([1.0, 0.3, 0.3])
        self.pos = np.vstack([self.pos, p])
        self.age = np.concatenate([self.age, np.zeros(k, dtype=np.int32)])
        self.seed_id = np.concatenate([self.seed_id, ids.astype(np.int32)])

    def advect(self, field, steps: float):
        """field: CpuField(또는 같은 메서드의 GPU 장). steps 스텝만큼 이동 → 도메인 밖·나이 초과·고체 안 입자 제거."""
        if len(self.pos) == 0:
            return
        p = field.advect(self.pos, steps)
        self.pos = p
        self.age += int(round(steps))
        keep = inside_domain(p, field.shape) & (self.age < self.max_age) & ~field.solid_at(p)
        self.pos, self.age, self.seed_id = self.pos[keep], self.age[keep], self.seed_id[keep]

    def step(self, field, steps: float):
        self.advect(field, steps)
        self.emit()


def substeps(steps: float, umax: float, max_sub_cells: float = 1.0) -> int:
    """입자 RK2 부분 단계 수: 한 단계 이동 ≤ max_sub_cells 셀, 최대 64."""
    return min(64, max(1, int(math.ceil(steps * max(umax, 1e-6) / max_sub_cells))))


class CpuField:
    """numpy 속도장 연산(기준 구현 — selftest·시험용. GPU 구현은 xlb_backend.GpuField 가 같은 메서드를 가진다).

    u: (3, nx, ny, nz) 격자 단위 속도(셀/스텝), solid: (nx, ny, nz) bool.
    """

    def __init__(self, u: np.ndarray, solid: np.ndarray | None = None):
        self.u = u
        self.solid = solid
        self.shape = tuple(u.shape[1:])
        self._umax = None

    def max_speed(self) -> float:
        if self._umax is None:
            self._umax = float(np.max(np.abs(self.u))) if self.u.size else 0.0
        return self._umax

    def sample(self, pts: np.ndarray) -> np.ndarray:
        return trilinear(self.u, pts)

    def solid_at(self, pts: np.ndarray) -> np.ndarray:
        return solid_at(self.solid, pts)

    def advect(self, pos: np.ndarray, steps: float) -> np.ndarray:
        nsub = substeps(steps, self.max_speed())
        h = steps / nsub
        p = np.asarray(pos, dtype=float)
        for _ in range(nsub):
            v1 = trilinear(self.u, p)
            v2 = trilinear(self.u, p + 0.5 * h * v1)
            p = p + h * v2
        return p

    def streamlines(self, seeds: np.ndarray, h: float = 0.6, max_points: int = 700) -> list[np.ndarray]:
        return streamlines(self.u, seeds, self.solid, h, max_points)

    def slice(self, plane: str, index: int) -> dict:
        return slice_plane(self.u, self.solid, plane, index)


def streamlines(u: np.ndarray, seeds: np.ndarray, solid: np.ndarray | None = None, h: float = 0.6, max_points: int = 700,
                min_speed: float = 1e-5) -> list[np.ndarray]:
    """순간 유선(RK2, 고정 호 길이 h 셀): 씨앗마다 (M, 4) [x, y, z, |u|] 배열(격자 좌표, 격자 속력). 벡터화."""
    p = np.asarray(seeds, dtype=float).copy()
    n = len(p)
    buf = np.zeros((max_points, n, 4))
    length = np.zeros(n, dtype=np.int64)
    alive = np.ones(n, dtype=bool)
    for step in range(max_points):
        idx = np.nonzero(alive)[0]
        if idx.size == 0:
            break
        q = p[idx]
        v = trilinear(u, q)
        s = np.linalg.norm(v, axis=1)
        buf[step, idx, :3] = q
        buf[step, idx, 3] = s
        length[idx] += 1
        ok = s > min_speed
        mid = q + 0.5 * h * v / np.maximum(s, 1e-12)[:, None]
        v2 = trilinear(u, mid)
        s2 = np.linalg.norm(v2, axis=1)
        ok &= s2 > min_speed
        q2 = q + h * v2 / np.maximum(s2, 1e-12)[:, None]
        ok &= inside_domain(q2, u.shape[1:]) & ~solid_at(solid, q2)
        p[idx] = q2
        alive[idx[~ok]] = False
    return [buf[:length[i], i, :].copy() for i in range(n) if length[i] >= 2]


def slice_plane(u: np.ndarray, solid: np.ndarray | None, plane: str, index: int) -> dict:
    """단면(격자 단위): plane 'xz' = y = index 평면(피치면), 'xy' = z = index 평면(요잉면).

    반환: speed (n_a, n_b) — 격자 속력, vort (n_a, n_b) — 면에 수직인 와도 성분(셀/스텝/셀), mask(고체) True.
    축 a = x(흐름), b = z 또는 y.
    """
    if plane == "xz":
        index = int(np.clip(index, 0, u.shape[2] - 1))
        slab = u[:, :, index, :]
        m = solid[:, index, :] if solid is not None else None
    elif plane == "xy":
        index = int(np.clip(index, 0, u.shape[3] - 1))
        slab = u[:, :, :, index]
        m = solid[:, :, index] if solid is not None else None
    else:
        raise ValueError(f"단면은 xz | xy: {plane}")
    return slice_from_slab(slab, m, plane)


def slice_from_slab(slab: np.ndarray, mask: np.ndarray | None, plane: str) -> dict:
    """단면 속도 3 성분 slab (3, n_a, n_b) → speed · 면 수직 와도 · mask. xz: b = z, xy: b = y."""
    ux, uy, uz = slab[0], slab[1], slab[2]
    if plane == "xz":
        ua, ub = ux, uz
        vort = np.gradient(ua, axis=1) - np.gradient(ub, axis=0)       # ω_y = ∂u_x/∂z − ∂u_z/∂x
    else:
        ua, ub = ux, uy
        vort = np.gradient(ub, axis=0) - np.gradient(ua, axis=1)       # ω_z = ∂u_y/∂x − ∂u_x/∂y
    speed = np.sqrt(ux * ux + uy * uy + uz * uz)
    m = np.zeros(ua.shape, dtype=bool) if mask is None else np.asarray(mask, dtype=bool)
    return {"speed": speed, "vort": vort, "mask": m}


def slice_geometry(plan: GridPlan, plane: str, index: int) -> dict:
    """단면 사각형(풍동 좌표 m): 원점(셀 [0,0] 의 모서리) · a 축 끝 · b 축 끝 벡터 · 셀 수."""
    nx, ny, nz = plan.shape
    if plane == "xz":
        c0 = np.array([0.0, index + 0.5, 0.0])
        ca = np.array([nx, index + 0.5, 0.0])
        cb = np.array([0.0, index + 0.5, nz])
        n_a, n_b = nx, nz
    else:
        c0 = np.array([0.0, 0.0, index + 0.5])
        ca = np.array([nx, 0.0, index + 0.5])
        cb = np.array([0.0, ny, index + 0.5])
        n_a, n_b = nx, ny
    o = lattice_to_tunnel(c0, plan)
    return {"origin": o.tolist(), "axisA": (lattice_to_tunnel(ca, plan) - o).tolist(),
            "axisB": (lattice_to_tunnel(cb, plan) - o).tolist(), "nA": n_a, "nB": n_b}


def surface_points(tris_cells: np.ndarray, spacing: float = 0.45) -> np.ndarray:
    """삼각형 수프(격자 좌표)의 표면 표본점 — 모서리 간격 ≤ spacing 셀(얇은 판·핀도 놓치지 않게)."""
    t = np.asarray(tris_cells, dtype=float).reshape(-1, 3, 3)
    if len(t) == 0:
        return np.zeros((0, 3))
    edge = np.max(np.linalg.norm(t - t[:, [1, 2, 0]], axis=2), axis=1)
    n = np.maximum(1, np.ceil(edge / spacing)).astype(int)
    out = [t.reshape(-1, 3)]
    for k in np.unique(n):
        if k <= 1:
            continue
        sel = t[n == k]
        ii, jj = np.meshgrid(np.arange(k + 1), np.arange(k + 1), indexing="ij")
        m = ii + jj <= k
        a, b = ii[m] / k, jj[m] / k
        c = 1 - a - b
        # (T, P, 3) = a·v0 + b·v1 + c·v2
        pts = (sel[:, None, 0, :] * a[None, :, None] + sel[:, None, 1, :] * b[None, :, None]
               + sel[:, None, 2, :] * c[None, :, None])
        out.append(pts.reshape(-1, 3))
    return np.concatenate(out)


def fill_enclosed(surface: np.ndarray) -> np.ndarray:
    """표면 셀 + 밖과 이어지지 않은 빈 공간(닫힌 속 — 속이 빈 외피 STL 의 내부) = 고체. 6-연결 바깥 유체 기준."""
    s = np.asarray(surface, dtype=bool)
    try:
        from scipy import ndimage

        return ndimage.binary_fill_holes(s)
    except ImportError:
        pass
    ext = np.zeros_like(s)
    for sl in [(0, slice(None), slice(None)), (-1, slice(None), slice(None)), (slice(None), 0, slice(None)),
               (slice(None), -1, slice(None)), (slice(None), slice(None), 0), (slice(None), slice(None), -1)]:
        ext[sl] = ~s[sl]
    while True:
        grow = ext.copy()
        grow[1:] |= ext[:-1]
        grow[:-1] |= ext[1:]
        grow[:, 1:] |= ext[:, :-1]
        grow[:, :-1] |= ext[:, 1:]
        grow[:, :, 1:] |= ext[:, :, :-1]
        grow[:, :, :-1] |= ext[:, :, 1:]
        grow &= ~s
        if np.array_equal(grow, ext):
            break
        ext = grow
    return ~ext


def voxelize(tris_cells: np.ndarray, shape: tuple[int, int, int]) -> np.ndarray:
    """격자 좌표 삼각형 수프 → 고체 셀(표면이 지나는 셀 + 닫힌 속). 감김 방향·열린 STL·속 빈 외피에 무관."""
    shape = tuple(int(x) for x in shape)
    surf = np.zeros(shape, dtype=bool)
    p = surface_points(tris_cells)
    idx = np.floor(p).astype(np.int64)
    ok = np.all((idx >= 0) & (idx < np.asarray(shape)), axis=1)
    idx = idx[ok]
    surf[idx[:, 0], idx[:, 1], idx[:, 2]] = True
    return fill_enclosed(surf)


def running_mean(values: list[float], window: int) -> float:
    if not values:
        return float("nan")
    w = values[-window:]
    return float(sum(w) / len(w))
