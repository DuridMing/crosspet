#!/usr/bin/env python3
"""把 CrossPet 接入各個 AI（或撤銷）。install.sh / uninstall.sh 呼叫它，也可以單獨用：

    python3 tools/integrate.py install   claude-hooks | claude-mod | codex | deepseek | gemini | antigravity | workbuddy | zcode | hermes | qwen | cursor | grok
    python3 tools/integrate.py uninstall claude-hooks | claude-mod | codex | deepseek | gemini | antigravity | workbuddy | zcode | hermes | qwen | cursor | grok
    python3 tools/integrate.py status

原則：
- 改任何檔案前先備份成 <檔案>.bak-crosspet-<時間>
- 只新增 CrossPet 自己的條目，不動你原有的配置；解除安裝只刪 CrossPet 的條目
- 識別 CrossPet 條目的標記：命令裡含 crosspet-hook.py，或外掛 id 為 crosspet
"""
import json
import re
import os
import shutil
import sys
import time
from pathlib import Path

HOME = Path.home()
REPO = Path(__file__).resolve().parent.parent
SUPPORT = Path(os.environ.get("CROSSPET_DATA_DIR") or (str(Path(os.environ.get("LOCALAPPDATA", str(HOME / "AppData/Local"))) / "CrossPet") if os.name == "nt" else str(HOME / "Library/Application Support/CrossPet")))
MARK = "crosspet-hook.py"


QUIET = False   # 自動接回去（heal）時不備份：只補 CrossPet 自己的一項，備份多了會把之前真正有用的備份擠掉


def backup(path: Path) -> None:
    if path.exists() and not QUIET:
        dst = path.with_name(f"{path.name}.bak-crosspet-{time.strftime('%Y%m%d-%H%M%S')}")
        shutil.copy2(path, dst)
        print(f"  已備份 {path} → {dst.name}")
        # 只留最近 3 份，免得每次接入 / 更新都多一個
        for old in sorted(path.parent.glob(f"{path.name}.bak-crosspet-*"))[:-3]:
            try:
                old.unlink()
            except OSError:
                pass


def load_json(path: Path) -> dict:
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8").strip()
    return json.loads(text) if text else {}


def save_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def ours(group: dict) -> bool:
    return any(MARK in str(h.get("command", "")) for h in group.get("hooks", []))


def without_ours(groups: list) -> list:
    """Keep other commands even when they share a matcher group with CrossPet."""
    kept = []
    for group in groups:
        if not ours(group):
            kept.append(group)
            continue
        hooks = [h for h in group.get("hooks", []) if MARK not in str(h.get("command", ""))]
        if hooks:
            kept.append({**group, "hooks": hooks})
    return kept


def win_arg(path: Path) -> str:
    """鉤子命令裡的一個路徑。各家 AI 在 Windows 上用不同的殼跑鉤子（cmd、PowerShell、Git Bash），
    帶引號的寫法總有一種會壞：cmd 會剝掉首尾引號，PowerShell 會把開頭帶引號的東西當成字串而不是命令。
    所以儘量不加引號：沒有空格直接用正斜槓路徑；有空格先換成 8.3 短路徑（不含空格）；都不行才加引號。"""
    text = str(path)
    if " " in text:
        try:
            import ctypes
            buf = ctypes.create_unicode_buffer(1024)
            if ctypes.windll.kernel32.GetShortPathNameW(text, buf, 1024) and " " not in buf.value:
                text = buf.value
        except Exception:
            pass
    text = Path(text).as_posix()
    return f'"{text}"' if " " in text else text


def load_template(template: Path, via_cmd: bool = False) -> dict:
    """讀接入模板；在 Windows 上把 macOS 的 python3 + $HOME 路徑換成隨包 Python 和本機鉤子指令碼的絕對路徑。
    via_cmd：這個 AI 用 cmd /c 跑鉤子（Antigravity），命令以引號開頭時要再包一層引號，抵消 cmd 剝引號。"""
    data = load_json(template)
    if os.name != "nt":
        return data
    SUPPORT.mkdir(parents=True, exist_ok=True)
    shutil.copy2(REPO / "integrations/crosspet-hook.py", SUPPORT / "crosspet-hook.py")
    prefix = f"{win_arg(Path(sys.executable))} {win_arg(SUPPORT / MARK)}"
    original = 'python3 "$HOME/Library/Application Support/CrossPet/crosspet-hook.py"'

    def command(value: str) -> str:
        cmd = prefix + value[len(original):]
        return f'"{cmd}"' if via_cmd and cmd.startswith('"') else cmd

    def adapt(obj):
        if isinstance(obj, dict):
            return {key: (command(value) if key == "command" and isinstance(value, str) and value.startswith(original) else adapt(value))
                    for key, value in obj.items()}
        if isinstance(obj, list):
            return [adapt(item) for item in obj]
        return obj
    return adapt(data)


def merge_hooks(target: Path, template: Path) -> None:
    """把模板裡的鉤子按事件追加進 target 的 hooks 欄位。
    先清掉所有事件裡 CrossPet 自己的舊條目（包括新版模板已經不用的事件），再按模板加回去，保證不重複、不殘留。"""
    data = load_json(target)
    hooks = data.setdefault("hooks", {})
    for event in list(hooks):
        kept = without_ours(hooks[event])
        if kept:
            hooks[event] = kept
        else:
            del hooks[event]
    for event, groups in load_template(template)["hooks"].items():
        hooks[event] = hooks.get(event, []) + groups
    backup(target)
    save_json(target, data)
    print(f"  已寫入 {target}")


def remove_hooks(target: Path) -> None:
    if not target.exists():
        return
    data = load_json(target)
    hooks = data.get("hooks", {})
    changed = False
    for event in list(hooks):
        kept = without_ours(hooks[event])
        if kept != hooks[event]:
            changed = True
            if kept:
                hooks[event] = kept
            else:
                del hooks[event]
    if not hooks and "hooks" in data:
        del data["hooks"]
    if changed:
        backup(target)
        save_json(target, data)
        print(f"  已從 {target} 移除 CrossPet 鉤子")


def found(path: Path, app: str, ours: tuple = (), quiet: bool = False) -> bool:
    """接入前先確認這個 AI 裝過：它的配置目錄在，而且裡面有它自己的東西。
    ours 是 CrossPet 自己會往這個目錄裡寫的檔案 / 資料夾（連同備份），只有這些的話不算裝過——
    舊版接入工具在沒裝的電腦上憑空建過這些，不能因為它們還在就顯示「已接入」"""
    if not ours and path.is_dir():
        return True  # CrossPet 從來不會建的目錄：在就說明裝過
    try:
        own = [p for p in path.iterdir() if p.name not in ours and ".bak-crosspet-" not in p.name and p.name != ".DS_Store"]
    except OSError:
        own = []
    if own:
        return True
    if not quiet:
        print(f"  沒找到 {app} 的配置（{path}），請先安裝並開啟一次 {app} 再接入")
    return False


# ---- Claude Code ----
# 各 AI 的配置目錄都可以用環境變數挪走：CLAUDE_CONFIG_DIR、CODEX_HOME、DSH_HOME，沒設就用使用者目錄下的預設位置
CLAUDE_HOME = Path(os.environ.get("CLAUDE_CONFIG_DIR") or HOME / ".claude")
CLAUDE_SETTINGS = CLAUDE_HOME / "settings.json"
CLAUDE_MOD_DIR = CLAUDE_HOME / "mods/crosspet"
CLAUDE_OURS = ("settings.json", "mods")


def claude_hooks(install: bool) -> None:
    if install:
        if not found(CLAUDE_HOME, "Claude Code", CLAUDE_OURS):
            return
        merge_hooks(CLAUDE_SETTINGS, REPO / "integrations/claude-code/hooks.json")
        print("  → 新開的 Claude Code 會話生效（已開的會話要重開）")
    else:
        remove_hooks(CLAUDE_SETTINGS)


MOD_MIN_CLI = (2, 1, 287)   # 終端裡的 Claude Code 從這一版起預設載入 mod（桌面 App 自帶的那份從 2.1.286 起）


def claude_cli_version():
    """終端裡 claude --version 的版本號；找不到 claude 時返回 None"""
    import subprocess
    candidates = [shutil.which("claude"), str(HOME / ".local/bin/claude"), str(HOME / ".claude/local/claude")]
    for exe in [c for c in candidates if c and Path(c).exists()]:
        try:
            out = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=10).stdout
        except Exception:
            continue
        m = re.search(r"(\d+)\.(\d+)\.(\d+)", out)
        if m:
            return tuple(int(x) for x in m.groups())
    return None


