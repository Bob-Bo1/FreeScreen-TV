param(
    [string]$TvIp = '',
    [ValidateRange(1024,65535)][int]$Port = 28643,
    [string]$ListenAddress = '',
    [switch]$NonInteractive
)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root '直播源\home_ready.m3u'
$epg = Join-Path $root '直播源\epg.xml.gz'
$listener = $null
$exitCode = 0

function Test-PrivateAddress([string]$Value) {
    return $Value -match '^(192\.168\.|10\.|172\.(1[6-9]|2\d|3[01])\.)'
}
function Test-SameSubnet {
    param([Net.IPAddress]$A, [int]$Prefix, [Net.IPAddress]$B)
    if ($Prefix -lt 1 -or $Prefix -gt 32 -or $null -eq $A -or $null -eq $B) { return $false }
    $ab=$A.GetAddressBytes(); $bb=$B.GetAddressBytes()
    $full=[math]::Floor($Prefix/8)
    for($i=0;$i -lt $full;$i++) { if($ab[$i] -ne $bb[$i]) { return $false } }
    $rem=$Prefix % 8
    if($rem -gt 0) {
        $mask=(0xFF -shl (8-$rem)) -band 0xFF
        if(($ab[$full] -band $mask) -ne ($bb[$full] -band $mask)) { return $false }
    }
    return $true
}
function Get-LanAddress([Net.IPAddress]$Target) {
    $virtualHints=@('singbox','sing-box','tailscale','zerotier','wintun','wireguard','vethernet','vmware','virtualbox','hyper-v','docker','wsl','tap-','tun','loopback','bluetooth','蓝牙')
    $cands=@()
    foreach($ni in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if($ni.OperationalStatus -ne 'Up' -or $ni.NetworkInterfaceType -eq 'Loopback') { continue }
        $desc=($ni.Name+' '+$ni.Description).ToLowerInvariant()
        $skip=$false
        foreach($hint in $virtualHints) { if($desc.Contains($hint)) { $skip=$true; break } }
        if($skip) { continue }
        foreach($ua in $ni.GetIPProperties().UnicastAddresses) {
            if($ua.Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { continue }
            $ip=$ua.Address.ToString()
            if(-not (Test-PrivateAddress $ip)) { continue }
            $same=Test-SameSubnet -A $ua.Address -Prefix $ua.PrefixLength -B $Target
            # 子网匹配先于地址偏好，不能让不同子网的192.168压过正确的10网段。
            $rank=2
            if($ip.StartsWith('192.168.')) { $rank=0 } elseif($ip.StartsWith('10.')) { $rank=1 }
            $cands += [pscustomobject]@{ SubnetRank=([int](-not $same)); Rank=$rank; IP=$ip }
        }
    }
    if($cands.Count -eq 0) { return $null }
    $chosen=$cands | Sort-Object SubnetRank,Rank,IP | Select-Object -First 1
    if($chosen.SubnetRank -ne 0) { Write-Host '[提示] 未找到与电视同子网的物理网卡，请检查电视IP、访客网络及AP隔离。' }
    return $chosen.IP
}
function Get-FilePayload([string]$Path,[string]$Kind) {
    $dataRoot = Split-Path $source -Parent
    $lease = $null
    try {
        $lease = New-Object IO.FileStream((Join-Path $dataRoot '.iptv-write.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        if (Test-Path -LiteralPath (Join-Path $dataRoot '.iptv-transaction.pending')) { throw '发现未完成更新，请打开助手的直播维护恢复数据。' }
        return ,(Read-FilePayload $Path $Kind)
    } finally { if ($null -ne $lease) { $lease.Dispose() } }
}
function Read-FilePayload([string]$Path,[string]$Kind) {
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $fi=Get-Item -LiteralPath $Path
    if($fi.Length -gt 16777216) { throw '文件超过16MB分发上限，请先检查导入文件。' }
    # 列表很小、节目表通常不足1MB；直接重读避免同大小/同时间戳缓存失效。
    if($Kind -eq 'm3u') {
        $text=[IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8)
        if($text -notmatch '(?m)^#EXTM3U(?:\s|$)') { throw '播放列表缺少EXTM3U头，请重新导入有效列表。' }
        if (-not (Test-Path -LiteralPath $epg -PathType Leaf)) { return ,((New-Object Text.UTF8Encoding $false).GetBytes($text)) }
        $replacement=' x-tvg-url="'+$url+'/epg.xml.gz"'
        $text=[regex]::Replace($text,'(?m)^#EXTM3U[^\r\n]*',{
            param($m)
            $header=[regex]::Replace($m.Value,'(?i)\s+(?:x-tvg-url|url-tvg)\s*=\s*"[^"]*"','')
            return $header+$replacement
        })
        return ,((New-Object Text.UTF8Encoding $false).GetBytes($text))
    }
    return ,([IO.File]::ReadAllBytes($Path))
}
try {
    if([string]::IsNullOrWhiteSpace($TvIp)) {
        if($NonInteractive) { throw '没有电视IP；请在助手中填写电视IPv4地址。' }
        $TvIp=(Read-Host '请输入电视 IP 地址').Trim()
    }
    $parsed=$null
    if(-not [Net.IPAddress]::TryParse($TvIp,[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw '电视IP地址格式不正确。' }
    if([string]::IsNullOrWhiteSpace($ListenAddress)) { $ListenAddress=Get-LanAddress $parsed }
    if([string]::IsNullOrWhiteSpace($ListenAddress)) { throw '没有找到可供电视访问的家庭网卡，请连接家庭WiFi或网线，关闭仅有虚拟网卡的环境。' }
    $bindAddress=$null
    if(-not [Net.IPAddress]::TryParse($ListenAddress,[ref]$bindAddress) -or (-not (Test-PrivateAddress $ListenAddress) -and $ListenAddress -ne '127.0.0.1')) { throw '仅允许绑定家庭局域网地址或本机测试地址。' }
    $url="http://${ListenAddress}:$Port"
    if(-not (Test-Path -LiteralPath $source)) { throw '找不到播放列表，请先导入M3U文件。' }
    $null=Get-FilePayload $source 'm3u'
    if(-not (Test-Path -LiteralPath $epg)) { Write-Host '[提示] 未找到节目表：频道列表仍可使用，请在助手中更新节目表。' }
    $listener=New-Object Net.Sockets.TcpListener($bindAddress,$Port)
    $listener.Start()
    Write-Host "播放列表：$url/home_ready.m3u"
    Write-Host "节目表：$url/epg.xml.gz"
    Write-Host "TVASSISTANT_SERVER_READY|$url"
    Write-Host '服务已启动；这只表示电脑正在提供文件，不代表电视已添加订阅或频道能够播放。'
    Write-Host '刷新列表和节目表时请保持电脑、助手开启。服务不转发视频。建议路由器固定分配电脑IP。'
    while($true) {
        $client=$listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout=5000; $client.SendTimeout=15000
            $stream=$client.GetStream()
            $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::ASCII,$false,1024,$true)
            $request=$reader.ReadLine()
            if($null -eq $request -or $request.Length -gt 4096) { throw '请求行异常' }
            $headerLength=$request.Length
            for($n=0;$n -lt 64;$n++) {
                $line=$reader.ReadLine()
                if([string]::IsNullOrEmpty($line)) { break }
                $headerLength+=$line.Length
                if($headerLength -gt 16384) { throw '请求头过长' }
            }
            $status='404 Not Found'; $type='text/plain; charset=utf-8'; $payload=$null; $head=$false
            if($request -match '^(GET|HEAD) (/[^ ]*) HTTP/1\.[01]$') {
                $head=$Matches[1] -eq 'HEAD'
                $path=($Matches[2] -split '\?',2)[0]
                try {
                    if($path -eq '/home_ready.m3u') { $payload=Get-FilePayload $source 'm3u'; $type='audio/x-mpegurl; charset=utf-8' }
                    elseif($path -eq '/epg.xml.gz') { $payload=Get-FilePayload $epg 'epg'; $type='application/gzip' }
                    if($null -ne $payload) { $status='200 OK' }
                } catch {
                    $status='503 Service Unavailable'; $type='text/plain; charset=utf-8'
                    $payload=[Text.Encoding]::UTF8.GetBytes('数据正在更新或需要恢复，请稍后重试并查看助手记录。')
                    Write-Host ('暂停分发：'+$_.Exception.Message)
                }
            }
            if($null -eq $payload) { $payload=[Text.Encoding]::UTF8.GetBytes('文件不存在；请检查助手中的导入文件。') }
            $header=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: $type`r`nContent-Length: $($payload.Length)`r`nCache-Control: no-store, no-cache, must-revalidate`r`nPragma: no-cache`r`nConnection: close`r`n`r`n")
            $stream.Write($header,0,$header.Length)
            if(-not $head) { $stream.Write($payload,0,$payload.Length) }
            $stream.Flush()
            Write-Host ("[$(Get-Date -Format 'HH:mm:ss')] $status "+$request)
        } catch { Write-Host ('请求未完成：'+$_.Exception.Message) }
        finally { $client.Close() }
    }
} catch {
    $exitCode=1
    Write-Host ('直播源服务未启动：'+$_.Exception.Message) -ForegroundColor Red
    Write-Host 'TVASSISTANT_SERVER_FAILED'
    if(-not $NonInteractive) { Read-Host '按回车关闭' | Out-Null }
} finally { if($null -ne $listener) { $listener.Stop() } }
exit $exitCode
