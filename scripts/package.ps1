param(
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.0.0',
    [string]$SigningThumbprint = ''
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

$authenticodeSigned = $false
if (-not [string]::IsNullOrWhiteSpace($SigningThumbprint)) {
    if (-not $IsWindows) {
        throw 'Authenticode signing поддерживается только в Windows.'
    }

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName -First 1
    }
    if (-not $signtool) {
        throw 'signtool.exe не найден.'
    }

    $signTargets = Get-ChildItem -LiteralPath $output -File -Recurse |
        Where-Object { $_.Extension -in '.exe', '.dll' }
    foreach ($target in $signTargets) {
        & $signtool sign /sha1 $SigningThumbprint /fd SHA256 /td SHA256 `
            /tr 'http://timestamp.digicert.com' /v $target.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Не удалось подписать $($target.FullName)."
        }
        & $signtool verify /pa /v $target.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification не пройдена: $($target.FullName)."
        }
    }
    $authenticodeSigned = $true
}

$marker = [ordered]@{
    schemaVersion = 1
    product = 'WinState'
    version = $Version
    runtime = $Runtime
    repository = 'Onmaynec/WinState'
    packagedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    authenticodeSigned = $authenticodeSigned
}
$marker | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $output 'winstate.release.json') `
    -Encoding utf8

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding utf8
Write-Host "Создано: $archive"
Write-Host "SHA-256: $hash"
Write-Host "Authenticode: $authenticodeSigned"
