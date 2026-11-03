#!/usr/bin/env python3
"""假的 codex 命令：只模擬 `--version` 和 `app-server` 的 account/rateLimits/read，給 CrossPet 的測試用。
FAKE_CODEX_MODE：exit（一啟動就退出，測寫 stdin 時對面已經沒了）/ ok（兩個視窗）/ single（只有一個視窗）/ auth（沒登入）/ nomethod（舊版沒有這個介面）/ hang（一直不回）/ garbage（亂回）
"""
import json, os, sys, time

mode = os.environ.get("FAKE_CODEX_MODE", "ok")
if sys.argv[1:] == ["--version"]:
    print("codex-cli 0.0.0-fake"); sys.exit(0)
if sys.argv[1:2] != ["app-server"]:
    sys.exit(2)
if mode == "exit":
    sys.exit(0)
now = int(time.time())
LIMITS = {
    "ok": {"limitId": "codex", "primary": {"usedPercent": 3, "windowDurationMins": 300, "resetsAt": now + 3600},
           "secondary": {"usedPercent": 40, "windowDurationMins": 10080, "resetsAt": now + 3 * 86400}, "planType": "plus"},
    "single": {"limitId": "codex", "primary": {"usedPercent": 12, "windowDurationMins": 10080, "resetsAt": now + 5 * 86400}, "secondary": None},
}
for line in sys.stdin:
    msg = json.loads(line)
    if msg.get("method") == "initialize":
        print(json.dumps({"id": msg["id"], "result": {"userAgent": "fake"}}), flush=True)
    elif msg.get("method") == "account/rateLimits/read":
        if mode == "hang":
            time.sleep(3600)
        elif mode == "garbage":
            print("not json", flush=True)
        elif mode == "auth":
            print(json.dumps({"id": msg["id"], "error": {"code": -32600, "message": "codex account authentication required to read rate limits"}}), flush=True)
        elif mode == "nomethod":
            print(json.dumps({"id": msg["id"], "error": {"code": -32601, "message": "method not found"}}), flush=True)
        else:
            rl = LIMITS[mode]
            print(json.dumps({"id": msg["id"], "result": {"rateLimits": rl, "rateLimitsByLimitId": {"codex": rl},
                              "rateLimitResetCredits": {"availableCount": 1}, "accountId": "acct-secret"}}), flush=True)
