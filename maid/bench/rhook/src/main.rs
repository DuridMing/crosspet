//! Rust equivalent of the `crosspet-hook.py` PreToolUse path, for timing only (MC-39).
//!
//! Does the same file work as the Python hook: parse the event, pick a pose, write the state file
//! atomically, update the sessions file under a lock, append to the ledger and trim it, write the
//! heartbeat. Not a replacement for the hook: it handles only the common events.
use serde_json::{json, Map, Value};
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const LEDGER_KEEP: usize = 200;
const LEDGER_MAX: usize = 240;
const SESSION_KEEP_SECS: f64 = 600.0;

fn now() -> f64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0)
}

fn data_dir() -> PathBuf {
    if let Some(d) = env::var_os("CROSSPET_DATA_DIR") {
        return PathBuf::from(d);
    }
    env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(env::temp_dir).join("CrossPet")
}

fn pose_for_tool(tool: &str) -> &'static str {
    let has = |keys: &[&str]| keys.iter().any(|k| tool.contains(k));
    if has(&["read", "grep", "glob", "list", "view", "cat"]) {
        "reading"
    } else if has(&["write", "edit", "patch", "replace", "create"]) {
        "writing"
    } else if has(&["web", "fetch", "browse", "search", "http"]) {
        "searching"
    } else if has(&["agent", "task", "subagent", "delegate"]) {
        "delegating"
    } else {
        "running"
    }
}

/// Short session tag. The Python hook uses SHA-1; FNV-1a is enough here and the cost difference is microseconds.
fn session_tag(sid: &str) -> String {
    if sid.is_empty() {
        return String::new();
    }
    let hash = sid.bytes().fold(0xcbf29ce484222325u64, |h, b| (h ^ b as u64).wrapping_mul(0x100000001b3));
    format!("{:06x}", hash & 0xffffff)
}

fn replace_atomic(tmp: &Path, target: &Path) -> std::io::Result<()> {
    // The pet may be reading the previous file on Windows: retry briefly, as the Python hook does.
    let mut result = fs::rename(tmp, target);
    for _ in 0..7 {
        if result.is_ok() {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
        result = fs::rename(tmp, target);
    }
    result
}

fn locked(path: &Path) -> std::io::Result<File> {
    let lock = OpenOptions::new().create(true).append(true).open(path)?;
    lock.lock()?;
    Ok(lock)
}

fn update_sessions(state_dir: &Path, character: &str, sid: &str, entry: Value) -> std::io::Result<()> {
    let _lock = locked(&state_dir.join(format!(".{character}-sessions.lock")))?;
    let path = state_dir.join(format!("{character}-sessions.json"));
    let mut known: Map<String, Value> = fs::read_to_string(&path)
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_default();
    known.insert(sid.to_string(), entry);
    let t = now();
    known.retain(|_, v| v.get("ts").and_then(Value::as_f64).is_some_and(|ts| t - ts < SESSION_KEEP_SECS));
    let tmp = state_dir.join(format!(".{character}-sessions.json"));
    fs::write(&tmp, Value::Object(known).to_string())?;
    replace_atomic(&tmp, &path)
}

fn ledger(data: &Path, stem: &str, line: &Value) -> std::io::Result<()> {
    let dir = data.join("events");
    fs::create_dir_all(&dir)?;
    let _lock = locked(&dir.join(format!(".{stem}.lock")))?;
    let path = dir.join(format!("{stem}.jsonl"));
    let mut f = OpenOptions::new().create(true).append(true).open(&path)?;
    writeln!(f, "{line}")?;
    drop(f);
    let text = fs::read_to_string(&path)?;
    let rows: Vec<&str> = text.lines().collect();
    if rows.len() > LEDGER_MAX {
        let tmp = dir.join(format!(".{stem}.{}.tmp", std::process::id()));
        fs::write(&tmp, rows[rows.len() - LEDGER_KEEP..].join("\n") + "\n")?;
        fs::rename(&tmp, &path)?;
    }
    Ok(())
}

fn main() {
    let character = env::args().nth(1).unwrap_or_else(|| "claude".to_string());
    let data = data_dir();
    let state_dir = env::var_os("CROSSPET_STATE_DIR").map(PathBuf::from).unwrap_or_else(|| data.join("state"));
    let mut input = String::new();
    let _ = std::io::stdin().take(4 << 20).read_to_string(&mut input);
    let event: Value = serde_json::from_str(&input).unwrap_or(Value::Null);
    let get = |k: &str| event.get(k).and_then(Value::as_str).unwrap_or("");
    let name = get("hook_event_name");
    let tool = get("tool_name").to_lowercase();
    let sid = get("session_id");
    let pose = match name {
        "PreToolUse" => Some(pose_for_tool(&tool)),
        "PostToolUse" => Some("thinking"),
        "UserPromptSubmit" => Some("listening"),
        "Stop" => Some("happy"),
        _ => None,
    };
    if fs::create_dir_all(&state_dir).is_err() {
        return;
    }
    let t = now();
    if let Some(pose) = pose {
        let state = json!({"pose": pose, "event": name, "tool": tool, "ts": t, "host": character, "session": sid, "turn": ""});
        let tmp = state_dir.join(format!(".{character}-state.{}.json", std::process::id()));
        if fs::write(&tmp, state.to_string()).is_ok() {
            let _ = replace_atomic(&tmp, &state_dir.join(format!("{character}-state.json")));
        }
        if !sid.is_empty() {
            let _ = update_sessions(&state_dir, &character, sid, json!({"pose": pose, "event": name, "ts": t}));
        }
    }
    let _ = fs::write(state_dir.join(format!("seen-{character}")), format!("{}", t as u64));
    let line = json!({"t": (t * 100.0).round() / 100.0, "ev": name, "tool": tool, "sid": session_tag(sid), "turn": false,
                      "result": if pose.is_some() { "shown" } else { "dropped" },
                      "detail": format!("{character} {}", pose.unwrap_or(""))});
    let _ = ledger(&data, &format!("{character}-hooks"), &line);
}