def claude_mod(install: bool) -> None:
    if not install and not CLAUDE_MOD_DIR.exists() and not CLAUDE_SETTINGS.exists():
        return  # 從沒裝過：別憑空建出一個 settings.json
    if install and not found(CLAUDE_HOME, "Claude Code", CLAUDE_OURS):
        return
    data = load_json(CLAUDE_SETTINGS)
    env = data.setdefault("env", {})
    # 多個目錄用系統的路徑分隔符連線：macOS 是冒號，Windows 是分號（Windows 路徑裡本身就有冒號）
    dirs = [d for d in env.get("CLAUDE_CODE_PLUGIN_DIRS", "").split(os.pathsep) if d]
    dirs = [d for d in dirs if Path(os.path.expanduser(d)) != CLAUDE_MOD_DIR]
    if install:
        if CLAUDE_MOD_DIR.exists():
            shutil.rmtree(CLAUDE_MOD_DIR)
        shutil.copytree(REPO / "integrations/claude-code/mod", CLAUDE_MOD_DIR)
        dirs.append(str(CLAUDE_MOD_DIR))
        print(f"  已複製 mod 到 {CLAUDE_MOD_DIR}")
    else:
        if CLAUDE_MOD_DIR.exists():
            shutil.rmtree(CLAUDE_MOD_DIR)
            print(f"  已刪除 {CLAUDE_MOD_DIR}")
    if dirs:
        env["CLAUDE_CODE_PLUGIN_DIRS"] = os.pathsep.join(dirs)
    else:
        env.pop("CLAUDE_CODE_PLUGIN_DIRS", None)
    if not env:
        data.pop("env", None)
    backup(CLAUDE_SETTINGS)
    save_json(CLAUDE_SETTINGS, data)
    print(f"  已更新 {CLAUDE_SETTINGS} 的 env.CLAUDE_CODE_PLUGIN_DIRS")
    if install:
        print("  → 新開的會話生效。需要終端裡的 Claude Code 2.1.287 以上（桌面 App 2.1.286 以上）")
        v = claude_cli_version()
        if v and v < MOD_MIN_CLI:
            print(f"  ！終端裡的 Claude Code 是 {'.'.join(map(str, v))}，還不會載入 mod：請先更新 Claude Code，"
                  "或者改用標準鉤子（claude-hooks，所有版本可用，只是不顯示額度）")
        print("  → 公司 / 組織賬號如果被管理員禁了自己裝的 mod，或者在 WSL 裡用，mod 不會執行，請改用標準鉤子")


def claude_mod_ok() -> bool:
    """mod 真的接著：資料夾在，而且 settings.json 的 CLAUDE_CODE_PLUGIN_DIRS 裡有它"""
    if not CLAUDE_MOD_DIR.exists():
        return False
    try:
        dirs = load_json(CLAUDE_SETTINGS).get("env", {}).get("CLAUDE_CODE_PLUGIN_DIRS", "")
    except Exception:
        return True   # 正被別的程式寫到一半：先當沒事
    return any(Path(os.path.expanduser(d)) == CLAUDE_MOD_DIR for d in dirs.split(os.pathsep) if d)


def ccswitch_note() -> None:
    """CC Switch 3.x 切換服務商時會整個重寫 ~/.claude/settings.json，CrossPet 的接入跟著被沖掉（4.0 起只換關鍵欄位）"""
    if not (HOME / ".cc-switch").exists():
        return
    version = None
    plist = Path("/Applications/CC Switch.app/Contents/Info.plist")
    if plist.exists():
        try:
            import plistlib
            version = plistlib.loads(plist.read_bytes()).get("CFBundleShortVersionString")
        except Exception:
            pass
    if version and not version.startswith(("1.", "2.", "3.")):
        return
    which = f"CC Switch {version}" if version else "CC Switch（如果是 3.x 版）"
    print(f"  ！檢測到 {which}：切換服務商時會重寫 Claude 的配置，把 CrossPet 的接入一起沖掉。"
          "桌寵開著時會自動接回去（新開的會話生效），建議把 CC Switch 升級到 4.0 以上，就不會再被沖掉")


# 使用者接入過、想一直接著的目標。被別的工具改掉了（比如 CC Switch 3.x 切換服務商會整個重寫 settings.json），
# 桌寵發現後調 `integrate.py heal` 接回去；在 CrossPet 裡撤銷接入的就不再管
WANTED = SUPPORT / "wanted.json"
HEALABLE = {"claude-mod": claude_mod_ok, "claude-hooks": lambda: has_hooks(CLAUDE_SETTINGS)}


def wanted() -> set:
    try:
        return set(json.loads(WANTED.read_text(encoding="utf-8")))
    except Exception:
        return set()


def set_wanted(target: str, on: bool) -> None:
    w = wanted()
    if on == (target in w):
        return
    (w.add if on else w.discard)(target)
    SUPPORT.mkdir(parents=True, exist_ok=True)
    WANTED.write_text(json.dumps(sorted(w)), encoding="utf-8")


def heal() -> list:
    """接過、現在卻不在了的接回去，返回接回去的目標。還沒記過的老使用者：mod 資料夾在就算接過"""
    global QUIET
    if not CLAUDE_SETTINGS.exists():
        return []
    try:
        load_json(CLAUDE_SETTINGS)
    except Exception:
        return []   # 正被別的程式寫到一半，下次再看
    if CLAUDE_MOD_DIR.exists():
        set_wanted("claude-mod", True)
    if has_hooks(CLAUDE_SETTINGS):
        set_wanted("claude-hooks", True)
    healed = []
    import contextlib
    import io
    for target in sorted(wanted() & set(HEALABLE)):
        if HEALABLE[target]():
            continue
        QUIET = True
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                TARGETS[target](True)
        finally:
            QUIET = False
        if HEALABLE[target]():
            healed.append(target)
    return healed


# ---- Codex ----
CODEX_HOOKS = Path(os.environ.get("CODEX_HOME", str(HOME / ".codex"))) / "hooks.json"


def codex(install: bool) -> None:
    if install:
        if not found(CODEX_HOOKS.parent, "Codex", ("hooks.json",)):
            return
        merge_hooks(CODEX_HOOKS, REPO / "integrations/codex/hooks.json")
        for w in codex_warnings():
            print("  ！" + w)
        print("  → 下次開啟 Codex 時要審查並「信任」這些鉤子（CLI 裡用 /hooks），之後才會生效")
    else:
        remove_hooks(CODEX_HOOKS)


# ---- DeepSeek Harness（外掛）----
DSH_BLOCK_START = "# ── CrossPet 桌寵（integrations/deepseek）"


def dsh_profile() -> Path:
    """DeepSeek Harness 當前的 profile 目錄。home 和 profile 名都可能不一樣（有的機器只有 profiles/web），
    所以不寫死：DSH_PROFILE_DIR / DSH_PROFILE 環境變數優先；否則先找已經接入過 CrossPet 的，
    再找 desktop，再取最近改過的那個。一個都沒有時返回預設位置（用來提示）"""
    if os.environ.get("DSH_PROFILE_DIR"):
        return Path(os.environ["DSH_PROFILE_DIR"])
    if os.environ.get("DSH_HOME"):
        homes = [Path(os.environ["DSH_HOME"])]  # 設了就只認它
    else:
        homes = [HOME / ".dsh"]
        if os.name == "nt" and os.environ.get("APPDATA"):
            homes.append(Path(os.environ["APPDATA"]) / "dsh-desktop/harness")
    profiles = []
    for home in homes:
        try:
            profiles += [p for p in (home / "profiles").iterdir()
                         if p.is_dir() and not p.name.startswith(".") and p.name != "node_modules"
                         and ((p / "package.json").exists() or (p / "cordis.patch.yml").exists())]
        except OSError:
            pass
    if not profiles:
        return homes[0] / "profiles/desktop"
    wanted = os.environ.get("DSH_PROFILE")
    for p in profiles:
        if wanted and p.name == wanted:
            return p

    def ours(p: Path) -> bool:
        try:
            return DSH_BLOCK_START in (p / "cordis.patch.yml").read_text(encoding="utf-8")
        except OSError:
            return False

    def mtime(p: Path) -> float:
        return max((f.stat().st_mtime for f in (p / "cordis.patch.yml", p / "package.json") if f.exists()), default=0)

    return (next((p for p in profiles if ours(p)), None) or next((p for p in profiles if p.name == "desktop"), None)
            or max(profiles, key=mtime))


