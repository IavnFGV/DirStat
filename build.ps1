param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build (Join-Path $root 'DiskSpaceMonitor.sln') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $root 'tests/DiskSpaceMonitor.Tests/DiskSpaceMonitor.Tests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    dotnet publish (Join-Path $root 'src/DiskSpaceMonitor/DiskSpaceMonitor.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=none -o (Join-Path $root 'publish/win-x64')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $pdb = Join-Path $root 'publish/win-x64/DiskSpaceMonitor.pdb'
    if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb }
}
