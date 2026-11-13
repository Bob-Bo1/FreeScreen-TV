param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '需要 Windows .NET Framework 4.8。' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$exe = Join-Path $OutputDirectory '电视助手.exe'
$icon = Join-Path $PSScriptRoot '电视助手.ico'
$sources = (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File).FullName
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$icon" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw "编译失败 ($LASTEXITCODE)。" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '电视助手.exe.config') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'files') -Destination $OutputDirectory -Recurse -Force

