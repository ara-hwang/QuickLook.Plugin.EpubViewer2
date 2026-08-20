param(
    [string]$Configuration = "Release",
    [string]$ProjectName = "QuickLook.Plugin.EpubViewer2"
)

$ErrorActionPreference = "Stop"

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Bin = Join-Path $Root "$ProjectName\bin\$Configuration"

# MSBuild 출력 위치 폴백 (QuickLook 트리 내부 빌드)
$AltBin1 = Join-Path $Root "..\..\Build\$Configuration\QuickLook.Plugin\$ProjectName"
$AltBin2 = Join-Path $Root "bin\$Configuration"

if (-not (Test-Path $Bin) -and (Test-Path $AltBin1)) { $Bin = $AltBin1 }
if (-not (Test-Path $Bin)) { $Bin = $AltBin2 }

if (-not (Test-Path $Bin)) {
    Write-Error "빌드 출력을 찾을 수 없습니다: $Bin`n먼저 Release 빌드를 수행하세요."
    exit 1
}

$Output = Join-Path $Root "$ProjectName.qlplugin"

Write-Host "Packing $Bin -> $Output" -ForegroundColor Cyan

# .qlplugin은 사실상 ZIP
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 기존 파일 제거 후 새로 생성
if (Test-Path $Output) { Remove-Item $Output -Force }

# .qlplugin 내부에는 플러그인 폴더 자체가 포함되어야 함 (QuickLook Installer가 폴더명으로 인식)
# 구조: QuickLook.Plugin.EpubViewer2.qlplugin (ZIP) 내부에 dll/config 등
# 공식 스크립트는 폴더를 ZIP으로 묶음. 여기서는 bin 내용을 그대로 ZIP

$TempZip = "$Output.tmp.zip"
if (Test-Path $TempZip) { Remove-Item $TempZip -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory($Bin, $TempZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item $TempZip $Output -Force

Write-Host "생성 완료: $Output" -ForegroundColor Green
Write-Host "크기: $((Get-Item $Output).Length / 1KB) KB"

# 검증
Write-Host "`n패키지 내용:" -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression
$zip = [System.IO.Compression.ZipFile]::OpenRead($Output)
$zip.Entries | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.FullName) ($($_.Length) bytes)" }
$zip.Dispose()

Write-Host "`n설치 방법: QuickLook 실행 상태에서 $ProjectName.qlplugin 파일을 Space로 미리보기 → Install 클릭" -ForegroundColor Cyan
