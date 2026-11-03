// CrossPet × DeepSeek Harness
// - 監聽 Harness 的工作事件（收到訊息 / 調工具 / 子任務 / 一輪結束），寫 <狀態目錄>/<角色>-state.json
//   Harness 能接好幾家模型：按會話實際在用的模型選角色（DeepSeek / GPT / Claude / Gemini），
//   當前角色另外記在 deepseek-host.json，桌寵切到 Harness 視窗時按它換角色
// - 每 1 分鐘查一次餘額（發訊息、一輪結束時也查，30 秒內只查一次）（有 API Key 用 Key；沒有就用 Harness 裡登入的賬號），寫 <狀態目錄>/deepseek-quota.json
// - 跟著 AI 出現：Harness 啟動、新會話、發訊息時，桌寵沒開著就把它開啟（使用者從選單退出後到 Harness 重開前不管；在設定 / 外掛配置 autostart: false 裡關掉了也不管）
// 狀態目錄預設 /tmp/crosspet，可在外掛配置裡改 stateDir。外掛只觀察，不改變 DeepSeek 的任何行為。
import { appendFile, mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { existsSync, readFileSync, statSync } from "node:fs";
import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { homedir } from "node:os";
import { lowThresholds, summarize } from "./balance.js";

export const name = "crosspet";
export const inject = ["credentials"];

const ID = "deepseek";
const PLUGIN_VERSION = "1.3.9";  // 出現在 deepseek-plugin.json 裡，用來確認跑的是哪一版外掛。必須是三段的合法版本號（同步寫進 package.json，Harness 裝外掛時會校驗）
const BIG_JOB_TOOLS = 8;
const BALANCE_EVERY_MS = 60 * 1000;   // 定時查餘額：充了值、沒再對話，最多等 1 分鐘（以前 10 分鐘，使用者充值後半天沒變）。只是要一次賬戶餘額，不耗 token

function poseForTool(name, args) {
  const t = String(name ?? "").toLowerCase();
  const input = (() => { try { return JSON.stringify(args ?? "").toLowerCase(); } catch { return ""; } })();
  if (t === "ask_user_question" || t === "ask_user") return "asking";  // 停下來問你問題、讓你選選項
  if (/image_gen|imagegen|generate_image|draw|paint/.test(t) || input.includes("image_gen")) return "drawing";
  if (/read|grep|glob|list|view|cat/.test(t)) return "reading";
  if (/write|edit|patch|replace|create/.test(t)) return "writing";
  if (/web|fetch|browse|search|http/.test(t)) return "searching";
  if (/agent|task|subagent|delegate/.test(t)) return "delegating";
  return "running";
}

// 模型 → 角色：先看模型名，再看 provider 名（provider 是使用者自己起的路由名，可能叫什麼都有），都認不出算 DeepSeek。
// 外掛配置裡的 characters 可以覆蓋，比如 { "my-gateway": "gpt" }（鍵是 provider 或模型名）
function characterFor(provider, model, custom = {}) {
  const p = String(provider ?? ""), m = String(model ?? "");
  if (custom[m]) return custom[m];
  if (custom[p]) return custom[p];
  for (const s of [m.toLowerCase(), p.toLowerCase()]) {
    if (/deepseek/.test(s)) return "deepseek";
    if (/glm|zhipu|bigmodel|chatglm|z-ai|zai-/.test(s)) return "glm";
    if (/qwen|dashscope|bailian|tongyi/.test(s)) return "qwen";
    if (/grok|x-ai|xai/.test(s)) return "grok";
    if (/claude|anthropic/.test(s)) return "claude";
    if (/gemini|google|vertex/.test(s)) return "gemini";
    if (/gpt|codex|openai|chatgpt|(^|[^a-z])o[1-9]/.test(s)) return "gpt";
  }
  return ID;
}

function failed(result) {
  if (!result || typeof result !== "object") return false;
  return result.isError === true || result.is_error === true || result.ok === false;
}

export function apply(ctx, config = {}) {
  const data = process.env.CROSSPET_DATA_DIR || join(process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local"), "CrossPet");
  const dir = config.stateDir || process.env.CROSSPET_STATE_DIR || (process.platform === "win32" ? join(data, "state") : "/tmp/crosspet");
  const lowBalance = lowThresholds(config.lowBalance);  // 按幣種的門檻，見 balance.js
  let tools = 0;

  let writes = 0;
  async function writeJson(file, data) {
    await mkdir(dir, { recursive: true });
    // 臨時檔名每次不同：幾次寫入同時發生時（比如啟動時查兩次餘額、連續的工作事件）不會互相搶同一個檔案
    const tmp = join(dir, `.${file}.${process.pid}.${++writes}`);
    await writeFile(tmp, JSON.stringify(data));
    await rename(tmp, join(dir, file));
  }
  // 當前在用的模型對應的角色：每條會話事件都看一眼會話的請求路由（和 Harness 自帶外掛一樣用 requestContext）
  const custom = config.characters && typeof config.characters === "object" ? config.characters : {};
  let character = ID;
  // 最近一次請求走的服務商：記在 <狀態目錄>/deepseek-host.json，Harness 重新開啟、還沒發訊息時先按上次的算
  let provider = null;
  try { provider = JSON.parse(readFileSync(join(dir, `${ID}-host.json`), "utf8")).provider || null; } catch {}
  function noteRoute(session) {
    let route;
    try { route = session?.requestContext?.(); } catch { return; }
    if (!route?.provider && !route?.model) return;
    const next = characterFor(route.provider, route.model, custom);
    const switched = route.provider && route.provider !== provider;
    if (switched) {
      provider = route.provider;
      publishQuota();   // 換了服務商：餘額該顯示還是該藏起來
    }
    if (next === character && !switched) return;
    character = next;
    writeJson(`${ID}-host.json`, { character, provider: route.provider ?? "", model: route.model ?? "", ts: Date.now() / 1000 })
      .catch(() => {});
  }

  let seenAt = 0;
  function state(pose, event, tool = "") {
    if (Date.now() - seenAt > 10000) {   // 心跳：最近一次收到訊息（桌寵記下來，設定和反饋裡看）
      seenAt = Date.now();
      mkdir(dir, { recursive: true }).then(() => writeFile(join(dir, "seen-deepseek"), String(Math.floor(seenAt / 1000)))).catch(() => {});
    }
    ledger(event, tool, "shown", `${character} ${pose}`);
    writeJson(`${character}-state.json`, { pose, event, tool, ts: Date.now() / 1000 }).catch((e) =>
      ctx.logger?.warn?.(`crosspet: 寫狀態失敗 ${String(e)}`),
    );
  }

  // ---- 跟著 AI 出現 ----
  // 桌寵在跑時把自己的程式號寫在 <狀態目錄>/pet.pid；從選單手動退出時在資料目錄寫 user-quit（Harness 重開或重啟電腦後作廢），
  // 設定裡關掉時寫 ai-autostart-off。用獨立程式開啟（detached），Harness 退出時桌寵不會被一起關掉
  const appData = process.platform === "win32" ? data : join(homedir(), "Library", "Application Support", "CrossPet");

  // ---- 事件記錄（反饋「AI 幹活時她沒反應」用）：資料目錄 events/deepseek.jsonl，格式和 crosspet-hook.py 一樣（見 docs/架构.md「记录」）。
  // 只記事件名、工具名、結果，不記訊息和工具引數；只留最近 200 行。外掛只有一個程式，按順序排隊寫就行
  const ledgerDir = join(config.dataDir || (process.env.CROSSPET_DATA_DIR ? data : appData), "events");
  let ledgerChain = Promise.resolve();
  function ledger(ev, tool, result, detail) {
    const line = JSON.stringify({ t: Math.round(Date.now() / 10) / 100, ev, tool: String(tool || "").toLowerCase().slice(0, 60), sid: "", turn: false, result, detail });
    const file = join(ledgerDir, `${ID}.jsonl`);
    ledgerChain = ledgerChain.then(async () => {
      await mkdir(ledgerDir, { recursive: true });
      await appendFile(file, line + "\n");
      const rows = (await readFile(file, "utf8")).split("\n").filter(Boolean);
      if (rows.length > 240) {
        const tmp = join(ledgerDir, `.${ID}.${process.pid}.tmp`);
        await writeFile(tmp, rows.slice(-200).join("\n") + "\n");
        await rename(tmp, file);
      }
    }).catch(() => {});   // 記錄寫不了不能影響外掛本身
  }
  let lastEnsure = 0;
  function noteAutostart(source, result) {  // 寫進 <狀態目錄>/autostart.txt，排查「為什麼沒出來」用
    const at = new Date().toLocaleString("zh-CN", { hour12: false });
    // Windows 上帶 BOM：系統自帶的 PowerShell 5 沒有 BOM 就按 GBK 讀，中文會亂碼
    const bom = process.platform === "win32" ? "\ufeff" : "";
    mkdir(dir, { recursive: true }).then(() => writeFile(join(dir, "autostart.txt"), `${bom}${at} deepseek ${source}：${result}\n`)).catch(() => {});
  }
  function ensurePet(source) {
    if (config.autostart === false) return noteAutostart(source, "不開啟：外掛配置裡 autostart 是 false");
    if (Date.now() - lastEnsure < 30000) return;
    lastEnsure = Date.now();
    try {
      // 手動退出過：Harness 是在那之後才打開的（關掉重開過、或者重啟過電腦）就作廢，否則先不管
      const quitFlag = join(appData, "user-quit");
      if (existsSync(quitFlag)) {
        const hm = (ms) => new Date(ms).toLocaleTimeString("zh-CN", { hour12: false });
        const started = Date.now() - process.uptime() * 1000, quitAt = statSync(quitFlag).mtimeMs;
        if (started < quitAt)
          return noteAutostart(source, `不開啟：從選單手動退出過，Harness 是在那之前就開著的（Harness 開啟於 ${hm(started)}，退出於 ${hm(quitAt)}）`);
      }
      if (existsSync(join(appData, "ai-autostart-off"))) return noteAutostart(source, "不開啟：設定裡關掉了「AI 開始工作時自動出現」");
      try {
        process.kill(Number(readFileSync(join(dir, "pet.pid"), "utf8").trim()), 0);
        return noteAutostart(source, "已經在跑");
      } catch (e) {
        if (e?.code === "EPERM") return noteAutostart(source, "已經在跑");
      }
      let child;
      if (process.platform === "win32") {
        const exe = join(process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local"), "Programs", "CrossPet", "CrossPet.exe");
        if (!existsSync(exe)) return noteAutostart(source, `不開啟：沒找到 ${exe}`);
        child = spawn(exe, [], { detached: true, stdio: "ignore", cwd: dirname(exe) });
      } else if (process.platform === "darwin") {
        child = spawn("/usr/bin/open", ["-g", "-b", "io.github.crosspet"], { detached: true, stdio: "ignore" });
      } else return;
      child.on("error", (e) => noteAutostart(source, `開啟失敗：${String(e?.message ?? e)}`));
      child.unref();
      noteAutostart(source, "已開啟");
    } catch (e) {
      noteAutostart(source, `出錯：${String(e?.message ?? e)}`);
      ctx.logger?.warn?.(`crosspet: 開啟桌寵失敗 ${String(e?.message ?? e)}`);
    }
  }
  ctx.effect(() => {
    const timer = setTimeout(() => ensurePet("Harness 啟動"), 2500);  // 稍微錯開 Harness 自己的啟動
    return () => clearTimeout(timer);
  }, "crosspet: open pet with Harness");

  ctx.on("agent/created", () => { state("idle", "SessionStart"); ensurePet("新會話"); });
  ctx.on("agent/pre-step", async ({ messages }, next) => {
    if (messages?.length) {
      ensurePet("發訊息");
      tools = 0;
      state("listening", "UserPromptSubmit");
      pollBalanceSoon();   // 充完值回來一說話，餘額馬上跟上
    }
    return next();
  });
  ctx.on("tools/pre-execute", async (exec, next) => {
    tools += 1;
    state(poseForTool(exec.name, exec.arguments), "PreToolUse", String(exec.name ?? ""));
    return next();
  });
  ctx.on("tools/post-execute", async (exec, result, next) => {
    state(failed(result) ? "oops" : "thinking", "PostToolUse", String(exec.name ?? ""));
    return next();
  });
  ctx.on("agent/turn-stopping", () => {
    state(tools >= BIG_JOB_TOOLS ? "proud" : "happy", "Stop");
    tools = 0;
    pollBalanceSoon();   // 這一輪花掉的錢
  });
  // 要你授權：只旁聽，決定原樣交回給 Harness。自動放行的請求也會經過這裡，
  // 所以超過 0.4 秒還沒答覆（真的在等人）才換成等你回答的表情，免得幹活時一閃一閃
  ctx.on("approval/request", async (_request, next) => {
    const waiting = setTimeout(() => state("asking", "PermissionRequest"), 400);
    try {
      return await next();
    } finally {
      clearTimeout(waiting);
    }
  });
  ctx.on("subagent/start", () => state("delegating", "SubagentStart"));
  // 壓縮上下文：會話日誌裡的 compaction/start、compaction/end
  // ---- 多會話：按對話另記 <角色>-sessions.json（{會話id: {pose, event, ts}}）----
  // 好幾個對話同時在幹活時桌寵演「手忙腳亂」，每個對話一張小卡片。全靠 session/event：每條都帶著是哪個對話，
  // 一輪開始 / 調工具 / 工具結果 / 請求授權 / 一輪結束都有。子任務（subagent）的會話不算單獨的對話
  const perChar = {};      // 角色 → {會話id: 狀態}
  const turnTools = {};    // 會話id → 這一輪用了幾次工具
  let sessionsDirty = new Set(), sessionsTimer = null;
  function noteSession(session, pose, ev) {
    const header = session?.header;
    if (!session?.id || header?.origin === "subagent" || header?.parentSession !== undefined) return;
    let who = ID;
    try { const r = session.requestContext?.(); if (r?.provider || r?.model) who = characterFor(r.provider, r.model, custom); } catch {}
    const now = Date.now() / 1000;
    for (const [c, map] of Object.entries(perChar)) if (c !== who && map[session.id]) { delete map[session.id]; sessionsDirty.add(c); }   // 中途換了模型
    const map = (perChar[who] ??= {});
    map[session.id] = { pose, event: ev, ts: now };
    for (const [k, v] of Object.entries(map)) if (now - v.ts > 600) delete map[k];
    sessionsDirty.add(who);
    // 合併 0.2 秒內的連續事件再寫盤
    sessionsTimer ??= setTimeout(() => {
      sessionsTimer = null;
      for (const c of sessionsDirty) writeJson(`${c}-sessions.json`, perChar[c] || {}).catch(() => {});
      sessionsDirty = new Set();
    }, 200);
  }
  function sessionPose(session, event) {
    const sid = session?.id, d = event?.data ?? {};
    switch (event?.type) {
      case "turn/start": turnTools[sid] = 0; return noteSession(session, "listening", "UserPromptSubmit");
      case "step/start": return noteSession(session, "thinking", "PreInvocation");
      case "tool/call":
        turnTools[sid] = (turnTools[sid] || 0) + 1;
        return noteSession(session, poseForTool(d.name, d.arguments), "PreToolUse");
      case "tool/result": return noteSession(session, d.message?.isError || d.error ? "oops" : "thinking", "PostToolUse");
      case "approval/asked": return noteSession(session, "asking", "PermissionRequest");
      case "approval/decided": return noteSession(session, "thinking", "PostToolUse");
      case "compaction/start": return noteSession(session, "compact", "PreCompact");
      case "turn/end": {
        const kind = d.reason?.kind, n = turnTools[sid] || 0;
        delete turnTools[sid];
        return noteSession(session, kind === "aborted" ? "surprised" : kind === "error" ? "oops" : kind === "completed" ? (n >= BIG_JOB_TOOLS ? "proud" : "happy") : "idle", "Stop");
      }
    }
  }

  ctx.on("session/event", (session, event) => {
    noteRoute(session);
    try { sessionPose(session, event); } catch {}
    // 一輪結束都會記 turn/end（reason.kind：completed / aborted / error / blocked）；
    // 正常完成由 agent/turn-stopping 處理，這裡補上被你打斷、出錯停下的情況，不然會一直停在思考
    if (event?.type === "turn/end") {
      const kind = event.data?.reason?.kind;
      if (kind === "aborted") { tools = 0; state("surprised", "Stop"); }
      else if (kind === "error") { tools = 0; state("oops", "Stop"); }
    }
    if (event?.type === "compaction/start") state("compact", "PreCompact");
    else if (event?.type === "compaction/end") state("thinking", "PostCompact");
  });
  ctx.on("subagent/end", () => state("thinking", "SubagentStop"));

  // ---- 餘額 ----
  // 兩條路，都是 Harness 官方介面，Key / 登入憑據都只在 Harness 內部使用，外掛只拿到餘額數字：
  // 1. 配置了 API Key（模型頁填的 sk-…）：用憑據介面取 Key，調官方餘額介面（值只在記憶體裡用，不寫盤、不打日誌）
  // 2. 沒有 Key、但在 Harness 裡登入了 DeepSeek 賬號：調 Harness 自己的賬號服務 deepseekAccount.getBalance
  // 賬號服務不是每個版本都有：用 ctx.inject 宣告成「有就用」，沒有時外掛照常工作
  let account = null;
  try {
    ctx.inject(["deepseekAccount"], (sub) => {
      account = sub.deepseekAccount;
      sub.effect(() => {
        pollBalance();
        return () => { account = null; };
      }, "crosspet: account balance");
    });
  } catch (e) {
    // 萬一這個版本的 Harness 不支援：只是少了「用賬號查餘額」，工作狀態和用 Key 查餘額照常
    ctx.logger?.warn?.(`crosspet: 賬號服務不可用 ${String(e?.message ?? e)}`);
  }

  // 餘額只屬於 DeepSeek 官方的兩個服務商（登入的賬號、填的 API Key）。正在用別家的服務商
  // （OpenCode Go、OpenRouter、自定義閘道器……）時官方餘額跟她幹活沒關係：不顯示，也不因為餘額低沒精神
  const OFFICIAL = new Set(["deepseek-account", "deepseek-official"]);
  let lastQuota = null;
  function publishQuota() {
    if (provider && !OFFICIAL.has(provider)) return writeJson(`${ID}-quota.json`, { text: "", low: false }).catch(() => {});
    if (lastQuota) return writeJson(`${ID}-quota.json`, lastQuota).catch(() => {});
    return Promise.resolve();
  }
  const setQuota = (q) => { lastQuota = q; return publishQuota(); };

  // entries：[{ currency, amount }]。顯示、餘額低、充值比對的規則都在 balance.js（兩條路共用）
  const show = (entries, available = true) => {
    const q = summarize(entries, { available, low: lowBalance });
    return q ? setQuota(q).then(() => "ok") : "no-balance";
  };

  async function balanceByKey() {
    const hit = await ctx.credentials.resolve("DEEPSEEK_API_KEY");
    if (!hit?.value) return "no-key";
    const res = await fetch("https://api.deepseek.com/user/balance", {
      headers: { Authorization: `Bearer ${hit.value}`, Accept: "application/json" },
    });
    if (!res.ok) return `HTTP ${res.status}`;
    const body = await res.json();
    // 有人民幣也有美元時一次返回兩項，順序不固定（#10）：全部按幣種讀，不取第 0 項
    const infos = Array.isArray(body?.balance_infos) ? body.balance_infos : [];
    return show(infos.map((i) => ({ currency: i?.currency, amount: i?.total_balance })), body?.is_available !== false);
  }

  async function balanceByAccount() {
    if (!account?.getBalance) return "no-account";
    const r = await account.getBalance({
      version: "crosspet", locale: "zh-CN", timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
    });
    if (r === null) return "not-signed-in";
    if (r?.status !== "ready") return "failed";
    // 現金錢包 + 贈送錢包，同一幣種加在一起，各幣種分別顯示
    const wallets = [...(r.value || []), ...(r.bonusWallets || [])];
    return show(wallets.map((w) => ({ currency: w?.currency, amount: w?.balance })));
  }

  // 每次查餘額都把過程記到 deepseek-plugin.json（只有結果程式碼，沒有 Key / 令牌），
  // 餘額不顯示時看它就知道卡在哪一步：Harness 的日誌不落盤，沒有別的辦法看
  const diagnose = (info) => writeJson(`${ID}-plugin.json`, { plugin: PLUGIN_VERSION, at: new Date().toISOString(),
    accountService: !!account?.getBalance, ...info }).catch(() => {});

  // 發訊息、一輪結束時也查：間隔太近（預設 30 秒內查過）就不再查，連著對話時不會問得太勤。
  // 查餘額只是向 DeepSeek 官方要一次賬戶餘額，不消耗 token。balanceGapMs 可在外掛配置裡改（測試時設 0）
  const balanceGap = typeof config.balanceGapMs === "number" ? config.balanceGapMs : 30 * 1000;
  let lastPollAt = 0;
  function pollBalanceSoon() {
    if (Date.now() - lastPollAt < balanceGap) return;
    pollBalance();
  }

  async function pollBalance() {
    lastPollAt = Date.now();
    let byKey = "not-tried", byAccount = "not-tried";
    try {
      byKey = await balanceByKey().catch((e) => `error: ${String(e?.message ?? e)}`);
      if (byKey !== "ok") byAccount = await balanceByAccount().catch((e) => `error: ${String(e?.message ?? e)}`);
      await diagnose({ apiKey: byKey, account: byAccount });
      if (byKey === "ok" || byAccount === "ok") return;
      if (byKey === "no-key" && ["no-account", "not-signed-in"].includes(byAccount)) {
        // 兩樣都沒有：名牌上說清楚怎麼辦，別一聲不吭
        await setQuota({ text: "餘額：請在 Harness 登入賬號或填 API Key", low: false });
        return;
      }
      ctx.logger?.warn?.(`crosspet: 查餘額失敗（API Key：${byKey}，賬號：${byAccount}）`);
    } catch (e) {
      await diagnose({ apiKey: byKey, account: byAccount, error: String(e?.message ?? e) });
      ctx.logger?.warn?.(`crosspet: 查餘額失敗 ${String(e?.message ?? e)}`);
    }
  }
  // 定時查的間隔：外掛配置裡的 balanceIntervalMs，0 = 不定時查（只在啟動、發訊息、一輪結束時查），
  // 不寫或寫錯（不是數、負數）按預設每分鐘。常開著 Harness、人又不在時可以調慢或關掉（#29，@mkinnqijn）
  const every = config.balanceIntervalMs;
  const balanceInterval = typeof every === "number" && Number.isFinite(every) && every >= 0 ? every : BALANCE_EVERY_MS;
  ctx.effect(() => {
    pollBalance();
    if (balanceInterval === 0) return () => {};
    const timer = setInterval(pollBalance, balanceInterval);
    return () => clearInterval(timer);
  }, "crosspet: balance timer");
}
