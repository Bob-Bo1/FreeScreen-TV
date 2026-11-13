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
function Current-Home([switch]$Diagnostics) {
    $query = @('package','resolve-activity','--brief','-a','android.intent.action.MAIN','-c','android.intent.category.HOME')
    $result = Invoke-Adb (@('-s',$script:address,'shell','cmd') + $query)
    $attempts = @($result)
    if ($result.Code -ne 0 -or $result.Text -notmatch '(?m)^\s*[A-Za-z0-9_.]+/[A-Za-z0-9_.$]+\s*$') {
        $attempts += Invoke-Adb (@('-s',$script:address,'shell','pm') + $query[1..($query.Length-1)])
    }
    foreach ($attempt in $attempts) {
        if ($attempt.Code -ne 0) { continue }
        $lines = @($attempt.Text -split "`r?`n" | ForEach-Object {$_.Trim()} | Where-Object { $_ -match '^[A-Za-z0-9_.]+/[A-Za-z0-9_.$]+$' } | Select-Object -Unique)
        if ($lines.Count -eq 1 -and $lines[0] -notmatch '^(android|com\.android\.internal\.app)/|/(?:[^/]*\.)?(ResolverActivity|ChooserActivity)$') {
            return (Expand-HomeComponent $lines[0])
        }
    }
    if ($Diagnostics) {
        Write-Host '默认主页查询结果：'
        foreach ($attempt in $attempts) {
            if ($attempt.Text) { Write-Host $attempt.Text } else { Write-Host '（电视没有返回内容）' }
        }
        $candidates = Invoke-Adb @('-s',$script:address,'shell','cmd','package','query-activities','--brief','-a','android.intent.action.MAIN','-c','android.intent.category.HOME')
        Write-Host '候选主页查询结果：'
        if ($candidates.Text) { Write-Host $candidates.Text } else { Write-Host '（电视没有返回内容）' }
    }
    return ''
}
function Test-Installed([string]$package) {
    $installed = Shell @('pm','list','packages','--user','0',$package)
    return @($installed -split "`r?`n") -contains "package:$package"
}
function Remove-OwnedPackage([string]$package, [string]$expectedHash) {
    if (!$expectedHash -or !(Test-Installed $package)) { return }
    $path = Shell @('pm','path',$package)
    if ($path -notmatch '^package:([A-Za-z0-9/_~.=+-]+)$') {
        throw "无法核对 $package 的安装路径，已保留恢复记录。"
    }
    $digest = Shell @('sha256sum',$Matches[1])
    if (($digest -split '\s+')[0] -ne $expectedHash) {
        throw "$package 已变更，无法确认是本次安装的版本，已保留恢复记录。"
    }
    $r = Shell @('pm','uninstall','--user','0',$package)
    Write-Host $r
    if ($r -notmatch '(?m)^\s*Success\s*$' -or (Test-Installed $package)) {
        throw "$package 清理失败，已保留恢复记录。"
    }
    Write-Host "已清理 $package"
}
function Save-Recovery($saved) {
    New-Item -ItemType Directory -Force -Path (Split-Path $record -Parent) | Out-Null
    $pending = $record + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $saved | ConvertTo-Json | Set-Content -LiteralPath $pending -Encoding UTF8
        Move-Item -LiteralPath $pending -Destination $record
    } finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    }
}
function Archive-Recovery {
    $name = $key + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json'
    Move-Item -LiteralPath $record -Destination (Join-Path (Split-Path $record -Parent) $name)
}
function Complete-Recovery($saved) {
    if ($saved.previewOnly) {
        Remove-OwnedPackage 'app.freescreen.tvpreview' $saved.previewSha256
        Write-Host '已移除试用主页，电视继续使用原来的主页设置。'
    } else {
        Restore-Original $saved
        Shell @('input','keyevent','HOME') | Out-Null
        Remove-OwnedPackage 'com.ts.launcher' $saved.bridgeSha256
        Remove-OwnedPackage 'com.gathin.tvhome' $saved.homeSha256
        if (!$saved.homeSha256) {
            Write-Host '未记录新主页的安装来源，已保留该应用。'
        }
        Write-Host '原主页已恢复。'
    }
    Archive-Recovery
}
function Expand-PreviewApk([string]$destination) {
    $patch = Join-Path $PSScriptRoot 'home-preview.patch'
    $base = Join-Path $PSScriptRoot 'home.apk'
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($patch))
    try {
        if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'FST1') {
            throw '试用安装包数据无效，请重新解压。'
        }
        $baseHash = [BitConverter]::ToString($reader.ReadBytes(32)).Replace('-','')
        $previewHash = [BitConverter]::ToString($reader.ReadBytes(32)).Replace('-','')
        $length = $reader.ReadInt32()
        if ($length -le 0 -or $length -gt 20MB -or (Get-FileHash -LiteralPath $base).Hash -ne $baseHash) {
            throw '主页安装包校验失败，请重新解压。'
        }
        $source = [IO.BinaryReader]::new([IO.File]::OpenRead($base))
        try {
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try {
                while ($output.Length -lt $length) {
                    $offset = $reader.ReadInt32()
                    $count = $reader.ReadInt32()
                    if ($count -le 0 -or $count -gt ($length - $output.Length) -or $offset -lt -1) {
                        throw '试用安装包数据不完整，请重新解压。'
                    }
                    if ($offset -eq -1) {
                        $chunk = $reader.ReadBytes($count)
                    } else {
                        if ([long]$offset + $count -gt $source.BaseStream.Length) {
                            throw '试用安装包数据超出范围，请重新解压。'
                        }
                        $source.BaseStream.Position = $offset
                        $chunk = $source.ReadBytes($count)
                    }
                    if ($chunk.Length -ne $count) { throw '试用安装包数据不完整，请重新解压。' }
                    $output.Write($chunk,0,$count)
                }
            } finally { $output.Dispose() }
        } finally { $source.Dispose() }
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $previewHash) {
            throw '试用安装包校验失败，请重新解压。'
        }
    } finally { $reader.Dispose() }
}
function Install-Preview {
    $file = Join-Path $PSScriptRoot ('home-preview-' + [Guid]::NewGuid().ToString('N') + '.apk')
    try {
        Expand-PreviewApk $file
        if ($record -and !(Test-Installed 'app.freescreen.tvpreview')) {
            Save-Recovery ([ordered]@{
                serial=$serial; model=$model; fingerprint=$fingerprint; previewOnly=$true
                previewSha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            })
        } else {
            Write-Host '没有可用的试用安装记录时，可以在电视的应用管理中卸载「电视主页试用」。'
        }
        Write-Host '无法确认原主页，正在安装试用版。按遥控器 Home 键仍可使用原来的主页设置。'
        $r = Adb @('install','-r',$file)
        Write-Host $r
        if ($r -notmatch '(?m)^\s*Success\s*$') { throw "试用版安装失败：$r" }
        $r = Shell @('am','start','-W','-n','app.freescreen.tvpreview/com.gathin.tvhome.MainActivity')
        Write-Host $r
        if ($r -match '(?i)error|exception|unable to resolve' -or $r -notmatch '(?m)^Status:\s+ok\s*$') {
            throw '试用版已安装，请在电视应用列表打开「电视主页试用」。'
        }
        Write-Host '已安装并打开试用版。以后可以从电视应用列表打开「电视主页试用」。'
        Write-Host 'TVASSISTANT_SETUP_PREVIEW'
    } finally {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}
function Restore-Original($saved) {
    if ($saved.originalStockEnabled -and $saved.originalHome -eq $stock) {
        $r = Shell @('pm','enable','--user','0','com.mitv.tvhome'); Write-Host $r
        if ($r -notmatch 'new state: enabled') { throw '启用小米原主页失败。' }
    }
    $original = Expand-HomeComponent $saved.originalHome
    if ((Current-Home) -ne $original) {
        $r = Shell @('cmd','package','set-home-activity','--user','0',[string]$saved.originalHome); Write-Host $r
        if ($r -notmatch 'Success') { throw '恢复原主页设置失败。' }
    }
    if ((Current-Home) -ne $original) { throw '系统没有确认原主页已恢复。' }
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
function Install-SelectedPlayer {
    if ($Mode -ne 'install') { return }
    Install-TiviMateDownload $TiviMateApk
    Write-Host '正在安装 TiviMate 播放器。'
    $result = Adb @('install','-r',$TiviMateApk)
    Write-Host $result
    if ($result -notmatch '(?m)^\s*Success\s*$') { throw "TiviMate 安装失败：$result" }
}
function Get-OfficialDownload([uri]$uri, [string]$destination, [long]$maximumBytes) {
    Add-Type -AssemblyName System.Net.Http
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient -ArgumentList $handler
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('FreeScreen-TV-Assistant/1.0')
    $client.DefaultRequestHeaders.Accept.ParseAdd('application/octet-stream')
    $client.Timeout = [TimeSpan]::FromSeconds(90)
    $client.MaxResponseContentBufferSize = $maximumBytes
    try {
        for ($hop = 0; $hop -lt 5; $hop++) {
            if ($uri.Scheme -ne 'https') { throw '官方地址重定向到了非 HTTPS 链接。' }
            $response = $client.GetAsync($uri).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308) -and $response.Headers.Location) {
                    $uri = [uri]::new($uri, $response.Headers.Location)
                    continue
                }
                $null = $response.EnsureSuccessStatusCode()
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                [IO.File]::WriteAllBytes($destination, $bytes)
                return
            } finally { $response.Dispose() }
        }
        throw '官方地址重定向次数过多。'
    } finally { $client.Dispose() }
}

