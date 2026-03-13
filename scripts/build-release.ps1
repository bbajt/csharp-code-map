param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "Building CodeMap v$Version"

# Pack as .NET global tool
Write-Host "=== Packing .NET global tool ==="
dotnet pack src/CodeMap.Daemon -c Release "-p:Version=$Version" -o dist/nupkg
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Self-contained binaries (no trimming — Roslyn uses runtime reflection)
foreach ($RID in @("win-x64", "linux-x64", "osx-arm64")) {
    Write-Host "=== Publishing $RID ==="
    dotnet publish src/CodeMap.Daemon -c Release -r $RID `
        --self-contained `
        "-p:PublishSingleFile=true" `
        "-p:EnableCompressionInSingleFile=true" `
        "-p:PublishTrimmed=false" `
        "-p:Version=$Version" `
        -o "dist/$RID"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "=== Build complete ==="
Write-Host "Tool package:  dist/nupkg/codemap-mcp.$Version.nupkg"
Write-Host "Windows x64:   dist/win-x64/CodeMap.Daemon.exe"
Write-Host "Linux x64:     dist/linux-x64/CodeMap.Daemon"
Write-Host "macOS ARM64:   dist/osx-arm64/CodeMap.Daemon"
