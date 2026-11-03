# CrossPet 安裝：複製到 %LOCALAPPDATA%\Programs\CrossPet，建開始選單快捷方式，按新版重新整理已經接入的 AI，然後開啟。不需要管理員許可權。
# 覆蓋安裝即更新：設定、改的名字、自己加的角色、AI 接入都保留。
# -Update：桌寵右鍵「更新到 …」時由舊版呼叫（在後臺執行、不彈視窗），出錯寫到 %LOCALAPPDATA%\CrossPet\update.log
param([switch]$Update)
$ErrorActionPreference = 'Stop'
$destination = Join-Path $env:LOCALAPPDATA 'Programs\CrossPet'
$exe = Join-Path $destination 'CrossPet.exe'
$log = Join-Path $env:LOCALAPPDATA 'CrossPet\update.log'

function Say($text, $color = 'Gray') {
    if ($Update) { Add-Content -LiteralPath $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss ') + $text) -Encoding UTF8 }
    else { Write-Host $text -ForegroundColor $color }
}

try {
    if ($Update) { New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null }
    # 只關掉這個位置的 CrossPet（一鍵更新時舊版自己會退出，這裡等它退乾淨）
    for ($i = 0; $i -lt 20; $i++) {
        $running = @(Get-Process CrossPet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
        if ($running.Count -eq 0) { break }
        if ($i -ge 6) { $running | Stop-Process -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
    if ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($destination)) {
        # 接入的 AI（Codex、DeepSeek Harness……）每次調鉤子都會用 Programs\CrossPet\python\python.exe 跑一下 crosspet-hook.py，一兩秒就結束。
        # 它們在跑時這些檔案刪不掉、目錄也改不了名（#24）：先等它們退出，等 10 秒還在就結束掉（只少記一個事件）
        # 只查會從安裝目錄裡跑起來的幾個程式名：讀全部程式的路徑在 Windows 上一次就要一秒多，按次數算的「10 秒」會拖成半分鐘（test.5 實測 37 秒）
        function OurProcesses { @(Get-Process -Name python, pythonw, CrossPet -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) }) }
        $deadline = (Get-Date).AddSeconds(10)
        while ((OurProcesses).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        $busy = OurProcesses
        if ($busy.Count -gt 0) { Say "結束佔用安裝目錄的程式：$(($busy | ForEach-Object { $_.Name + ' ' + $_.Id }) -join '，')"; $busy | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }

        # 不原地刪（#24：以前先刪舊目錄再複製，刪到一半失敗就留下缺了 characters、integrations 的目錄，之後每次啟動都崩）。
        # 現在新版先完整複製到 CrossPet.new，再把舊目錄改名成 CrossPet.old-時間、新目錄改名成 CrossPet，最後刪舊的。
        # 哪一步失敗都退得回去：舊目錄要麼沒動過、要麼改回原名
        $parent = Split-Path $destination
        $staged = "$destination.new"
        Get-ChildItem -LiteralPath $parent -Directory -Filter 'CrossPet.old-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue   # 上次沒刪掉的舊版本
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
        New-Item -ItemType Directory -Path $staged -Force | Out-Null
        Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $staged -Recurse -Force
        foreach ($need in 'CrossPet.exe', 'characters', 'web', 'integrations', 'tools', 'python') {
            if (-not (Test-Path -LiteralPath (Join-Path $staged $need))) { Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue; throw "新版本複製不完整（缺少 $need），舊版本沒有動" }
        }
        $old = "$destination.old-" + (Get-Date -Format 'yyyyMMddHHmmss')
        $hadOld = Test-Path -LiteralPath $destination
        if ($hadOld) {
            try { Rename-Item -LiteralPath $destination -NewName (Split-Path $old -Leaf) }
            catch {
                Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
                throw "舊版本的檔案正被別的程式佔用，沒法替換（$($_.Exception.Message)）。舊版本沒有動；請關掉 Codex、DeepSeek Harness 等正在用的 AI 後再更新"
            }
        }
        try { Rename-Item -LiteralPath $staged -NewName (Split-Path $destination -Leaf) }
        catch {
            if ($hadOld) { Rename-Item -LiteralPath $old -NewName (Split-Path $destination -Leaf) -ErrorAction SilentlyContinue }   # 換回舊版本
            throw
        }
        if ($hadOld) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }   # 刪不掉（還有檔案被佔用）就留著，下次安裝時再清
    }
    # 去掉「從網上下載的」標記：不然由資源管理器開啟（AI 開始工作時自動出現）可能會彈安全警告
    Get-ChildItem -LiteralPath $destination -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CrossPet.lnk'
    $ws = New-Object -ComObject WScript.Shell
    $link = $ws.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $destination
    $link.IconLocation = (Join-Path $destination 'AppIcon.ico')
    $link.Save()
    Say "已安裝到 $destination" 'Green'

    # 已經接入的 AI 按新版重新整理一遍（沒接入過的不動）；失敗不影響安裝
    try {
        $ErrorActionPreference = 'Continue'   # PowerShell 5.1 會把 Python 的 stderr 當成錯誤，這裡不能因此中斷
        $env:PYTHONIOENCODING = 'utf-8'
        try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
        $out = & (Join-Path $destination 'python\python.exe') (Join-Path $destination 'tools\integrate.py') refresh 2>&1 | Out-String
        if ($out.Trim()) { Say $out.Trim() }
    } catch { Say "重新整理 AI 接入失敗：$_（可以在桌寵右鍵「接入 AI」裡手動更新）" 'Yellow' }

    if (-not $Update) { Write-Host '右鍵桌寵 →「接入 AI」，把你用的 AI 接上。' }
    Start-Process -FilePath $exe
} catch {
    Say "安裝失敗：$_" 'Red'
    if ($Update) {
        # 一鍵更新失敗時把舊版（如果還在）重新開啟，並且明說沒裝上——以前只寫日誌，使用者完全不知道
        if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe }
        try {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show("更新沒裝上：$_`n`nCrossPet 還是原來的版本，可以稍後在右鍵選單裡再點更新。", 'CrossPet') | Out-Null
        } catch { }
    }
    exit 1
}
