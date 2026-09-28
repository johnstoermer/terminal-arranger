$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'TerminalRearranger.csproj'
$outputPath = Join-Path $PSScriptRoot 'dist'
dotnet publish $projectPath -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Output (Join-Path $outputPath 'TerminalRearranger.exe')
