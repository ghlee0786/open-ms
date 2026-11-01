"""XLB(Autodesk, Apache-2.0) 격자 볼츠만 솔버 연결 — NVIDIA Warp(CUDA) 백엔드, D3Q27 + KBC 충돌(높은 Re 안정).

솔버 자체는 XLB 가 푼다(스트리밍·충돌·경계조건·운동량 교환 힘). 이 모듈은 설정·복셀화·장 꺼내기만 한다.
- 입구: RegularizedBC 속도(u_lb, 0, 0) · 출구: ExtrapolationOutflowBC · 풍동 벽: FullwayBounceBackBC(닫힌 시험부)
- 물체: HalfwayBounceBackBC(indices = 고체 껍질 셀) — XLB 구(sphere) 예제와 같은 지수 경로.
  XLB 0.3.1 의 STL 마스커(MeshBoundaryMasker, AABB)는 warp 1.10 에서 표면 셀을 대부분 놓쳐(반지름 6 셀 구 → 11 셀)
  쓰지 않고, 고체 판정은 lattice.voxelize(표면 표본점이 지나는 셀 + 닫힌 속 채우기 — 속 빈 외피 STL·감김 혼재에 강건)로 한다.
  고체 셀 전부를 지수로 넘긴다(내부 셀은 모든 방향이 결손 → 제자리 반사, 힘 합산에서는 빠짐 — XLB 운동량 교환 규칙).
- 받음각·옆미끄럼 변경 = 같은 격자·같은 경계조건 객체로 마스크만 다시 만든다(흐름장 유지 → 바뀐 자세로 이어서 흐름).
  경계조건 객체를 새로 만들면 XLB 등록 id(uint8)가 계속 늘고 커널을 다시 컴파일하므로 피한다.
- Re 변경 = 완화 계수 ω 만 바꾼다(실행 인자).
"""

import time  # (from __future__ annotations 금지 — Warp 커널 주석을 문자열로 만들면 지역 이름 wp 를 못 찾는다)

import numpy as np

import lattice as lt


