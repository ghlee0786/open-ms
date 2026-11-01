"""Calibration of the FR-UAV-02 v2 descent-thrust model (Sim.Core RotorModel) on the UIUC VRS data.

Model (same as src/Sim.Core/Models/RotorModel.cs):
  CT/CT0(J) = BET(J)/CT0 * g(x)/g(0),  x = |J|/sqrt(2 CT0/pi),  g(x) = 1 - D exp(-((x - xc)/w)^2)
  BET: fixed-pitch uniform-inflow blade element, CT_r = (sigma a/2)(theta/3 - lambda/2), theta from hover CT0,
       induced velocity: momentum theory (climb/hover, windmill x<=-2), total inflow vh(1+x) in between.

Fitted parameters (a_eff, D, xc, w) minimise the summed squared RMS of the calibration propellers
(TE10x5, SF9x4.7, SP10x5); the remaining propellers in validation/public/rotor_vrs_uiuc are validation.
Usage: python tools/validation/fit_rotor_vrs.py [validation/public/rotor_vrs_uiuc/case.json]
"""
import json
import math
import os
import sys

CALIBRATION = ("TE10x5", "SF9x4.7", "SP10x5")


def induced_ratio(x):
    if x >= 0:
        return -x / 2 + math.sqrt(x * x / 4 + 1)
    if x <= -2:
        return -x / 2 - math.sqrt(x * x / 4 - 1)
    return 1.0


def bet_ct(j, sigma, theta, a):
    lc, ct = j / math.pi, 0.01
    for _ in range(200):
        lh = math.sqrt(max(ct, 1e-9) / 2)
        li = lh * induced_ratio(lc / lh)
        ct = 0.5 * ct + 0.5 * (sigma * a / 2 * (theta / 3 - (lc + li) / 2))
    return ct


def theta_for_hover(ct0r, sigma, a):
    return 3 * (ct0r / (sigma * a / 2) + math.sqrt(ct0r / 2) / 2)


def base_ratio(j, sigma, ct0, a):
    ct0r = ct0 * 4 / math.pi ** 3
    th = theta_for_hover(ct0r, sigma, a)
    return bet_ct(j, sigma, th, a) / bet_ct(0.0, sigma, th, a)


def dip(x, d, xc, w):
    g = lambda y: 1 - d * math.exp(-((y - xc) / w) ** 2)
    return g(x) / g(0)


def ct_ratio(j, sigma, ct0, a, d, xc, w):
    x = abs(j) / math.sqrt(2 * ct0 / math.pi)
    return base_ratio(j, sigma, ct0, a) * dip(x, d, xc, w)


def frange(a, b, s):
    n = int(round((b - a) / s))
    return [a + k * s for k in range(n + 1)]


def fit(curves):
    cal = [c for c in curves if c["label"] in CALIBRATION]
    best = None
    for a in frange(2.0, 4.5, 0.25):
        bases = [[(abs(j) / math.sqrt(2 * c["ct0"] / math.pi), r, base_ratio(j, c["solidity"], c["ct0"], a)) for j, r in c["data"]] for c in cal]
        for d in frange(0.0, 0.6, 0.02):
            for xc in frange(0.9, 1.4, 0.05):
                for w in frange(0.2, 1.2, 0.05):
                    s = 0.0
                    for rows in bases:
                        s += sum((r - b * dip(x, d, xc, w)) ** 2 for x, r, b in rows) / len(rows)
                    if best is None or s < best[0]:
                        best = (s, a, d, xc, w)
    return best[1:]


def load_curves(path):
    with open(path, encoding="utf-8") as f:
        c = json.load(f)
    return [{"label": p["label"], **p["inputs"]} for p in c["points"]]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "validation", "public", "rotor_vrs_uiuc", "case.json")
    curves = load_curves(path)
    a, d, xc, w = fit(curves)
    print(f"a_eff={a:.4g} D={d:.4g} xc={xc:.4g} w={w:.4g}")
    for c in curves:
        pred = [ct_ratio(j, c["solidity"], c["ct0"], a, d, xc, w) for j, _ in c["data"]]
        rms = math.sqrt(sum((p - r) ** 2 for p, (_, r) in zip(pred, c["data"])) / len(pred))
        role = "cal" if c["label"] in CALIBRATION else "val"
        print(f"{c['label']:<10} {role} RMS {rms:.4f}")


if __name__ == "__main__":
    main()
