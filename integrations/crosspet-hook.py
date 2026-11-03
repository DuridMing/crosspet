#!/usr/bin/env python3
"""CrossPet 通用鉤子指令碼：把 AI 的工作狀態寫給桌寵。

用法（在各 AI 的鉤子配置裡）：
    python3 crosspet-hook.py <角色id>          # 角色id：claude / gpt / deepseek / gemini
    python3 crosspet-hook.py <角色id> <事件名>  # 事件 JSON 裡不帶事件名的（Antigravity）
    python3 crosspet-hook.py workbuddy          # 一個程式裡能用好幾家模型（WorkBuddy、ZCode、Hermes Agent）：按模型選角色
    python3 crosspet-hook.py --ensure           # 只確保桌寵在跑（沒開著就開啟；使用者手動退出過或關了這個功能就不管）
鉤子事件 JSON 從 stdin 傳入。認得 Claude Code、Codex、DeepSeek Harness、Gemini CLI、Antigravity 的事件。

只寫狀態目錄（預設 /tmp/crosspet，可用環境變數 CROSSPET_STATE_DIR 改）裡的兩個小檔案：
    <角色id>-state.json   當前狀態，桌寵讀取
    <宿主id>-turn-tools.json  按會話和輪次記錄用了幾次工具（判斷「大活」用）
不影響 AI 本身的行為：一般不向 stdout 輸出；Antigravity 要求回一個 JSON，就回空的 {}。
用 /tmp 是因為有的 AI（比如 DeepSeek Harness）的鉤子跑在沙箱裡，只允許寫 /tmp。
"""
import hashlib
import json
import os
import sys
import time
from pathlib import Path

BIG_JOB_TOOLS = 8  # 一輪裡用了這麼多次工具，結束時「得意」一下

