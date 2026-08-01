$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet restore .\WinState.sln
dotnet build .\WinState.sln --configuration Release --no-restore
