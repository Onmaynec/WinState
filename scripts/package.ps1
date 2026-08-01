param(
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0-alpha.1'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$output = Join-Path $root 'artifacts\publish'
$archive = Join-Path $root "artifacts\WinState-$Version-$Runtime.zip"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $output -ItemType Directory -Force | Out-Null

dotnet publish (Join-Path $root 'src\WinState.Cli\WinState.Cli.csproj') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained false `
    --output $output

Copy-Item (Join-Path $root 'README.md') $output
Copy-Item (Join-Path $root 'LICENSE') $output
Copy-Item (Join-Path $root 'schemas') $output -Recurse
Copy-Item (Join-Path $root 'samples') $output -Recurse

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding utf8
Write-Host "Создано: $archive"