character = sys.argv[1] if len(sys.argv) > 1 else "claude"
default_data = Path(os.environ.get("CROSSPET_DATA_DIR") or str(Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData/Local"))) / "CrossPet"))
# 桌寵的資料目錄（角色、退出記號）：設了 CROSSPET_DATA_DIR 就用它（測試也靠它），沒設時 Windows 在 %LOCALAPPDATA%\CrossPet，macOS 在 Application Support
app_data = default_data if (os.name == "nt" or os.environ.get("CROSSPET_DATA_DIR")) else Path.home() / "Library/Application Support/CrossPet"
state_dir = Path(os.environ.get("CROSSPET_STATE_DIR") or (str(default_data / "state") if os.name == "nt" else "/tmp/crosspet"))

# ---- 事件記錄（反饋「AI 幹活時她沒反應」用）：資料目錄 events/<接入名>.jsonl，每條事件一行，記處理結果 ----
# 只記事件名、工具名、會話 id 的雜湊前 6 位、有沒有輪次號、結果（shown 演了 / dropped 被哪條規則丟了 / error 出錯）。
# 絕不記內容：prompt、工具引數和結果、AI 的回答、對話記錄路徑、工作目錄。只留最近 200 行（超過 240 行時截回 200）。
# 格式和 DeepSeek Harness 外掛、Claude Code mod 寫的一樣，見 docs/架构.md「记录」
LEDGER_KEEP, LEDGER_MAX = 200, 240
ledger_info = {"ev": "", "tool": "", "sid": "", "turn": False}


def integration_name(argv) -> str:
    """接入名（和 integrate.py、反饋裡「最近收到訊息」用的一樣）：鉤子的第一個引數是角色 / 宿主名，不完全一樣"""
    host = argv[1] if len(argv) > 1 else "claude"
    if host == "claude":
        return "claude-hooks"
    if host == "gpt":
        return "codex"
    if host == "gemini" and len(argv) > 2:   # Antigravity 多帶一個事件名引數
        return "antigravity"
    return host


def _lock(f) -> None:
    try:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(f.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(f, fcntl.LOCK_EX)
    except Exception:
        pass


def ledger(result: str, detail: str) -> None:
    try:
        d = app_data / "events"
        d.mkdir(parents=True, exist_ok=True)
        stem = integration_name(sys.argv)
        path = d / f"{stem}.jsonl"
        line = json.dumps({"t": round(time.time(), 2), **ledger_info, "result": result, "detail": detail}, ensure_ascii=False)
        with open(d / f".{stem}.lock", "a+") as lock:   # 好幾個鉤子程式可能同時寫：排隊
            _lock(lock)
            with open(path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
            rows = path.read_text(encoding="utf-8").splitlines()
            if len(rows) > LEDGER_MAX:
                tmp = d / f".{stem}.{os.getpid()}.tmp"
                tmp.write_text("\n".join(rows[-LEDGER_KEEP:]) + "\n", encoding="utf-8")
                tmp.replace(path)
            if os.name == "nt":
                try:
                    import msvcrt
                    lock.seek(0)
                    msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
                except Exception:
                    pass
    except Exception:
        pass   # 記錄寫不了不能影響鉤子本身

# 跟著 AI 出現：開始幹活（會話開始 / 發訊息 / 調模型）時桌寵沒開著就把它開啟。
# 使用者從選單手動退出過（user-quit）就先不管，直到這個 AI 程式關掉重開、或者重啟電腦：
# 看 AI 程式（鉤子往上找到的頂層程式）是在退出之後才打開的，記號就作廢。設定裡關掉了（ai-autostart-off）就不管。
# 也可以單獨呼叫：crosspet-hook.py --ensure（不讀事件、不寫狀態，只確保桌寵在跑；Windows 上 Claude Code 的 mod 用它）
LAUNCH_EVENTS = ("SessionStart", "UserPromptSubmit", "BeforeAgent", "PreInvocation")


def app_started_at():
    """呼叫這個鉤子的 AI 程式是哪個、什麼時候開啟的：順著父程式往上找，到系統程式下面那一層為止
    （macOS：launchd；Windows：系統目錄裡的程式，比如 explorer、svchost，或者虛擬機器工具的常駐程式）。
    中間的命令列外殼（cmd、PowerShell）會跳過去。終端裡跑的命令列工具找到的是終端程式。返回 (時間, 程式名)"""
    now = time.time()
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.windll.kernel32
        k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        k32.OpenProcess.restype = wintypes.HANDLE

        class Entry(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                        ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                        ("szExeFile", ctypes.c_wchar * 260)]
        procs = {}
        snap = k32.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
        entry = Entry()
        entry.dwSize = ctypes.sizeof(Entry)
        ok = k32.Process32FirstW(wintypes.HANDLE(snap), ctypes.byref(entry))
        while ok:
            procs[entry.th32ProcessID] = entry.th32ParentProcessID
            ok = k32.Process32NextW(wintypes.HANDLE(snap), ctypes.byref(entry))
        k32.CloseHandle(wintypes.HANDLE(snap))

        def info(pid: int):
            """(開啟時間, 程式完整路徑)；打不開（許可權不夠、已經退出）返回 (0, "")"""
            h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
            if not h:
                return 0, ""
            h = wintypes.HANDLE(h)
            times = [wintypes.FILETIME() for _ in range(4)]
            path = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(1024)
            try:
                if not k32.GetProcessTimes(h, *[ctypes.byref(t) for t in times]):
                    return 0, ""
                k32.QueryFullProcessImageNameW(h, 0, path, ctypes.byref(size))
            finally:
                k32.CloseHandle(h)
            ft = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
            return ft / 1e7 - 11644473600, path.value  # 1601 年起的 100 納秒 → Unix 秒

        windows = os.environ.get("SystemRoot", r"C:\Windows").lower().rstrip("\\") + "\\"
        shells = ("cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "bash.exe", "sh.exe", "wsl.exe")
        tools = ("\\parallels\\", "\\vmware\\", "\\oracle\\virtualbox")

        def system(path: str) -> bool:
            p = path.lower()
            name = p.rsplit("\\", 1)[-1]
            return not p or (p.startswith(windows) and name not in shells) or any(t in p for t in tools)

        pid = os.getpid()
        start, path = info(pid)
        while pid in procs:
            parent = procs[pid]
            if parent not in procs:
                break
            parent_start, parent_path = info(parent)
            if system(parent_path) or not parent_start or parent_start > start:  # 後者：程式號被重用了，真正的父程式早沒了
                break
            pid, start, path = parent, parent_start, parent_path
        return start, path.rsplit("\\", 1)[-1]
    import subprocess
    out = subprocess.run(["/bin/ps", "-A", "-o", "pid=,ppid=,etime=,comm="], capture_output=True, text=True, timeout=5).stdout
    procs = {}
    for line in out.splitlines():
        parts = line.split(None, 3)
        if len(parts) == 4:
            days, _, clock = parts[2].rpartition("-")
            secs = 0
            for piece in clock.split(":"):
                secs = secs * 60 + int(piece)
            procs[int(parts[0])] = (int(parts[1]), secs + int(days or 0) * 86400, parts[3])
    pid = os.getpid()
    while pid in procs and procs[pid][0] > 1 and procs[pid][0] in procs:
        pid = procs[pid][0]
    if pid not in procs:
        return 0, ""
    name = procs[pid][2]
    return now - procs[pid][1], (name.split(".app/")[0].rsplit("/", 1)[-1] if ".app/" in name else name.rsplit("/", 1)[-1])


def ensure_pet() -> str:
    """返回這次的結果（寫進 <狀態目錄>/autostart.txt，排查「為什麼沒出來」用）"""
    data = app_data
    quit_flag = data / "user-quit"
    if quit_flag.exists():
        quit_at = quit_flag.stat().st_mtime
        try:
            started, app = app_started_at()
            note = f"（{app or '沒找到 AI 程式'} 開啟於 {time.strftime('%H:%M:%S', time.localtime(started))}，退出於 {time.strftime('%H:%M:%S', time.localtime(quit_at))}）"
        except Exception as e:
            started, note = 0, f"（查 AI 啟動時間出錯 {e!r}）"
        if started <= quit_at:
            return "不開啟：從選單手動退出過，這個 AI 是在那之前就開著的" + note
        fresh_note = "手動退出後重新開啟過 AI" + note + "，"
    else:
        fresh_note = ""
    if (data / "ai-autostart-off").exists():
        return "不開啟：設定裡關掉了「AI 開始工作時自動出現」"
    import subprocess
    quiet = dict(stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
    if os.name == "nt":
        import ctypes
        kernel32 = ctypes.windll.kernel32
        # 桌寵在跑時握著這個互斥量（單例項）；能開啟就是在跑
        handle = kernel32.OpenMutexW(0x00100000, False, "Local\\CrossPet-" + os.environ.get("USERNAME", ""))
        if handle:
            kernel32.CloseHandle(handle)
            return "已經在跑"
        exe = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData/Local"))) / "Programs/CrossPet/CrossPet.exe"
        if not exe.exists():
            return f"不開啟：沒找到 {exe}"
        # 獨立程式：不掛在 AI 程式下面，AI 退出時桌寵不會被一起關掉
        detached = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        try:
            subprocess.Popen([str(exe)], cwd=str(exe.parent), creationflags=detached | 0x01000000, **quiet)  # + CREATE_BREAKAWAY_FROM_JOB
        except OSError:
            # 有的 AI（WorkBuddy）把鉤子放進不許脫離的作業物件，一輪結束就把裡面的程式全關掉，直接開啟的桌寵會跟著閃退。
            # 交給已經在跑的資源管理器去開啟（和從開始選單開啟一樣），桌寵就不在這個作業物件裡了
            explorer = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "explorer.exe"
            subprocess.Popen([str(explorer), str(exe)], creationflags=detached, **quiet)
            return fresh_note + "已開啟（由資源管理器開啟）"
        return fresh_note + "已開啟"
    else:
        try:
            os.kill(int((state_dir / "pet.pid").read_text().strip()), 0)
            return "已經在跑"
        except PermissionError:
            return "已經在跑"
        except Exception:
            pass
        # -g：在後臺開啟，不搶你正在用的視窗的焦點
        subprocess.Popen(["/usr/bin/open", "-g", "-b", "io.github.crosspet"], start_new_session=True, **quiet)
        return fresh_note + "已開啟"


def ensure_pet_logged(source: str) -> None:
    try:
        result = ensure_pet()
    except Exception as e:
        result = f"出錯：{e!r}"
    try:
        state_dir.mkdir(parents=True, exist_ok=True)
        # Windows 上帶 BOM：系統自帶的 PowerShell 5 沒有 BOM 就按 GBK 讀，中文會亂碼
        (state_dir / "autostart.txt").write_text(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {source}：{result}\n",
                                                 encoding="utf-8-sig" if os.name == "nt" else "utf-8")
    except Exception:
        pass


if character == "--ensure":
    ensure_pet_logged("--ensure")
    sys.exit(0)

IDLE_SECONDS = 1.5      # 這麼久沒有新資料進來就不等了（宿主都是一口氣寫完的）
MAX_SECONDS = 15        # 最多讀這麼久
MAX_BYTES = 64 << 20    # 最多讀這麼多（Claude Code 的 PostToolUse 帶著工具輸出，可能有好幾 MB）


def read_chunk(fd: int, timeout: float):
    """讀一塊：有資料返回 bytes，輸入關了返回 b""，等了 timeout 還沒資料返回 None"""
    if os.name == "nt":
        # Windows 的管道不能 select：先看管道里有沒有資料（PeekNamedPipe），有了再讀，讀就不會卡住
        import ctypes
        import msvcrt
        from ctypes import wintypes
        try:
            handle = msvcrt.get_osfhandle(fd)
        except OSError:
            return b""
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        avail = wintypes.DWORD()
        end = time.monotonic() + timeout
        while True:
            if not k32.PeekNamedPipe(wintypes.HANDLE(handle), None, 0, None, ctypes.byref(avail), None):
                if ctypes.get_last_error() == 109:   # ERROR_BROKEN_PIPE：寫的那頭關了
                    return b""
                return os.read(fd, 1 << 16)       # 不是管道（重定向的檔案）：直接讀，讀到頭就是 EOF
            if avail.value:
                return os.read(fd, min(avail.value, 1 << 16))
            if time.monotonic() >= end:
                return None
            time.sleep(0.01)
    import select
    ready, _, _ = select.select([fd], [], [], timeout)
    return os.read(fd, 1 << 16) if ready else None


def read_event():
    """從 stdin 讀一個事件 JSON。讀到一個完整的就返回，不等輸入關閉（#19：Windows 上 Codex 的 app-server
    寫完事件不關輸入，以前在這裡一直等，鉤子程式掛著不退，每次留下一個空白終端）。
    沒有輸入（互動終端）、超時、內容不是 JSON 時返回 None"""
    if sys.stdin is None or sys.stdin.isatty():
        return None
    fd = sys.stdin.fileno()
    buf = b""
    start = time.monotonic()
    decoder = json.JSONDecoder()
    while len(buf) < MAX_BYTES and time.monotonic() - start < MAX_SECONDS:
        chunk = read_chunk(fd, IDLE_SECONDS)
        if not chunk:   # 輸入關了（b""）或一陣子沒資料（None）：拿讀到的試最後一次
            break
        buf += chunk
        if buf.rstrip()[-1:] != b"}":   # 還沒到物件的結尾：接著讀（不每塊都解析，幾 MB 的事件也不慢）
            continue
        try:
            # 嚴格按 UTF-8 解：漢字被切在兩塊之間時先接著讀，湊齊了再解
            return decoder.raw_decode(buf.decode("utf-8").lstrip())[0]
        except (ValueError, UnicodeDecodeError):
            continue
    try:
        # 按 UTF-8 讀：Windows 上 Python 預設用系統編碼（中文系統是 GBK），事件裡一有中文就會解碼失敗
        return json.loads(buf.decode("utf-8", errors="replace"))
    except ValueError:
        return None


try:
    event = read_event()
except Exception:
    event = None
if not isinstance(event, dict):
    ledger("dropped", "bad-json")
    sys.exit(0)

_sid = str(event.get("session_id") or event.get("sessionId") or event.get("conversationId") or "")
_call = event.get("toolCall") if isinstance(event.get("toolCall"), dict) else {}
ledger_info.update(ev=str(event.get("hook_event_name") or (sys.argv[2] if len(sys.argv) > 2 else ""))[:60],
                   tool=str(event.get("tool_name") or _call.get("name", "")).lower()[:60],
                   sid=hashlib.sha1(_sid.encode("utf-8")).hexdigest()[:6] if _sid else "",
                   turn=bool(event.get("turn_id")))
_prev_excepthook = sys.excepthook


def _ledger_excepthook(kind, value, tb):
    ledger("error", kind.__name__)
    _prev_excepthook(kind, value, tb)


sys.excepthook = _ledger_excepthook

def cursor_bridge_installed() -> bool:
    """只在 Cursor 確實配置了獨立橋時，跳過它匯入的 Claude 鉤子。解除安裝後自然恢復舊接入。"""
    try:
        config = json.loads((Path.home() / ".cursor/hooks.json").read_text(encoding="utf-8-sig"))
        hooks = config.get("hooks") if isinstance(config, dict) else None
        if not isinstance(hooks, dict):
            return False
        return any(isinstance(entry, dict) and isinstance(entry.get("command"), str)
                   and any(mark in entry["command"] for mark in ("crosspet-cursor-bridge.py", "crosspet_cursor_bridge.py"))
                   for entries in hooks.values() if isinstance(entries, list) for entry in entries)
    except (OSError, ValueError):
        return False  # 正在寫配置 / 未安裝時，保留已有 observer 的行為。


# Cursor 會匯入 Claude hooks；只在它另有獨立橋接時去重，不能讓未遷移的使用者升級後斷聯。
if event.get("cursor_version") and cursor_bridge_installed():
    ledger("dropped", "cursor-imported-claude-hook")
    sys.exit(0)

# Grok Build：每個鉤子程式都帶 GROK_HOOK_EVENT。它還會讀 ~/.claude/settings.json 的鉤子，那邊的 CrossPet 鉤子
# 就會以 claude 的名義被它觸發：單獨接入了 Grok（~/.grok/hooks/crosspet.json）時丟掉，免得同一件事演兩遍；
# 沒接入時改成 Grok 來演，不能讓 Grok 的活算到 Claude 頭上
if os.environ.get("GROK_HOOK_EVENT") and character != "grok":
    grok_home = Path(os.environ.get("GROK_HOME") or Path.home() / ".grok")
    if (grok_home / "hooks" / "crosspet.json").exists():
        ledger("dropped", "grok-imported-claude-hook")
        sys.exit(0)
    character = "grok"
# Grok Build 關閉會話時，在 SessionEnd 之後還會補發一個 reason=shutdown 的 Stop（不是一輪做完）：
# 演了會把剛睡著的她拉回「開心」（10-27 實測）
if os.environ.get("GROK_HOOK_EVENT") and event.get("hook_event_name") == "Stop" and event.get("reason") == "shutdown":
    ledger("dropped", "grok-shutdown-stop")
    sys.exit(0)
# Grok Build 的欄位是駝峰寫法；事件名（hook_event_name）已經是 Claude 的寫法
if os.environ.get("GROK_HOOK_EVENT"):
    for camel, snake in (("toolName", "tool_name"), ("toolInput", "tool_input"), ("notificationType", "notification_type")):
        if camel in event and snake not in event:
            event[snake] = event[camel]

# Codex 會在對話結束後自己在後臺整理記憶（工作目錄在 ~/.codex/memories），也會調工具，但不是你讓它乾的活，
# 而且沒有開始 / 結束事件，演了會一直停在思考：工作目錄在 Codex 自己的配置目錄裡的事件都不管
if character == "gpt":
    try:
        codex_home = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex").resolve()
        cwd = Path(str(event.get("cwd") or "")).resolve() if event.get("cwd") else None
        if cwd and (cwd == codex_home or codex_home in cwd.parents):
            ledger("dropped", "codex-memory")
            sys.exit(0)
    except (OSError, ValueError):
        pass

# Codex 桌面版首頁的「主動建議」：它在後臺開臨時對話翻你的檔案、郵件、日曆來寫建議卡片，一開啟 Codex、或者隔一陣就跑一次，
# 不是你讓它乾的活，演了就是「啥都沒輸入她就開工」，還常常一次開兩個、直接進手忙腳亂。
# 這種臨時對話不存檔：你自己開的對話從一開始就有會話記錄檔案（事件裡的 transcript_path），它們沒有。
# 剛開的對話記錄檔案可能晚一點點才寫出來：開頭兩個事件等一下再判斷
if character == "gpt" and "transcript_path" in event:
    tp = str(event.get("transcript_path") or "")
    tries = 6 if event.get("hook_event_name") in ("SessionStart", "UserPromptSubmit") else 1
    for i in range(tries):
        try:
            if tp and Path(tp).exists():
                break
        except (OSError, ValueError):
            pass
        if i + 1 < tries:
            time.sleep(0.1)
    else:
        ledger("dropped", "codex-suggestion")
        sys.exit(0)

arg_event = sys.argv[2] if len(sys.argv) > 2 else ""
name = event.get("hook_event_name") or arg_event
source_host = character  # 不能用按模型換過的角色來分會話 / 工具計數


# 從門禁到狀態、模型路由、會話卡片寫完共用一把宿主鎖。只鎖 turn_gate 會留下
# 「工具已獲準 → Stop 寫完 → 舊工具才替換狀態」的視窗。寫完會話再關控制程式碼；異常退出也會釋放。
def event_lock(owner: str):
    state_dir.mkdir(parents=True, exist_ok=True)
    lock = open(state_dir / f".{owner}-events.lock", "a+b")
    try:
        deadline = time.monotonic() + .75
        while True:
            try:
                lock.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise TimeoutError("observer event lock busy")
                time.sleep(.005)
        return lock
    except BaseException:
        lock.close()
        raise


try:
    _event_lock = event_lock(source_host)
except OSError:
    # 觀察者寫不了狀態時安靜退出，不繞過鎖寫共享檔案，也不影響 AI。
    ledger("dropped", "event-lock-busy")
    if arg_event:
        print("{}")
    sys.exit(0)


# ---- 按輪次丟掉遲到的事件（Codex：事件帶 turn_id；PR #26 @KQYaili 報的）----
# 沿用上游的會話 + 輪次賬本。子任務另記首次看到時所屬的主輪次，換輪後不重新歸屬。
TURN_END = ("Stop", "StopFailure", "StopCancelled", "Interrupt", "SessionEnd")
TURN_KEEP = 24 * 3600


def turn_gate(owner: str, sid: str, tid: str, name: str, child: bool) -> tuple:
    """返回 (這條事件要不要演, 工具計數所屬的主輪次)。呼叫者持有整條事件的鎖。"""
    path = state_dir / f"{owner}-turns.json"
    try:
        book = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(book, dict):
            book = {}
    except Exception:
        book = {}
    now = time.time()
    book = {k: v for k, v in book.items() if isinstance(v, dict) and now - float(v.get("ts", 0)) < TURN_KEEP}
    rec = book.get(sid) or {"turn": "", "done": False, "finished": []}
    finished = rec.get("finished") or []
    children = rec.get("children")
    children = children if isinstance(children, dict) else {}
    if child:
        key = json.dumps([str(event.get("agent_id")), tid])
        binding = children.get(key)
        binding = binding if isinstance(binding, dict) else {}
        explicit_parent = str(event.get("parent_turn_id") or event.get("root_turn_id") or "")
        parent = str(binding.get("turn") or explicit_parent or rec.get("turn") or "")
        allow = not rec.get("done") and not binding.get("done") and parent == rec.get("turn", "")
        if explicit_parent and explicit_parent != rec.get("turn"):
            allow = False
        children[key] = {"turn": parent, "done": bool(binding.get("done") or name in TURN_END or name == "SubagentStop")}
    elif tid in finished:
        allow = False
    else:
        allow = True
        # compact / resume 的 SessionStart 不是新輪次，不能退休正在工作的主輪次。
        metadata = name == "SessionStart" and str(event.get("source", "")).lower() in ("compact", "resume")
        if tid != rec.get("turn") and not metadata:
            if rec.get("turn") and rec["turn"] not in finished:
                finished = (finished + [rec["turn"]])[-128:]
            rec = {"turn": tid, "done": False, "finished": finished}
        ends = name in TURN_END or (name == "PostCompact" and str(event.get("trigger", "")).lower() == "manual")
        if ends:
            rec["done"] = True
            rec["finished"] = (finished + [tid])[-128:]
    rec["children"] = dict(list(children.items())[-128:])
    rec["ts"] = now
    book[sid] = rec
    tmp = state_dir / f".{owner}-turns.{os.getpid()}.json"
    tmp.write_text(json.dumps(book))
    tmp.replace(path)
    return allow, str(rec.get("turn") or tid)


turn_id = str(event.get("turn_id") or "")
turn_sid = str(event.get("session_id") or event.get("sessionId") or event.get("conversationId") or "")
effective_turn = turn_id
if turn_id and turn_sid:
    is_child = bool(event.get("agent_id"))
    try:
        allowed, effective_turn = turn_gate(source_host, turn_sid, turn_id, name, is_child)
    except Exception:
        # 記賬失敗不能讓舊事件繞過門禁去改模型路由或可見狀態；下一條訊息再試。
        allowed = False
    if not allowed:
        ledger("dropped", "turn-finished")
        if arg_event:
            print("{}")
        sys.exit(0)
    if is_child:
        if name == "UserPromptSubmit":
            name = "SubagentStart"
        elif name in TURN_END:
            name = "SubagentStop"


# 心跳：「最近一次收到這個 AI 的訊息」，桌寵記下來，設定裡能看出「接上了但從沒收到過訊息」，反饋裡也帶著。
# 檔名按接入的是哪個 AI 分（Antigravity 和 Gemini CLI 都用 gemini 角色，Antigravity 多帶一個事件名引數）
try:
    seen_key = "antigravity" if character == "gemini" and len(sys.argv) > 2 else character
    state_dir.mkdir(parents=True, exist_ok=True)
    (state_dir / f"seen-{seen_key}").write_text(str(int(time.time())))
except Exception:
    pass

# 多模型宿主（WorkBuddy、ZCode、Hermes Agent、Qwen Code）：事件裡帶當前模型名就按它選角色；認不出的模型（混元、Kimi、GLM……）
# 寫給「當前角色」（current），桌寵用正在顯示的角色演，不換人。當前角色另外記在 <宿主>-host.json，
# 桌寵切到這個程式的視窗時按它換角色
MULTI_MODEL_HOSTS = ("workbuddy", "zcode", "hermes", "qwen")
# ZCode 預設用智譜的 GLM；Qwen Code 預設用自家的千問。它們只在會話開始時告訴模型名，
# 之後的事件按會話 id 查當時記下的模型
HOST_DEFAULT_MODEL = {"zcode": "glm", "qwen": "qwen"}
characters_dir = app_data / "characters"


def character_for_model(model: str):
    m = model.lower()
    if "glm" in m or "zhipu" in m or "chatglm" in m or "z.ai" in m or "zai-" in m:
        return "glm"
    # coder-model：Qwen Code 用千問賬號登入時報的模型別名（實為 Qwen 3.7 Max）
    if "qwen" in m or "dashscope" in m or "bailian" in m or "tongyi" in m or m == "coder-model":
        return "qwen"
    if "kimi" in m or "moonshot" in m:
        return "kimi"
    if "grok" in m:   # xAI 的 grok-4.7、OpenRouter 的 x-ai/grok-…
        return "grok"
    if "deepseek" in m:
        return "deepseek"
    if "claude" in m or "anthropic" in m:
        return "claude"
    if "gemini" in m:
        return "gemini"
    if m.startswith(("gpt", "o1", "o3", "o4", "codex", "chatgpt")) or "openai" in m:
        return "gpt"
    return None


def remember_session_model(host: str, fallback: str = "") -> str:
    """有的宿主只在會話開始時給模型名：記下來（按會話 id），之後的事件查它"""
    sid = str(event.get("session_id") or event.get("sessionId") or "")
    # 宿主名本身就是角色名時（claude / gpt / qwen），<名字>-sessions.json 已經被多會話狀態佔用了（見 update_sessions），
    # 模型得另記一個檔案：不然記模型會把會話狀態當模型名讀回來（character_for_model 收到 dict 直接崩）
    path = state_dir / (f"{host}-models.json" if host in NAMED_HOSTS else f"{host}-sessions.json")
    model = str(event.get("model") or "")
    if model and sid:
        try:
            state_dir.mkdir(parents=True, exist_ok=True)
            # 同一宿主的幾個會話會同時啟動：鎖住「讀舊錶 → 加新項 → 替換」整段。
            # 臨時檔案也按目標檔案命名，不能與 update_sessions 的 .qwen-sessions.json 搶同一路徑。
            with open(state_dir / f".{host}-models.lock", "a+b") as lock:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock, fcntl.LOCK_EX)
                try:
                    try:
                        known = json.loads(path.read_text())
                    except Exception:
                        known = {}
                    if not isinstance(known, dict):
                        known = {}
                    known[sid] = model
                    known = dict(list(known.items())[-50:])  # 只留最近的會話
                    tmp = state_dir / f".{path.stem}.{os.getpid()}.json"
                    tmp.write_text(json.dumps(known))
                    tmp.replace(path)
                finally:
                    if os.name == "nt":
                        lock.seek(0)
                        msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
        except Exception:
            pass
    try:
        known = json.loads(path.read_text())
    except Exception:
        known = {}
    saved = known.get(sid, "") if isinstance(known, dict) else ""
    if not isinstance(saved, str):
        saved = ""
    return model or saved or fallback or HOST_DEFAULT_MODEL.get(host, "")


def character_for_url(url: str):
    """Claude Code 走別家的相容介面（ANTHROPIC_BASE_URL）：看介面地址是誰家的。
    DeepSeek、智譜的 Anthropic 相容介面也收 Claude 的模型名（會換成自家模型），所以地址比模型名可信"""
    u = url.lower()
    if "deepseek.com" in u:
        return "deepseek"
    if "bigmodel.cn" in u or "z.ai" in u or "zhipu" in u:
        return "glm"
    if "moonshot" in u or "kimi" in u:
        return "kimi"
    if "x.ai" in u:   # api.x.ai
        return "grok"
    return None


def installed(c) -> bool:
    return bool(c) and (characters_dir / c / "character.json").exists() and \
        any((characters_dir / c / f"idle.{ext}").exists() for ext in ("webp", "png"))


# 自己就有角色、但也能接別家介面的：Claude Code（ANTHROPIC_BASE_URL + 別家模型名）、Codex（換了 model_provider）。
# 按模型換成對應的角色，認不出的模型還是由自己來演。Codex 每條事件都帶模型名；Claude Code 只在會話開始時給，
# 另外看環境變數裡的模型名和介面地址
SELF_HOSTS = ("claude", "gpt")
# 宿主名同時也是角色名的：它們的 <名字>-sessions.json 被多會話狀態佔用，記模型要另開一個檔案（見 remember_session_model）
NAMED_HOSTS = SELF_HOSTS + ("qwen",)
if character in SELF_HOSTS:
    host = character
    # 先看事件裡的模型名（Claude Code 會話開始時給的、Codex 每條都給的，是實際要用的那個）；
    # 環境變數只在事件裡沒有時兜底：CC Switch 之類會把它們寫進 settings.json，但 Claude 桌面 App 登入的賬號照樣走 Anthropic
    env_model = os.environ.get("ANTHROPIC_MODEL", "") if host == "claude" else ""
    model = remember_session_model(host, env_model)
    mapped = character_for_model(model) \
        or (character_for_url(os.environ.get("ANTHROPIC_BASE_URL", "")) if host == "claude" else None)
    if not installed(mapped):
        mapped = host
    character = mapped
    event["model"] = model
    try:
        state_dir.mkdir(parents=True, exist_ok=True)
        hp = state_dir / f".{host}-host.json"
        hp.write_text(json.dumps({"character": mapped, "model": model, "ts": time.time()}))
        hp.replace(state_dir / f"{host}-host.json")
    except Exception:
        pass

if character in MULTI_MODEL_HOSTS:
    host = character
    model = remember_session_model(host)
    mapped = character_for_model(model)
    # 對應的角色還沒裝（比如還沒畫立繪的新角色）：當成認不出的模型，由當前角色來演
    if mapped and not installed(mapped):
        mapped = None
    character = mapped or "current"
    event["model"] = model
    if model:
        try:
            state_dir.mkdir(parents=True, exist_ok=True)
            hp = state_dir / f".{host}-host.json"
            hp.write_text(json.dumps({"character": mapped, "model": str(event.get("model")), "ts": time.time()}))
            hp.replace(state_dir / f"{host}-host.json")
        except Exception:
            pass

call = event.get("toolCall") if isinstance(event.get("toolCall"), dict) else {}  # Antigravity
tool = str(event.get("tool_name") or call.get("name", "")).lower()
tool_input = json.dumps(event.get("tool_input") or call.get("args", ""), ensure_ascii=False).lower()


# Antigravity 的工具名（檔案裡的名字和內部步驟名都可能出現）
ANTIGRAVITY_TOOLS = {
    "reading": ("view_file", "view_file_outline", "view_code_item", "view_content_chunk", "list_dir", "list_directory",
                "find_by_name", "find", "grep_search", "code_search", "find_all_references", "read_notebook", "read_resource"),
    "writing": ("write_to_file", "replace_file_content", "multi_replace_file_content", "code_action", "file_change",
                "propose_code", "edit_notebook", "write_blob", "delete_directory", "move"),
    "running": ("run_command", "command_status", "send_command_input", "read_terminal", "shell_exec", "execute_notebook"),
    "searching": ("search_web", "read_url_content", "open_browser_url", "read_browser_page", "browser_subagent"),
    "drawing": ("generate_image",),
    "delegating": ("invoke_subagent",),
}
# 這些是 Antigravity 內部的記賬 / 提問步驟，不算幹活
ANTIGRAVITY_IGNORE = ("task_boundary", "notify_user", "planner_response", "ask_question", "ask_permission",
                      "manage_task", "checkpoint", "ephemeral_message", "system_message", "finish", "wait")


# AI 停下來問你問題、讓你選選項的工具：Claude Code 的 AskUserQuestion、Codex 的 request_user_input、
# DeepSeek Harness 的 ask_user_question 等
ASKING_TOOLS = ("askuserquestion", "request_user_input", "ask_user_question", "ask_user", "ask_question", "clarify")
# Notification 裡表示「在等你授權 / 等你填」的型別（Claude Code、Gemini CLI、WorkBuddy）；idle_prompt 是一輪做完後閒著，不算
ASKING_NOTIFICATIONS = ("permission_prompt", "elicitation_dialog", "toolpermission", "agent_needs_input")


def pose_for_tool(t: str) -> str:
    if t in ASKING_TOOLS:
        return "asking"
    # 畫圖：Codex 內建 image_gen 工具，或用指令碼 image_gen.py 生成
    if any(k in t for k in ("image_gen", "imagegen", "generate_image", "video_gen", "draw", "paint")) or "image_gen" in tool_input:
        return "drawing"
    # Hermes Agent：在本地找檔案 / 翻歷史會話 / 看終端輸出 / 看圖，都算看資料（名字裡帶 search，別當成上網搜）
    if t in ("search_files", "session_search", "read_terminal", "vision_analyze"):
        return "reading"
    if "view_image" in t or t in ("find_by_name", "zoom_image"):   # zoom_image：Qwen Code 放大看圖
        return "reading"
    if t in ("read_url_content",) or t.startswith("browser"):
        return "searching"
    if any(k in t for k in ("read", "grep", "glob", "list", "view", "cat")):
        return "reading"
    if any(k in t for k in ("write", "edit", "patch", "replace", "create")):
        return "writing"
    if any(k in t for k in ("web", "fetch", "browse", "search", "http")):
        return "searching"
    if any(k in t for k in ("agent", "task", "subagent", "delegate")):
        return "delegating"
    return "running"


def tool_failed() -> bool:
    if event.get("error"):  # Antigravity
        return True
    resp = event.get("tool_response")
    if isinstance(resp, dict):
        if resp.get("is_error") is True or resp.get("success") is False:
            return True
        if resp.get("error"):
            return True
    return False


# 計數跟原始宿主 + 會話 + 主輪次走，不能讓另一段對話的發訊息 / Stop 把它清零。
# 沒有會話 id 的舊宿主保留一份共享計數；有 id 的宿主即使不帶 turn_id 也按會話分開。
turn_file = state_dir / f"{source_host}-turn-tools.json"
turn_key = json.dumps([turn_sid, effective_turn])


def read_tool_counts() -> dict:
    try:
        known = json.loads(turn_file.read_text())
        return known if isinstance(known, dict) else {}
    except Exception:
        return {}


def turn_tools() -> int:
    entry = read_tool_counts().get(turn_key)
    try:
        return int(entry.get("tools", 0)) if isinstance(entry, dict) else 0
    except (TypeError, ValueError):
        return 0


def set_turn_tools(n: int) -> None:
    known = read_tool_counts()
    now = time.time()
    known = {k: v for k, v in known.items() if isinstance(v, dict) and now - float(v.get("ts", 0)) < TURN_KEEP}
    known[turn_key] = {"tools": n, "ts": now}
    known = dict(sorted(known.items(), key=lambda item: item[1].get("ts", 0))[-256:])
    tmp = state_dir / f".{source_host}-turn-tools.{os.getpid()}.json"
    tmp.write_text(json.dumps(known))
    tmp.replace(turn_file)


state_dir.mkdir(parents=True, exist_ok=True)

pose = None
if arg_event and name == "PostToolUse":
    # Antigravity：不掛 PreToolUse（它要求鉤子回答「放行 / 攔截」，會幹擾正常的許可權確認），
    # 工具跑完時再按工具名顯示動作，下一次呼叫模型（PreInvocation）時回到思考
    agy = next((p for p, names in ANTIGRAVITY_TOOLS.items() if tool in names), None)
    if tool_failed():
        pose = "oops"
    elif not tool or tool in ANTIGRAVITY_IGNORE:
        pose = None
    else:
        set_turn_tools(turn_tools() + 1)
        pose = agy or ("searching" if tool.startswith("browser") else pose_for_tool(tool))
    name = "PreToolUse"  # 讓桌寵當作「正在用這個工具」顯示，而不是「工具結束、回去思考」
elif name in ("PermissionRequest",):
    pose = "asking"  # 等你授權（Codex）
elif name in ("Notification",):
    kind = str(event.get("notification_type", "")).lower()
    message = str(event.get("message", "")).lower()
    if kind in ASKING_NOTIFICATIONS or (not kind and "permission" in message):
        pose = "asking"
elif name in ("SessionStart",):
    pose = "idle"
elif name in ("UserPromptSubmit", "BeforeAgent"):
    set_turn_tools(0)
    pose = "listening"
elif name in ("PreToolUse", "BeforeTool"):
    set_turn_tools(turn_tools() + 1)
    pose = pose_for_tool(tool)
elif name in ("PostToolUse", "AfterTool"):
    pose = "oops" if tool_failed() else "thinking"
elif name in ("PostToolUseFailure",):
    pose = "oops"
elif name in ("Stop", "AfterAgent"):
    if event.get("error"):  # Antigravity：出錯停下
        pose = "oops"
    else:
        pose = "proud" if turn_tools() >= BIG_JOB_TOOLS else "happy"
    set_turn_tools(0)
elif name in ("StopFailure",):
    # 這一輪因為出錯停下（介面報錯、超限……；Claude Code、WorkBuddy）
    pose = "oops"
    name = "Stop"
    set_turn_tools(0)
elif name in ("Interrupt", "StopCancelled"):
    # 被你打斷（Codex 的 Interrupt、Grok Build 的 StopCancelled）：嚇一跳，然後回去待機；網頁把 Stop + surprised 當成「被打斷」
    pose = "surprised"
    name = "Stop"
    set_turn_tools(0)
elif name in ("PreCompact", "PreCompress"):
    pose = "compact"  # 壓縮上下文：把一大堆東西往小箱子裡塞
elif name in ("PostCompact",):
    if str(event.get("trigger", "")).lower() == "manual":
        # 手動 /compact：壓完這一輪就結束了，後面不會再有 Stop
        pose = "happy"
        name = "Stop"
        set_turn_tools(0)
    else:
        pose = "thinking"  # 自動壓縮發生在一輪中途，壓完接著幹活
elif name in ("SubagentStart",):
    pose = "delegating"
elif name in ("SubagentStop",):
    pose = "thinking"
elif name in ("BeforeModel", "PreInvocation"):
    pose = "thinking"
elif name in ("SessionEnd",):
    pose = "sleeping"

state = {"pose": pose, "event": name, "tool": tool, "ts": time.time(),
         "host": source_host, "session": turn_sid, "turn": effective_turn}
if pose == "thinking" and name in ("PreInvocation", "PostToolUse", "AfterTool"):
    # 快的工具（ls、讀檔案、grep）從開始到結束常常不到 0.3 秒，桌寵每 0.3 秒才讀一次，
    # 剛寫的工具動作會被「回去思考」直接蓋掉（Antigravity 甚至幾毫秒內就開始下一輪思考）。
    # 所以把它捎帶上：桌寵先演完那個動作再進入思考；已經演過的（同一個 afterTs）桌寵會跳過。
    try:
        prev = json.loads((state_dir / f"{character}-state.json").read_text())
        if (prev.get("event") == "PreToolUse" and time.time() - float(prev.get("ts", 0)) < 3
                and (prev.get("host"), prev.get("session"), prev.get("turn")) == (source_host, turn_sid, effective_turn)):
            state.update(after=prev.get("pose"), afterTs=prev.get("ts"), tool=prev.get("tool", ""))
    except Exception:
        pass

if name == "SessionStart" and str(event.get("source", "")).lower() in ("compact", "resume"):
    # 壓縮 / 恢復不是新的一輪。丟掉它，免得把正在做的動作蓋成待機。
    ledger("dropped", "session-restart")
    sys.exit(0)

if pose:
    # 臨時檔名帶程式號：同一個角色的幾個鉤子同時寫（好幾個對話一起幹活）時不會搶同一個臨時檔案
    tmp = state_dir / f".{character}-state.{os.getpid()}.json"
    tmp.write_text(json.dumps(state))
    for attempt in range(8):
        try:
            tmp.replace(state_dir / f"{character}-state.json")
            break
        except PermissionError:
            # Windows 上桌寵可能正讀著上一份狀態。等一下再換，不要讓觀察者鉤子報錯退出。
            if attempt == 7:
                raise
            time.sleep(0.05)


# 同一個 AI 同時開著好幾個對話在幹活：按會話另外記一份 <角色>-sessions.json（{會話id: {pose, event, ts}}），
# 桌寵看到兩個以上同時在幹活時演「手忙腳亂」，每個對話一張小卡片。幾個鉤子可能同時寫，用檔案鎖排隊
SESSION_KEEP = 10 * 60   # 多久沒動靜的會話不再記


def update_sessions(sid: str, entry) -> None:
    path = state_dir / f"{character}-sessions.json"
    with open(state_dir / f".{character}-sessions.lock", "a+") as lock:
        try:
            if os.name == "nt":
                import msvcrt
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
            else:
                import fcntl
                fcntl.flock(lock, fcntl.LOCK_EX)
        except Exception:
            raise  # 沒拿到共享會話鎖時不能繼續讀改寫，避免覆蓋 Cursor 的卡片。
        try:
            known = json.loads(path.read_text())
        except Exception:
            known = {}
        if not isinstance(known, dict):
            known = {}
        known = {k: v for k, v in known.items() if isinstance(v, dict)}
        now = time.time()
        if entry is None:
            known.pop(sid, None)
        else:
            known[sid] = entry
        known = {k: v for k, v in known.items() if now - float(v.get("ts", 0)) < SESSION_KEEP}
        tmp = state_dir / f".{character}-sessions.json"
        tmp.write_text(json.dumps(known))
        tmp.replace(path)
        if os.name == "nt":
            try:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            except Exception:
                pass


session_id = str(event.get("session_id") or event.get("sessionId") or event.get("conversationId") or "")
if session_id and (pose or name == "SessionEnd"):
    try:
        update_sessions(session_id, None if name == "SessionEnd" else
                        {"pose": state.get("after") or pose if pose == "thinking" else pose, "event": name, "ts": time.time()})
    except Exception:
        pass

ledger("shown", f"{character} {pose}") if pose else ledger("dropped", "unknown-event")

_event_lock.close()  # 自動啟動可能查父程式 / 等系統響應，不讓下一條鉤子等這一步。

if name in LAUNCH_EVENTS:
    ensure_pet_logged(f"{character} {name}")

if arg_event:
    print("{}")
sys.exit(0)
