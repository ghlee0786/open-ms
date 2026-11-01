"""Open M&S README 인포그래픽(애니메이션 SVG) 생성기 — CSS keyframes · SMIL 만, 자바스크립트 · 외부 폰트 없음.

사용: python scripts/make_infographics.py [출력 폴더 — 기본 assets/infographics]
"""
import math
import random
import sys
from pathlib import Path
from xml.sax.saxutils import escape

FONT = "'Segoe UI','Malgun Gothic','Apple SD Gothic Neo','Noto Sans KR',sans-serif"
BG, CARD, LINE, LANE = "#111318", "#1f2430", "#3a4150", "#171b23"
TXT, SUB, DIM = "#f4f4f5", "#a1a1aa", "#71717a"
C = {"blue": "#3b82f6", "cyan": "#06b6d4", "violet": "#a78bfa", "green": "#22c55e",
     "amber": "#f59e0b", "red": "#ef4444", "pink": "#f472b6", "slate": "#94a3b8"}

WARN = []


def tw(s, size):
    """대략의 글자 폭(한글 = 1em, 라틴 = 0.56em)."""
    w = 0.0
    for ch in s:
        o = ord(ch)
        w += size * (1.0 if (0xAC00 <= o <= 0xD7A3 or 0x3130 <= o <= 0x318F) else 0.36 if ch in " .·,:'|il()[]" else 0.58)
    return w


def T(x, y, s, size=14, fill=SUB, weight=None, anchor="start", cls=None, extra=""):
    a = f' text-anchor="{anchor}"' if anchor != "start" else ""
    wt = f' font-weight="{weight}"' if weight else ""
    c = f' class="{cls}"' if cls else ""
    return f'<text x="{x:g}" y="{y:g}" font-size="{size}" fill="{fill}"{wt}{a}{c}{extra}>{escape(s)}</text>'


class Svg:
    def __init__(self, name, w, h, title, subtitle, desc, cycle, steps):
        self.name, self.w, self.h, self.cycle, self.steps = name, w, h, cycle, steps
        self.title, self.subtitle, self.desc = title, subtitle, desc
        self.body = []
        self.css = []

    def add(self, s):
        self.body.append(s)

    def on_pct(self, i):
        """단계 i(0부터)가 켜지는 시점 [%]."""
        return 3 + i * (82 / self.steps)

    def step_css(self):
        out = []
        for i in range(self.steps):
            p = self.on_pct(i)
            q = p + 3
            out.append(f"@keyframes on{i} {{ 0%, {p:.1f}% {{ opacity: 0; }} {q:.1f}%, 94% {{ opacity: 1; }} 100% {{ opacity: 0; }} }}")
            out.append(f".on{i} {{ opacity: 0; animation: on{i} {self.cycle}s infinite; }}")
            # 지금 단계 강조(잠깐 밝게)
            out.append(f"@keyframes now{i} {{ 0%, {p:.1f}% {{ opacity: 0; }} {q:.1f}% {{ opacity: .55; }} {p + 82 / self.steps:.1f}%, 100% {{ opacity: 0; }} }}")
            out.append(f".now{i} {{ opacity: 0; animation: now{i} {self.cycle}s infinite; }}")
        return "\n    ".join(out)

    def box(self, x, y, w, h, title, lines, step, color, tsize=17, ssize=13, badge=None, center=False):
        g = [f'<g transform="translate({x:g} {y:g})">']
        g.append(f'<rect class="now{step}" x="-5" y="-5" width="{w + 10:g}" height="{h + 10:g}" rx="16" fill="{color}"/>')
        g.append(f'<rect width="{w:g}" height="{h:g}" rx="12" fill="{CARD}" stroke="{LINE}" stroke-width="1.5"/>')
        g.append(f'<rect class="on{step}" width="{w:g}" height="{h:g}" rx="12" fill="none" stroke="{color}" stroke-width="2.5"/>')
        g.append(f'<rect class="on{step}" x="0" y="10" width="4" height="{h - 20:g}" rx="2" fill="{color}"/>')
        tx = w / 2 if center else (40 if badge else 16)
        anchor = "middle" if center else "start"
        ty = 26 if lines else h / 2 + 6
        if badge is not None:
            g.append(f'<circle cx="22" cy="21" r="11" fill="{color}" opacity=".9"/>')
            g.append(T(22, 25.5, str(badge), 12, "#0b0d12", 700, "middle"))
        g.append(T(tx, ty, title, tsize, TXT, 700, anchor))
        self.check(title, tsize, w - (tx if not center else 16), x, y)
        sx = 16 if badge is not None else tx
        for k, ln in enumerate(lines):
            g.append(T(sx if not center else w / 2, ty + 21 + k * (ssize + 5), ln, ssize, SUB, None, anchor))
            self.check(ln, ssize, w - (sx if not center else 16) - 8, x, y)
        g.append("</g>")
        self.add("".join(g))

    def check(self, s, size, room, x, y):
        if tw(s, size) > room:
            WARN.append(f"{self.name}: '{s}' 폭 {tw(s, size):.0f} > {room:.0f} (상자 {x},{y})")

    def arrow(self, d, step=None, color="#52525b", flow=True, red=False):
        cls = "flow" if flow else ""
        m = "headR" if red else "head"
        self.add(f'<path class="{cls}" d="{d}" stroke="{color}" stroke-width="2.5" fill="none" marker-end="url(#{m})"/>')
        if step is not None:
            self.add(f'<path class="on{step} flow" d="{d}" stroke="{C["slate"]}" stroke-width="2.5" fill="none"/>')

    def render(self):
        css = f"""
    .flow {{ stroke-dasharray: 7 7; animation: dash 1s linear infinite; }}
    @keyframes dash {{ to {{ stroke-dashoffset: -14; }} }}
    {self.step_css()}
    {' '.join(self.css)}"""
        head = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {self.w} {self.h}" width="{self.w}" height="{self.h}" '
                f'font-family="{FONT}" role="img" aria-labelledby="t d">\n'
                f'  <title id="t">{escape(self.title)}</title>\n  <desc id="d">{escape(self.desc)}</desc>\n'
                f'  <!-- Open M&amp;S README 인포그래픽. 단계가 차례로 켜지고 흐름이 움직인다(CSS keyframes · SMIL, 자바스크립트 없음). 다시 만들기: scripts/make_infographics.py -->\n'
                f'  <style>{css}\n  </style>\n'
                '  <defs>\n'
                '    <marker id="head" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0L10 5L0 10z" fill="#8b8b95"/></marker>\n'
                f'    <marker id="headR" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0L10 5L0 10z" fill="{C["red"]}"/></marker>\n'
                '  </defs>\n'
                f'  <rect width="{self.w}" height="{self.h}" rx="18" fill="{BG}" stroke="#2a2f3a"/>\n'
                f'  {T(28, 38, self.title, 20, TXT, 700)}\n'
                f'  {T(28, 60, self.subtitle, 13.5, SUB)}\n')
        self.check(self.subtitle, 13.5, self.w - 56, 0, 0)
        return head + "\n".join("  " + b for b in self.body) + "\n</svg>\n"