DSH_PROFILE = dsh_profile()
DSH_PLUGIN_NAME = "@local/dsh-plugin-crosspet"
DSH_PLUGIN_DIR = SUPPORT / "dsh-plugin-crosspet"
DSH_BLOCK_END = "# ── CrossPet 結束 ──"
DSH_BLOCK = f"""
{DSH_BLOCK_START} ──
# 把 DeepSeek 的工作狀態和餘額寫給桌寵；移除本段即恢復原樣。
- insert:
    - id: crosspet
      name: '{DSH_PLUGIN_NAME}'
{DSH_BLOCK_END}
"""
DSH_RESTORED = SUPPORT / "dsh-patch-restored"   # 一次性找回做過了（見 dsh_restore）


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip())


def dsh_strip(text: str) -> str:
    """去掉 CrossPet 自己寫進 cordis.patch.yml 的那幾行，別的一行不動。
    Harness 加服務商等配置時會寫到檔案末尾，也就是我們這段的兩行註釋中間（甚至掛在我們的 - insert: 下面），
    所以不能按註釋區間整段刪：只刪兩行註釋、結束註釋、我們那一項；- insert: 下面沒別的東西了才刪它"""
    lines = text.split("\n")
    drop, headers = set(), []
    for i, line in enumerate(lines):
        s = line.strip()
        if s.startswith(DSH_BLOCK_START) or s.startswith("# 把 DeepSeek 的工作狀態和餘額寫給桌寵") or s == DSH_BLOCK_END:
            drop.add(i)
            if s.startswith(DSH_BLOCK_START) and i > 0 and not lines[i - 1].strip():
                drop.add(i - 1)   # 接入時在這段前面空的那一行
        elif re.fullmatch(r"-\s+id:\s*['\"]?crosspet['\"]?", s):
            body = [i]
            j = i + 1
            while j < len(lines) and lines[j].strip() and _indent(lines[j]) > _indent(line) and not lines[j].lstrip().startswith("-"):
                body.append(j)
                j += 1
            if not any(DSH_PLUGIN_NAME in lines[k] for k in body):
                continue
            drop.update(body)
            k = i - 1
            while k >= 0 and (k in drop or not lines[k].strip() or lines[k].lstrip().startswith("#")):
                k -= 1
            if k >= 0 and re.fullmatch(r"-\s*insert:\s*", lines[k]):
                headers.append(k)
    for k in headers:   # 我們那個 - insert: 下面還掛著別人的條目就留著
        after = (lines[j] for j in range(k + 1, len(lines)) if j not in drop)
        nxt = next((l for l in after if l.strip() and not l.lstrip().startswith("#")), "")
        if _indent(nxt) == 0:
            drop.add(k)
    out = "\n".join(l for i, l in enumerate(lines) if i not in drop).rstrip("\n")
    return out + "\n" if out.strip() else ""


def dsh_units(text: str) -> list:
    """把 cordis.patch.yml 拆成一條條配置：頂層每個 - xxx: 一條；- insert: 這種下面是列表的，按列表項拆開"""
    items = []
    for line in text.split("\n"):
        if not line.strip() or (_indent(line) == 0 and line.startswith("#")):
            continue
        if _indent(line) == 0:
            items.append([line])
        elif items:
            items[-1].append(line)
    units = []
    for head, *body in items:
        firsts = [l for l in body if not l.lstrip().startswith("#")]
        if re.fullmatch(r"-\s*\w+:\s*", head) and firsts and firsts[0].lstrip().startswith("- "):
            ind, cur = _indent(firsts[0]), None
            for l in body:
                if _indent(l) == ind and l.lstrip().startswith("- "):
                    cur = [head, l]
                    units.append(cur)
                elif cur:
                    cur.append(l)
        else:
            units.append([head] + body)
    return ["\n".join(u) for u in units]


def dsh_key(unit: str) -> tuple:
    m = re.search(r"^\s*(?:-\s+)?id:\s*['\"]?([^'\"\s#]+)", unit, re.M)
    head = unit.split("\n", 1)[0].strip()
    return ("id", head, m.group(1)) if m else ("text", " ".join(unit.split()))


def dsh_restore(patch: str, patch_path: Path) -> str:
    """一次性找回：1.3.0 及以前的接入工具按註釋區間整段刪，把夾在我們兩行註釋中間的服務商配置刪掉了。
    當時改檔案前都備份過：從備份裡找出被夾在中間的條目，現在檔案裡沒有的（按 id 比對）補回去。只做一次，
    免得以後使用者自己刪掉的又被加回來"""
    if DSH_RESTORED.exists():
        return patch
    have = {dsh_key(u) for u in dsh_units(dsh_strip(patch))}
    restored = []
    for bak in sorted(patch_path.parent.glob(f"{patch_path.name}.bak-crosspet-*"), reverse=True):   # 新的優先
        try:
            text = bak.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        if DSH_BLOCK_START not in text or DSH_BLOCK_END not in text[text.index(DSH_BLOCK_START):]:
            continue
        start = text.index(DSH_BLOCK_START)
        region = text[start:text.index(DSH_BLOCK_END, start) + len(DSH_BLOCK_END)]
        for unit in dsh_units(dsh_strip(region)):
            key = dsh_key(unit)
            if key not in have:
                have.add(key)
                restored.append(unit)
    if restored:
        patch = (patch.rstrip("\n") + "\n" if patch.strip() else "") + "\n".join(restored) + "\n"
        print(f"  已從備份找回 {len(restored)} 條被舊版接入工具誤刪的配置（比如第三方服務商）")
    try:
        DSH_RESTORED.parent.mkdir(parents=True, exist_ok=True)
        DSH_RESTORED.write_text(time.strftime("%Y-%m-%d %H:%M:%S"), encoding="utf-8")
    except OSError:
        pass
    return patch


def make_link(link: Path, target: Path) -> None:
    """macOS 用軟連結；Windows 建軟連結要管理員或開發者模式，改用不需要許可權的目錄聯接（junction）。"""
    if os.name == "nt":
        import _winapi
        _winapi.CreateJunction(str(target), str(link))
    else:
        link.symlink_to(target)


def remove_link(link: Path) -> None:
    """只拆連結本身，絕不順著連結刪到外掛本體。"""
    if link.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(link)):
        os.rmdir(link) if os.name == "nt" and link.is_dir() else link.unlink()
    elif link.exists():
        if link.is_dir():
            shutil.rmtree(link)
        else:
            link.unlink()


# 合法的版本號（x.y.z，可帶 -字尾）。DeepSeek Harness 裝 / 更新任何外掛時都會校驗每個外掛 package.json 的版本，
# 寫個不合法的（1.3.8-beta.2 帶的 "1.3.8.1"）會把使用者裝別的外掛也攔住
SEMVER = r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"


def valid_version(v: str) -> bool:
    return bool(re.match(SEMVER, v or ""))


def deepseek(install: bool) -> None:
    if not DSH_PROFILE.exists():
        print(f"  沒找到 {DSH_PROFILE}，請先開啟一次 DeepSeek Harness 桌面版"
              "（如果 Harness 裝在別處，可以用環境變數 DSH_PROFILE_DIR 指定 profile 目錄）")
        return
    pkg_path = DSH_PROFILE / "package.json"
    patch_path = DSH_PROFILE / "cordis.patch.yml"
    link = DSH_PROFILE / "node_modules/@local/dsh-plugin-crosspet"
    pkg = load_json(pkg_path)
    deps = pkg.setdefault("dependencies", {})
    patch = patch_path.read_text(encoding="utf-8") if patch_path.exists() else ""
    patch = dsh_restore(dsh_strip(patch), patch_path)   # 先去掉舊的那幾行（只去我們自己的）；找回要趕在下面備份刪掉老備份之前
    if install:
        if DSH_PLUGIN_DIR.exists():
            shutil.rmtree(DSH_PLUGIN_DIR)
        shutil.copytree(REPO / "integrations/deepseek/dsh-plugin-crosspet", DSH_PLUGIN_DIR)
        # Harness 的外掛列表顯示 package.json 裡的版本：跟外掛程式碼裡的 PLUGIN_VERSION 保持一致
        try:
            ver = re.search(r'PLUGIN_VERSION = "([^"]+)"', (DSH_PLUGIN_DIR / "lib/index.js").read_text(encoding="utf-8")).group(1)
            pj = load_json(DSH_PLUGIN_DIR / "package.json")
            if valid_version(ver):   # 不合法就不寫，留著隨包 package.json 裡的（測試保證兩邊一致）
                pj["version"] = ver
                save_json(DSH_PLUGIN_DIR / "package.json", pj)
        except Exception:
            pass
        deps[DSH_PLUGIN_NAME] = f"link:{DSH_PLUGIN_DIR.as_posix()}"
        link.parent.mkdir(parents=True, exist_ok=True)
        remove_link(link)
        make_link(link, DSH_PLUGIN_DIR)
        patch = patch.rstrip("\n") + "\n" + DSH_BLOCK
        print(f"  已安裝外掛到 {DSH_PLUGIN_DIR}")
    else:
        deps.pop(DSH_PLUGIN_NAME, None)
        remove_link(link)
        if DSH_PLUGIN_DIR.exists():
            shutil.rmtree(DSH_PLUGIN_DIR)
    backup(pkg_path)
    save_json(pkg_path, pkg)
    backup(patch_path)
    patch_path.write_text(patch, encoding="utf-8")
    print(f"  已更新 {pkg_path.name} 和 {patch_path.name}")
    if os.name == "nt":
        print("  → 完全退出 DeepSeek Harness（包括工作列右下角的托盤圖示）再開啟生效")
    else:
        print("  → 完全退出（⌘Q）再開啟 DeepSeek Harness 生效")


