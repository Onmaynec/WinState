param(
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.6.0-alpha.1'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts ("publish-" + $Runtime)
$archive = Join-Path $artifacts "WinState-$Version-$Runtime.zip"

New-Item $artifacts -ItemType Directory -Force | Out-Null
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $archive -Force -ErrorAction SilentlyContinue
Remove-Item "$archive.sha256" -Force -ErrorAction SilentlyContinue
New-Item $output -ItemType Directory -Force | Out-Null

dotnet publish (Join-Path $root 'src\WinState.Cli\WinState.Cli.csproj') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

Copy-Item (Join-Path $root 'README.md') $output
Copy-Item (Join-Path $root 'LICENSE') $output
Copy-Item (Join-Path $root 'schemas') $output -Recurse
Copy-Item (Join-Path $root 'samples') $output -Recurse

$marker = [ordered]@{
    schemaVersion = 1
    product = 'WinState'
    version = $Version
    runtime = $Runtime
    repository = 'Onmaynec/WinState'
    packagedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$marker | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $output 'winstate.release.json') `
    -Encoding utf8

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding utf8
Write-Host "Создано: $archive"
Write-Host "SHA-256: $hash"