def dot_motion(path, cycle, color="#fafafa", r=6, end=0.85):
    return (f'<circle r="{r}" fill="{color}"><animateMotion dur="{cycle}s" repeatCount="indefinite" path="{path}" '
            f'keyPoints="0;1;1" keyTimes="0;{end};1" calcMode="linear"/>'
            f'<animate attributeName="opacity" values="0;1;1;0;0" keyTimes="0;0.03;{end};{end + 0.04:.2f};1" dur="{cycle}s" repeatCount="indefinite"/></circle>')


# ---------------------------------------------------------------- ① 전체 구조
def architecture():
    s = Svg("architecture", 960, 500, "Open M&S 전체 구조",
            "CAD 모델과 시나리오 JSON 을 넣으면 6DOF 엔진이 계산하고, 필요할 때만 외부 해석기를 불러 결과 · 보고서로 남긴다",
            "입력(Fusion 내보내기 · 시나리오 JSON · 공개 샘플) → 시뮬레이터(6DOF 엔진 · GPU 몬테카를로 · 근사 구조 분석 · 3D 재생) → "
            "외부 해석기(CalculiX · OpenCourant · OpenFOAM · XLB, 선택) → 결과(.mnsr · 결과 폴더 · 보고서 · Blender).", 10, 4)
    cols = [
        ("① 입력", C["amber"], [
            ("Fusion 내보내기", ["STL · massprops.json", "재료 물성 · 외관 · USD"]),
            ("시나리오 JSON", ["물체 · 낙하산 · 이벤트", "바람 · 난류 · 분산"]),
            ("공개 샘플 · 데모", ["합성 STL 샘플", "데모 시나리오 3종"])]),
        ("② 시뮬레이터", C["blue"], [
            ("6DOF 다물체 엔진", ["RK4 · 이벤트 · 분리", "충돌 · 착지 · 관입"]),
            ("GPU 몬테카를로", ["ILGPU(CUDA) 질점 모델", "수만 회 · 착지 분산"]),
            ("구조 분석 (근사)", ["구간 최대 하중 · 안전율", "최초 항복 · 파단 시각"]),
            ("3D 재생 · 대시보드", ["Avalonia + OpenGL", "그래프 · 로그 · 단면"])]),
        ("③ 외부 해석기 (선택)", C["violet"], [
            ("CalculiX", ["정적 · 고유진동 · 좌굴", "모달 과도 (개방 충격)"]),
            ("OpenCourant", ["착지 충돌 양해법", "소성 · 파단"]),
            ("OpenFOAM (Docker)", ["공력 계수 · 표면 압력", "유선 · 유동 영역 규칙"]),
            ("XLB + Warp", ["GPU 가상 풍동", "실시간 연기 · 와류"])]),
        ("④ 결과", C["green"], [
            ("결과 파일 .mnsr", ["계산 없이 다시 열기", "MC 결과까지 함께"]),
            ("결과 폴더", ["CSV 시계열 · events.json", "scene.json · meshes/"]),
            ("보고서 · 신뢰도", ["NASA-STD-7009A 8요인", "신뢰 · 참고 · 추정 등급"]),
            ("Blender", ["결과 폴더 가져오기", "렌더 · 애니메이션"])]),
    ]
    W, GAP = 196, 38
    for i, (hdr, col, boxes) in enumerate(cols):
        x = 24 + i * (W + GAP)
        s.add(f'<rect x="{x - 8}" y="80" width="{W + 16}" height="370" rx="14" fill="{LANE}"/>')
        s.add(T(x + W / 2, 102, hdr, 14, col, 700, "middle"))
        for k, (t, ls) in enumerate(boxes):
            s.box(x, 116 + k * 82, W, 72, t, ls, i, col, tsize=16, ssize=12.5)
    # 열 사이 흐름
    for i in range(3):
        x0 = 24 + i * (W + GAP) + W + 8
        x1 = x0 + GAP - 16
        if i == 1:
            s.arrow(f"M{x0} 180H{x1}", i + 1)
            s.arrow(f"M{x1} 205H{x0}", i + 1)
            s.arrow(f"M{x0} 344H{x1}", i + 1)
            s.arrow(f"M{x1} 369H{x0}", i + 1)
        else:
            for y in (192, 274, 356):
                s.arrow(f"M{x0} {y}H{x1}", i + 1)
    s.add(T(480, 478, "외부 해석기는 별도 프로세스로만 실행한다 — 설치되어 있지 않으면 그 기능만 꺼지고 나머지는 그대로 동작한다", 13, DIM, None, "middle"))
    return s


