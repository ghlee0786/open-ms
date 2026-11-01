"""유동 해석 영역 · 격자 크기 자동 결정 — GPU 가상 풍동(tools/airflow)과 OpenFOAM(tools/cfd)이 함께 쓰는 규칙(표준 라이브러리만).

Fusion 유동(전자 냉각) 해석의 복셀 영역 방식을 따른다: 물체 경계 상자를 넓혀(비율 또는 최소 셀 수) 영역을 잡고,
'크기 유지'(셀 크기 고정 → 셀 수가 따라감) 또는 '개수 유지'(셀 수 고정 → 셀 크기가 따라감)를 고른다.

입력: 물체 경계 상자(lo, hi — 영역과 같은 축), 유동 방향(자유류 속도 방향), 규칙(DomainRule), 해상도(Resolution).
규칙:
- 확장 = 기준 길이 L × 배수(상류 · 하류 · 측면). L = 경계 상자 최대 변(max) | 최소 변(min) | 그 축의 물체 길이(axis — Fusion
  'Bounding Box Extension Percentage' 와 같다: 배수 0.5 = 50 %). 유동이 축에 비스듬하면 축마다 상류·하류 배수를 방향 성분으로 섞는다.
- 확장마다 최소 셀 수(min_cells — Fusion 'Minimal Voxel Count'): 확장 ≥ min_cells × 셀 크기.
- 크기 유지(size): 셀 = cell[m] 또는 (최소 특성 길이 / cells_per_length). 축별 셀 수 = max(min_axis_cells, round(길이 / 셀)).
- 개수 유지(count): shape(축별 셀 수 고정) → 모든 확장을 만족하는 가장 작은 셀(남는 칸은 큰 쪽 확장에, 같으면 반씩),
  또는 cell_budget(총 셀 수) → 셀 = (영역 부피 / 예산)^(1/3).
출력(DomainPlan): 영역 상자 · 셀 크기 · 축별 셀 수 · 물체 중심의 셀 위치 · 실제 확장 · 막힘 비율 · 경고 · 근거(사람이 읽는 문장).
막힘 비율 = 물체 정면 투영 넓이 / 영역 단면(유동에 수직 투영) — 정면 넓이를 안 주면 경계 상자 투영(상한). 기준(기본 5 %)을 넘으면 경고.
"""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass, field

BLOCKAGE_WARN = 0.05          # 막힘 비율 경고 기준(닫힌 풍동 · 외부 유동 CFD 에서 흔히 쓰는 5 %)


@dataclass
class DomainRule:
    """확장 규칙: 배수(기준 길이 단위) · 기준 길이 종류 · 확장마다 최소 셀 수."""

    upstream: float = 5.0
    downstream: float = 12.0
    lateral: float = 5.0
    ref: str = "max"              # max | min | axis
    min_cells: int = 0


@dataclass
class Resolution:
    """격자 크기: mode = size(크기 유지) | count(개수 유지)."""

    mode: str = "size"
    cell: float = 0.0             # size: 셀 크기[m] (0 이면 cells_per_length 로)
    cells_per_length: float = 0.0  # size: 최소 특성 길이(경계 상자 최소 양의 변) 당 셀 수
    shape: tuple | None = None    # count: 축별 셀 수 고정
    cell_budget: int = 0          # count: 총 셀 수(shape 가 없을 때)
    min_axis_cells: int = 4
    max_cells: int = 0            # 0 = 검사 안 함. 넘으면 경고(메모리)


@dataclass
class DomainPlan:
    lo: tuple
    hi: tuple
    cell: float
    counts: tuple
    center_cells: tuple           # 물체 경계 상자 중심의 위치(영역 lo 에서 셀 단위, 등방 셀 = cell 기준)
    ext_lo: tuple                 # 실제 확장(m) — 축의 lo 쪽
    ext_hi: tuple
    mode: str
    blockage: float
    warnings: list = field(default_factory=list)
    reasons: list = field(default_factory=list)

    @property
    def cells(self) -> int:
        return int(math.prod(self.counts))

    def to_dict(self) -> dict:
        d = asdict(self)
        d["cells"] = self.cells
        return d


def _unit(v) -> list:
    m = math.sqrt(sum(x * x for x in v))
    if not m > 0:
        raise ValueError("유동 방향이 0 벡터입니다")
    return [x / m for x in v]