class XlbBackend:
    name = "XLB"

    def __init__(self, log=print, collision: str = "KBC"):
        self.log = log
        self.collision = collision
        self._xlb = None
        self.grid = None
        self.plan: lt.GridPlan | None = None
        self.f0 = self.f1 = self.bc_mask = self.missing = None
        self.solid: np.ndarray | None = None
        self.step_count = 0
        self.omega = 1.0
        self.u_lb = lt.U_LB
        self.device = ""

    # ------------------------------------------------------------------ 준비

    def _import(self):
        if self._xlb is not None:
            return
        import warp as wp
        import xlb
        from xlb.compute_backend import ComputeBackend
        from xlb.precision_policy import PrecisionPolicy

        wp.config.quiet = True
        wp.init()
        if wp.get_cuda_device_count() < 1:
            raise RuntimeError("CUDA GPU 를 찾지 못했습니다(NVIDIA Warp). GPU 가상 풍동은 NVIDIA GPU 가 필요합니다.")
        self.device = str(wp.get_device("cuda:0").name)
        self.wp = wp
        self.backend = ComputeBackend.WARP
        self.precision = PrecisionPolicy.FP32FP32
        self.velocity_set = xlb.velocity_set.D3Q27(precision_policy=self.precision, compute_backend=self.backend)
        xlb.init(velocity_set=self.velocity_set, default_backend=self.backend, default_precision_policy=self.precision)
        self._xlb = xlb

    def voxelize(self, verts_lattice: np.ndarray, shape) -> np.ndarray:
        return lt.voxelize(verts_lattice, shape)

    def build(self, plan: lt.GridPlan, verts_lattice: np.ndarray, u_lb: float = lt.U_LB):
        """격자·경계조건·스테퍼를 새로 만든다(해상도·물체가 바뀔 때)."""
        self._import()
        wp = self.wp
        from xlb.grid import grid_factory
        from xlb.operator.boundary_condition import (ExtrapolationOutflowBC, FullwayBounceBackBC, HalfwayBounceBackBC,
                                                     RegularizedBC)
        from xlb.operator.force.momentum_transfer import MomentumTransfer
        from xlb.operator.macroscopic import Macroscopic
        from xlb.operator.stepper import IncompressibleNavierStokesStepper
        from xlb.helper.initializers import initialize_eq
        from xlb.precision_policy import Precision

        t0 = time.perf_counter()
        self.plan = plan
        self.u_lb = u_lb
        self.grid = grid = grid_factory(plan.shape, compute_backend=self.backend)
        box = grid.bounding_box_indices()
        box_ne = grid.bounding_box_indices(remove_edges=True)
        walls = [box["bottom"][i] + box["top"][i] + box["front"][i] + box["back"][i] for i in range(3)]
        walls = np.unique(np.array(walls), axis=-1).tolist()
        solid = self.voxelize(verts_lattice, plan.shape)
        self.bc_inlet = RegularizedBC("velocity", prescribed_value=(u_lb, 0.0, 0.0), indices=box_ne["left"])
        self.bc_outlet = ExtrapolationOutflowBC(indices=box_ne["right"])
        self.bc_walls = FullwayBounceBackBC(indices=walls)
        self.bc_body = HalfwayBounceBackBC(indices=self._indices(solid))
        bcs = [self.bc_walls, self.bc_inlet, self.bc_outlet, self.bc_body]
        self.stepper = IncompressibleNavierStokesStepper(grid=grid, boundary_conditions=bcs, collision_type=self.collision)

        vs, pp, cb = self.velocity_set, self.precision, self.backend

        def init_uniform(grid_, vs_, pp_, cb_):
            f = grid_.create_field(cardinality=vs_.q, dtype=pp_.store_precision)
            u = np.zeros((3,) + tuple(plan.shape), dtype=np.float32)
            u[0] = u_lb
            u[0][solid] = 0.0
            rho = grid_.create_field(cardinality=1, fill_value=1.0, dtype=pp_.compute_precision)
            return initialize_eq(f, grid_, vs_, pp_, cb_, rho=rho, u=wp.array(u, dtype=wp.float32))

        self.f0, self.f1, self.bc_mask, self.missing = self.stepper.prepare_fields(initializer=init_uniform)
        self.solid = solid
        self.momentum = MomentumTransfer(self.bc_body, velocity_set=vs, precision_policy=pp, compute_backend=cb)
        self.macro = Macroscopic(velocity_set=vs, precision_policy=pp, compute_backend=cb)
        self.rho = grid.create_field(cardinality=1, dtype=Precision.FP32)
        self.u = grid.create_field(cardinality=3, dtype=Precision.FP32)
        self._precision_enum = Precision
        self.step_count = 0
        wp.synchronize()
        self.log(f"XLB grid {plan.shape} ({plan.cells / 1e6:.2f} M cells, solid {int(solid.sum())}) ready {time.perf_counter() - t0:.1f} s")

    @staticmethod
    def _indices(solid: np.ndarray) -> list:
        """고체 셀 지수(XLB 형식 [[i…], [j…], [k…]]). 물체가 격자에 하나도 안 걸리면 구석 한 셀(경계조건 목록 유지용)."""
        idx = np.array(np.nonzero(solid))
        if idx.shape[1] == 0:
            raise ValueError("물체가 격자에 복셀화되지 않았습니다(너무 작거나 격자 밖)")
        return idx.tolist()

    def remask(self, verts_lattice: np.ndarray):
        """같은 격자에서 물체 자세만 바꾼다(마스크 다시 만들기, 분포함수 유지)."""
        Precision = self._precision_enum
        solid = self.voxelize(verts_lattice, self.plan.shape)
        bc_mask = self.grid.create_field(cardinality=1, dtype=Precision.UINT8)
        missing = self.grid.create_field(cardinality=self.velocity_set.q, dtype=Precision.BOOL)
        self.bc_body.indices = self._indices(solid)
        self.bc_mask, self.missing = self.stepper._process_boundary_conditions(self.stepper.boundary_conditions, bc_mask, missing)
        self.solid = solid
        self.wp.synchronize()

    def set_omega(self, omega: float):
        self.omega = float(omega)

    # ------------------------------------------------------------------ 실행

    def run(self, steps: int):
        st = self.stepper
        f0, f1 = self.f0, self.f1
        for _ in range(int(steps)):
            f0, f1 = st(f0, f1, self.bc_mask, self.missing, self.omega, self.step_count)
            f0, f1 = f1, f0
            self.step_count += 1
        self.f0, self.f1 = f0, f1

    def sync(self):
        self.wp.synchronize()

    def velocity(self) -> np.ndarray:
        """(3, nx, ny, nz) 격자 단위 속도(고체 셀 0) — CPU 로 전체 복사(시험·디버그용, 화면 프레임은 field())."""
        self.macro(self.f0, self.rho, self.u)
        u = self.u.numpy()
        if self.solid is not None:
            u[:, self.solid] = 0.0
        lt.check_diverged(np.max(np.abs(u)))
        return u

    def force(self) -> np.ndarray:
        return np.asarray(self.momentum(self.f0, self.f1, self.bc_mask, self.missing), dtype=float)

    def field(self) -> "GpuField":
        """현재 속도장(GPU 에 둔 채) — 후처리(입자·유선·단면)를 GPU 커널로. CPU 로는 작은 배열만 옮긴다."""
        self.macro(self.f0, self.rho, self.u)
        if getattr(self, "_solid_key", None) is not self.solid:
            self._solid_wp = self.wp.array(self.solid.astype(np.uint8), dtype=self.wp.uint8)
            self._solid_key = self.solid
        return GpuField(self.wp, _kernels(self.wp), self.u, self._solid_wp, self.solid)


