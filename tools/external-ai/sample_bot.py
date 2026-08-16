#!/usr/bin/env python3
"""Minimal array-batched mjai JSONL smoke-test bot.

This is not a strong AI. Each input line must be one JSON array of mjai events;
one JSON action is flushed for every input line.
"""
from __future__ import annotations
import json
import os
import sys

player_id = int(os.environ.get("DOMAN_MJAI_PLAYER_ID", "0"))


def react(events: list[dict]) -> dict:
    for event in events:
        if event.get("type") == "start_game":
            global player_id
            player_id = int(event.get("id", player_id))
    for event in reversed(events):
        if event.get("type") == "tsumo" and event.get("actor") == player_id:
            return {
                "type": "dahai",
                "actor": player_id,
                "pai": event.get("pai", "1m"),
                "tsumogiri": True,
            }
    return {"type": "none"}


for line in sys.stdin:
    try:
        payload = json.loads(line)
        events = payload if isinstance(payload, list) else [payload]
        print(json.dumps(react(events), ensure_ascii=False, separators=(",", ":")), flush=True)
    except Exception as exc:
        print(json.dumps({"type": "none", "error": str(exc)}, separators=(",", ":")), flush=True)
