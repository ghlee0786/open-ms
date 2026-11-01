"""GPU 가상 풍동 서버(앱이 별도 프로세스로 띄움). XLB 격자 볼츠만을 돌리며 단면 장·연기 입자·유선을 프레임으로 보낸다.

실행: tools/airflow/.venv/Scripts/python.exe tools/airflow/server.py [--port 0] [--selftest]
  표준출력 첫 줄 "AIRFLOW_PORT <n>" → 앱이 127.0.0.1:<n> 에 접속(한 클라이언트). 연결이 끊기면 종료.
  --selftest: XLB 대신 해석적 가짜 장(프로토콜·앱 시험 전용 — 물리 아님, 프레임에 backend="selftest" 표시).
형식: protocol.py(schema mns.airflow-ipc/1). 좌표: lattice.py 머리말(풍동 좌표 m, 흐름 −X).
"""

from __future__ import annotations

import argparse
import math
import os
import queue
import socket
import sys
import threading
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import lattice as lt  # noqa: E402
import protocol as pr  # noqa: E402

FRAME_SECONDS = 0.08           # 프레임마다 GPU 계산에 쓰는 시간(후처리는 별도)
FORCE_EVERY = 50               # 힘 표본 간격(스텝) — 표본마다 GPU 동기화가 생겨 너무 잦으면 처리량이 반으로 준다
LINES_EVERY = 4                # 유선은 몇 프레임마다 다시


def log(msg: str):
    print(msg, file=sys.stderr, flush=True)


DEFAULTS = {
    "mesh": "", "alpha": 0.0, "beta": 0.0, "speed": 168.0, "altitude": 1000.0, "re": 0.0, "resolution": "low",
    "refArea": 0.0, "refLength": 0.0, "plane": "xz", "sliceOffset": 0.0, "particles": True, "lines": True, "paused": False,
    "domain": None,   # 선택: 영역·격자 규칙(lattice.domain_settings 키) — 없으면 resolution 프리셋(기존 배치)
}


class SelfTestBackend:
    """해석적 가짜 장(시험 전용): 균일류 + 물체 고체 셀 0 + 후류에 시간에 따라 흔들리는 z 성분. 물리 아님."""

    name = "selftest"

    def __init__(self, log=log):
        self.plan = None
        self.solid = None
        self.step_count = 0
        self.omega = 1.0
        self.device = "CPU (selftest)"
        self.u_lb = lt.U_LB

    def build(self, plan, verts_lattice, u_lb=lt.U_LB):
        self.plan, self.u_lb = plan, u_lb
        self.solid = lt.voxelize(verts_lattice, plan.shape)
        self.step_count = 0

    def remask(self, verts_lattice):
        self.solid = lt.voxelize(verts_lattice, self.plan.shape)

    def set_omega(self, omega):
        self.omega = omega

    def run(self, steps):
        self.step_count += int(steps)

    def sync(self):
        pass

    def velocity(self):
        nx, ny, nz = self.plan.shape
        u = np.zeros((3, nx, ny, nz), dtype=np.float32)
        u[0] = self.u_lb
        x = np.arange(nx)[:, None, None]
        wake = (x > self.plan.origin_cells[0]) * np.sin(0.15 * x - 0.01 * self.step_count)
        u[2] = 0.2 * self.u_lb * wake
        u[:, self.solid] = 0
        return u

    def field(self):
        return lt.CpuField(self.velocity(), self.solid)

    def force(self):
        area = 1.0
        return np.array([0.5 * self.u_lb ** 2 * area * 1.0, 0.0, 0.1 * math.sin(0.01 * self.step_count)])


