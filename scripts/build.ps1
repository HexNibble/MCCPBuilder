param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    $candidate = Join-Path $programFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw '未找到 dotnet。请安装官方 .NET 8 SDK (x64)。'
    }
    $dotnetPath = $candidate
} else {
    $dotnetPath = $dotnet.Source
}

$sdkList = & $dotnetPath --list-sdks
if (-not ($sdkList -match '^8\.')) {
    throw '检测到 dotnet 主机，但未安装 .NET 8 SDK (x64)。'
}

& $dotnetPath restore (Join-Path $projectRoot 'MCCPBuilder.sln')
if ($LASTEXITCODE -ne 0) { throw 'NuGet 还原失败。' }
& $dotnetPath build (Join-Path $projectRoot 'MCCPBuilder.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw '项目构建失败。' }
& $dotnetPath test (Join-Path $projectRoot 'tests\MCCPBuilder.Tests\MCCPBuilder.Tests.csproj') -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw '单元测试失败。' }

$launcherProject = Join-Path $projectRoot 'src\MCCPBuilder.Launcher\MCCPBuilder.Launcher.csproj'
$launcherOutput = Join-Path $projectRoot 'artifacts\launcher'
& $dotnetPath publish $launcherProject -c $Configuration --no-restore -o $launcherOutput
if ($LASTEXITCODE -ne 0) { throw 'Launcher.exe 发布失败。' }
if (-not (Test-Path -LiteralPath (Join-Path $launcherOutput 'Launcher.exe'))) {
    throw 'Launcher.exe 发布结果不存在。'
}
