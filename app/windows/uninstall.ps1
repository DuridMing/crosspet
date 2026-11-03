param([switch]$Interactive)
$ErrorActionPreference = 'Stop'
$exitCode = 0
$transcribing = $false
$destination = Join-Path $env:LOCALAPPDATA 'Programs\CrossPet'
$data = Join-Path $env:LOCALAPPDATA 'CrossPet'
$log = Join-Path $data 'uninstall.log'
try {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Start-Transcript -Path $log -Append -Force | Out-Null
    $transcribing = $true
    # Double-clicking starts in the install directory; release our own directory handle.
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
    if (!(Test-Path -LiteralPath $destination)) {
        Write-Host '沒有找到已安裝的 CrossPet。解壓後直接執行的，退出後刪掉那個資料夾就行。'
    } else {
        $exe = Join-Path $destination 'CrossPet.exe'
        $python = Join-Path $destination 'python\python.exe'
        $integrator = Join-Path $destination 'tools\integrate.py'
        if (!(Test-Path -LiteralPath $python) -or !(Test-Path -LiteralPath $integrator)) {
            throw '安裝不完整：請先用完整的安裝包執行 install.cmd，再解除安裝。'
        }
        $env:PYTHONUTF8 = '1'
        $env:CROSSPET_DATA_DIR = $data
        foreach ($target in @('claude-hooks', 'claude-mod', 'codex', 'deepseek', 'gemini', 'antigravity', 'workbuddy', 'zcode', 'hermes', 'qwen')) {
            & $python $integrator uninstall $target
            if ($LASTEXITCODE -ne 0) { throw "撤銷 $target 的接入失敗，程式檔案已保留。" }
        }
        # 只關掉這個位置的 CrossPet，等它釋放檔案
        $processes = @(Get-Process CrossPet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
        foreach ($process in $processes) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        if ($processes.Count -gt 0) {
            Wait-Process -Id $processes.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (Test-Path $run) {
            $key = Get-Item $run
            foreach ($name in $key.GetValueNames()) {
                $value = [string]$key.GetValue($name)
                if ($value.Contains($exe)) { Remove-ItemProperty -Path $run -Name $name }
            }
        }
        # Missing/redirected Start Menu directories must not prevent application removal.
        $programs = [Environment]::GetFolderPath('Programs')
        if (![string]::IsNullOrWhiteSpace($programs)) {
            $shortcut = Join-Path $programs 'CrossPet.lnk'
            if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
        }
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            try { Remove-Item -LiteralPath $destination -Recurse -Force; break }
            catch { if ($attempt -eq 4) { throw }; Start-Sleep -Milliseconds 500 }
        }
        Write-Host 'CrossPet 已解除安裝：各 AI 的接入都已撤銷。角色和設定保留在 %LOCALAPPDATA%\CrossPet，不需要的話可以手動刪除。' -ForegroundColor Green
    }
} catch {
    $exitCode = 1
    Write-Host "解除安裝失敗：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "日誌：$log"
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    # Keep errors visible for double-click users. CLI/tests remain non-interactive.
    if ($Interactive) { Read-Host '按回車關閉' | Out-Null }
}
exit $exitCode
