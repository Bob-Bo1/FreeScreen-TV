param([ValidateSet('install','home','restore','tivimate','fill')][string]$Mode = 'install', [switch]$NonInteractive)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'Stop'
$exitCode = 0
$adb = Join-Path $PSScriptRoot 'adb.exe'
$root = Split-Path $PSScriptRoot -Parent
$legacyBackupDir = Join-Path $root '电视恢复记录'
$backupDir = Join-Path (Split-Path $root -Parent) '电视助手恢复记录'
$stock = 'com.mitv.tvhome/com.mitv.tvhome.MainActivityUserMode'
$bridge = 'com.ts.launcher/com.gathin.tvhome.MainActivity'
function Invoke-Adb([string[]]$argsList) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = & $adb @argsList 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    $text = ($lines | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
    }) -join "`n"
    return [pscustomobject]@{ Code = $code; Text = $text.Trim() }
}
function Adb([string[]]$argsList) {
    $r = Invoke-Adb (@('-s', $script:address) + $argsList)
    if ($r.Code -ne 0) { throw $r.Text }
    return $r.Text
}
function Shell([string[]]$argsList) { return Adb (@('shell') + $argsList) }
function AdbRaw([string[]]$argsList) { return ((Invoke-Adb (@('-s', $script:address) + $argsList)).Text) }
function Pause-And-Exit { if (-not $NonInteractive) { Read-Host '按回车关闭窗口' | Out-Null } }
function Expand-HomeComponent([string]$component) {
    $package, $activity = $component -split '/', 2
    if ($activity -and $activity.StartsWith('.')) { return "$package/$package$activity" }
    return $component
}
function Current-Home {
    $out = Shell @('cmd','package','resolve-activity','--brief','-a','android.intent.action.MAIN','-c','android.intent.category.HOME')
    $lines = @($out -split "`r?`n" | Where-Object { $_ -match '^[A-Za-z0-9_\.]+/[A-Za-z0-9_\.$]+$' })
    if ($lines.Count -ne 1 -or $lines[0] -match '^(android|com\.android\.internal\.app)/') { return '' }
    return (Expand-HomeComponent $lines[0].Trim())
}
function Restore-Original($saved) {
    if ($saved.originalStockEnabled -and $saved.originalHome -eq $stock) {
        $r = Shell @('pm','enable','--user','0','com.mitv.tvhome'); Write-Host $r
        if ($r -notmatch 'new state: enabled') { throw '启用小米原主页失败。' }
    }
    $r = Shell @('cmd','package','set-home-activity','--user','0',[string]$saved.originalHome); Write-Host $r
    if ($r -notmatch 'Success') { throw '恢复原主页设置失败。' }
    if ((Current-Home) -ne (Expand-HomeComponent $saved.originalHome)) { throw '系统没有确认原主页已恢复。' }
}
function Read-TiviMateField {
    $activity = Shell @('dumpsys','activity','activities')
    if ($activity -notmatch '(?m)(topResumedActivity|mResumedActivity).*ar\.tvplayer\.tv/[^ ]*PlaylistWizardActivity') {
        throw '请先在电视 TiviMate 打开添加 M3U 的网址输入页。'
    }
    $remote = '/data/local/tmp/tv-assistant-' + [Guid]::NewGuid().ToString('N') + '.xml'
    try {
        $null = Shell @('uiautomator','dump',$remote)
        [xml]$screen = Shell @('cat',$remote)
    } finally {
        $null = AdbRaw @('shell','rm','-f',$remote)
    }
    $field = $screen.SelectSingleNode('//node[@package="ar.tvplayer.tv" and @focused="true" and (@class="android.widget.EditText" or @resource-id="ar.tvplayer.tv:id/h")]')
    if ($null -eq $field) { throw '请先选中 TiviMate 的网址输入框。也可以复制地址后手动输入。' }
    return $field.GetAttribute('text')
}
try {
    $TiviMateApk = Join-Path $PSScriptRoot 'tivimate.apk'
    if ($Mode -in @('install','tivimate') -and -not (Test-Path -LiteralPath $TiviMateApk -PathType Leaf)) {
        throw '请把从 TiviMate 官网下载的安装包命名为 tivimate.apk，放到 files 文件夹。'
    }
    if (!(Test-Path -LiteralPath $adb -PathType Leaf)) { throw '请把 Android SDK Platform-Tools 的 Windows 版解压到 files 文件夹，确保 adb.exe 与 setup.ps1 在同一级。' }
    Write-Host '请先确认电视和电脑处于同一局域网，电视已开启 USB / ADB 调试。'
    $ip = (Read-Host '请输入电视 IP 地址，例如 192.168.1.100').Trim()
    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsed) -or $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'IP 地址格式不正确。' }
    $address = "$ip`:5555"
    $connect = Invoke-Adb @('connect', $address)
    Write-Host $connect.Text
    $state = AdbRaw @('get-state')
