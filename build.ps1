[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Publish')]
    [string]$Action = 'Build',
    [string]$PackageCache
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src/UsbDisplayHost.Desktop/UsbDisplayHost.Desktop.csproj'
$packageArguments = @()
if ($PackageCache) {
    $packageArguments = @("-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))")
}

Push-Location $PSScriptRoot
try {
    switch ($Action) {
        'Restore' {
            & dotnet restore $projectPath --locked-mode @packageArguments
        }
        'Build' {
            & dotnet build $projectPath -c Release -p:RestoreLockedMode=true @packageArguments
        }
        'Publish' {
            $outputPath = Join-Path $PSScriptRoot '.artifacts/publish/win-x64'
            & dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
                -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishAot=false `
                -o $outputPath @packageArguments
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $Action 失败，退出码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
