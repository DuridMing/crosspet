# 效能測試（MC-39）

評估報告的測試程式已經找不到，這裡是照「CrossPet 評估與桌寵效能測試報告」重寫的版本，用來在 Windows 上重測，也是 MC-35 效能驗收的基準線。不進安裝包。

| 測試 | 檔案 | 量什麼 |
| --- | --- | --- |
| hook 計時 | `bench.py` | Python 空啟動、Python hook、Rust hook 每次耗時 |
| Rust hook 等效版 | `rhook/` | 和 Python hook 做一樣的檔案工作 |
| 解碼與整張重畫 | `native/src/main.rs` | 全部立繪解碼、每幀整張重畫 |
| 只重畫變動區域 | `native/src/bin/damage.rs` | 只重畫與送出變動區域、浮動改成移動視窗 |
| WebView2 實機 | `webbench.js` | C# 外殼的冷啟動，各姿態的 CPU 與記憶體 |

## 怎麼跑

```bash
cd maid/bench
cargo build --release --manifest-path rhook/Cargo.toml
cargo build --release --manifest-path native/Cargo.toml

# hook：--python 指定安裝包裡的 Python，才是實際會用的版本
python bench.py --python <CrossPet>/python/python.exe --out results/hook.json

# 原生繪圖
native/target/release/native.exe > results/native.jsonl
native/target/release/damage.exe > results/damage.jsonl

# WebView2：有 GPU 與純 CPU 各跑一次
node webbench.js --exe <CrossPet>/CrossPet.exe --out results/web-gpu.json
node webbench.js --exe <CrossPet>/CrossPet.exe --disable-gpu --out results/web-cpu.json
```

`results/` 與 `target/` 不進 git。量之前先關掉其他吃 CPU 的程式，也不能開著全螢幕程式或鎖定螢幕：這時桌寵會藏起來並暫停所有動畫，`webbench.js` 偵測到就會中止，不留下錯誤的數字。

所有測試都用暫存的資料目錄與假的 Claude 設定，不會動到 `~/.claude/settings.json`，也不需要先跑 `integrate.py`。

## 這些數字不包含什麼

- **`native`、`damage`：** 只算算圖與資料複製，沒有開視窗，不含 Windows 合成到螢幕與移動視窗的 DWM 成本，特效也是簡化版。報告建議實際數字抓 1.3～1.5 倍。真正的數字要等 MC-35 的成品。
- **`webbench.js`：** CPU 是程序樹的總和，不含 DWM 與 GPU 本身。記憶體看 `private_mb`；`working_set_mb` 會把共用分頁重複算。
- **`rhook`：** 只處理常見事件；另外用 FNV-1a 取代 SHA-1 算 session 標記，也沒有 Python hook 找父程式與自動開啟桌寵的部分（`PreToolUse` 不會走到）。
- **`bench.py`：** 不關 Defender，數字已包含它掃描每個新程序的時間。

## 比較基準

報告在 Linux 雲端容器（2 核 vCPU）量的數字，方案之間的比例可以參考，絕對值不能直接比。

| 項目 | 報告的數字 |
| --- | --- |
| Python 空啟動 | 中位數 13.91 ms |
| Python hook | 中位數 40.91 ms，p95 59.27 ms |
| Rust hook | 中位數 2.57 ms，p95 3.51 ms |
| 60 張立繪全部解碼 | 78.8 MB，899 ms |
| 每幀整張重畫 | 每幀縮放 24.37 ms；先放大存起來 11.48 ms |
| 只重畫變動區域 | 閒置 2.03 ms；浮動改移動視窗 0 ms；工作 2.69 ms；工作加移動視窗 0.58 ms |
| WebView 純 CPU | 閒置 10%、工作約 20% 單核；記憶體 184～210 MB |

`native` 只解碼姿態圖，不含 `avatar`、`portrait`、`portrait-card`。報告當時資料夾裡有 60 張 WebP，其中姿態圖 57 張；第一批補畫匯入後姿態圖是 80 張，解碼總量會比報告多，比較時看每張的平均值。

MC-35 的效能驗收標準：閒置 CPU 接近 0、工作時低於單核 5%、記憶體低於 60 MB、事件到畫面低於 50 ms。
