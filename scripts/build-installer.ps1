# Open M&S 설치 프로그램 만들기 — release.yml 과 로컬이 같은 이 스크립트를 부른다.
#
#   .\scripts\build-installer.ps1 -Version 1.2.3     # publish → ISCC → installer\Output\OpenMS-Setup-1.2.3.exe
#
# Inno Setup 6 이 필요하다(CI 는 choco install innosetup). ISCC.exe 는 사용자 설치 위치 · Program Files 에서 찾는다.

param(
    # 앱 버전(태그 v1.2.3 → 1.2.3). 0.0.0 = 개발 빌드(자동 업데이트 확인 안 함)
    [string]$Version = "0.0.0",
    # 이미 publish 한 폴더를 쓴다
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Write-Error "버전은 1.2.3 꼴이어야 합니다: '$Version'" }
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish\win-x64"

if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    # self-contained 폴더(단일 파일 아님 — 네이티브 OpenGL · GPU 라이브러리를 실행 폴더에 그대로)
    & dotnet publish (Join-Path $root "src\Sim.App\Sim.App.csproj") `
        -c Release -r win-x64 --self-contained true `
        "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Error "Inno Setup 6 (ISCC.exe) 을 찾지 못했습니다. https://jrsoftware.org/isdl.php 또는 'choco install innosetup'"
}

& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root "installer\OpenMS.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$out = Join-Path $root "installer\Output\OpenMS-Setup-$Version.exe"
Write-Host "설치 프로그램: $out"
exit 0
