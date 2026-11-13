# 4DDiG DLL Fixer Rebuild - build script
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1 -DataDir "<original install dir>"
# Output:
#   bin\4DDiG DLL Fixer.Rebuild.exe  (+ bin\Json\ and bin\7z\ copied from -DataDir)
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads .ps1 as ANSI).

param(
  [string]$DataDir = ''
)

$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src   = Join-Path $here 'src'
$bin   = Join-Path $here 'bin'
$fwDir = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$csc   = Join-Path $fwDir 'csc.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
New-Item -ItemType Directory -Force -Path $bin | Out-Null

function Find-Ref([string]$name) {
  $fwPath = Join-Path $fwDir ($name + '.dll')
  if (Test-Path $fwPath) { return $fwPath }
  foreach ($g in 'GAC_MSIL','GAC_32','GAC_64') {
    $p = 'C:\Windows\Microsoft.NET\assembly\' + $g + '\' + $name
    if (Test-Path $p) {
      $d = Get-ChildItem $p -Filter ($name + '.dll') -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($d) { return $d.FullName }
    }
  }
  return $null
}

$refs = @()
foreach ($n in 'WindowsBase','PresentationCore','PresentationFramework','System.Xaml',
               'System.Web.Extensions','System.Management','System.IO.Compression.FileSystem',
               'System.IO.Compression') {
  $p = Find-Ref $n
  if ($p) { $refs += ('/r:"' + $p + '"') } else { Write-Host ('warn: missing ref ' + $n) }
}
$refs += '/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Drawing.dll'

$sources  = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
$out      = Join-Path $bin 'DllSmith.exe'
$manifest = Join-Path $src 'app.manifest'
$icon     = Join-Path $src 'app.ico'

$cscArgs = @(
  '/nologo','/target:winexe','/platform:x86','/optimize+',
  ('/out:"' + $out + '"'),
  ('/win32manifest:"' + $manifest + '"'),
  ('/win32icon:"' + $icon + '"'),
  '/nowarn:1591,0618'
) + $refs + $sources

Write-Host ('csc: ' + $csc)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw ('compile failed: ' + $LASTEXITCODE) }

if ($DataDir) {
  $origJson = Join-Path $DataDir 'Json'
  $dstJson  = Join-Path $bin 'Json'
  if (Test-Path $origJson) {
    if (Test-Path $dstJson) { Remove-Item $dstJson -Recurse -Force }
    Copy-Item $origJson $dstJson -Recurse -Force
    Write-Host ('json copied -> ' + $dstJson)
  } else {
    Write-Host ('warn: Json not found under ' + $DataDir)
  }

  $orig7z = Join-Path $DataDir '7z'
  $dst7z  = Join-Path $bin '7z'
  if (Test-Path $orig7z) {
    if (Test-Path $dst7z) { Remove-Item $dst7z -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dst7z | Out-Null
    Copy-Item (Join-Path $orig7z '7z.exe') $dst7z -Force
    $dll = Join-Path $orig7z '7z.dll'
    if (Test-Path $dll) { Copy-Item $dll $dst7z -Force }
    Write-Host ('7z copied -> ' + $dst7z)
  }
} else {
  Write-Host 'note: -DataDir not given, Json\ and 7z\ were not copied'
}

# 客户端配置：指向线上目录服务
$server = 'http://123.57.94.141:8787'
$cfgPath = Join-Path $bin 'config.json'
'{ "server": "' + $server + '" }' | Set-Content -Path $cfgPath -Encoding UTF8

Get-ChildItem $bin | Select-Object Name,Length | Format-Table -AutoSize
Write-Host ('BUILD OK -> ' + $out)