# ---- WorkBuddy（騰訊）----
# 桌面版裡跑的是 CodeBuddy 的智慧體，鉤子格式和 Claude Code 一樣，配置目錄由安裝包決定：
# 國際版 ~/.workbuddy-ai，國內版 ~/.workbuddy（專享版另有名字，可用 WORKBUDDY_CONFIG_DIR 指定）。
# 每條事件都帶當前模型名，鉤子指令碼按它選角色（crosspet-hook.py workbuddy）
def workbuddy_dirs() -> list:
    env = os.environ.get("WORKBUDDY_CONFIG_DIR")
    dirs = [Path(env)] if env else [HOME / ".workbuddy-ai", HOME / ".workbuddy"]
    # 智慧體真正用的目錄裡會有它自己的東西（下載的元件、記憶、人設檔案）；只有 device-id、logs 的不算
    return [d for d in dirs if any((d / n).exists() for n in ("binaries", "memory", "SOUL.md"))]


def workbuddy(install: bool) -> None:
    dirs = workbuddy_dirs()
    if install:
        if not dirs:
            print("  沒找到 WorkBuddy 的配置（~/.workbuddy-ai 或 ~/.workbuddy），請先安裝並開啟一次 WorkBuddy 再接入")
            return
        for d in dirs:
            merge_hooks(d / "settings.json", REPO / "integrations/workbuddy/hooks.json")
        print("  → 完全退出再開啟 WorkBuddy 生效；用 GPT / Claude / Gemini / DeepSeek 時換成對應角色，其他模型由當前角色來演")
    else:
        for d in dirs:
            remove_hooks(d / "settings.json")


# ---- ZCode（智譜）----
# 鉤子寫在 ~/.zcode/cli/config.json 的 hooks 裡，格式和 Claude Code 不同：要開啟 hooks.enabled，
# 事件放在 hooks.events 下，命令和引數分開寫（直接啟動程式，不經過 shell，所以路徑要寫成絕對路徑）。
# 模型名只在會話開始時給，鉤子指令碼按會話記住（crosspet-hook.py zcode）
ZCODE_HOME = HOME / ".zcode"
ZCODE_CONFIG = ZCODE_HOME / "cli/config.json"
ZCODE_FLAG = SUPPORT / "zcode-hooks-enabled-by-crosspet"   # 鉤子總開關是 CrossPet 開啟的：撤銷時關回去
ZCODE_EVENTS = ("SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "Stop")


def zcode_ours(group: dict) -> bool:
    return any(MARK in " ".join(map(str, [h.get("command", "")] + list(h.get("args", [])))) for h in group.get("hooks", []))


def zcode(install: bool) -> None:
    if install and not found(ZCODE_HOME, "ZCode", ("cli",)):
        return
    if not install and not ZCODE_CONFIG.exists():
        return
    data = load_json(ZCODE_CONFIG) if ZCODE_CONFIG.exists() else {}
    hooks = data.setdefault("hooks", {})
    events = hooks.setdefault("events", {})
    for ev in list(events):
        kept = [g for g in events[ev] if not zcode_ours(g)]
        if kept:
            events[ev] = kept
        else:
            del events[ev]
    if install:
        if os.name == "nt":
            SUPPORT.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / "integrations/crosspet-hook.py", SUPPORT / MARK)
            command = sys.executable
        else:
            command = "python3"
        hook = {"type": "process", "command": command, "args": [str(SUPPORT / MARK), "zcode"]}
        for ev in ZCODE_EVENTS:
            events.setdefault(ev, []).append({"hooks": [dict(hook)]})
        if hooks.get("enabled") is not True:
            SUPPORT.mkdir(parents=True, exist_ok=True)
            ZCODE_FLAG.write_text("1")
            hooks["enabled"] = True
    else:
        if ZCODE_FLAG.exists():   # 開關原本是關著的（或者沒有）：還原。但你自己還有別的鉤子時不關，免得把它們也停了
            if not events:
                hooks["enabled"] = False
            ZCODE_FLAG.unlink()
        if not events:
            hooks.pop("events", None)
        # 只剩 CrossPet 留下的東西（空的事件表、被我們關回去的開關）才整段拿掉；有別的設定一律保留
        if set(hooks) <= {"enabled"} and hooks.get("enabled") is not True:
            data.pop("hooks", None)
    if ZCODE_CONFIG.exists():
        backup(ZCODE_CONFIG)
    ZCODE_CONFIG.parent.mkdir(parents=True, exist_ok=True)
    save_json(ZCODE_CONFIG, data)
    print(f"  已更新 {ZCODE_CONFIG}")
    if install:
        print("  → 新開的 ZCode 會話生效；用 GLM 時換成 GLM 角色（還沒裝這個角色時由當前角色來演），用別家模型時換成對應角色")


def has_zcode() -> bool:
    try:
        return any(zcode_ours(g) for gs in load_json(ZCODE_CONFIG).get("hooks", {}).get("events", {}).values() for g in gs)
    except Exception:
        return False


# ---- Gemini CLI ----
GEMINI_SETTINGS = HOME / ".gemini/settings.json"


def gemini(install: bool) -> None:
    if install:
        if not found(GEMINI_SETTINGS.parent, "Gemini CLI", ("settings.json", "config")):
            return
        merge_hooks(GEMINI_SETTINGS, REPO / "integrations/gemini/hooks.json")
        print("  → 只對 Gemini CLI 有效；Gemini 桌面 App 沒有鉤子介面")
    else:
        remove_hooks(GEMINI_SETTINGS)


# ---- Qwen Code（阿里）----
# 使用者設定在 ~/.qwen/settings.json，鉤子寫在裡面的 hooks 欄位：格式和 Claude Code 一樣
#（要寫 type: "command"，matcher 支援 "*" / ".*"），不是它上游 Gemini CLI 的那套。
# timeout 的單位也跟著 Claude Code 是「秒」：Gemini CLI 模板裡那個 5000 在這裡會被當成毫秒相容值。
# 沒有能把 ~/.qwen 整體挪走的環境變數（只有 QWEN_CODE_SYSTEM_SETTINGS_PATH 這類系統級路徑），所以按預設位置找。
QWEN_SETTINGS = HOME / ".qwen/settings.json"


def qwen(install: bool) -> None:
    if install:
        if not found(QWEN_SETTINGS.parent, "Qwen Code", ("settings.json",)):
            return
        merge_hooks(QWEN_SETTINGS, REPO / "integrations/qwen/hooks.json")
        print("  → 新開的 Qwen Code 會話生效（鉤子在會話開始時讀配置）")
    else:
        remove_hooks(QWEN_SETTINGS)


# ---- Grok Build（xAI）----
# 個人鉤子放 ~/.grok/hooks/*.json，格式和 Claude Code 一樣（matcher 組 + type: "command"，timeout 單位是秒）。
# CrossPet 單獨佔一個檔案 crosspet.json：接入就寫這個檔案，撤銷就刪掉，不碰使用者自己的鉤子檔案。
# 它也會讀 ~/.claude/settings.json 的鉤子：鉤子指令碼看到 GROK_HOOK_EVENT 時會把那邊觸發的丟掉（見 crosspet-hook.py）。
# 新資料夾第一次用要先「信任」（/hooks-trust），之前個人鉤子也不跑；grok -p 無介面執行時當作已信任
GROK_HOME = Path(os.environ.get("GROK_HOME") or HOME / ".grok")
GROK_HOOKS = GROK_HOME / "hooks" / "crosspet.json"


