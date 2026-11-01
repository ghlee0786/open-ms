"""README · 매뉴얼 스크린샷과 데모 GIF 만들기 — docs/manual/_shots.json 대로 앱을 실제로 띄워 찍는다.

    python scripts/make_readme_assets.py                 # 빌드 → 전부 촬영(자동으로 못 찍는 것은 건너뛰고 보고)
    python scripts/make_readme_assets.py --only menu     # 파일 이름에 'menu' 가 든 것만
    python scripts/make_readme_assets.py --no-build --list

- 각 항목: `dotnet src/Sim.App/bin/Debug/net8.0/MnS.Simulator.dll --smoke <scenario> <smoke>` 를 저장소 루트에서 실행하고
  `output`(results/smoke/ 아래 캡처)을 `file` 로 복사한다. 시나리오 · smoke 가 같은 항목은 한 번만 실행한다.
- `prepare: "credibility"` → 먼저 `mns credibility <scenario>` 로 신뢰도 분석을 만든다(신뢰도 창 내용).
- `gif: {fps, width, hold}` → `output` 폴더의 frame_*.png 를 이어 붙여 GIF 로(Pillow). 마지막 프레임은 hold 초 멈춤.
- `smoke` 가 빈 항목(앱 밖 화면)은 건너뛴다 — `manual` 에 적힌 방법으로 따로 찍는다.
- 스모크 모드는 화면의 절대 경로를 저장소 상대 · %USERPROFILE% 꼴로 바꿔 보인다(개발 PC 경로 · 사용자 이름이 그림에 남지 않음).
- 공개 데모 시나리오(config/scenarios/demo_*.json)만 쓴다. GPU 풍동 항목은 NVIDIA GPU 와 tools/airflow/.venv 가 필요하다.
"""
from __future__ import annotations

import argparse
import json
import shlex
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHOTS = ROOT / "docs" / "manual" / "_shots.json"
APP = ROOT / "src" / "Sim.App" / "bin" / "Debug" / "net8.0" / "MnS.Simulator.dll"
CLI = ROOT / "src" / "Sim.Cli" / "bin" / "Debug" / "net8.0" / "mns.dll"


def run(cmd: list[str], timeout: int) -> int:
    t0 = time.time()
    # 출력은 버린다(파이프로 받으면 앱이 띄운 자식 — 메셔 등 — 이 파이프를 쥐고 남아 끝나지 않는다). 시간이 넘으면 자식까지 끝낸다
    p = subprocess.Popen(cmd, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        code = p.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        if sys.platform == "win32":
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True)
        else:
            p.kill()
        p.wait()
        code = -1
    print(f"    ({time.time() - t0:.0f} s, 종료 {code})")
    return code


def prepare(kind: str, scenario: str, done: set[str]) -> None:
    if (kind, scenario) in done:
        return
    done.add((kind, scenario))
    if kind == "credibility":
        print(f"  준비: mns credibility {scenario}")
        run(["dotnet", str(CLI), "credibility", scenario], 1800)


def window_size(smoke: str) -> tuple[int, int]:
    """창 캡처의 기대 크기(DIP = 픽셀, 배율 100 %) — --size 가 있으면 그것, 없으면 기본 창 1680×980."""
    args = shlex.split(smoke)
    if "--size" in args and args.index("--size") + 1 < len(args):
        w, h = args[args.index("--size") + 1].split("x")
        return int(w), int(h)
    return 1680, 980


def bad_window_shots(out: Path, smoke: str) -> list[str]:
    """창 캡처가 기대 크기와 다른 파일(촬영 중 창이 최대화되는 등) — 다시 찍을지 판단."""
    from PIL import Image

    want = window_size(smoke)
    files = sorted(out.glob("frame_*.png")) if out.is_dir() else [out] if out.name.startswith("window") and out.is_file() else []
    return [f.name for f in files if Image.open(f).size != want]


