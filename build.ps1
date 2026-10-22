[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Publish', 'Package')]
    [string]$Action = 'Build',
    [string]$PackageCache
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj'
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
            Invoke-DotNet (@('restore', $projectPath, '--locked-mode') + $packageArguments)
        }
        'Build' {
            Invoke-DotNet (@('build', $projectPath, '-c', 'Release', '-p:RestoreLockedMode=true') + $packageArguments)
        }
        { $_ -in 'Publish', 'Package' } {
            $artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.artifacts'))
            $outputPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'publish/win-x64'))
            if (-not $outputPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw '发布目录必须位于仓库 .artifacts 内'
            }
            # 清空生成目录，避免旧文件混入安装包。
            if (Test-Path -LiteralPath $outputPath) {
                Remove-Item -LiteralPath $outputPath -Recurse -Force
            }
            Invoke-DotNet (@('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                '-p:RestoreLockedMode=true', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-o', $outputPath) + $packageArguments)

            if ($Action -eq 'Package') {
                Invoke-DotNet @('tool', 'restore', '--tool-manifest', (Join-Path $PSScriptRoot '.config/dotnet-tools.json'))
                [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw
                $version = [string]$buildProperties.Project.PropertyGroup.Version
                $installerPath = Join-Path $artifactRoot "installers/win-x64/$version"
                # 打包不启动应用；入口运行核验留待授权后进行。
                Invoke-DotNet @('tool', 'run', 'vpk', '--', 'pack', '--packId', 'USBDisplayHost',
                    '--packTitle', 'USB Display', '--packVersion', $version, '--packDir', $outputPath,
                    '--mainExe', 'UsbDisplayHost.Desktop.exe', '--runtime', 'win-x64',
                    '--icon', (Join-Path $outputPath 'Assets/Branding/app.ico'), '--outputDir', $installerPath,
                    '--channel', 'win-x64', '--delta', 'None', '--noPortable', '--skipVeloAppCheck', '--yes')
                Write-Host "离线安装包目录：$installerPath（未签名，未执行安装或运行验证）"
            }
        }
    }

}
finally {
    Pop-Location
}