def grok(install: bool) -> None:
    if install:
        if not found(GROK_HOME, "Grok Build", ("hooks",)):
            return
        if GROK_HOOKS.exists():
            backup(GROK_HOOKS)
        save_json(GROK_HOOKS, load_template(REPO / "integrations/grok/hooks.json"))
        print(f"  已寫入 {GROK_HOOKS}")
        print("  → 新開的 Grok Build 會話生效；在一個資料夾裡第一次用時，要先在 Grok 裡用 /hooks-trust 信任這個資料夾")
    elif GROK_HOOKS.exists():
        GROK_HOOKS.unlink()
        print(f"  已刪除 {GROK_HOOKS}")


def has_grok() -> bool:
    try:
        return MARK in GROK_HOOKS.read_text(encoding="utf-8")
    except OSError:
        return False


# ---- Antigravity ----
# 它的 hooks.json 是「名字 → 各事件」的格式，CrossPet 只佔 "crosspet" 這一項
ANTIGRAVITY_HOOKS = HOME / ".gemini/config/hooks.json"


def antigravity(install: bool) -> None:
    if not install and not ANTIGRAVITY_HOOKS.exists():
        return
    # Antigravity 第一次開啟時會建 ~/.gemini/antigravity；只有 Gemini CLI 時沒有這個目錄
    if install and not found(ANTIGRAVITY_HOOKS.parent.parent / "antigravity", "Antigravity"):
        return
    data = load_json(ANTIGRAVITY_HOOKS) if ANTIGRAVITY_HOOKS.exists() else {}
    if install:
        data.update(load_template(REPO / "integrations/antigravity/hooks.json", via_cmd=True))
    elif data.pop("crosspet", None) is None:
        return
    if ANTIGRAVITY_HOOKS.exists():
        backup(ANTIGRAVITY_HOOKS)
    ANTIGRAVITY_HOOKS.parent.mkdir(parents=True, exist_ok=True)
    save_json(ANTIGRAVITY_HOOKS, data)
    print(f"  已更新 {ANTIGRAVITY_HOOKS}")
    if install:
        print("  → 重開 Antigravity 生效；桌面版和 Antigravity CLI 都有效")


def has_antigravity() -> bool:
    try:
        return "crosspet" in load_json(ANTIGRAVITY_HOOKS)
    except Exception:
        return False


# ---- Hermes Agent（Nous Research）----
# 用 Hermes 的 Python 外掛（不是 config.yaml 裡的 shell 鉤子：那個每條命令第一次都要你點同意，閘道器裡還不生效）。
# 外掛放在 <HERMES_HOME>/plugins/crosspet/，要寫進 config.yaml 的 plugins.enabled 才會載入：
# 有 hermes 命令就用 `hermes plugins enable crosspet`，沒有就小心地改文本（看不懂的寫法不動，提示你手動開）
HERMES_HOME = Path(os.environ.get("HERMES_HOME") or (str(Path(os.environ.get("LOCALAPPDATA", str(HOME / "AppData/Local"))) / "hermes") if os.name == "nt" else str(HOME / ".hermes")))
HERMES_PLUGIN = HERMES_HOME / "plugins/crosspet"
HERMES_CONFIG = HERMES_HOME / "config.yaml"


def hermes_cli():
    for c in (shutil.which("hermes"), HOME / ".local/bin/hermes", HERMES_HOME / "hermes-agent/venv/bin/hermes",
              HERMES_HOME / "hermes-agent/venv/Scripts/hermes.exe"):
        if c and Path(c).exists():
            return str(c)
    return None


def hermes_enabled_edit(enable: bool) -> bool:
    """在 config.yaml 的 plugins.enabled 里加上 / 去掉 crosspet。只認常見寫法，認不出返回 False"""
    text = HERMES_CONFIG.read_text(encoding="utf-8") if HERMES_CONFIG.exists() else ""
    lines = [("plugins:" if re.match(r"^plugins:\s*\{\s*\}\s*$", l) else l) for l in text.splitlines()]   # plugins: {} 當成空的一段
    top = next((i for i, l in enumerate(lines) if re.match(r"^plugins:\s*(#.*)?$", l)), None)
    if top is None:
        if re.search(r"^plugins:", text, re.M):
            return False   # plugins: 後面跟著行內寫法，不碰
        if enable:
            lines += ["plugins:", "  enabled:", "    - crosspet"]
    else:
        end = next((i for i in range(top + 1, len(lines)) if lines[i].strip() and not lines[i].startswith((" ", "\t", "#"))), len(lines))
        en = next((i for i in range(top + 1, end) if re.match(r"^\s+enabled:", lines[i])), None)
        if en is None:
            if enable:
                lines[top + 1:top + 1] = ["  enabled:", "    - crosspet"]
        else:
            m = re.match(r"^(\s+)enabled:\s*(.*?)\s*(#.*)?$", lines[en])
            rest = m.group(2)
            if rest in ("", "[]"):
                items_end = next((i for i in range(en + 1, end) if lines[i].strip() and not re.match(r"^\s+-\s", lines[i])), end)
                items = [i for i in range(en + 1, items_end) if re.match(r"^\s+-\s*['\"]?crosspet['\"]?\s*$", lines[i])]
                if enable and not items:
                    indent = re.match(r"^(\s*)", lines[en + 1]).group(1) if en + 1 < items_end else m.group(1) + "  "
                    lines[en] = f"{m.group(1)}enabled:"
                    lines.insert(en + 1, f"{indent}- crosspet")
                if not enable and items:
                    for i in reversed(items):
                        del lines[i]
                    if not any(re.match(r"^\s+-\s", lines[i]) for i in range(en + 1, min(items_end - len(items), len(lines)))):
                        lines[en] = f"{m.group(1)}enabled: []"   # 刪空了：寫成空列表
            else:
                return False   # enabled: [a, b] 這類行內寫法，不碰
    new = "\n".join(lines) + "\n"
    if new != (text if text.endswith("\n") else text + "\n"):
        backup(HERMES_CONFIG)
        HERMES_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        HERMES_CONFIG.write_text(new, encoding="utf-8")
    return True


def hermes(install: bool) -> None:
    if install and not found(HERMES_HOME, "Hermes Agent"):
        return
    cli = hermes_cli()
    if install:
        if os.name == "nt":
            SUPPORT.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / "integrations/crosspet-hook.py", SUPPORT / MARK)
        if HERMES_PLUGIN.exists():
            shutil.rmtree(HERMES_PLUGIN)
        shutil.copytree(REPO / "integrations/hermes/crosspet", HERMES_PLUGIN, ignore=shutil.ignore_patterns("__pycache__"))
        save_json(HERMES_PLUGIN / "crosspet.json", {"hook": str(SUPPORT / MARK)})
        print(f"  已裝外掛到 {HERMES_PLUGIN}")
    ok = False
    if cli:
        import subprocess
        try:
            r = subprocess.run([cli, "plugins", "enable" if install else "disable", "crosspet"], capture_output=True, text=True, timeout=60)
            ok = r.returncode == 0
        except Exception:
            ok = False
    if not ok:
        ok = hermes_enabled_edit(install)
    if not install and HERMES_PLUGIN.exists():
        shutil.rmtree(HERMES_PLUGIN)
        print(f"  已刪除 {HERMES_PLUGIN}")
    if install:
        if ok:
            print(f"  已在 {HERMES_CONFIG} 裡啟用外掛 crosspet → 新開的 Hermes 會話生效；按當前模型換角色，沒有對應角色的模型由當前角色來演")
        else:
            print("  ！沒能自動啟用外掛，請執行一次：hermes plugins enable crosspet")


def has_hermes() -> bool:
    return HERMES_PLUGIN.is_dir() and (HERMES_PLUGIN / "plugin.yaml").exists()


# ---- Cursor ----
# Cursor 的 hooks.json 是扁平的：每個事件直接是命令陣列，不是 Codex / Claude 那種 {hooks:[...]} 分組。
CURSOR_MARK = "crosspet-cursor-bridge.py"
CURSOR_HOOKS = HOME / ".cursor" / "hooks.json"