# ---------------------------------------------------------------- ② 공중투하 이벤트 시퀀스
def bez(p0, p1, p2, p3, t):
    u = 1 - t
    return tuple(u ** 3 * a + 3 * u * u * t * b + 3 * u * t * t * c + t ** 3 * d for a, b, c, d in zip(p0, p1, p2, p3))


def airdrop():
    cyc = 12
    s = Svg("airdrop-sequence", 960, 540, "공중투하 이벤트 시퀀스",
            "시나리오의 events[] 가 시간 · 고도 · 속도 조건으로 다음 단계를 켠다 — 데모 1(수송기 후방 램프 사출)의 흐름",
            "사출(레일) → 덮개 분리 → 드로그 전개 → 감속 조건에서 주낙하산 → 바람 속 하강 · 표류 → 착지 · 충격 · 관입. 단계마다 엔진이 내는 값을 함께 적었다.",
            cyc, 6)
    # 하늘 · 지면
    s.add(f'<rect x="24" y="76" width="912" height="248" rx="12" fill="#141b26"/>')
    s.add(f'<rect x="24" y="300" width="912" height="24" fill="#2b2418"/>')
    s.add(f'<path d="M24 300H936" stroke="#6b5a3a" stroke-width="2"/>')
    for x in range(40, 930, 22):
        s.add(f'<path d="M{x} 304l-8 14" stroke="#4a3f2a" stroke-width="1.5"/>')
    # 바람
    for k, y in enumerate((110, 140, 170)):
        s.add(f'<path class="flow" d="M640 {y}H{760 + 20 * k}" stroke="#334155" stroke-width="2" fill="none" marker-end="url(#head)"/>')
    s.add(T(700, 98, "바람 · 난류 →", 12.5, "#64748b", None, "middle"))
    # 수송기
    s.add('<g transform="translate(200 86) rotate(-6)"><rect width="160" height="24" rx="12" fill="#8a949e"/>'
          '<path d="M2 4 L-4 -20 L12 -20 L34 5z" fill="#6b7480"/><path d="M64 16 L44 34 L58 34 L98 16z" fill="#6b7480"/></g>')
    s.add(T(372, 92, "운반체 (수송기) — 진행 방향 →", 12, "#94a3b8"))
    P = [(205, 112), (330, 118), (420, 210), (560, 250)]
    Q = [(560, 250), (700, 290), (820, 294), (880, 298)]
    path = "M205 112 C330 118 420 210 560 250 C700 290 820 294 880 298"
    s.add(f'<path d="{path}" stroke="#334155" stroke-width="2" stroke-dasharray="3 6" fill="none"/>')
    ts = [0.02, 0.28, 0.55, 0.95, 0.55, 1.0]
    pts = [bez(*P, ts[0]), bez(*P, ts[1]), bez(*P, ts[2]), bez(*P, ts[3]), bez(*Q, ts[4]), bez(*Q, ts[5])]
    cols = [C["amber"], C["slate"], C["cyan"], C["blue"], C["violet"], C["red"]]
    names = ["사출", "분리", "드로그", "주낙하산", "하강", "착지"]
    for i, ((x, y), col, nm) in enumerate(zip(pts, cols, names)):
        s.add(f'<circle class="now{i}" cx="{x:.0f}" cy="{y:.0f}" r="16" fill="{col}"/>')
        s.add(f'<circle cx="{x:.0f}" cy="{y:.0f}" r="5" fill="#475569"/><circle class="on{i}" cx="{x:.0f}" cy="{y:.0f}" r="5" fill="{col}"/>')
        dy = -16 if i in (0, 1) else 26 if i in (2, 3) else -14
        if i == 5:
            dy = -14
        if i in (0, 1):
            dy = 26
        s.add(f'<g class="on{i}">{T(x + (-14 if i == 0 else 0), y + dy, f"{i + 1} {nm}", 12.5, col, 700, "middle")}</g>')
    # 덮개 조각
    x, y = pts[1]
    s.add(f'<rect class="on1" x="{x + 6:.0f}" y="{y - 22:.0f}" width="5" height="14" fill="#c8a050" transform="rotate(25 {x + 8:.0f} {y - 15:.0f})"/>')
    # 드로그 · 주낙하산 캐노피
    x, y = pts[2]
    s.add(f'<g class="on2"><path d="M{x - 11:.0f} {y - 22:.0f} a11 9 0 0 1 22 0z" fill="{C["cyan"]}"/>'
          f'<path d="M{x - 11:.0f} {y - 22:.0f}L{x:.0f} {y - 4:.0f}L{x + 11:.0f} {y - 22:.0f}" stroke="{C["cyan"]}" fill="none"/></g>')
    x, y = pts[3]
    s.add(f'<g class="on3"><path d="M{x - 26:.0f} {y - 44:.0f} a26 18 0 0 1 52 0z" fill="{C["blue"]}"/>'
          f'<path d="M{x - 26:.0f} {y - 44:.0f}L{x:.0f} {y - 6:.0f}L{x + 26:.0f} {y - 44:.0f}" stroke="{C["blue"]}" fill="none"/></g>')
    # 착지 충격 물결
    x, y = pts[5]
    s.add(f'<ellipse class="on5" cx="{x:.0f}" cy="306" rx="22" ry="4" fill="#7c2d12" opacity=".8"/>')
    s.add(f'<circle cx="{x:.0f}" cy="{y:.0f}" r="4" fill="none" stroke="{C["red"]}" stroke-width="2" opacity="0">'
          f'<animate attributeName="r" values="4;4;30;30" keyTimes="0;0.84;0.94;1" dur="{cyc}s" repeatCount="indefinite"/>'
          f'<animate attributeName="opacity" values="0;0;.9;0;0" keyTimes="0;0.84;0.85;0.94;1" dur="{cyc}s" repeatCount="indefinite"/></circle>')
    # 움직이는 물체
    s.add(dot_motion(path, cyc, "#fafafa", 6, 0.86))
    # 아래 설명 카드
    cards = [
        ("사출", ["레일을 미끄러져 나감 (마찰 · 밀어내기)", "값: 레일 이탈 시각 · 사출 속도"]),
        ("분리", ["덮개가 정해진 상대 속도로 떨어짐", "값: 분리 시각 · 물체 사이 이격"]),
        ("드로그 전개", ["라인 신장 → 충만 곡선 → 감속", "값: 개방하중 피크 [N] · 감속도 [G]"]),
        ("주낙하산", ["속도 조건(예 35 m/s 이하)에서 교체", "조건이 안 맞으면 시간 백업으로 전개"]),
        ("하강 · 표류", ["평균풍 + Dryden 난류에 밀려 내려옴", "값: 하강률 · 표류 거리 · 흔들림 각"]),
        ("착지 · 충격 · 관입", ["지면 충돌 → 재질 판정 · 토양 관입", "값: 충돌 속도 · 자세 · 관입 깊이 · 손상"]),
    ]
    for i, ((t, ls), col) in enumerate(zip(cards, cols)):
        x = 24 + (i % 3) * 308
        y = 340 + (i // 3) * 98
        s.box(x, y, 296, 86, t, ls, i, col, tsize=16, ssize=13, badge=i + 1)
    return s


# ---------------------------------------------------------------- ③ 몬테카를로 · 신뢰도
def monte_carlo():
    cyc = 12
    s = Svg("monte-carlo", 960, 520, "몬테카를로 · 분산 · 결과 신뢰도",
            "dispersions[] 로 흔들 값을 정하면 수백(CPU 6DOF) ~ 수만(GPU) 번 돌려 착지 분산과 신뢰 구간을 낸다",
            "시나리오 + dispersions → LHS 표본 → CPU 6DOF 또는 GPU 질점 병렬 실행 → CEP · 95 % 타원 · 고장 비율 → "
            "NASA-STD-7009A 8요인 채점과 신뢰 · 참고 · 추정 등급, 90 % 구간, 시험 우선순위.", cyc, 5)
    steps = [
        ("분산 정의", ["dispersions[] 경로 · 분포", "풍속 · Cd · 질량 · 지연"], C["amber"]),
        ("표본 추출", ["LHS(라틴 하이퍼큐브)", "시드로 같은 결과 재현"], C["cyan"]),
        ("병렬 실행", ["CPU: 매회 전체 6DOF", "GPU: 질점 (ILGPU)"], C["blue"]),
        ("통계", ["CEP(50 %) · 95 % 타원", "고장 비율 · 착지 분산"], C["green"]),
        ("신뢰도", ["NASA-STD-7009A 8요인", "90 % 구간 · 등급"], C["violet"]),
    ]
    for i, (t, ls, col) in enumerate(steps):
        x = 24 + i * 186
        s.box(x, 78, 172, 84, t, ls, i, col, tsize=16, ssize=11.5, badge=i + 1)
        if i < 4:
            s.arrow(f"M{x + 172} 120H{x + 184}", i + 1)
    # 착지 산포 패널
    s.add(f'<rect x="24" y="182" width="430" height="314" rx="12" fill="{LANE}"/>')
    s.add(T(40, 206, "착지 산포 (위에서 본 지면)", 14, TXT, 700))
    cx, cy = 140, 400
    s.add(f'<path d="M40 {cy}H440M{cx} 220V485" stroke="#2a3140" stroke-width="1"/>')
    s.add(f'<path d="M{cx - 8} {cy}H{cx + 8}M{cx} {cy - 8}V{cy + 8}" stroke="{TXT}" stroke-width="2"/>')
    s.add(T(cx - 12, cy + 22, "목표점", 11.5, DIM, None, "end"))
    rnd = random.Random(7)
    mx, my, ang = 260, 330, math.radians(-20)
    dots = []
    for k in range(70):
        u, v = rnd.gauss(0, 55), rnd.gauss(0, 24)
        x = mx + u * math.cos(ang) - v * math.sin(ang)
        y = my + u * math.sin(ang) + v * math.cos(ang)
        if 40 < x < 440 and 220 < y < 485:
            dots.append((x, y))
    p2, p3 = s.on_pct(2), s.on_pct(3)
    for k, (x, y) in enumerate(dots):
        t0 = (p2 + (k / len(dots)) * (p3 - p2)) / 100
        fail = k % 9 == 4
        col = C["red"] if fail else "#cbd5e1"
        s.add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="3" fill="{col}" opacity="0"><animate attributeName="opacity" values="0;0;.95;.95;0" '
              f'keyTimes="0;{t0:.3f};{t0 + 0.01:.3f};0.94;1" dur="{cyc}s" repeatCount="indefinite"/></circle>')
    s.add(f'<g class="on3"><circle cx="{mx}" cy="{my}" r="46" fill="none" stroke="{C["green"]}" stroke-width="2.5"/>'
          f'<ellipse cx="{mx}" cy="{my}" rx="135" ry="59" transform="rotate(-20 {mx} {my})" fill="none" stroke="{C["red"]}" stroke-width="2" stroke-dasharray="6 5"/>'
          f'{T(mx - 60, my - 52, "CEP (50 % 원)", 12, C["green"], 700, "end")}'
          f'{T(60, 470, "95 % 타원", 12, C["red"], 700)}</g>')
    s.add(f'<path class="flow" d="M60 240H140" stroke="#334155" stroke-width="2" fill="none" marker-end="url(#head)"/>')
    s.add(T(60, 232, "바람", 11.5, "#64748b"))
    s.add(f'<circle cx="300" cy="474" r="3" fill="{C["red"]}"/>' + T(310, 478, "낙하산 고장(주입) 실행", 11.5, DIM))
    # 신뢰도 패널
    X = 474
    s.add(f'<rect x="{X}" y="182" width="462" height="314" rx="12" fill="{LANE}"/>')
    s.add(T(X + 16, 206, "결과 신뢰도 (NASA-STD-7009A) — 예시 화면", 14, TXT, 700))
    rows = [("착지 거리", "신뢰", C["green"], 0.35, 0.62), ("개방하중 피크", "참고", C["amber"], 0.28, 0.74),
            ("충돌 속도", "신뢰", C["green"], 0.44, 0.58), ("관입 깊이", "추정", C["red"], 0.15, 0.9)]
    for k, (nm, grade, col, a, b) in enumerate(rows):
        y = 236 + k * 40
        s.add(f'<rect x="{X + 16}" y="{y - 14}" width="44" height="22" rx="11" fill="none" stroke="{LINE}"/>' + T(X + 38, y + 1.5, grade, 12, DIM, None, "middle"))
        s.add(f'<g class="on4"><rect x="{X + 16}" y="{y - 14}" width="44" height="22" rx="11" fill="{col}"/>'
              f'{T(X + 38, y + 1.5, grade, 12, "#0b0d12", 700, "middle")}</g>')
        s.add(T(X + 72, y + 1, nm, 13, TXT))
        bx, bw = X + 190, 250
        s.add(f'<rect x="{bx}" y="{y - 4}" width="{bw}" height="4" rx="2" fill="#2a3140"/>')
        s.add(f'<rect class="on4" x="{bx + a * bw:.0f}" y="{y - 7}" width="{(b - a) * bw:.0f}" height="10" rx="5" fill="{col}" opacity=".75"/>')
        s.add(f'<circle class="on4" cx="{bx + (a + b) / 2 * bw:.0f}" cy="{y - 2}" r="4" fill="{TXT}"/>')
    s.add(T(X + 190, 395, "막대 = 90 % 구간 · 점 = 공칭값", 11.5, DIM))
    facts = "8요인: 개발 자료 출처 · 검증 · 확인 · 입력 출처 ·", "불확도 특성화 · 결과 강건성(민감도) · 사용 이력 · M&S 관리"
    s.add(T(X + 16, 425, facts[0], 12, SUB))
    s.add(T(X + 16, 443, facts[1], 12, SUB))
    s.add(f'<g class="on4">{T(X + 16, 474, "시험 우선순위 — 신뢰도를 가장 많이 올리는 시험부터 순서대로", 12.5, C["violet"], 700)}</g>')
    return s