if ($state -ne 'device') {
    Write-Host ('当前 ADB 状态：' + $state)
    throw '电视尚未连接：请确认电视已开启 ADB 调试并在电视上允许授权，且电视 IP 填写正确。'
}
    if ($Mode -eq 'fill') {
        $url = (Read-Host '订阅地址').Trim()
        if ($url -notmatch '^http://(?:[0-9]{1,3}\.){3}[0-9]{1,3}:28643/home_ready\.m3u$') { throw '订阅地址格式不正确。' }
        if ((Read-TiviMateField) -ne '') { throw '网址框已有内容，请先清空再试。' }
        $null = Shell @('input','text',$url)
        if ((Read-TiviMateField) -ne $url) { throw '未能确认地址已完整填入，请在电视核对或手动输入。' }
        Write-Host '订阅地址已填入，请在电视上点下一步。'
        exit 0
    }
    $model = Shell @('getprop','ro.product.model')
    $fingerprint = Shell @('getprop','ro.build.fingerprint')
    Write-Host "已连接电视型号：$model"
    Write-Host '其他型号可以测试；Home 替身的效果取决于具体电视系统。'
    if ($Mode -eq 'tivimate') {
        Write-Host '只安装 TiviMate 播放器：不修改桌面、不变更 Home、不写恢复记录。'
        $r = Adb @('install','-r',$TiviMateApk); Write-Host $r
        if ($r -notmatch 'Success') { throw "TiviMate 安装失败：$r" }
        Shell @('monkey','-p','ar.tvplayer.tv','-c','android.intent.category.LAUNCHER','1') | Out-Null
        Write-Host 'TiviMate 已安装，并已尝试在电视上打开。'
        Write-Host '请在电视上完成首次启动，再回电脑点“启动直播源”。'
        Write-Host '装好后建议在电视设置中关闭 ADB 调试；以后重装或恢复桌面时再打开。'
        Write-Host 'TVASSISTANT_SETUP_OK|tivimate'
        exit 0
    }
    $serial = Shell @('getprop','ro.serialno')
    $record = $null
    if ($serial -and $serial -ne 'unknown') {
        $key = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($serial)).TrimEnd('=').Replace('/','_').Replace('+','-')
        $record = Join-Path $backupDir "$key.json"
        $legacyRecord = Join-Path $legacyBackupDir "$key.json"
        if (-not (Test-Path -LiteralPath $record) -and (Test-Path -LiteralPath $legacyRecord)) {
            $record = $legacyRecord
        }
    }
    if ($Mode -eq 'restore') {
        if (-not $record) { throw '无法读取电视序列号，不能安全匹配恢复记录。' }
        if (-not (Test-Path -LiteralPath $record)) { throw '没有找到这台电视的恢复记录。请将新版放在旧版同一级目录，或从最初安装的版本恢复。' }
        $saved = Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($saved.serial -ne $serial -or $saved.model -ne $model -or $saved.fingerprint -ne $fingerprint) { throw '恢复记录与当前电视或固件不一致，请先人工检查。' }
        Restore-Original $saved
        Shell @('input','keyevent','HOME') | Out-Null
        $bridgeInstalled = Shell @('pm','list','packages','--user','0','com.ts.launcher')
        if ($bridgeInstalled -match '(?m)^package:com\.ts\.launcher\s*$' -and $saved.bridgeSha256) {
            $path = Shell @('pm','path','com.ts.launcher')
            if ($path -match '(?m)^package:([A-Za-z0-9/_~.=-]+)\s*$') {
                $digest = Shell @('sha256sum',$Matches[1])
                if (($digest -split '\s+')[0] -eq $saved.bridgeSha256) {
                    $r = Shell @('pm','uninstall','--user','0','com.ts.launcher')
                    if ($r -notmatch 'Success') { throw '原主页已恢复，主页替身清理失败。' }
                }
            }
        }
        Write-Host '原主页已恢复。'
        $archived = Join-Path (Split-Path $record -Parent) ($key + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
        Move-Item -LiteralPath $record -Destination $archived
    } else {
        $homeBefore = Current-Home
        if (-not $homeBefore) { throw '无法确定原主页，已停止安装。' }
        Write-Host "安装前主页：$homeBefore"
        if (-not $record) { throw '无法读取电视序列号，不能安全保存恢复记录。' }
        if (Test-Path -LiteralPath $record) { throw '已有本机恢复记录，请先恢复原主页。' }
        $disabled = Shell @('pm','list','packages','-d','--user','0')
        if ($homeBefore -eq $stock -and $disabled -match '(?m)^package:com\.mitv\.tvhome\s*$') { throw '小米原主页已经停用，不能安全判断安装前状态。' }
        $bridgeBefore = Shell @('pm','list','packages','--user','0','com.ts.launcher')
        if ($bridgeBefore -match '(?m)^package:com\.ts\.launcher\s*$') { throw '电视已有同名替身应用，无法确认其来源，请先人工检查。' }
        $files = @()
        if ($Mode -eq 'install') { $files += $TiviMateApk }
        $files += Join-Path $PSScriptRoot 'home.apk'
        Write-Host '正在安装所选应用。'
        foreach ($file in $files) {
            $r = Adb @('install','-r',$file); Write-Host $r
            if ($r -notmatch 'Success') { throw "$file 安装失败：$r" }
        }
        $r = Shell @('am','start','-W','-n','com.gathin.tvhome/.MainActivity'); Write-Host $r
        if ($r -match '(?i)error|exception|unable to resolve') { throw '新主页未能启动，未修改系统主页。' }
        $standby = Shell @('settings','get','global','disable_back_home_whitelist')
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $bridgeHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'bridge.apk')))).Replace('-','') } finally { $sha.Dispose() }
        $saved = [ordered]@{ serial=$serial; model=$model; fingerprint=$fingerprint; originalHome=$homeBefore; originalStockEnabled=($homeBefore -eq $stock); originalStandby=$standby; bridgeSha256=$bridgeHash; created=(Get-Date -Format o) }
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
        $saved | ConvertTo-Json | Set-Content -LiteralPath $record -Encoding UTF8
        Write-Host "已保存恢复记录：$record"
        Write-Host '接下来会尝试设置 Home 替身；已验证的小米主页会先被停用。其他原主页不会停用。'
        if ((Read-Host '输入 Y 继续，输入其他内容取消') -notmatch '^[Yy]$') { throw '已取消切换；原主页未被停用。' }
        Write-Host '正在安装 Home 替身。'
        foreach ($file in @('bridge.apk')) {
            $r = Adb @('install','-r',(Join-Path $PSScriptRoot $file)); Write-Host $r
            if ($r -notmatch 'Success') { throw "$file 安装失败：$r" }
        }
        try {
            if ($homeBefore -eq $stock) {
                $r = Shell @('pm','disable-user','--user','0','com.mitv.tvhome'); Write-Host $r
                if ($r -notmatch 'new state: disabled-user') { throw '停用原主页失败。' }
            }
            $r = Shell @('cmd','package','set-home-activity','--user','0',$bridge); Write-Host $r
            if ($r -notmatch 'Success') { throw '设置 Home 替身失败。' }
            $resolved = Current-Home
            if ($resolved -ne $bridge) { throw "系统未确认替身：$resolved" }
            Shell @('input','keyevent','HOME') | Out-Null
            Start-Sleep -Seconds 3
            $activities = Shell @('dumpsys','activity','activities')
            if ($activities -notmatch '(?m)(topResumedActivity|mResumedActivity).*com\.gathin\.tvhome/\.MainActivity') { throw '无法确认新主页位于前台。' }
        } catch {
            $failure = $_.Exception.Message
            Write-Host '替身设置失败，正在尝试恢复原主页。'
            try {
                Restore-Original $saved
                $r = Shell @('pm','uninstall','--user','0','com.ts.launcher'); Write-Host $r
                if ($r -notmatch 'Success') { throw '替身卸载失败，请手动检查。' }
                Move-Item -LiteralPath $record -Destination (Join-Path (Split-Path $record -Parent) ($key + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
                Write-Host '已确认原主页恢复并移除本次替身。'
            } catch { throw "替身设置失败：$failure；自动恢复未确认：$($_.Exception.Message)。请保留恢复记录并检查电视。" }
            throw "替身设置失败：$failure；已恢复原主页。"
        }
        Write-Host '安装完成。请用真实遥控器检查 Home、重启和待机唤醒。'
        Write-Host '本程序没有修改待机保留开关；不同固件的待机行为需要单独验证。'
    }
    Write-Host "TVASSISTANT_SETUP_OK|$Mode"
} catch {
    $exitCode = 1
    Write-Host ("操作未完成：" + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'TVASSISTANT_SETUP_FAILED'
    if ($Mode -in @('home','install')) { Write-Host '如果切换后画面异常，请保持电视连接，在电视助手中使用“恢复原主页”。' }
} finally { Pause-And-Exit }
exit $exitCode
