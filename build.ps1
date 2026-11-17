[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Publish', 'Package')]
    [string]$Action = 'Build',
    [string]$PackageCache,
    [ValidateSet('Stable', 'Beta')]
    [string]$Channel = 'Stable',
    [string]$BetaUpdateRepository
)

$ErrorActionPreference = 'Stop'
$beta = $Channel -eq 'Beta'
$betaArguments = @()
if ($BetaUpdateRepository) {
    if (-not $beta -or $Action -notin 'Publish', 'Package' -or
        $BetaUpdateRepository -cnotmatch '\A[A-Za-z0-9-]+/(?!\.{1,2}\z)[A-Za-z0-9_.-]+\z' -or
        $BetaUpdateRepository -ieq 'iamshen/USBDisplayHost') {
        throw 'BetaUpdateRepository 只用于 Beta 发布或打包，须指定独立的 GitHub 仓库（owner/repository）。'
    }
    $betaArguments = @("-p:BetaUpdateRepository=$BetaUpdateRepository")
}
$projectName = if ($beta) { 'UsbDisplayHost.WinUI' } else { 'UsbDisplayHost.Desktop' }
$projectPath = Join-Path $PSScriptRoot "src/$projectName/$projectName.csproj"
$versionPath = if ($beta) { $projectPath } else { Join-Path $PSScriptRoot 'Directory.Build.props' }
[xml]$buildProperties = Get-Content -LiteralPath $versionPath -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
$versionPattern = if ($beta) { '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-beta\.[1-9]\d*$' }
    else { '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' }
if ($version -cnotmatch $versionPattern) { throw "版本 $version 不符合 $Channel 通道的格式" }
$packageChannel = if ($beta) { 'win-x64-beta' } else { 'win-x64' }
$packId = if ($beta) { 'USBDisplayHost.Beta' } else { 'USBDisplayHost' }
$packTitle = if ($beta) { 'USB Display Beta' } else { 'USB Display' }
$solutionPath = Join-Path $PSScriptRoot 'UsbDisplayHost.slnx'
$packageArguments = @()
if ($PackageCache) {
    $packageArguments = @("-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))")
}

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) 失败，退出码：$LASTEXITCODE"
    }
}

Push-Location $PSScriptRoot
try {
    switch ($Action) {
        'Restore' {
            Invoke-DotNet (@('restore', $solutionPath, '--locked-mode') + $packageArguments)
        }
        'Build' {
            Invoke-DotNet (@('build', $solutionPath, '-c', 'Release', '-p:RestoreLockedMode=true') + $packageArguments)
        }
        { $_ -in 'Publish', 'Package' } {
            $artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.artifacts'))
            $outputPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "publish/$packageChannel"))
            if (-not $outputPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw '发布目录必须位于仓库 .artifacts 内'
            }
            # 清空生成目录，避免旧文件混入安装包。
            if (Test-Path -LiteralPath $outputPath) {
                Remove-Item -LiteralPath $outputPath -Recurse -Force
            }
            Invoke-DotNet (@('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                '-p:RestoreLockedMode=true', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-o', $outputPath) + $packageArguments + $betaArguments)

            if ($Action -eq 'Package') {
                Invoke-DotNet @('tool', 'restore', '--tool-manifest', (Join-Path $PSScriptRoot '.config/dotnet-tools.json'))
                $installerPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "installers/$packageChannel/$version"))
                if (-not $installerPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw '安装包目录必须位于仓库 .artifacts 内'
                }
                if (Test-Path -LiteralPath $installerPath) {
                    Remove-Item -LiteralPath $installerPath -Recurse -Force
                }
                # 打包不启动应用；入口运行核验留待授权后进行。
                Invoke-DotNet @('tool', 'run', 'vpk', '--', 'pack', '--packId', $packId,
                    '--packTitle', $packTitle, '--packVersion', $version, '--packDir', $outputPath,
                    '--mainExe', "$projectName.exe", '--runtime', 'win-x64',
                    '--icon', (Join-Path $outputPath 'Assets/Branding/app.ico'), '--outputDir', $installerPath,
                    '--channel', $packageChannel, '--delta', 'None', '--noPortable', '--skipVeloAppCheck', '--yes')
                $checksums = Get-ChildItem -LiteralPath $installerPath -File | Sort-Object Name | ForEach-Object {
                    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                    "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
                }
                Set-Content -LiteralPath (Join-Path $installerPath 'SHA256SUMS.txt') -Value $checksums -Encoding utf8
                Write-Host "离线安装包目录：$installerPath（未签名，未执行安装或运行验证）"
            }
        }
    }

}
finally {
    Pop-Location
}
