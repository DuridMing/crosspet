#!/usr/bin/env python3
"""Hook timing: interpreter start, the Python hook, and the Rust equivalent (MC-39).

    python maid/bench/bench.py --python <bundled python.exe> --out results/hook.json

Every run uses a temp data dir and temp HOME, so real settings and the real pet are never touched.
Defender is left as it is: on Windows these numbers include its scan of each started process.
"""
import argparse
import json
import os
import platform
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
EVENT = {"hook_event_name": "PreToolUse", "session_id": "bench-session", "tool_name": "Bash", "tool_input": {"command": "ls"}}
EXPECTED_POSE = "running"


def isolated_env(root: Path) -> dict:
    env = dict(os.environ)
    data = root / "data"
    state = data / "state"
    state.mkdir(parents=True)
    env.update(CROSSPET_DATA_DIR=str(data), CROSSPET_STATE_DIR=str(state), HOME=str(root), USERPROFILE=str(root),
               LOCALAPPDATA=str(root / "local"), APPDATA=str(root / "roaming"), CLAUDE_CONFIG_DIR=str(root / ".claude"),
               CODEX_HOME=str(root / ".codex"))
    return env


def time_runs(cmd, payload, env, runs, warmup):
    for _ in range(warmup):
        subprocess.run(cmd, input=payload, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env, check=True)
    samples = []
    for _ in range(runs):
        t0 = time.perf_counter()
        subprocess.run(cmd, input=payload, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env, check=True)
        samples.append((time.perf_counter() - t0) * 1000)
    samples.sort()
    return {
        "runs": runs,
        "median_ms": round(statistics.median(samples), 2),
        "p95_ms": round(samples[min(len(samples) - 1, int(len(samples) * 0.95))], 2),
        "min_ms": round(samples[0], 2),
    }


def check_state(root: Path) -> None:
    """A timing is worthless if the hook did not do its work: it must have written the expected pose."""
    state = json.loads((root / "data/state/claude-state.json").read_text(encoding="utf-8"))
    if state.get("pose") != EXPECTED_POSE:
        raise SystemExit(f"hook wrote pose {state.get('pose')!r}, expected {EXPECTED_POSE!r}")


def defender_status():
    if os.name != "nt":
        return None
    r = subprocess.run(["powershell.exe", "-NoProfile", "-Command", "(Get-MpComputerStatus).RealTimeProtectionEnabled"],
                       capture_output=True, text=True, errors="replace")
    return r.stdout.strip() or None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=300)
    ap.add_argument("--warmup", type=int, default=20)
    ap.add_argument("--python", default=sys.executable, help="interpreter the hook runs on; use the bundled one on Windows")
    ap.add_argument("--hook", default=str(REPO / "integrations/crosspet-hook.py"))
    exe = "rhook.exe" if os.name == "nt" else "rhook"
    ap.add_argument("--rust", default=str(HERE / "rhook/target/release" / exe))
    ap.add_argument("--out")
    args = ap.parse_args()

    payload = json.dumps(EVENT).encode("utf-8")
    results = {}
    with tempfile.TemporaryDirectory(prefix="crosspet-bench-") as tmp:
        root = Path(tmp)
        env = isolated_env(root)
        results["python_empty"] = time_runs([args.python, "-c", "pass"], b"", env, args.runs, args.warmup)
        results["python_hook"] = time_runs([args.python, args.hook, "claude"], payload, env, args.runs, args.warmup)
        check_state(root)
    with tempfile.TemporaryDirectory(prefix="crosspet-bench-") as tmp:
        root = Path(tmp)
        env = isolated_env(root)
        results["rust_hook"] = time_runs([args.rust, "claude"], payload, env, args.runs, args.warmup)
        check_state(root)

    report = {
        "date": time.strftime("%Y-%m-%d %H:%M"),
        "platform": platform.platform(),
        "processor": platform.processor(),
        "defender_realtime": defender_status(),
        "python": subprocess.run([args.python, "--version"], capture_output=True, text=True).stdout.strip(),
        "results": results,
        "per_tool_call_ms": {k: round(v["median_ms"] * 2, 1) for k, v in results.items() if k.endswith("_hook")},
    }
    text = json.dumps(report, indent=1)
    print(text)
    if args.out:
        Path(args.out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.out).write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