class Session:
    """설정 → (격자 다시 만들기 | 마스크만 | ω 만) 판단, 프레임 계산."""

    def __init__(self, backend):
        self.be = backend
        self.cfg: dict | None = None
        self.verts_body: np.ndarray | None = None
        self.plan: lt.GridPlan | None = None
        self.lb: dict = {}
        self.ref_area = self.ref_length = 1.0
        self.particles: lt.ParticleSystem | None = None
        self.lines_cache: list[np.ndarray] = []
        self.frame_no = 0
        self.force_hist: list[tuple[int, np.ndarray]] = []
        self.rate = 300.0           # 스텝/s 추정
        self.settle_until = 0
        self.last_force: np.ndarray | None = None
        self.timing: dict[str, float] = {}
        self.built = False
        self.mesh_key = None
        self.blockage = 0.0
        self.warnings: list[str] = []      # 마지막 적용의 경고(막힘 비율 등) — 'ready' 상태 문장에 붙는다

    # ------------------------------------------------------------------ 설정

    def apply(self, cfg: dict, status=lambda phase, msg: None) -> list[str]:
        """새 설정 적용. 반환: 한 일 목록(rebuild | remask | omega | slice …)."""
        new = {**DEFAULTS, **(self.cfg or {}), **{k: v for k, v in cfg.items() if k in DEFAULTS}}
        old = self.cfg
        self.cfg = new
        done = []
        mesh_key = (new["mesh"], os.path.getmtime(new["mesh"]) if new["mesh"] and os.path.exists(new["mesh"]) else None)
        if (not self.built or old is None or mesh_key != self.mesh_key or new["resolution"] != old["resolution"]
                or new["domain"] != old["domain"]):
            if not new["mesh"]:
                raise ValueError("물체 메시(mesh) 경로가 없습니다")
            status("mesh", "메시 읽기")
            vb = pr.read_stl(new["mesh"])
            self.verts_body = vb
            self.plan = lt.plan_grid(vb, new["resolution"], domain=new["domain"], alpha_deg=new["alpha"], beta_deg=new["beta"])
            for w in self.plan.extra["domain"]["warnings"]:
                if not w.startswith("막힘"):
                    status("build", w)
            status("build", f"격자 {self.plan.shape} 복셀화·XLB 준비 (처음 한 번은 커널 컴파일 수십 초)")
            self.be.build(self.plan, lt.body_to_lattice(vb, self.plan, new["alpha"], new["beta"]))
            self.built = True
            self.mesh_key = mesh_key
            self.force_hist = []
            self.frame_no = 0
            self._reset_vis()
            done.append("rebuild")
        elif new["alpha"] != old["alpha"] or new["beta"] != old["beta"]:
            status("remask", f"자세 α {new['alpha']:.1f}° β {new['beta']:.1f}° 다시 복셀화")
            self.be.remask(lt.body_to_lattice(self.verts_body, self.plan, new["alpha"], new["beta"]))
            self.lines_cache = []
            done.append("remask")
        if {"rebuild", "remask"} & set(done):
            self.blockage = lt.blockage(self.plan, self.verts_body, new["alpha"], new["beta"])
            w = lt.fd.blockage_warning(self.blockage)
            self.warnings = ([w] if w else []) + [x for x in self.plan.extra["domain"]["warnings"] if not x.startswith("막힘")]
        self.ref_length = new["refLength"] if new["refLength"] > 0 else self._default_ref_length()
        self.ref_area = new["refArea"] if new["refArea"] > 0 else math.pi * self.ref_length ** 2 / 4
        lb = lt.lb_parameters(new["re"] if new["re"] > 0 else 1e12, self.ref_length, self.plan)
        if not self.lb or abs(lb["omega"] - self.lb["omega"]) > 1e-12:
            self.be.set_omega(lb["omega"])
            if old is not None and "rebuild" not in done:
                done.append("omega")
        self.lb = lb
        if {"rebuild", "remask", "omega"} & set(done):
            self.settle()
        if old is not None and (new["plane"] != old["plane"] or new["sliceOffset"] != old["sliceOffset"]):
            self.lines_cache = []
            self._reset_vis(particles=False)
            done.append("slice")
        return done

    def _default_ref_length(self) -> float:
        """기준 길이 기본값 = 동체 x 에 수직인 두 경계 상자 크기 중 큰 값(원통이면 지름)."""
        ext = self.verts_body.max(axis=0) - self.verts_body.min(axis=0)
        return float(max(ext[1], ext[2]))

    def _reset_vis(self, particles: bool = True):
        p = self.plan
        xz = (self.cfg or DEFAULTS)["plane"] == "xz"
        line = lt.rake(p, 1, 28, plane="xz", span=0.62) if xz else lt.rake(p, 28, 1, plane="xy", span=0.62)
        if particles:
            seeds = np.vstack([lt.rake(p, 9, 9, span=0.5), self._slice_rake()])
            self.particles = lt.ParticleSystem(seeds, max_particles=16000, max_age=int(2.0 * p.shape[0] / lt.U_LB))
        self.line_seeds = line
        self.lines_cache = []

    def _slice_rake(self) -> np.ndarray:
        cfg = self.cfg or DEFAULTS
        r = lt.rake(self.plan, 1 if cfg["plane"] == "xz" else 36, 36 if cfg["plane"] == "xz" else 1, span=0.6, plane=cfg["plane"])
        r[:, 1 if cfg["plane"] == "xz" else 2] += cfg["sliceOffset"] / self.plan.dx
        return r

    def slice_index(self) -> int:
        cfg = self.cfg
        axis = 1 if cfg["plane"] == "xz" else 2
        c = self.plan.origin_cells[axis] + cfg["sliceOffset"] / self.plan.dx
        return int(np.clip(math.floor(c), 0, self.plan.shape[axis] - 1))

    # ------------------------------------------------------------------ 프레임

    def advance(self) -> int:
        """한 프레임 분량 계산(힘 표본 포함). 반환: 진행 스텝. 계산 시간 ≈ FRAME_SECONDS(후처리 시간은 따로)."""
        steps = int(max(FORCE_EVERY, round(self.rate * FRAME_SECONDS / FORCE_EVERY) * FORCE_EVERY))
        steps = min(steps, 20 * FORCE_EVERY)
        t0 = time.perf_counter()
        for _ in range(steps // FORCE_EVERY):
            self.be.run(FORCE_EVERY)
            self.last_force = self.be.force()
            if self.be.step_count >= self.settle_until:
                self.force_hist.append((self.be.step_count, self.last_force))
        self.be.sync()
        dt = time.perf_counter() - t0
        if dt > 0:
            self.rate = 0.6 * self.rate + 0.4 * steps / dt
        self.timing["solve"] = dt
        return steps

    def mean_window_steps(self) -> int:
        """계수 평균 창(스텝) = 기준 길이 20 개를 자유류가 지나는 시간(와류 흘림 여러 주기)."""
        return int(20 * self.lb["ref_cells"] / lt.U_LB)

    def settle(self):
        """자세·Re·격자가 바뀐 직후의 충격 과도(압력파)는 계수 평균에서 뺀다: 기준 길이 8 개 통과 시간."""
        self.force_hist = []
        self.settle_until = self.be.step_count + int(8 * self.lb.get("ref_cells", 8) / lt.U_LB)

    def frame(self, steps: int) -> tuple[dict, dict]:
        cfg, plan, be = self.cfg, self.plan, self.be
        t0 = time.perf_counter()
        fld = be.field()
        lt.check_diverged(fld.max_speed())
        t1 = time.perf_counter()
        solid = be.solid
        arrays: dict[str, np.ndarray] = {}
        ui = lt.U_LB
        j = self.slice_index()
        sl = fld.slice(cfg["plane"], j)
        speed = (sl["speed"] / ui).astype(np.float32)
        speed[sl["mask"]] = np.nan
        vort = (sl["vort"] * self.lb["ref_cells"] / ui).astype(np.float32)
        vort[sl["mask"]] = np.nan
        arrays["slice_speed"] = speed
        arrays["slice_vort"] = vort
        t2 = time.perf_counter()
        if cfg["particles"] and self.particles is not None:
            self.particles.step(fld, steps)
            pp = self.particles.pos
            if len(pp):
                s = np.linalg.norm(fld.sample(pp), axis=1) / ui
                arrays["particles"] = np.column_stack([lt.lattice_to_tunnel(pp, plan), s]).astype(np.float32)
        t3 = time.perf_counter()
        if cfg["lines"]:
            if not self.lines_cache or self.frame_no % LINES_EVERY == 0:
                seeds = self.line_seeds.copy()
                seeds[:, 1 if cfg["plane"] == "xz" else 2] += cfg["sliceOffset"] / plan.dx
                self.lines_cache = fld.streamlines(seeds, h=0.9, max_points=360)
            if self.lines_cache:
                arrays["lines"] = np.vstack([np.column_stack([lt.lattice_to_tunnel(l[:, :3], plan), l[:, 3] / ui])
                                             for l in self.lines_cache]).astype(np.float32)
                arrays["line_counts"] = np.array([len(l) for l in self.lines_cache], dtype=np.int32)
        t4 = time.perf_counter()
        self.timing.update(velocity=t1 - t0, slice=t2 - t1, particles=t3 - t2, lines=t4 - t3)
        # 계수: 순간(마지막 표본)·평균(창)
        win = self.mean_window_steps()
        hist = [f for s, f in self.force_hist if s > be.step_count - win]
        self.force_hist = [(s, f) for s, f in self.force_hist if s > be.step_count - 4 * win]
        inst = lt.coefficients(self.last_force, plan, self.ref_area) if self.last_force is not None else {}
        mean = lt.coefficients(np.mean(hist, axis=0), plan, self.ref_area) if hist else {}
        phys = lt.physical_numbers(cfg["speed"], self.ref_length, cfg["altitude"])
        dt_step = lt.step_seconds(plan, cfg["speed"])
        geo = lt.slice_geometry(plan, cfg["plane"], j)
        self.frame_no += 1
        h = {
            "type": "frame", "frame": self.frame_no, "backend": be.name, "device": be.device,
            "step": be.step_count, "steps": steps, "stepsPerSecond": self.rate,
            "timePhys": be.step_count * dt_step, "stepSeconds": dt_step,
            "flowThroughs": be.step_count * ui / max(self.lb["ref_cells"], 1e-9),
            "alpha": cfg["alpha"], "beta": cfg["beta"], "speed": cfg["speed"],
            "re": self.lb["re"], "reMax": self.lb["re_max"], "reClamped": bool(cfg["re"] <= 0 or self.lb["re_clamped"]),
            "rePhys": phys["re"], "mach": phys["mach"], "maLattice": self.lb["ma_lb"], "tau": self.lb["tau"],
            "uLattice": ui, "dx": plan.dx, "shape": list(plan.shape), "resolution": cfg["resolution"], "blockage": self.blockage,
            "refArea": self.ref_area, "refLength": self.ref_length, "refCells": self.lb["ref_cells"],
            "bodyCenter": plan.body_center.tolist(), "solidCells": int(solid.sum()) if solid is not None else 0,
            "coeffs": inst, "coeffsMean": mean, "meanSamples": len(hist), "meanWindowSteps": win,
            "settling": be.step_count < self.settle_until, "timing": dict(self.timing),
            "slice": {"plane": cfg["plane"], "index": j, "offset": cfg["sliceOffset"], **geo},
            "paused": bool(cfg["paused"]),
        }
        return h, arrays


# ---------------------------------------------------------------------- TCP

def serve(backend, port: int = 0, accept_timeout: float = 120.0, port_file: str | None = None):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.bind(("127.0.0.1", port))
    srv.listen(1)
    real_port = srv.getsockname()[1]
    print(f"AIRFLOW_PORT {real_port}", flush=True)
    if port_file:
        with open(port_file, "w", encoding="ascii") as f:
            f.write(str(real_port))
    srv.settimeout(accept_timeout)
    try:
        conn, _ = srv.accept()
    except socket.timeout:
        log("접속 대기 시간 초과 — 종료")
        return 2
    finally:
        srv.close()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    return run_connection(conn, backend)


def run_connection(conn, backend) -> int:
    inbox: queue.Queue = queue.Queue()

    def reader():
        try:
            while True:
                h, _ = pr.recv(conn)
                inbox.put(h)
                if h.get("cmd") == "quit":
                    return
        except Exception as ex:  # 연결 끊김 = 종료
            inbox.put({"cmd": "quit", "reason": str(ex)})

    threading.Thread(target=reader, daemon=True).start()
    sess = Session(backend)
    send_lock = threading.Lock()

    def send(h, a=None):
        with send_lock:
            pr.send(conn, h, a)

    def status(phase, msg):
        log(msg)
        send({"type": "status", "phase": phase, "message": msg})

    versions = {}
    for mod in ("xlb", "warp"):
        try:
            versions[mod] = __import__(mod).__version__ if backend.name != "selftest" else "-"
        except Exception:
            versions[mod] = "?"
    send({"type": "hello", "schema": pr.SCHEMA, "backend": backend.name, "versions": versions, "pid": os.getpid(),
          "resolutions": {k: list(v) for k, v in lt.RESOLUTIONS.items()}, "uLattice": lt.U_LB, "nuMin": lt.NU_LB_MIN})
    failed = False
    try:
        while True:
            # 쌓인 명령: 마지막 설정만(슬라이더 드래그 중 중간값 건너뜀)
            cfg = None
            block = sess.cfg is None or failed or bool(sess.cfg.get("paused"))
            while True:
                try:
                    m = inbox.get(timeout=0.05) if block and cfg is None else inbox.get_nowait()
                except queue.Empty:
                    if block and cfg is None:
                        if sess.cfg is not None and not failed:
                            break      # 일시정지 — 프레임 없이 다시 확인
                        continue
                    break
                if m.get("cmd") == "quit":
                    log(f"종료: {m.get('reason', 'quit')}")
                    return 0
                if m.get("cmd") == "reset":       # 발산 뒤 다시 시작 등 — 격자부터 새로
                    sess.built = False
                    failed = False
                    cfg = cfg or {}
                if m.get("cmd") == "config":
                    cfg = {**(cfg or {}), **m}
            if cfg is not None:
                try:
                    done = sess.apply(cfg, status)
                    failed = False
                    if done:
                        status("ready", "적용: " + ", ".join(done) + "".join(f" · 경고: {w}" for w in sess.warnings))
                except Exception as ex:
                    failed = True
                    if not isinstance(ex, ValueError):
                        sess.built = False
                    send({"type": "error", "message": f"{type(ex).__name__}: {ex}"})
                    continue
            if sess.cfg is None or failed:
                continue
            if sess.cfg["paused"]:
                continue
            try:
                steps = sess.advance()
                h, a = sess.frame(steps)
            except FloatingPointError as ex:
                # 발산: Re 를 60 % 로 낮춰 격자부터 다시(앱은 diverged·newRe 를 받아 Re 표시를 맞춘다)
                new_re = max(20.0, 0.6 * sess.lb.get("re", 100.0))
                send({"type": "error", "message": f"{ex} → Re {new_re:.0f} 로 낮춰 다시 시작", "diverged": True, "newRe": new_re})
                sess.built = False
                try:
                    sess.apply({"re": new_re}, status)
                except Exception as ex2:
                    failed = True
                    send({"type": "error", "message": f"{type(ex2).__name__}: {ex2}"})
                continue
            send(h, a)
    except (ConnectionError, OSError) as ex:
        log(f"연결 끊김: {ex}")
        return 0
    finally:
        try:
            conn.close()
        except OSError:
            pass


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="MnS GPU 가상 풍동 서버 (XLB 격자 볼츠만)")
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--port-file")
    ap.add_argument("--selftest", action="store_true", help="XLB 대신 해석적 가짜 장(시험 전용)")
    ap.add_argument("--accept-timeout", type=float, default=120.0)
    a = ap.parse_args(argv)
    if a.selftest:
        backend = SelfTestBackend()
    else:
        from xlb_backend import XlbBackend
        backend = XlbBackend(log=log)
    return serve(backend, a.port, a.accept_timeout, a.port_file)


if __name__ == "__main__":
    sys.exit(main())