_KERNELS = None


def _kernels(wp):
    """Warp 후처리 커널(한 번만 만든다). 수식은 lattice.CpuField(numpy 기준 구현)와 같다 — 셀 중심 = 지수 + 0.5, 삼선형 보간."""
    global _KERNELS
    if _KERNELS is not None:
        return _KERNELS

    @wp.func
    def sample(u: wp.array4d(dtype=wp.float32), p: wp.vec3):
        nx, ny, nz = u.shape[1], u.shape[2], u.shape[3]
        q = p - wp.vec3(0.5, 0.5, 0.5)
        fx, fy, fz = wp.floor(q[0]), wp.floor(q[1]), wp.floor(q[2])
        tx, ty, tz = q[0] - fx, q[1] - fy, q[2] - fz
        i0, j0, k0 = int(fx), int(fy), int(fz)
        r = wp.vec3(0.0, 0.0, 0.0)
        for a in range(2):
            i = wp.clamp(i0 + a, 0, nx - 1)
            wx = wp.where(a == 1, tx, 1.0 - tx)
            for b in range(2):
                j = wp.clamp(j0 + b, 0, ny - 1)
                wy = wp.where(b == 1, ty, 1.0 - ty)
                for c in range(2):
                    k = wp.clamp(k0 + c, 0, nz - 1)
                    w = wx * wy * wp.where(c == 1, tz, 1.0 - tz)
                    r += w * wp.vec3(u[0, i, j, k], u[1, i, j, k], u[2, i, j, k])
        return r

    @wp.func
    def is_solid(solid: wp.array3d(dtype=wp.uint8), p: wp.vec3):
        i = wp.clamp(int(wp.floor(p[0])), 0, solid.shape[0] - 1)
        j = wp.clamp(int(wp.floor(p[1])), 0, solid.shape[1] - 1)
        k = wp.clamp(int(wp.floor(p[2])), 0, solid.shape[2] - 1)
        return solid[i, j, k] != wp.uint8(0)

    @wp.func
    def inside(u: wp.array4d(dtype=wp.float32), p: wp.vec3):
        return (p[0] >= 1.0 and p[1] >= 1.0 and p[2] >= 1.0 and p[0] <= float(u.shape[1]) - 1.0
                and p[1] <= float(u.shape[2]) - 1.0 and p[2] <= float(u.shape[3]) - 1.0)

    @wp.kernel
    def zero_solid(u: wp.array4d(dtype=wp.float32), solid: wp.array3d(dtype=wp.uint8)):
        i, j, k = wp.tid()
        if solid[i, j, k] != wp.uint8(0):
            u[0, i, j, k] = 0.0
            u[1, i, j, k] = 0.0
            u[2, i, j, k] = 0.0

    @wp.kernel
    def max_abs(u: wp.array4d(dtype=wp.float32), out: wp.array(dtype=wp.float32)):
        i, j, k = wp.tid()
        m = float(0.0)
        for c in range(3):
            v = u[c, i, j, k]
            if v != v:
                m = 1.0e30
            else:
                m = wp.max(m, wp.abs(v))
        wp.atomic_max(out, 0, m)

    @wp.kernel
    def advect(u: wp.array4d(dtype=wp.float32), pos: wp.array(dtype=wp.vec3), nsub: int, h: float):
        t = wp.tid()
        p = pos[t]
        for _ in range(nsub):
            v1 = sample(u, p)
            v2 = sample(u, p + 0.5 * h * v1)
            p = p + h * v2
        pos[t] = p

    @wp.kernel
    def sample_k(u: wp.array4d(dtype=wp.float32), pos: wp.array(dtype=wp.vec3), out: wp.array(dtype=wp.vec3)):
        t = wp.tid()
        out[t] = sample(u, pos[t])

    @wp.kernel
    def stream(u: wp.array4d(dtype=wp.float32), solid: wp.array3d(dtype=wp.uint8), seeds: wp.array(dtype=wp.vec3), h: float,
               max_points: int, out: wp.array2d(dtype=wp.vec4), length: wp.array(dtype=wp.int32)):
        t = wp.tid()
        p = seeds[t]
        n = int(0)
        for s in range(max_points):
            v = sample(u, p)
            sp = wp.length(v)
            out[t, s] = wp.vec4(p[0], p[1], p[2], sp)
            n = s + 1
            if sp <= 1.0e-5:
                break
            mid = p + 0.5 * h * v / sp
            v2 = sample(u, mid)
            s2 = wp.length(v2)
            if s2 <= 1.0e-5:
                break
            q = p + h * v2 / s2
            if not inside(u, q) or is_solid(solid, q):
                break
            p = q
        length[t] = n

    @wp.kernel
    def slab_xz(u: wp.array4d(dtype=wp.float32), j: int, out: wp.array3d(dtype=wp.float32)):
        i, k = wp.tid()
        for c in range(3):
            out[c, i, k] = u[c, i, j, k]

    @wp.kernel
    def slab_xy(u: wp.array4d(dtype=wp.float32), k: int, out: wp.array3d(dtype=wp.float32)):
        i, j = wp.tid()
        for c in range(3):
            out[c, i, j] = u[c, i, j, k]

    _KERNELS = {"zero_solid": zero_solid, "max_abs": max_abs, "advect": advect, "sample": sample_k, "stream": stream,
                "slab_xz": slab_xz, "slab_xy": slab_xy}
    return _KERNELS