# ---------------------------------------------------------------- ④ 구조 · 충격 해석
def structural():
    cyc = 11
    s = Svg("structural-pipeline", 960, 470, "구조 · 충격 해석 파이프라인",
            "비행 중 하중을 뽑아 메시를 만들고 외부 해석기로 풀어 안전율 · 고유진동 · 피로로 판정한다 (빠른 근사 F6 는 바로 판정)",
            "하중 케이스(구간 최대 · 개방 충격 · 착지) → 메시(Gmsh 사면체) → CalculiX(정적 · 고유진동 · 좌굴 · 모달 과도) 또는 OpenCourant(착지 충돌) → "
            "안전율 · 99 % 응력 · 고유진동 · 진동 피로 → 3D 응력 색 · 보고서.", cyc, 5)
    steps = [
        ("하중 케이스", ["구간 최대 · 개방 충격", "착지 충돌 가속도 [G]"], C["amber"]),
        ("메시", ["Gmsh 사면체 (STEP/STL)", "두께 2요소 규칙"], C["cyan"]),
        ("외부 해석기", ["CalculiX 정적 · 진동 · 좌굴", "OpenCourant 충돌 · 파단"], C["violet"]),
        ("판정", ["안전율 · 99 % 응력", "고유진동 · 진동 피로"], C["green"]),
        ("표시 · 보고서", ["3D 응력 색 · 변형 배율", "report.md · result.vtu"], C["blue"]),
    ]
    for i, (t, ls, col) in enumerate(steps):
        x = 24 + i * 186
        s.box(x, 150, 172, 84, t, ls, i, col, tsize=16, ssize=11.5, badge=i + 1)
        if i < 4:
            s.arrow(f"M{x + 172} 192H{x + 184}", i + 1)
    # 빠른 근사 경로
    s.add(f'<path class="flow" d="M109 146V108H668V142" stroke="{C["amber"]}" stroke-width="2" fill="none" marker-end="url(#head)" opacity=".8"/>')
    s.add(f'<rect x="198" y="94" width="380" height="28" rx="14" fill="{BG}"/>')
    s.add(T(388, 113, "F6 구조 분석 (근사) — 보 · 박판 응력, 외부 도구 없이 몇 초", 12.5, "#fcd34d", 700, "middle"))
    s.check("F6 구조 분석 (근사) — 보 · 박판 응력, 외부 도구 없이 몇 초", 12.5, 380, 198, 94)
    # 응력 색 막대
    s.add('<defs><linearGradient id="vm" x1="0" x2="1"><stop offset="0" stop-color="#2563eb"/><stop offset=".35" stop-color="#22c55e"/>'
          '<stop offset=".65" stop-color="#eab308"/><stop offset="1" stop-color="#dc2626"/></linearGradient></defs>')
    s.add(f'<rect class="on4" x="784" y="240" width="170" height="8" rx="4" fill="url(#vm)"/>')
    s.add(f'<g class="on4">{T(784, 262, "낮음", 11, DIM)}{T(954, 262, "항복 근접", 11, DIM, None, "end")}</g>')
    # 세부 카드
    cards = [
        ("CalculiX (F7)", ["정적 · 고유진동 · 좌굴 — 선택 물체", "모달 과도 — 개방 충격 창의 가속도 이력", "값: von Mises · 변위 · 고유진동수 [Hz]"], C["violet"], 2),
        ("OpenCourant (착지 충돌)", ["착지 순간을 양해법으로 짧게 계산", "소성 변형 · 파단 요소 · 접촉", "값: 최대 응력 · 소성 변형률 · 손상"], C["pink"], 2),
        ("진동 피로 (근사)", ["와류 흘림 f_s 와 고유진동 f_n 비교", "f_s / f_n 이 0.8 넘으면 공진 근접", "공개 S-N 곡선 · Miner 손상 D"], C["green"], 3),
    ]
    for k, (t, ls, col, st) in enumerate(cards):
        x = 24 + k * 308
        s.box(x, 284, 296, 108, t, ls, st, col, tsize=15.5, ssize=12.5)
    s.add(T(480, 426, "판정 기준: 안전율 = 허용 응력 / 응력. 최대값은 모서리 특이점에 민감해 99 % 백분위 응력과 메시 수렴(굵게 · 국부 절반)을 함께 본다", 12.5, DIM, None, "middle"))
    s.add(T(480, 448, "재질은 재질 라이브러리 · Fusion 재료 물성 · 시나리오 지정 순으로 우선한다", 12.5, DIM, None, "middle"))
    return s


