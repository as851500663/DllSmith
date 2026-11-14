# 4DDiG DLL Fixer Rebuild - build script
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1 -DataDir "<original install dir>"
#   powershell -ExecutionPolicy Bypass -File build.ps1 -TestKeys      # self-test build only
# Output:
#   bin\DllSmith.exe  (+ bin\Json\ and bin\7z\ copied from -DataDir)
#   with -TestKeys: bin\DllSmith-test.exe, version 1.4.0-test (never ship this)
#
# Release gate (t13): after compiling we ASSERT that the production binary does not
# contain the test key / test kid, and that the test vector has no private material.
# If any assertion fails the build throws - nothing gets packaged.
#
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads .ps1 as ANSI).

param(
  [string]$DataDir = '',
  [switch]$TestKeys
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
if ($TestKeys) { $exeName = 'DllSmith-test.exe' } else { $exeName = 'DllSmith.exe' }
$out      = Join-Path $bin $exeName
$manifest = Join-Path $src 'app.manifest'
$icon     = Join-Path $src 'app.ico'

$cscArgs = @(
  '/nologo','/target:winexe','/platform:x86','/optimize+',
  ('/out:"' + $out + '"'),
  ('/win32manifest:"' + $manifest + '"'),
  ('/win32icon:"' + $icon + '"'),
  '/nowarn:1591,0618'
) + $refs
if ($TestKeys) { $cscArgs += '/define:DS_TESTKEYS','/define:DS_TESTBUILD' }
$cscArgs += $sources

Write-Host ('csc: ' + $csc)
Write-Host ('testkeys: ' + [bool]$TestKeys + '  output: ' + $exeName)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw ('compile failed: ' + $LASTEXITCODE) }

# ---------------------------------------------------------------- release gate
function Read-Bytes([string]$path) { return [System.IO.File]::ReadAllBytes($path) }

# Test key material that must NEVER appear in a production binary
$testKid      = 'ds1-test'
$testModStart = 'r+xBT0Yct9/YzXPs25m23SZ+mOxl3cUxonJG4Z0Je/3FcGFfQkY2XgizV/OEvkjRyg8Neupze4V5E42rgvqW'

function Assert-Absent([string]$ascii, [string]$utf16, [string]$needle, [string]$label) {
  if ([string]::IsNullOrEmpty($needle)) { return }
  if ($ascii.IndexOf($needle, [System.StringComparison]::Ordinal) -ge 0) {
    throw ('GATE FAIL: ' + $label + ' found (ASCII) in ' + $exeName)
  }
  if ($utf16.IndexOf($needle, [System.StringComparison]::Ordinal) -ge 0) {
    throw ('GATE FAIL: ' + $label + ' found (UTF16) in ' + $exeName)
  }
}

$bytes = Read-Bytes $out
$asciiView = [System.Text.Encoding]::ASCII.GetString($bytes)
$utf16View = [System.Text.Encoding]::Unicode.GetString($bytes)
if (-not $TestKeys) {
  Assert-Absent $asciiView $utf16View $testKid 'test kid'
  Assert-Absent $asciiView $utf16View $testModStart 'test public modulus'
  Write-Host 'GATE OK: production binary carries no test key material'
} else {
  if ($exeName -notmatch '-test\.exe$') { throw 'GATE FAIL: test build must be named *-test.exe' }
  Write-Host 'GATE OK: test build (marked -test, never ship)'
}

# self-check: allowed kid list must be exactly what this build type should have.
# The exe is a winexe, so its stdout can be delivered after exit - read the file it writes.
$keysFile = Join-Path $bin 'Logs\selfcheck-keys.txt'
if (Test-Path $keysFile) { Remove-Item $keysFile -Force }
$proc = Start-Process -FilePath $out -ArgumentList '--selfcheck-keys' -PassThru -Wait
Start-Sleep -Milliseconds 300
if (-not (Test-Path $keysFile)) { throw 'GATE FAIL: --selfcheck-keys wrote no result file' }
$kids = (Get-Content $keysFile -Raw).Trim()
if ($TestKeys) {
  if ($kids -ne '[ds1, ds1-test]') { throw ('GATE FAIL: test build kid list = ' + $kids) }
} else {
  if ($kids -ne '[ds1]') { throw ('GATE FAIL: production kid list = ' + $kids + ' (expected [ds1])') }
}
Write-Host ('GATE OK: allowed kids = ' + $kids + ' (exit ' + $proc.ExitCode + ')')

# test vector must contain public material only, and must not be shippable
$tv = Join-Path (Split-Path -Parent $here) '_team\reports\client\tools\testvector-rs256.json'
if (Test-Path $tv) {
  $tvText = Get-Content $tv -Raw
  foreach ($k in '"d_b64"','"p_b64"','"q_b64"','"dp_b64"','"dq_b64"','"qinv_b64"') {
    if ($tvText -like ('*' + $k + '*')) { throw ('GATE FAIL: private key parameter ' + $k + ' present in testvector-rs256.json') }
  }
  Write-Host 'GATE OK: testvector holds public material only'
}
foreach ($dir in 'installer','dist') {
  $probe = Join-Path (Split-Path -Parent $here) $dir
  if (Test-Path $probe) {
    $leak = Get-ChildItem $probe -Filter 'testvector*' -Recurse -ErrorAction SilentlyContinue
    if ($leak) { throw ('GATE FAIL: testvector found under ' + $dir + ': ' + $leak[0].FullName) }
  }
}
Write-Host 'GATE OK: no testvector under installer\ or dist\'

# ---------------------------------------------------------------- data copy
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

# Client config: online catalog service (1.4.0 forces HTTPS + cert pinning)
$server  = 'https://123.57.94.141:9443'
$channel = 'stable'
$cfgPath = Join-Path $bin 'config.json'
'{ "server": "' + $server + '", "channel": "' + $channel + '", "telemetry": true }' | Set-Content -Path $cfgPath -Encoding UTF8
Write-Host ('config.json -> ' + $server + ' channel=' + $channel)

# Release gate warnings (do not fail the build; they must stay visible)
$sec = Get-Content (Join-Path $src 'UpdateSecurity.cs') -Raw
if ($sec -notmatch 'Ds1ModulusB64\s*=\s*\r?\n?\s*"[A-Za-z0-9+/=]{100,}') {
  Write-Host 'WARN: release public key (ReleaseKeys.Ds1ModulusB64) is EMPTY - auto update stays DISABLED'
}
if ($sec -notmatch 'RootModulusB64\s*=\s*\r?\n?\s*"[A-Za-z0-9+/=]{100,}') {
  Write-Host 'WARN: TLS pin root modulus is EMPTY - https catalog will be rejected'
}

Get-ChildItem $bin | Select-Object Name,Length | Format-Table -AutoSize
Write-Host ('BUILD OK -> ' + $out)