def make_gif(frames_dir: Path, dst: Path, fps: float, width: int, hold: float, colors: int, max_mb: float) -> None:
    from PIL import Image

    files = sorted(frames_dir.glob("frame_*.png"))
    if not files:
        raise FileNotFoundError(f"{frames_dir}: frame_*.png 없음")
    first = Image.open(files[0])
    size = (width, round(first.height * width / first.width))
    imgs = [Image.open(f).convert("RGB").resize(size, Image.LANCZOS) for f in files]
    # 공통 팔레트(첫 · 가운데 · 끝 프레임 합본에서) — 프레임마다 색이 튀지 않게. 크기 제한을 넘으면 색 수를 줄여 다시
    strip = Image.new("RGB", (size[0], size[1] * 3))
    for k, im in enumerate((imgs[0], imgs[len(imgs) // 2], imgs[-1])):
        strip.paste(im, (0, k * size[1]))
    dur = [round(1000 / fps)] * len(imgs)
    dur[-1] += round(hold * 1000)
    dst.parent.mkdir(parents=True, exist_ok=True)
    while True:
        pal = strip.quantize(colors=colors, method=Image.MEDIANCUT)
        q = [im.quantize(palette=pal, dither=Image.Dither.NONE) for im in imgs]
        q[0].save(dst, save_all=True, append_images=q[1:], duration=dur, loop=0, optimize=True, disposal=1)
        mb = dst.stat().st_size / 1e6
        print(f"    GIF {len(q)} 프레임 · {size[0]}×{size[1]} · {colors} 색 · {mb:.2f} MB")
        if mb <= max_mb or colors <= 16:
            return
        colors -= 8


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--only", help="file 경로에 이 글자가 든 항목만")
    ap.add_argument("--no-build", action="store_true", help="dotnet build 생략")
    ap.add_argument("--list", action="store_true", help="항목만 보이고 끝")
    ap.add_argument("--timeout", type=int, default=600, help="항목 하나의 시간 제한 [s]")
    a = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # 콘솔 코드 페이지가 cp949 여도 — · 같은 글자로 멈추지 않게

    shots = json.loads(SHOTS.read_text(encoding="utf-8"))["shots"]
    if a.only:
        shots = [s for s in shots if a.only in s["file"]]
    if a.list:
        for s in shots:
            print(f"{s['file']:55s} {'(수동) ' + s.get('manual', '') if not s.get('smoke') else s['smoke']}")
        return 0
    if not a.no_build:
        for proj in ("src/Sim.App", "src/Sim.Cli"):
            if subprocess.run(["dotnet", "build", proj, "-v", "q", "-nologo"], cwd=ROOT).returncode != 0:
                return 1

    ok, skipped, failed = [], [], []
    done_runs: dict[tuple[str, str], int] = {}
    prepared: set = set()
    smoke_dir = ROOT / "results" / "smoke"
    # 같은 실행(시나리오 · smoke)을 쓰는 항목을 붙여 둔다 — 다음 실행이 캡처를 지우기 전에 모두 복사
    order = {}
    for s in shots:
        order.setdefault((s["scenario"], s.get("smoke", "")), len(order))
    shots = sorted(shots, key=lambda s: order[(s["scenario"], s.get("smoke", ""))])
    for s in shots:
        dst = ROOT / s["file"]
        if not s.get("smoke"):
            skipped.append((s["file"], s.get("manual", "앱 밖 화면")))
            continue
        key = (s["scenario"], s["smoke"])
        out = ROOT / s["output"]
        if key not in done_runs:
            # 같은 실행을 쓰는 항목 중 하나라도 준비가 필요하면 실행 전에
            for o in shots:
                if (o["scenario"], o.get("smoke", "")) == key and o.get("prepare"):
                    prepare(o["prepare"], o["scenario"], prepared)
            print(f"  촬영: {s['scenario']} {s['smoke']}")
            if smoke_dir.exists():
                for old in smoke_dir.glob("*.png"):
                    old.unlink()
            for attempt in range(3):
                done_runs[key] = run(["dotnet", str(APP), "--smoke", s["scenario"], *shlex.split(s["smoke"])], a.timeout)
                bad = bad_window_shots(out, s["smoke"])
                if not bad:
                    break
                print(f"    창 크기가 다른 캡처 {len(bad)}개({bad[0]} …) — 다시 찍음")
        try:
            if "gif" in s:
                g = s["gif"]
                make_gif(out, dst, g.get("fps", 10), g.get("width", 960), g.get("hold", 1.5), g.get("colors", 64), g.get("maxMB", 3.0))
            else:
                if not out.is_file():
                    raise FileNotFoundError(f"{s['output']} 없음(스모크 종료 {done_runs[key]})")
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(out, dst)
            ok.append(s["file"])
            print(f"    → {s['file']} ({dst.stat().st_size / 1024:.0f} KB)")
        except Exception as ex:  # noqa: BLE001 — 한 장이 실패해도 나머지는 찍는다
            failed.append((s["file"], str(ex)))
            print(f"    실패: {ex}")

    print(f"\n찍음 {len(ok)} · 건너뜀(수동) {len(skipped)} · 실패 {len(failed)}")
    for f, why in skipped:
        print(f"  건너뜀 {f}: {why}")
    for f, why in failed:
        print(f"  실패 {f}: {why}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