class GpuField:
    """lattice.CpuField 와 같은 메서드의 GPU 구현(Warp). 속도장은 GPU 에 두고 결과(입자·유선·단면)만 CPU 로."""

    def __init__(self, wp, k, u, solid_wp, solid_np):
        self.wp, self.k, self.u, self.solid_wp, self.solid = wp, k, u, solid_wp, solid_np
        self.shape = tuple(u.shape[1:])
        wp.launch(k["zero_solid"], dim=self.shape, inputs=[u, solid_wp])
        self._umax = None

    def max_speed(self) -> float:
        if self._umax is None:
            out = self.wp.zeros(1, dtype=self.wp.float32)
            self.wp.launch(self.k["max_abs"], dim=self.shape, inputs=[self.u, out])
            self._umax = float(out.numpy()[0])
        return self._umax

    def solid_at(self, pts: np.ndarray) -> np.ndarray:
        return lt.solid_at(self.solid, pts)

    def sample(self, pts: np.ndarray) -> np.ndarray:
        wp = self.wp
        p = wp.array(np.asarray(pts, dtype=np.float32).reshape(-1, 3), dtype=wp.vec3)
        out = wp.zeros(len(pts), dtype=wp.vec3)
        if len(pts):
            wp.launch(self.k["sample"], dim=len(pts), inputs=[self.u, p, out])
        return out.numpy().astype(np.float64)

    def advect(self, pos: np.ndarray, steps: float) -> np.ndarray:
        wp = self.wp
        if len(pos) == 0:
            return np.asarray(pos, dtype=float)
        nsub = lt.substeps(steps, self.max_speed())
        p = wp.array(np.asarray(pos, dtype=np.float32), dtype=wp.vec3)
        wp.launch(self.k["advect"], dim=len(pos), inputs=[self.u, p, nsub, float(steps / nsub)])
        return p.numpy().astype(np.float64)

    def streamlines(self, seeds: np.ndarray, h: float = 0.6, max_points: int = 700) -> list[np.ndarray]:
        wp = self.wp
        n = len(seeds)
        if n == 0:
            return []
        s = wp.array(np.asarray(seeds, dtype=np.float32), dtype=wp.vec3)
        out = wp.zeros((n, max_points), dtype=wp.vec4)
        ln = wp.zeros(n, dtype=wp.int32)
        wp.launch(self.k["stream"], dim=n, inputs=[self.u, self.solid_wp, s, float(h), int(max_points), out, ln])
        o, lens = out.numpy(), ln.numpy()
        return [o[i, :lens[i]].astype(np.float64) for i in range(n) if lens[i] >= 2]

    def slice(self, plane: str, index: int) -> dict:
        wp = self.wp
        nx, ny, nz = self.shape
        if plane == "xz":
            index = int(np.clip(index, 0, ny - 1))
            out = wp.zeros((3, nx, nz), dtype=wp.float32)
            wp.launch(self.k["slab_xz"], dim=(nx, nz), inputs=[self.u, index, out])
            mask = self.solid[:, index, :]
        else:
            index = int(np.clip(index, 0, nz - 1))
            out = wp.zeros((3, nx, ny), dtype=wp.float32)
            wp.launch(self.k["slab_xy"], dim=(nx, ny), inputs=[self.u, index, out])
            mask = self.solid[:, :, index]
        return lt.slice_from_slab(out.numpy().astype(np.float64), mask, plane)