def load_cursor_template() -> dict:
    data = load_json(REPO / "integrations/cursor/hooks.json")
    SUPPORT.mkdir(parents=True, exist_ok=True)
    shutil.copy2(REPO / "integrations/cursor/crosspet-cursor-bridge.py", SUPPORT / "crosspet-cursor-bridge.py")
    if os.name != "nt":
        return data
    prefix = f"{win_arg(Path(sys.executable))} {win_arg(SUPPORT / 'crosspet-cursor-bridge.py')}"
    original = 'python3 "$HOME/Library/Application Support/CrossPet/crosspet-cursor-bridge.py"'

    def command(value: str) -> str:
        if isinstance(value, str) and value.startswith(original):
            return prefix + value[len(original):]
        return value

    def adapt(obj):
        if isinstance(obj, dict):
            return {key: command(value) if key == "command" else adapt(value) for key, value in obj.items()}
        if isinstance(obj, list):
            return [adapt(item) for item in obj]
        return obj
    return adapt(data)


def cursor_ours(entry) -> bool:
    return isinstance(entry, dict) and CURSOR_MARK in str(entry.get("command", ""))


def cursor_character(entry):
    """Read only our optional final character argument; do not execute a shell command.
    沒寫角色引數（預設）返回空串：認不出的模型由當前角色來演"""
    match = re.search(r'''crosspet-cursor-bridge\.py["']?(?:\s+(?:"([\w-]+)"|'([\w-]+)'|([\w-]+)))?\s*$''',
                      str(entry.get("command", "")))
    return next((arg for arg in match.groups() if arg is not None), "") if match else None


def merge_cursor_hooks(target: Path) -> bool:
    data = load_json(target)
    if not isinstance(data, dict) or not isinstance(data.setdefault("hooks", {}), dict):
        print(f"  ！{target} 不是 Cursor 的鉤子格式（頂層要是 {{\"hooks\": {{...}}}}），沒有改")
        return False
    data.setdefault("version", 1)
    hooks = data["hooks"]
    old = [entry for entries in hooks.values() if isinstance(entries, list) for entry in entries if cursor_ours(entry)]
    roles = {cursor_character(entry) for entry in old}
    if None in roles or len(roles) > 1:
        print(f"  ！{target} 的 CrossPet 角色引數不一致或命令格式無法識別，保留原配置；請先統一橋接命令末尾的角色 id 後再更新")
        return False
    character = next(iter(roles), "")
    # 先檢查新模板會使用的事件，避免發現舊的分組格式時只更新了一半。
    template_events = load_json(REPO / "integrations/cursor/hooks.json")["hooks"]
    for event in template_events:
        current = hooks.get(event, [])
        if not isinstance(current, list) or any(isinstance(entry, dict) and "hooks" in entry and "command" not in entry for entry in current):
            print(f"  ！{target} 的 {event} 不是 Cursor 的鉤子格式，保留原配置")
            return False
    # 退役事件也清理，只移除本橋條目；第三方命令及其它設定原樣留下。
    for event in list(hooks):
        current = hooks[event]
        if not isinstance(current, list):
            continue
        kept = [entry for entry in current if not cursor_ours(entry)]
        if kept or kept == current:
            hooks[event] = kept
        else:
            del hooks[event]
    for event, entries in load_cursor_template()["hooks"].items():
        for entry in entries:
            if character:
                entry["command"] += " " + character
        hooks[event] = hooks.get(event, []) + entries
    backup(target)
    save_json(target, data)
    print(f"  已寫入 {target}")
    return True


def remove_cursor_hooks(target: Path) -> None:
    if not target.exists():
        return
    data = load_json(target)
    hooks = data.get("hooks") if isinstance(data, dict) else None
    if not isinstance(hooks, dict):
        return
    changed = False
    for event in list(hooks):
        current = hooks.get(event)
        if not isinstance(current, list):
            continue
        kept = [entry for entry in current if not cursor_ours(entry)]
        if kept != current:
            changed = True
            if kept:
                hooks[event] = kept
            else:
                del hooks[event]
    if changed:
        backup(target)
        save_json(target, data)
        print(f"  已從 {target} 移除 CrossPet 鉤子")


def has_cursor() -> bool:
    try:
        hooks = load_json(CURSOR_HOOKS).get("hooks", {})
    except Exception:
        return False
    if not isinstance(hooks, dict):
        return False
    return any(cursor_ours(entry) for entries in hooks.values() if isinstance(entries, list) for entry in entries)


def cursor(install: bool) -> None:
    if install:
        if not found(CURSOR_HOOKS.parent, "Cursor", ("hooks.json",)):
            return
        if merge_cursor_hooks(CURSOR_HOOKS):
            print("  → 新開的 Cursor 對話生效。按當前模型換角色，認不出的由當前角色來演；更新保留已有角色 id")
    else:
        remove_cursor_hooks(CURSOR_HOOKS)


TARGETS = {"claude-hooks": claude_hooks, "claude-mod": claude_mod, "codex": codex, "deepseek": deepseek, "gemini": gemini, "antigravity": antigravity, "workbuddy": workbuddy, "zcode": zcode, "hermes": hermes, "qwen": qwen, "cursor": cursor, "grok": grok}


def has_hooks(p: Path) -> bool:
    try:
        return any(ours(g) for gs in load_json(p).get("hooks", {}).values() for g in gs)
    except Exception:
        return False


def installed() -> list:
    dsh = DSH_PROFILE / "cordis.patch.yml"
    # 只算這個 AI 真裝過的：舊版接入工具在沒裝的電腦上憑空建過配置，那些不算「已接入」
    claude = found(CLAUDE_HOME, "", CLAUDE_OURS, quiet=True)
    result = []
    if claude and has_hooks(CLAUDE_SETTINGS): result.append("claude-hooks")
    if claude and claude_mod_ok(): result.append("claude-mod")
    if has_hooks(CODEX_HOOKS) and found(CODEX_HOOKS.parent, "", ("hooks.json",), quiet=True): result.append("codex")
    if dsh.exists() and DSH_BLOCK_START in dsh.read_text(encoding="utf-8"): result.append("deepseek")
    if has_hooks(GEMINI_SETTINGS) and found(GEMINI_SETTINGS.parent, "", ("settings.json", "config"), quiet=True): result.append("gemini")
    if has_antigravity() and (ANTIGRAVITY_HOOKS.parent.parent / "antigravity").is_dir(): result.append("antigravity")
    if any(has_hooks(d / "settings.json") for d in workbuddy_dirs()): result.append("workbuddy")
    if has_zcode() and found(ZCODE_HOME, "", ("cli",), quiet=True): result.append("zcode")
    if has_hermes(): result.append("hermes")
    if has_hooks(QWEN_SETTINGS) and found(QWEN_SETTINGS.parent, "", ("settings.json",), quiet=True): result.append("qwen")
    if has_cursor() and found(CURSOR_HOOKS.parent, "", ("hooks.json",), quiet=True): result.append("cursor")
    if has_grok(): result.append("grok")
    return result


# ---- 診斷（反饋時附帶）----
# 一次把能想到的接入資訊都列出來，不用遇到一個問題再加一項：每個 AI 的配置在哪、在不在、CrossPet 的條目在不在、
# 最近一次收到它的訊息是什麼時候、相關的環境變數、Codex 可能在 WSL 裡 / CODEX_HOME 只在終端裡設了……
# 只有路徑、有沒有、時間和版本號，不讀對話、不帶任何 Key / 令牌（使用者目錄換成 ~）
STATE_DIR = Path(os.environ.get("CROSSPET_STATE_DIR") or (str(SUPPORT / "state") if os.name == "nt" else "/tmp/crosspet"))


def _plugin_version(path: Path):
    try:
        return re.search(r'PLUGIN_VERSION = "([^"]+)"', path.read_text(encoding="utf-8")).group(1)
    except Exception:
        return None


def plugin_status(ids=None, bundled: Path = None, plugin_dir: Path = None, state_dir: Path = None) -> list:
    """接入的 AI 裡帶版本號的外掛（目前只有 DeepSeek Harness）：自帶的 / 裝到 Harness 那邊的 / Harness 里正在跑的版本。
    只讀，不寫任何檔案。設定頁和桌寵據此提醒使用者自己點「更新」——不自動改別家的配置（1.3.0 自動重新整理刪過使用者的服務商配置）"""
    ids = installed() if ids is None else ids
    if "deepseek" not in ids:
        return []
    bundled = bundled or (REPO / "integrations/deepseek/dsh-plugin-crosspet/lib/index.js")
    plugin_dir = plugin_dir or DSH_PLUGIN_DIR
    state_dir = state_dir or STATE_DIR
    try:
        running = json.loads((state_dir / "deepseek-plugin.json").read_text(encoding="utf-8")).get("plugin")
    except Exception:
        running = None
    return [{"id": "deepseek", "label": "DeepSeek Harness", "bundled": _plugin_version(bundled),
             "installed": _plugin_version(plugin_dir / "lib/index.js"), "running": running}]