function Install-PlatformTools {
    $tools = @('AdbWinApi.dll','AdbWinUsbApi.dll','adb.exe')
    $missing = @($tools | Where-Object { !(Test-Path -LiteralPath (Join-Path $PSScriptRoot $_) -PathType Leaf) })
    if (!$missing.Count) { return }
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (!$architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    if (!$architecture) {
        $architecture = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
    }
    switch ($architecture) {
        'AMD64' { $folder = 'scrcpy-win64-v5.0.1'; $asset = '620992965'; $sha = 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5' }
        'ARM64' { $folder = 'scrcpy-winarm64-v5.0.1'; $asset = '620993016'; $sha = '16c3fc2068df64946f670e3504b4752073ebae47a416a45b46e79a5ab19c689b' }
        'x86' { $folder = 'scrcpy-win32-v5.0.1'; $asset = '620992917'; $sha = '0cb65ebf1bce892fa4d1502d699af0c7a1a7f24d7febcb9ef72894e3f23fc2ad' }
        default { throw "暂不支持在 $architecture 架构下载 ADB。" }
    }
    $tools = @('AdbWinApi.dll','AdbWinUsbApi.dll','libusb-1.0.dll','adb.exe')
    Write-Host '正在从 scrcpy 官方 GitHub 下载电视连接组件…'
    $staging = Join-Path $PSScriptRoot ('.adb-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $staging
    $archivePath = Join-Path $staging 'platform-tools.zip'
    try {
        Get-OfficialDownload "https://api.github.com/repos/Genymobile/scrcpy/releases/assets/$asset" $archivePath 40MB
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $sha) {
            throw '电视连接组件校验失败。'
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($name in $tools) {
                $entry = $archive.GetEntry("$folder/$name")
                if ($null -eq $entry -or $entry.Length -le 0 -or $entry.Length -gt 30MB) {
                    throw "scrcpy 官方压缩包缺少有效的 $name。"
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $name))
            }
        } finally { $archive.Dispose() }
        foreach ($name in $tools) {
            Move-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $PSScriptRoot $name) -Force
        }
    } catch { throw "下载电视连接组件失败，请检查是否能访问 GitHub 官方下载地址后重试。$($_.Exception.Message)" }
    finally {
        foreach ($name in $tools + @('platform-tools.zip')) {
            $path = Join-Path $staging $name
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        Remove-Item -LiteralPath $staging
    }
}

function Install-TiviMateDownload([string]$apk) {
    if (Test-Path -LiteralPath $apk -PathType Leaf) { return }
    Write-Host '正在从 TiviMate 官网下载播放器…'
    $partial = $apk + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Get-OfficialDownload 'https://tivimate.com/apk' $partial 80MB
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($partial)
        try {
            if (!$archive.GetEntry('AndroidManifest.xml') -or !$archive.GetEntry('classes.dex')) {
                throw '官网下载的文件缺少 APK 必需内容。'
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $partial -Destination $apk
    } catch { throw "TiviMate 下载失败，请检查是否能访问 tivimate.com 后重试。$($_.Exception.Message)" }
    finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}

try {
    $TiviMateApk = Join-Path $PSScriptRoot 'tivimate.apk'
    Write-Host '请先确认电视和电脑处于同一局域网，电视已开启 USB / ADB 调试。'
    $ip = (Read-Host '请输入电视 IP 地址，例如 192.168.1.100').Trim()
    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsed) -or $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'IP 地址格式不正确。' }
    $address = "$ip`:5555"
    Install-PlatformTools
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
    $release = Invoke-Adb @('-s',$script:address,'shell','getprop','ro.build.version.release')
    $sdkResult = Invoke-Adb @('-s',$script:address,'shell','getprop','ro.build.version.sdk')
    $sdk = 0
    if ($sdkResult.Code -eq 0 -and [int]::TryParse($sdkResult.Text, [ref]$sdk) -and $sdk -gt 0) {
        $version = if ($release.Code -eq 0 -and $release.Text) { $release.Text } else { '未知' }
        Write-Host "电视安卓版本：$version（API $sdk）"
        if ($Mode -in @('home','install') -and $sdk -lt 26) {
            Write-Host '当前电视系统版本过低。主页和试用版最低要求 Android 8.0（API 26）。'
            exit 12
        }
    } else {
        Write-Host '电视未返回可识别的安卓 API 版本，将继续检查。'
    }
    $model = Shell @('getprop','ro.product.model')
    $fingerprint = Shell @('getprop','ro.build.fingerprint')
    Write-Host "已连接电视型号：$model"
    Write-Host '其他型号可以测试；Home 替身的效果取决于具体电视系统。'
    if ($Mode -eq 'tivimate') {
        Install-TiviMateDownload $TiviMateApk
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
        Complete-Recovery $saved
    } else {
        if ($record -and (Test-Path -LiteralPath $record)) { throw '已有本机恢复记录，请先恢复原主页。' }
        $homeBefore = Current-Home -Diagnostics
        if (-not $homeBefore) {
            Install-SelectedPlayer
            Install-Preview
            exit 10
        }
        Write-Host "安装前主页：$homeBefore"
        if (-not $record) { throw '无法读取电视序列号，不能安全保存恢复记录。' }
        $disabled = Shell @('pm','list','packages','-d','--user','0')
        if ($homeBefore -eq $stock -and $disabled -match '(?m)^package:com\.mitv\.tvhome\s*$') { throw '小米原主页已经停用，不能安全判断安装前状态。' }
        $bridgeBefore = Shell @('pm','list','packages','--user','0','com.ts.launcher')
        if ($bridgeBefore -match '(?m)^package:com\.ts\.launcher\s*$') { throw '电视已有同名替身应用，无法确认其来源，请先人工检查。' }
        $homeApk = Join-Path $PSScriptRoot 'home.apk'
        $bridgeApk = Join-Path $PSScriptRoot 'bridge.apk'
        $homeHash = $null
        if (!(Test-Installed 'com.gathin.tvhome')) {
            $homeHash = (Get-FileHash -LiteralPath $homeApk -Algorithm SHA256).Hash
        }
        $saved = [ordered]@{
            serial=$serial; model=$model; fingerprint=$fingerprint; originalHome=$homeBefore
            originalStockEnabled=($homeBefore -eq $stock)
            bridgeSha256=(Get-FileHash -LiteralPath $bridgeApk -Algorithm SHA256).Hash
            homeSha256=$homeHash; created=(Get-Date -Format o)
        }
        if ((Read-Host '输入 Y 继续，输入其他内容取消') -notmatch '^[Yy]$') { throw '已取消安装。' }
        Install-SelectedPlayer
        Save-Recovery $saved
        Write-Host '已保存原主页恢复记录。'
        try {
            Write-Host '正在安装新主页。'
            $r = Adb @('install','-r',$homeApk); Write-Host $r
            if ($r -notmatch '(?m)^\s*Success\s*$') { throw "新主页安装失败：$r" }
            $r = Shell @('am','start','-W','-n','com.gathin.tvhome/.MainActivity'); Write-Host $r
            if ($r -notmatch '(?m)^Status:\s+ok\s*$') { throw '新主页未能启动。' }
            Write-Host '正在安装 Home 替身。'
            $r = Adb @('install','-r',$bridgeApk); Write-Host $r
            if ($r -notmatch '(?m)^\s*Success\s*$') { throw "Home 替身安装失败：$r" }
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
                Complete-Recovery $saved
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









