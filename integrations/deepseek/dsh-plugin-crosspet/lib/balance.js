// DeepSeek 餘額 → 桌寵的額度檔案。用 Key 查和用賬號查兩條路都走這裡，規則只有一份。
// 賬戶可能同時有人民幣和美元（#10）：官方餘額介面一次返回兩項，而且順序每次可能不一樣，
// 所以不能取第 0 項，要按幣種認、各幣種分別彙總、分別顯示。

const SYMBOL = { CNY: "¥", USD: "$" };
const ORDER = ["CNY", "USD"];  // 顯示順序固定：人民幣在前，不跟著介面返回的順序跳
// 餘額低的門檻按幣種分：¥5 和 $0.7 差不多是同一筆錢
export const DEFAULT_LOW = { CNY: 5, USD: 0.7 };

// 外掛配置 lowBalance：數字 = 人民幣門檻（老寫法，美元還用預設）；物件 = 按幣種，如 { CNY: 10, USD: 1.5 }
export function lowThresholds(config) {
  if (typeof config === "number") return { ...DEFAULT_LOW, CNY: config };
  if (config && typeof config === "object") return { ...DEFAULT_LOW, ...config };
  return { ...DEFAULT_LOW };
}

// entries：[{ currency, amount }]，同一幣種可以出現多次（現金錢包 + 贈送錢包），這裡加起來
// 返回寫進 deepseek-quota.json 的內容；沒有任何幣種時返回 null
export function summarize(entries, { available = true, low = DEFAULT_LOW } = {}) {
  const totals = {};
  for (const e of entries || []) {
    const c = String(e?.currency || "").toUpperCase();
    if (!c) continue;
    totals[c] = (totals[c] || 0) + (Number(e.amount) || 0);
  }
  const currencies = Object.keys(totals).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  if (!currencies.length) return null;
  const balances = {};
  for (const c of currencies) balances[c] = round2(totals[c]);
  // 顯示：餘額為 0 的幣種不佔地方（比如只充過美元的人，介面也可能帶一項 ¥0.00）；全是 0 就顯示主幣種
  const shown = currencies.filter((c) => balances[c] > 0);
  const text = "餘額 " + (shown.length ? shown : currencies.slice(0, 1)).map((c) => fmt(c, balances[c])).join("｜");
  const primary = currencies[0];
  return {
    text,
    balance: balances[primary],  // 老版本桌寵只認這一個數：人民幣優先，和以前一樣
    balances,                    // 新版本桌寵按幣種分別比，判斷是不是充值
    // 所有幣種都低於各自的門檻才算低：有一種還夠用，她就不該喊餓
    low: !available || currencies.every((c) => balances[c] < (low[c] ?? low.CNY ?? DEFAULT_LOW.CNY)),
  };
}

function rank(c) { const i = ORDER.indexOf(c); return i < 0 ? ORDER.length : i; }
function round2(n) { return Math.round(n * 100) / 100; }
function fmt(c, n) { return (SYMBOL[c] ?? `${c} `) + n.toFixed(2); }
