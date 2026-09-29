Get-ChildItem -Path $PSScriptRoot -Directory -Recurse -Include 'bin','obj' |
    Remove-Item -Recurse -Force
Write-Host "Cleaned bin/ and obj/ directories."
