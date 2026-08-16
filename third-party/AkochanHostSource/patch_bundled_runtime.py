#!/usr/bin/env python3
"""Verify/apply the v0.8.0.69 post-call trigger patch to the bundled Akochan host.

The source-of-truth fix is in akochan_pipe.cpp. This deterministic patch keeps the
bundled prebuilt Windows runtime in sync when rebuilding the full upstream C++
engine is not part of the normal .NET packaging step.
"""
from pathlib import Path
import hashlib

ORIGINAL_SHA256 = "f228edd660a089e7743616d74a11c127797920cab73cef002a3b9e80d877fa7c"
PATCHED_SHA256 = "e3c1132aba2a40fb0a3e7d42df8b8d13f8802f8cb51e8b89826750828887d886"
OFFSET = 0xCA40F
OLD = bytes.fromhex("0f854ef9ffff")
NEW = bytes.fromhex("e94ff9ffff90")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> None:
    exe = Path(__file__).resolve().parents[1] / "AkochanRuntime" / "akochan_pipe.exe"
    data = bytearray(exe.read_bytes())
    current = sha256(data)
    if current == PATCHED_SHA256:
        print(f"OK: already patched ({current})")
        return
    if current != ORIGINAL_SHA256:
        raise SystemExit(f"ERROR: unsupported akochan_pipe.exe hash: {current}")
    if bytes(data[OFFSET:OFFSET + len(OLD)]) != OLD:
        raise SystemExit("ERROR: expected instruction sequence was not found")
    data[OFFSET:OFFSET + len(NEW)] = NEW
    exe.write_bytes(data)
    final = sha256(data)
    if final != PATCHED_SHA256:
        raise SystemExit(f"ERROR: patched hash mismatch: {final}")
    print(f"OK: patched ({final})")


if __name__ == "__main__":
    main()
