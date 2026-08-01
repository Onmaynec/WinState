$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet test .\WinState.sln --configuration Release --no-build