SEEN_KEY = {"claude-hooks": "claude", "claude-mod": "claude-mod", "codex": "gpt", "deepseek": "deepseek", "gemini": "gemini",
            "antigravity": "antigravity", "workbuddy": "workbuddy", "zcode": "zcode", "hermes": "hermes", "qwen": "qwen", "cursor": "cursor", "grok": "grok"}


def short(p) -> str:
    s = str(p)
    h = str(HOME)
    return "~" + s[len(h):] if s.startswith(h) else s


def ago(ts: float) -> str:
    d = time.time() - ts
    if d < 0:
        return "剛剛"
    for unit, n in (("天", 86400), ("小時", 3600), ("分鐘", 60)):
        if d >= n:
            return f"{int(d // n)} {unit}前"
    return f"{int(d)} 秒前"


def seen(target: str) -> str:
    f = STATE_DIR / f"seen-{SEEN_KEY[target]}"
    try:
        return ago(float(f.read_text().strip() or f.stat().st_mtime))
    except Exception:
        return "沒有記錄"


def file_state(p: Path, marker_ok=None) -> str:
    if not p.exists():
        return f"{short(p)}（不存在）"
    try:
        size = p.stat().st_size
        mtime = ago(p.stat().st_mtime)
    except OSError:
        return f"{short(p)}（讀不了）"
    extra = ""
    if marker_ok is not None:
        try:
            extra = "，有 CrossPet 條目" if marker_ok() else "，**沒有** CrossPet 條目"
        except Exception as e:
            extra = f"，解析失敗：{type(e).__name__}"
    return f"{short(p)}（{size} 位元組，{mtime}改過{extra}）"


def env_line(*names) -> str:
    vals = [f"{n}={short(os.environ[n])}" for n in names if os.environ.get(n)]
    return "，".join(vals) if vals else "無"


def shell_profile_mentions(name: str) -> list:
    """終端配置檔案裡有沒有設這個變數（設了但 CrossPet 程式看不到，說明只在終端裡生效）"""
    cands = [HOME / f for f in (".zshrc", ".zprofile", ".bashrc", ".bash_profile", ".profile", ".config/fish/config.fish")]
    if os.name == "nt":
        docs = HOME / "Documents"
        cands += [docs / "PowerShell/Microsoft.PowerShell_profile.ps1", docs / "WindowsPowerShell/Microsoft.PowerShell_profile.ps1"]
    hits = []
    for f in cands:
        try:
            if f.exists() and name in f.read_text(encoding="utf-8", errors="ignore"):
                hits.append(short(f))
        except OSError:
            pass
    return hits


def wsl_codex_homes() -> list:
    """Windows：WSL 裡有沒有 ~/.codex（Codex 命令列很多人在 WSL 裡用，那邊的配置 CrossPet 接不上）"""
    if os.name != "nt":
        return []
    import subprocess
    try:
        out = subprocess.run(["wsl.exe", "-l", "-q"], capture_output=True, timeout=8).stdout
        distros = [d.strip() for d in out.decode("utf-16-le", errors="ignore").replace("\x00", "").splitlines() if d.strip()]
    except Exception:
        return []
    found_ = []
    for d in distros:
        for root in (Path(f"//wsl.localhost/{d}/home"), Path(f"//wsl$/{d}/home")):
            try:
                for u in root.iterdir():
                    if (u / ".codex").is_dir():
                        found_.append(f"{d}:~{u.name}/.codex" + ("（有 CrossPet 鉤子）" if has_hooks(u / ".codex/hooks.json") else ""))
                break
            except OSError:
                continue
    return found_ or [f"裝了 WSL（{', '.join(distros)}），裡面沒找到 .codex"] if distros else []


def codex_programs() -> list:
    """GPT 額度要問 Codex 本地服務：列出能找到的 codex 程式和版本（和桌寵找的順序一致）"""
    import shutil
    import subprocess
    cands = []
    if os.environ.get("CROSSPET_CODEX_BIN"):
        cands.append(os.environ["CROSSPET_CODEX_BIN"])
    if os.name == "nt":
        for name in ("codex.exe", "codex.cmd"):
            p = shutil.which(name)
            if p:
                cands.append(p)
        appdata = os.environ.get("APPDATA", "")
        if appdata:
            cands.append(os.path.join(appdata, "npm", "codex.cmd"))
        try:   # Codex 桌面版開著時，它的 codex.exe 在跑：拿到真實路徑（裝在受保護目錄裡也能拿到）
            out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process codex -ErrorAction SilentlyContinue).Path"],
                                 capture_output=True, text=True, timeout=8).stdout
            cands += [l.strip() for l in out.splitlines() if l.strip()]
        except Exception:
            pass
    else:
        cands.append("/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex")
        p = shutil.which("codex")
        if p:
            cands.append(p)
        cands += ["/opt/homebrew/bin/codex", "/usr/local/bin/codex"]
    seen_, out_ = set(), []
    for c in cands:
        if c in seen_ or not os.path.isfile(c):
            continue
        d = os.path.dirname(c)
        if any(os.path.exists(os.path.join(d, m)) for m in ("resources.pak", "ffmpeg.dll", "icudtl.dat", "chrome_100_percent.pak")):
            continue   # Codex 桌面版的圖形介面程式（可能也叫 Codex.exe）：跑 --version 會彈視窗，跳過
        seen_.add(c)
        try:
            v = subprocess.run([c, "--version"], capture_output=True, text=True, timeout=8).stdout.strip() or "版本未知"
        except Exception as e:
            v = f"執行失敗：{e.__class__.__name__}"
        out_.append(f"{c}（{v}）")
    return out_


def codex_warnings() -> list:
    """接入 Codex 時的提醒：配置可能不在 CrossPet 寫的地方"""
    warn = []
    if not os.environ.get("CODEX_HOME"):
        hits = shell_profile_mentions("CODEX_HOME")
        if hits:
            warn.append(f"你在終端配置（{'、'.join(hits)}）裡設了 CODEX_HOME，但 CrossPet 讀不到，鉤子寫在了預設的 {short(CODEX_HOOKS.parent)}。"
                        "請把 CODEX_HOME 設成系統環境變數（macOS 用 launchctl setenv，Windows 在「系統屬性 → 環境變數」里加），再重新接入")
    wsl = [w for w in wsl_codex_homes() if ".codex" in w and "沒找到" not in w]
    if wsl:
        warn.append(f"WSL 裡也有 Codex 的配置（{'、'.join(wsl)}）。如果你是在 WSL 裡用 Codex，CrossPet 暫時接不上，桌寵不會跟著動")
    return warn


