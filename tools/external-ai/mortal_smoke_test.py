#!/usr/bin/env python3
"""Start Akagi-MjaiBot-Mortal and verify one complete mjai JSONL round-trip."""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
from pathlib import Path


def read_one(stream, box: list[str]) -> None:
    line = stream.readline()
    if line:
        box.append(line.strip())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bot", required=True)
    parser.add_argument("--timeout", type=float, default=180.0)
    args = parser.parse_args()

    bot_dir = Path(args.bot).resolve()
    bot_py = bot_dir / "bot.py"
    if not bot_py.is_file():
        raise FileNotFoundError(bot_py)

    env = os.environ.copy()
    env.update({"PYTHONUTF8": "1", "PYTHONUNBUFFERED": "1", "OMP_NUM_THREADS": "2", "MKL_NUM_THREADS": "2"})
    process = subprocess.Popen(
        [sys.executable, "-u", str(bot_py)],
        cwd=str(bot_dir),
        env=env,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    assert process.stdin and process.stdout and process.stderr

    events = [
        {"type": "start_game", "id": 0, "names": ["Doman-0", "Doman-1", "Doman-2", "Doman-3"]},
        {
            "type": "start_kyoku",
            "bakaze": "E",
            "kyoku": 1,
            "honba": 0,
            "kyotaku": 0,
            "oya": 0,
            "scores": [25000, 25000, 25000, 25000],
            "dora_marker": "4p",
            "tehais": [
                ["1m", "2m", "3m", "4m", "5m", "6m", "2p", "3p", "4p", "6s", "7s", "8s", "E"],
                ["?"] * 13,
                ["?"] * 13,
                ["?"] * 13,
            ],
        },
        {"type": "tsumo", "actor": 0, "pai": "9p"},
    ]
    process.stdin.write(json.dumps(events, separators=(",", ":")) + "\n")
    process.stdin.flush()

    output: list[str] = []
    reader = threading.Thread(target=read_one, args=(process.stdout, output), daemon=True)
    reader.start()
    reader.join(args.timeout)
    if reader.is_alive():
        process.kill()
        stderr = process.stderr.read()[-4000:]
        raise TimeoutError(f"Mortal produced no response in {args.timeout}s. stderr={stderr}")

    line = output[0] if output else ""
    try:
        action = json.loads(line)
    except Exception as exc:
        process.kill()
        stderr = process.stderr.read()[-4000:]
        raise RuntimeError(f"Invalid Mortal response {line!r}; stderr={stderr}") from exc
    if not isinstance(action, dict) or "type" not in action:
        raise RuntimeError(f"Unexpected Mortal response: {action!r}")
    # bot.py converts internal exceptions to {"type":"none"}; accepting that
    # would make a broken model installation look healthy. This synthetic hand
    # is our turn with a legal discard, so Mortal must return a concrete action.
    if action.get("type") not in {"dahai", "reach"} or not action.get("pai"):
        process.kill()
        stderr = process.stderr.read()[-4000:]
        raise RuntimeError(f"Mortal did not return a playable action: {action!r}; stderr={stderr}")

    process.kill()
    process.wait(timeout=10)
    print(json.dumps({"ok": True, "action": action}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