# ---------------------------------------------------------------- ⑤ 자동 업데이트
def auto_update():
    cyc = 12
    s = Svg("auto-update", 960, 420, "자동 업데이트",
            "설치판은 GitHub 릴리스를 스스로 확인하고, 사용자가 고르면 내려받아 검증한 뒤 조용히 설치하고 다시 켠다",
            "시작 5초 뒤 하루 1회 확인 → GitHub 릴리스의 OpenMS-Setup-x.y.z.exe 찾기 → 새 버전 대화상자 → 내려받기 · SHA-256 검증 → "
            "/SILENT 설치 → 새 버전으로 재실행.", cyc, 6)
    steps = [
        ("시작 5초 뒤 확인", ["하루 1회 자동 (끌 수 있음)", "도움말 → 업데이트 확인… 은 언제나"], C["slate"]),
        ("GitHub 릴리스", ["최신 정식 태그 (초안 · 시험판 제외)", "자산 OpenMS-Setup-x.y.z.exe · .sha256"], C["cyan"]),
        ("새 버전 대화상자", ["현재 · 새 버전 · 릴리스 노트", "[지금 업데이트] [나중에] [건너뛰기]"], C["amber"]),
        ("내려받기 · 검증", ["진행 막대 · 실패 시 [다시 시도]", "SHA-256 불일치 → 파일 삭제 · 거부"], C["blue"]),
        ("조용히 설치", ["설치 프로그램 /SILENT 실행 · 앱 닫힘", "설정 · results 폴더는 그대로"], C["violet"]),
        ("재실행", ["설치가 끝나면 새 버전이 다시 뜸", "다시 확인하면 \"최신 버전입니다\""], C["green"]),
    ]
    for i, (t, ls, col) in enumerate(steps):
        x = 24 + (i % 3) * 308
        y = 80 + (i // 3) * 122
        s.box(x, y, 284, 92, t, ls, i, col, tsize=16, ssize=12.5, badge=i + 1)
        if i % 3 < 2:
            s.arrow(f"M{x + 286} {y + 46}H{x + 306}", i + 1)
    s.arrow("M790 176V188H166V198", 3)
    # 진행 막대
    b0, b1 = s.on_pct(3) / 100, s.on_pct(4) / 100
    s.add(f'<rect x="64" y="286" width="240" height="6" rx="3" fill="#2a3140"/><rect x="64" y="286" width="0" height="6" rx="3" fill="{C["blue"]}">'
          f'<animate attributeName="width" values="0;0;240;240;0" keyTimes="0;{b0:.3f};{b1:.3f};0.94;1" dur="{cyc}s" repeatCount="indefinite"/></rect>')
    s.add(f'<rect x="24" y="324" width="912" height="72" rx="12" fill="{LANE}"/>')
    s.add(T(44, 350, "조용히 넘기는 경우", 13.5, TXT, 700))
    s.add(T(44, 372, "오프라인 · 시간 초과 · 404(릴리스 없음) · 403/429(요청 한도)는 대화상자 없이 로그만 남기고 다음 시작 때 다시 확인한다.", 12.5, SUB))
    s.add(T(44, 389, "개발 빌드(버전 0.0.0)는 확인하지 않는다 · 건너뛴 버전은 자동 확인에서만 숨긴다 · 설치 위치 %LOCALAPPDATA%\\Programs\\OpenMS (관리자 권한 불필요)", 12.5, SUB))
    s.check("개발 빌드(버전 0.0.0)는 확인하지 않는다 · 건너뛴 버전은 자동 확인에서만 숨긴다 · 설치 위치 %LOCALAPPDATA%\\Programs\\OpenMS (관리자 권한 불필요)", 12.5, 880, 44, 389)
    return s


# ---------------------------------------------------------------- ⑥ 장면 환경
def scene_env():
    cyc = 12
    s = Svg("scene-environment", 960, 440, "장면 환경 — 하늘 · 지면 · 조명",
            "환경 파일 하나가 하늘 돔 · 지면 무늬 · 태양을 정한다. '자동' 은 시나리오 토양(environment.soil)에 맞는 환경을 고른다",
            "config/environments/<id>/environment.json → 하늘 돔(절차적 하늘) · 지면 텍스처 · 태양 방향광과 그림자 → 시나리오 토양으로 자동 선택 → "
            "3D 화면. 해석 결과는 바뀌지 않고 그림만 바뀐다.", cyc, 4)
    s.box(24, 84, 220, 96, "환경 파일", ["config/environments/<id>/", "environment.json (형식 /1)", "내장 7종 + 사용자 .hdr · .png"], 0, C["amber"], tsize=16, ssize=11.5, badge=1)
    parts = [("하늘 돔", ["절차적 하늘 · 지평선 나무 실루엣"]), ("지면", ["풀 · 흙 · 모래 · 암반 · 눈 · 낙엽 무늬"]), ("조명", ["태양 방향광 · 그림자 · 반구 환경광"])]
    for k, (t, ls) in enumerate(parts):
        s.box(282, 78 + k * 70, 250, 60, t, ls, 1, C["cyan"], tsize=15, ssize=12)
    s.arrow("M246 132H278", 1)
    s.arrow("M534 150H566", 3)
    # 토양 자동 선택 칩
    s.add(f'<rect x="24" y="300" width="508" height="116" rx="12" fill="{LANE}"/>')
    s.add(f'<g>{T(40, 324, "③ 자동 선택 — environment.soil → 환경", 14, TXT, 700)}</g>')
    chips = [("sand", "모래", "#d8c48e"), ("clay", "흙", "#8a6a48"), ("frozen", "눈", "#e8eef5"), ("rock", "암반", "#8b8f94"), ("grass · farmland · 그 밖", "풀밭", "#6b8f3a")]
    x = 40
    for k, (soil, env, col) in enumerate(chips):
        w = tw(f"{soil} → {env}", 12) + 36
        y = 338 if k < 3 else 374
        if k == 3:
            x = 40
        s.add(f'<g><rect x="{x}" y="{y}" width="{w:.0f}" height="30" rx="8" fill="{CARD}" stroke="{LINE}"/>'
              f'<rect class="on2" x="{x}" y="{y}" width="{w:.0f}" height="30" rx="8" fill="none" stroke="{col}" stroke-width="2"/>'
              f'<rect x="{x + 8}" y="{y + 10}" width="10" height="10" rx="2" fill="{col}"/>'
              f'{T(x + 24, y + 19.5, f"{soil} → {env}", 12, SUB)}</g>')
        x += w + 12
    s.add(T(x + 8, 393, "데모 1 모래 · 2 흙 · 3 눈", 12, DIM))
    s.check("데모 1 모래 · 2 흙 · 3 눈", 12, 520 - x - 8, x, 393)
    # 미리보기
    X, Y, W, H = 570, 78, 366, 338
    s.add(f'<defs><clipPath id="pv"><rect x="{X}" y="{Y}" width="{W}" height="{H}" rx="12"/></clipPath>'
          f'<linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#3f73c6"/><stop offset="1" stop-color="#b8c9dc"/></linearGradient></defs>')
    s.add(f'<g clip-path="url(#pv)"><rect x="{X}" y="{Y}" width="{W}" height="{H}" fill="url(#sky)"/>')
    # 해
    s.add(f'<circle r="16" fill="#fde68a"><animateMotion dur="{cyc}s" repeatCount="indefinite" path="M{X + 40} {Y + 120} Q{X + 180} {Y + 10} {X + 330} {Y + 110}"/></circle>')
    # 나무 실루엣
    tree = "".join(f'<path d="M{X + tx} {Y + 210}l9 -26l9 26z" fill="#2c3e2a"/>' for tx in range(4, W, 22))
    s.add(f'<g opacity=".55">{tree}</g>')
    # 지면: 세 데모 토양 색이 바뀐다(SMIL)
    s.add(f'<rect x="{X}" y="{Y + 210}" width="{W}" height="{H - 210}" fill="#d8c48e">'
          f'<animate attributeName="fill" values="#d8c48e;#8a6a48;#e8eef5;#d8c48e" keyTimes="0;0.333;0.666;1" calcMode="discrete" dur="{cyc}s" repeatCount="indefinite"/></rect>')
    for k in range(6):
        y = Y + 228 + k * 18
        s.add(f'<path d="M{X} {y} q45 -6 90 0 t90 0 t90 0 t96 0" stroke="#000" stroke-opacity=".12" stroke-width="2" fill="none"/>')
    # 물체와 그림자
    s.add(f'<ellipse cx="{X + 200}" cy="{Y + 282}" rx="34" ry="7" fill="#000" opacity=".3">'
          f'<animate attributeName="cx" values="{X + 214};{X + 186};{X + 214}" dur="{cyc}s" repeatCount="indefinite"/></ellipse>')
    s.add(f'<rect x="{X + 170}" y="{Y + 252}" width="60" height="24" rx="12" fill="#6f8f5a"/>')
    s.add('</g>')
    labels = ["데모 1 · 모래", "데모 2 · 흙", "데모 3 · 눈"]
    for k, lb in enumerate(labels):
        a, b = k / 3, (k + 1) / 3
        vals = "0;1;1;0;0" if k == 0 else "0;0;1;1;0;0" if k < 2 else "0;0;1;1"
        kt = f"0;0.01;{b - 0.01:.3f};{b:.3f};1" if k == 0 else f"0;{a:.3f};{a + 0.001:.3f};{b - 0.001:.3f};{b:.3f};1" if k < 2 else f"0;{a:.3f};{a + 0.001:.3f};1"
        s.add(f'<g opacity="0"><animate attributeName="opacity" values="{vals}" keyTimes="{kt}" dur="{cyc}s" repeatCount="indefinite"/>'
              f'<rect x="{X + 12}" y="{Y + 12}" width="120" height="26" rx="13" fill="#0b0d12" opacity=".7"/>{T(X + 72, Y + 30, lb, 12.5, TXT, 700, "middle")}</g>')
    s.add(f'<rect class="on3" x="{X}" y="{Y}" width="{W}" height="{H}" rx="12" fill="none" stroke="{C["green"]}" stroke-width="2.5"/>')
    s.add(f'<rect x="{X}" y="{Y}" width="{W}" height="{H}" rx="12" fill="none" stroke="{LINE}" stroke-width="1"/>')
    s.add(T(X + W - 12, Y + H - 12, "④ 3D 화면 (해석 결과는 그대로)", 12, "#0b0d12", 700, "end"))
    s.add(T(282, 292, "밝기 · 노출 · 회전 · 반사 · 그림자 · 안개 — 보기 메뉴", 11.5, DIM))
    s.check("밝기 · 노출 · 회전 · 반사 · 그림자 · 안개 — 보기 메뉴", 11.5, 270, 282, 292)
    return s


# ---------------------------------------------------------------- ⑦ Fusion 해석 패키지 교차 검증
def fusion_crosscheck():
    cyc = 10
    s = Svg("fusion-crosscheck", 960, 420, "Fusion 해석 패키지 교차 검증",
            "Fusion 의 구조 해석 패키지를 그대로 다시 풀어 같은 조건에서 Fusion 결과와 나란히 비교한다 (mns simpkg)",
            "Fusion 해석 패키지(.adsksimpkg) → mns simpkg 가 바디별 형상 · 재료 · 하중 · 경계를 복원 → CalculiX 로 정적 · 고유진동 · 메시 수렴을 다시 풂 → "
            "Fusion 보고서 · 범례 값과 반력 · 안전율 · 응력을 비교한 report.md.", cyc, 4)
    steps = [
        ("Fusion 해석 패키지", [".adsksimpkg (zip + JSON)", "형상 · 재료 · 하중 · 경계 · 접촉"], C["amber"]),
        ("mns simpkg", ["바디별 STEP 과 이름 맞추기", "재료 · 질량 이관 · 한글 이름 복원"], C["cyan"]),
        ("CalculiX 재풀이", ["정적 (+ --modal 고유진동)", "--converge 메시 수렴"], C["violet"]),
        ("비교 보고서", ["Fusion 보고서 · 범례와 대조", "report.md · summary.json"], C["green"]),
    ]
    for i, (t, ls, col) in enumerate(steps):
        x = 24 + i * 232
        s.box(x, 80, 212, 90, t, ls, i, col, tsize=16, ssize=11.5, badge=i + 1)
        if i < 3:
            s.arrow(f"M{x + 214} 125H{x + 230}", i + 1)
    # 비교표
    X, Y = 24, 196
    s.add(f'<rect x="{X}" y="{Y}" width="912" height="200" rx="12" fill="{LANE}"/>')
    hdr = ["비교 항목", "Fusion", "재현 (CalculiX)", "확인"]
    xs = [X + 20, X + 290, X + 420, X + 580]
    for h, x in zip(hdr, xs):
        s.add(T(x, Y + 28, h, 13.5, TXT, 700))
    rows = [("반력 = 모델 질량 × 하중 가속도", "보고서", "합 반력", "오차 % — 하중 재현부터 확인"),
            ("최소 안전율 (최대 응력 기준)", "범례 · 요약 표", "바디별 최대", "모서리 특이점 — 메시 민감"),
            ("99 % 백분위 응력 · 안전율", "—", "바디별 99 %", "메시 수렴 ±% 로 판단"),
            ("고유진동수 1~6차", "—", "--modal", "모드별 주 변위 바디")]
    for k, (a, b, c, d) in enumerate(rows):
        y = Y + 60 + k * 34
        s.add(f'<path d="M{X + 16} {y - 22}H{X + 896}" stroke="#262c38"/>')
        s.add(T(xs[0], y, a, 13, SUB))
        s.add(T(xs[1], y, b, 13, SUB))
        s.add(f'<g class="on{2 if k < 3 else 2}">{T(xs[2], y, c, 13, C["violet"])}</g>')
        s.add(f'<g class="on3">{T(xs[3], y, "✓ " + d, 12.5, C["green"])}</g>')
        s.check("✓ " + d, 12.5, 312, xs[3], y)
    s.add(T(X + 20, Y + 190, "경계 · 지지 조건이 다르면 같은 값을 기대하지 않는다 — 차이 원인(경계 · 메시 · 판정 지표 · 허용값)을 표로 남긴다", 12, DIM))
    return s


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "assets" / "infographics"
    out.mkdir(parents=True, exist_ok=True)
    for f in (architecture, airdrop, monte_carlo, structural, auto_update, scene_env, fusion_crosscheck):
        s = f()
        (out / f"{s.name}.svg").write_text(s.render(), encoding="utf-8", newline="\n")
        print("wrote", s.name)
    for w in WARN:
        print("WARN", w)


if __name__ == "__main__":
    main()