def projected_area(size, d) -> float:
    """축 정렬 상자(변 size)를 방향 d(단위) 에 수직인 면에 투영한 넓이 = Σ |d_i| × (i 에 수직인 면 넓이)."""
    return sum(abs(d[i]) * size[(i + 1) % 3] * size[(i + 2) % 3] for i in range(3))


def blockage_ratio(frontal_area: float, cross_area: float) -> float:
    return frontal_area / cross_area if cross_area > 0 else math.inf


def blockage_warning(ratio: float, warn: float = BLOCKAGE_WARN) -> str | None:
    if ratio > warn:
        return (f"막힘 비율 {100 * ratio:.1f} % > 기준 {100 * warn:.0f} % — 벽이 가까워 항력이 커진다(측면 확장을 늘리거나 "
                f"막힘 보정 필요)")
    return None


def plan_domain(lo, hi, flow_dir, rule: DomainRule | None = None, res: Resolution | None = None,
                frontal_area: float | None = None, blockage_warn: float = BLOCKAGE_WARN) -> DomainPlan:
    rule = rule or DomainRule()
    res = res or Resolution()
    lo, hi = [float(x) for x in lo], [float(x) for x in hi]
    size = [hi[i] - lo[i] for i in range(3)]
    if min(size) < 0 or not max(size) > 0:
        raise ValueError(f"물체 경계 상자가 잘못됐습니다: {lo} … {hi}")
    pos = [s for s in size if s > 0]
    l_max, l_min = max(size), min(pos)
    if rule.ref == "max":
        lref = [l_max] * 3
    elif rule.ref == "min":
        lref = [l_min] * 3
    elif rule.ref == "axis":
        lref = list(size)
    else:
        raise ValueError(f"기준 길이는 max | min | axis: {rule.ref}")
    d = _unit(flow_dir)
    # 배수 → m (축마다): 유동이 +i 로 가면 lo 쪽 = 상류. 비스듬하면 방향 성분으로 섞는다(기존 foamcase.domain_for 와 같은 식·순서)
    base_lo, base_hi = [], []
    for i in range(3):
        up, down, side = rule.upstream * lref[i], rule.downstream * lref[i], rule.lateral * lref[i]
        base_lo.append(side + (up - side) * max(0.0, d[i]) + (down - side) * max(0.0, -d[i]))
        base_hi.append(side + (down - side) * max(0.0, d[i]) + (up - side) * max(0.0, -d[i]))
    m = max(0, int(rule.min_cells))
    reasons = [f"물체 {size[0]:.4g} × {size[1]:.4g} × {size[2]:.4g} m, 유동 방향 ({d[0]:.3f}, {d[1]:.3f}, {d[2]:.3f}), "
               f"확장 기준 길이 {rule.ref} (상류 {rule.upstream:g} · 하류 {rule.downstream:g} · 측면 {rule.lateral:g} 배, "
               f"최소 {m} 셀)"]
    warnings = []

    if res.mode == "size":
        if res.cell > 0:
            cell = float(res.cell)
            reasons.append(f"크기 유지: 셀 {cell:.4g} m (지정)")
        elif res.cells_per_length > 0:
            cell = l_min / res.cells_per_length
            reasons.append(f"크기 유지: 셀 = 최소 특성 길이 {l_min:.4g} m / {res.cells_per_length:g} = {cell:.4g} m")
        else:
            raise ValueError("크기 유지(size)는 cell 또는 cells_per_length 가 필요합니다")
        e_lo = [max(base_lo[i], m * cell) for i in range(3)]
        e_hi = [max(base_hi[i], m * cell) for i in range(3)]
        dom_lo = [lo[i] - e_lo[i] for i in range(3)]
        dom_hi = [hi[i] + e_hi[i] for i in range(3)]
        counts = [max(res.min_axis_cells, int(round((dom_hi[i] - dom_lo[i]) / cell))) for i in range(3)]
    elif res.mode == "count" and res.shape:
        counts = [int(n) for n in res.shape]
        # n·dx ≥ size + max(a, m·dx) + max(b, m·dx) 를 만족하는 가장 작은 dx (각 경우의 하한 중 최대)
        cands = []
        for i in range(3):
            n, a, b = counts[i], base_lo[i], base_hi[i]
            cands.append((size[i] + a + b) / n)
            if n > m:
                cands += [(size[i] + a) / (n - m), (size[i] + b) / (n - m)]
            if n > 2 * m:
                cands.append(size[i] / (n - 2 * m))
            elif m > 0:
                raise ValueError(f"축 {i} 셀 수 {n} 이 최소 확장 셀 2 × {m} 이하입니다")
        cell = max(cands)
        e_lo, e_hi = [], []
        for i in range(3):
            a, b = max(base_lo[i], m * cell), max(base_hi[i], m * cell)
            slack = counts[i] * cell - size[i] - a - b
            if a == b:
                a, b = a + slack / 2, b + slack / 2
            elif b > a:
                b += slack
            else:
                a += slack
            e_lo.append(a)
            e_hi.append(b)
        dom_lo = [lo[i] - e_lo[i] for i in range(3)]
        dom_hi = [hi[i] + e_hi[i] for i in range(3)]
        reasons.append(f"개수 유지: 격자 {counts[0]} × {counts[1]} × {counts[2]} 고정 → 모든 확장을 만족하는 가장 작은 셀 "
                       f"{cell:.4g} m (남는 칸은 큰 쪽 확장에)")
    elif res.mode == "count" and res.cell_budget > 0:
        n_total = float(res.cell_budget)
        cell = math.prod(size[i] + base_lo[i] + base_hi[i] for i in range(3)) ** (1 / 3) / n_total ** (1 / 3)
        for _ in range(50):         # 최소 셀 수 확장이 셀 크기에 따라 바뀜 → 고정점
            ext = [size[i] + max(base_lo[i], m * cell) + max(base_hi[i], m * cell) for i in range(3)]
            new = (math.prod(ext) / n_total) ** (1 / 3)
            if abs(new - cell) <= 1e-12 * cell:
                break
            cell = new
        e_lo = [max(base_lo[i], m * cell) for i in range(3)]
        e_hi = [max(base_hi[i], m * cell) for i in range(3)]
        dom_lo = [lo[i] - e_lo[i] for i in range(3)]
        dom_hi = [hi[i] + e_hi[i] for i in range(3)]
        counts = [max(res.min_axis_cells, int(round((dom_hi[i] - dom_lo[i]) / cell))) for i in range(3)]
        reasons.append(f"개수 유지: 총 셀 예산 {res.cell_budget:,} → 셀 = (영역 부피 / 예산)^(1/3) = {cell:.4g} m")
    else:
        raise ValueError(f"해상도 모드는 size | count(shape 또는 cell_budget): {res.mode}")

    ext = [dom_hi[i] - dom_lo[i] for i in range(3)]
    center = tuple(counts[i] / 2 + (e_lo[i] - e_hi[i]) / (2 * cell) for i in range(3))
    reasons.append(f"영역 {ext[0]:.4g} × {ext[1]:.4g} × {ext[2]:.4g} m, 격자 {counts[0]} × {counts[1]} × {counts[2]} = "
                   f"{math.prod(counts):,} 셀, 최소 특성 길이 당 {l_min / cell:.3g} 셀")
    reasons.append("확장(lo/hi, 기준 길이 배수): " + ", ".join(
        f"{'xyz'[i]} {e_lo[i] / lref[i] if lref[i] > 0 else 0:.3g}/{e_hi[i] / lref[i] if lref[i] > 0 else 0:.3g}" for i in range(3)))
    if res.max_cells and math.prod(counts) > res.max_cells:
        warnings.append(f"셀 {math.prod(counts) / 1e6:.2f} M 이 상한 {res.max_cells / 1e6:.2f} M 을 넘는다 — 메모리 부족 가능")
    front = frontal_area if frontal_area is not None else projected_area(size, d)
    ratio = blockage_ratio(front, projected_area(ext, d))
    reasons.append(f"막힘 비율 {100 * ratio:.2f} % (정면 {front:.4g} m²{'' if frontal_area is not None else ' — 경계 상자 투영 상한'}, "
                   f"경고 기준 {100 * blockage_warn:.0f} %)")
    w = blockage_warning(ratio, blockage_warn)
    if w:
        warnings.append(w)
    return DomainPlan(lo=tuple(dom_lo), hi=tuple(dom_hi), cell=cell, counts=tuple(counts), center_cells=center,
                      ext_lo=tuple(e_lo), ext_hi=tuple(e_hi), mode=res.mode, blockage=ratio, warnings=warnings, reasons=reasons)