def diagnose() -> str:
    import platform
    L = [f"接入工具：Python {platform.python_version()}（{short(sys.executable)}），資料目錄 {short(SUPPORT)}，狀態目錄 {short(STATE_DIR)}"]
    inst = installed()
    L.append(f"已接入：{', '.join(inst) or '無'}；想一直接著：{', '.join(sorted(wanted())) or '無'}")
    # Claude
    L.append(f"[Claude] 配置目錄 {short(CLAUDE_HOME)}（{'在' if CLAUDE_HOME.exists() else '不在'}），環境變數 {env_line('CLAUDE_CONFIG_DIR')}")
    L.append(f"  settings.json：{file_state(CLAUDE_SETTINGS, lambda: has_hooks(CLAUDE_SETTINGS) or claude_mod_ok())}；標準鉤子 {'有' if has_hooks(CLAUDE_SETTINGS) else '無'}，"
             f"mod 資料夾 {'在' if CLAUDE_MOD_DIR.exists() else '不在'}、載入路徑裡 {'有' if claude_mod_ok() else '沒有'}")
    try:
        env = load_json(CLAUDE_SETTINGS).get("env", {})
        base = env.get("ANTHROPIC_BASE_URL", "")
        host = re.sub(r"^https?://([^/]+).*$", r"\1", base) if base else "預設"
        L.append(f"  settings 裡的介面：{host}，模型 {env.get('ANTHROPIC_MODEL', '預設')}")
    except Exception:
        pass
    v = claude_cli_version()
    L.append(f"  終端 claude 版本 {'.'.join(map(str, v)) if v else '沒找到'}；最近訊息：標準鉤子 {seen('claude-hooks')}，mod {seen('claude-mod')}")
    if (HOME / ".cc-switch").exists():
        L.append("  裝了 CC Switch")
    # Codex
    L.append(f"[Codex] hooks.json：{file_state(CODEX_HOOKS, lambda: has_hooks(CODEX_HOOKS))}；目錄裡其他檔案 {'有' if found(CODEX_HOOKS.parent, '', ('hooks.json',), quiet=True) else '沒有（Codex 可能沒在這裡執行過）'}")
    L.append(f"  config.toml {'在' if (CODEX_HOOKS.parent / 'config.toml').exists() else '不在'}；環境變數 {env_line('CODEX_HOME')}；終端配置裡設了 CODEX_HOME：{'、'.join(shell_profile_mentions('CODEX_HOME')) or '無'}")
    progs = codex_programs()
    L.append("  codex 程式（GPT 額度向它查）：" + ("；".join(short(Path(p.split("（")[0])) + "（" + p.split("（", 1)[1] for p in progs) if progs else "沒找到——GPT 額度顯示不了"))
    w = wsl_codex_homes()
    if w:
        L.append(f"  WSL：{'；'.join(w)}")
    L.append(f"  最近訊息：{seen('codex')}")
    # DeepSeek
    patch = DSH_PROFILE / "cordis.patch.yml"
    L.append(f"[DeepSeek Harness] profile {short(DSH_PROFILE)}（{'在' if DSH_PROFILE.exists() else '不在'}），環境變數 {env_line('DSH_HOME', 'DSH_PROFILE', 'DSH_PROFILE_DIR')}")
    L.append(f"  cordis.patch.yml：{file_state(patch, lambda: DSH_BLOCK_START in patch.read_text(encoding='utf-8'))}；"
             f"外掛 {'在' if (DSH_PLUGIN_DIR / 'lib/index.js').exists() else '不在'}，連結 {'在' if (DSH_PROFILE / 'node_modules/@local/dsh-plugin-crosspet').exists() else '不在'}")
    try:
        ver = re.search(r'PLUGIN_VERSION = "([^"]+)"', (DSH_PLUGIN_DIR / "lib/index.js").read_text(encoding="utf-8")).group(1)
        L.append(f"  外掛版本 {ver}")
    except Exception:
        pass
    try:
        diag = json.loads((STATE_DIR / "deepseek-plugin.json").read_text(encoding="utf-8"))
        L.append(f"  外掛自檢：{json.dumps(diag, ensure_ascii=False)}")
    except Exception:
        pass
    L.append(f"  最近訊息：{seen('deepseek')}")
    # 其他
    L.append(f"[Gemini CLI] {file_state(GEMINI_SETTINGS, lambda: has_hooks(GEMINI_SETTINGS))}；最近訊息 {seen('gemini')}")
    L.append(f"[Qwen Code] {file_state(QWEN_SETTINGS, lambda: has_hooks(QWEN_SETTINGS))}；最近訊息 {seen('qwen')}")
    L.append(f"[Cursor] {file_state(CURSOR_HOOKS, has_cursor)}；最近訊息 {seen('cursor')}")
    L.append(f"[Grok Build] {file_state(GROK_HOOKS, has_grok)}；最近訊息 {seen('grok')}")
    L.append(f"[Antigravity] {file_state(ANTIGRAVITY_HOOKS, has_antigravity)}；最近訊息 {seen('antigravity')}")
    for d in workbuddy_dirs():
        if d.exists():
            L.append(f"[WorkBuddy] {file_state(d / 'settings.json', lambda d=d: has_hooks(d / 'settings.json'))}")
    L.append(f"  WorkBuddy 最近訊息 {seen('workbuddy')}；環境變數 {env_line('WORKBUDDY_CONFIG_DIR')}")
    L.append(f"[ZCode] {file_state(ZCODE_CONFIG, has_zcode)}；最近訊息 {seen('zcode')}")
    hermes_on = HERMES_CONFIG.exists() and re.search(r"^\s+-\s*crosspet\s*$", HERMES_CONFIG.read_text(encoding="utf-8"), re.M)
    L.append(f"[Hermes] {short(HERMES_HOME)}（{'在' if HERMES_HOME.exists() else '不在'}），外掛 {'在' if has_hermes() else '不在'}，"
             f"config.yaml 裡啟用 {'是' if hermes_on else '否'}；"
             f"環境變數 {env_line('HERMES_HOME')}；最近訊息 {seen('hermes')}")
    # 狀態檔案：各角色最近一次的狀態
    states = []
    for f in sorted(STATE_DIR.glob("*-state.json")):
        try:
            d = json.loads(f.read_text(encoding="utf-8"))
            states.append(f"{f.name[:-11]}={d.get('pose')}/{d.get('event')}（{ago(f.stat().st_mtime)}）")
        except Exception:
            states.append(f"{f.name[:-11]}=讀不了")
    L.append(f"[狀態] {'；'.join(states) or '狀態目錄裡沒有狀態檔案'}")
    hosts = []
    for f in sorted(STATE_DIR.glob("*-host.json")):
        try:
            d = json.loads(f.read_text(encoding="utf-8"))
            hosts.append(f"{f.name[:-10]}→{d.get('character')}（{d.get('model') or '-'}，{ago(f.stat().st_mtime)}）")
        except Exception:
            pass
    if hosts:
        L.append(f"[按模型換角色] {'；'.join(hosts)}")
    try:
        auto = (STATE_DIR / "autostart.txt").read_text(encoding="utf-8-sig").strip().splitlines()[-3:]
        L.append("[自動出現] " + " | ".join(auto))
    except Exception:
        pass
    flags = [f for f in ("user-quit", "ai-autostart-off") if (SUPPORT / f).exists()]
    if flags:
        L.append(f"[標記] {', '.join(flags)}")
    return "\n".join(L)


def status() -> None:
    print("Claude Code 標準鉤子:", "已接入" if has_hooks(CLAUDE_SETTINGS) else "未接入")
    print("Claude Code mod     :", "已接入" if claude_mod_ok() else "被別的工具改掉了（桌寵開著時會自動接回去）" if CLAUDE_MOD_DIR.exists() else "未接入")
    print("Codex               :", "已接入" if has_hooks(CODEX_HOOKS) else "未接入")
    dsh = (DSH_PROFILE / "cordis.patch.yml")
    print("DeepSeek Harness    :", "已接入" if dsh.exists() and DSH_BLOCK_START in dsh.read_text(encoding="utf-8") else "未接入")
    print("Gemini CLI          :", "已接入" if has_hooks(GEMINI_SETTINGS) else "未接入")
    print("Antigravity         :", "已接入" if has_antigravity() else "未接入")
    print("WorkBuddy           :", "已接入" if any(has_hooks(d / "settings.json") for d in workbuddy_dirs()) else "未接入")
    print("ZCode               :", "已接入" if has_zcode() else "未接入")
    print("Hermes Agent        :", "已接入" if has_hermes() else "未接入")
    print("Qwen Code           :", "已接入" if has_hooks(QWEN_SETTINGS) else "未接入")
    print("Cursor              :", "已接入" if has_cursor() else "未接入")
    print("Grok Build          :", "已接入" if has_grok() else "未接入")


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "diagnose":   # 給反饋用：接入診斷（純文本）
        try:
            print(diagnose())
        except Exception as e:
            print(f"診斷出錯：{type(e).__name__}: {e}")
    elif len(sys.argv) == 2 and sys.argv[1] == "plugins":   # 給桌寵和設定視窗用：外掛版本（只讀），JSON 陣列
        print(json.dumps(plugin_status()))
    elif len(sys.argv) == 2 and sys.argv[1] == "installed":  # 給設定視窗用：已接入的目標，JSON 陣列
        print(json.dumps(installed()))
    elif len(sys.argv) == 2 and sys.argv[1] == "status":
        status()
    elif len(sys.argv) == 2 and sys.argv[1] == "refresh":
        for t in installed():
            print(f"更新 {t}：")
            TARGETS[t](True)
    elif len(sys.argv) == 2 and sys.argv[1] == "heal":   # 給桌寵用：被別的工具沖掉的接回去，輸出接回去的目標（JSON 陣列）
        print(json.dumps(heal()))
    elif len(sys.argv) == 3 and sys.argv[1] in ("install", "uninstall") and sys.argv[2] in TARGETS:
        target, install = sys.argv[2], sys.argv[1] == "install"
        print(f"{'接入' if install else '撤銷'} {target}：")
        TARGETS[target](install)
        if target in HEALABLE:
            set_wanted(target, install and HEALABLE[target]())
            if install:
                ccswitch_note()
    else:
        print(__doc__)
        sys.exit(1)
