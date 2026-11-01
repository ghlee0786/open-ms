"""GPU 가상 풍동 IPC 형식(앱 C# ↔ 파이썬 서버, 로컬 TCP 127.0.0.1) — schema mns.airflow-ipc/1.

메시지 = [u32 머리 길이 H][u32 본문 길이 P][머리 JSON UTF-8 H 바이트][본문 P 바이트], 정수는 little-endian.
머리 "arrays": [{"name", "dtype": "f4"|"i4"|"u1", "shape": [...], "offset", "nbytes"}] — 본문 안 배열 위치(C 순서).
C# 쪽 같은 형식: src/Sim.Core/Airflow/AirflowProtocol.cs.

앱 → 서버: {"cmd": "config", ...전체 설정} | {"cmd": "quit"}
서버 → 앱: {"type": "hello"} | {"type": "status", "phase", "message"} | {"type": "frame", ...} | {"type": "error", "message"}
"""

from __future__ import annotations

import json
import struct

import numpy as np

SCHEMA = "mns.airflow-ipc/1"
_DT = {"f4": np.float32, "i4": np.int32, "u1": np.uint8}
MAX_HEADER = 1 << 20
MAX_PAYLOAD = 1 << 30


def pack(header: dict, arrays: dict[str, np.ndarray] | None = None) -> bytes:
    h = dict(header)
    blobs, meta, off = [], [], 0
    for name, a in (arrays or {}).items():
        a = np.asarray(a)
        if a.dtype == bool or a.dtype == np.uint8:
            code = "u1"
        elif a.dtype.kind == "f":
            code = "f4"
        elif a.dtype.kind in "iu":
            code = "i4"
        else:
            raise TypeError(f"배열 {name}: 지원하지 않는 dtype {a.dtype}")
        b = np.ascontiguousarray(a, dtype=np.dtype(_DT[code]).newbyteorder("<")).tobytes()
        meta.append({"name": name, "dtype": code, "shape": list(a.shape), "offset": off, "nbytes": len(b)})
        blobs.append(b)
        off += len(b)
    h["arrays"] = meta
    hb = json.dumps(h, ensure_ascii=False, separators=(",", ":"), allow_nan=False, default=_json_default).encode("utf-8")
    return struct.pack("<II", len(hb), off) + hb + b"".join(blobs)


def _json_default(o):
    if isinstance(o, np.generic):
        return o.item()
    if isinstance(o, np.ndarray):
        return o.tolist()
    raise TypeError(f"JSON 으로 못 바꿈: {type(o)}")


def unpack(buf: bytes) -> tuple[dict, dict[str, np.ndarray]]:
    if len(buf) < 8:
        raise ValueError("메시지가 8 바이트보다 짧습니다")
    hl, pl = struct.unpack_from("<II", buf, 0)
    if len(buf) != 8 + hl + pl:
        raise ValueError(f"메시지 길이 불일치: {len(buf)} != 8 + {hl} + {pl}")
    return decode(buf[8:8 + hl], buf[8 + hl:])


def decode(header_bytes: bytes, payload: bytes) -> tuple[dict, dict[str, np.ndarray]]:
    h = json.loads(header_bytes.decode("utf-8"))
    arrays = {}
    for m in h.get("arrays", []):
        dt = np.dtype(_DT[m["dtype"]]).newbyteorder("<")
        o, n = int(m["offset"]), int(m["nbytes"])
        if o < 0 or o + n > len(payload):
            raise ValueError(f"배열 {m['name']} 이 본문 밖을 가리킵니다")
        arrays[m["name"]] = np.frombuffer(payload[o:o + n], dtype=dt).reshape(m["shape"])
    return h, arrays


def _recv_exact(sock, n: int) -> bytes:
    parts, got = [], 0
    while got < n:
        b = sock.recv(min(n - got, 1 << 20))
        if not b:
            raise ConnectionError("연결이 닫혔습니다")
        parts.append(b)
        got += len(b)
    return b"".join(parts)


def recv(sock) -> tuple[dict, dict[str, np.ndarray]]:
    hl, pl = struct.unpack("<II", _recv_exact(sock, 8))
    if hl > MAX_HEADER or pl > MAX_PAYLOAD:
        raise ValueError(f"메시지가 너무 큽니다(머리 {hl}, 본문 {pl})")
    return decode(_recv_exact(sock, hl), _recv_exact(sock, pl) if pl else b"")


def send(sock, header: dict, arrays: dict[str, np.ndarray] | None = None):
    sock.sendall(pack(header, arrays))


def read_stl(path: str) -> np.ndarray:
    """STL(이진·ASCII) → 삼각형 수프 정점 (3T, 3) float64."""
    with open(path, "rb") as f:
        data = f.read()
    if len(data) >= 84:
        n = struct.unpack_from("<I", data, 80)[0]
        if 84 + 50 * n == len(data):
            rec = np.frombuffer(data, dtype=np.dtype([("n", "<f4", 3), ("v", "<f4", (3, 3)), ("a", "<u2")]), count=n, offset=84)
            return rec["v"].reshape(-1, 3).astype(np.float64)
    text = data.decode("ascii", errors="ignore")
    vs = [list(map(float, ln.split()[1:4])) for ln in text.splitlines() if ln.strip().startswith("vertex")]
    if not vs or len(vs) % 3:
        raise ValueError(f"STL 을 읽지 못했습니다: {path}")
    return np.array(vs, dtype=np.float64)
