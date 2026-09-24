# Guard: Mihon.ExtensionsBridge.Models must stay IKVM-free.
#
# The whole Rensaio.RPC isolation strategy depends on this assembly being pure
# POCOs + interfaces, so RensaioBackend can reference it without pulling in
# IKVM, AndroidCompat, CEF, or any Mihon extension binary.
#
# This script fails the build if that invariant is broken.
#
# Usage:  pwsh -File verify-models-ikvm-free.ps1

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$modelsDir = Join-Path $repoRoot "Mihon.ExtensionsBridge.Net\Mihon.ExtensionsBridge.Models"
$modelsProj = Join-Path $modelsDir "Mihon.ExtensionsBridge.Models.csproj"

if (-not (Test-Path $modelsProj)) {
    Write-Error "Models project not found at $modelsProj"
    exit 1
}

$failed = $false

# 1) No PackageReferences (any external package could bring native/IKVM deps).
$projContent = Get-Content $modelsProj -Raw
if ($projContent -match "<PackageReference") {
    Write-Host "FAIL: Mihon.ExtensionsBridge.Models.csproj declares a PackageReference."
    $failed = $true
}

# 2) No IKVM/Android/Cef binaries referenced.
foreach ($forbidden in @("Android.Compat", "IKVM", "CefSharp", "cef")) {
    if ($projContent -match [regex]::Escape($forbidden)) {
        Write-Host "FAIL: Models project references '$forbidden'."
        $failed = $true
    }
}

# 3) No java.* / android.* / ikvm.* / kotlin.* / com.sun.* usings in any .cs file.
$patterns = @(
    "^\s*using\s+java\.",
    "^\s*using\s+android\.",
    "^\s*using\s+androidx\.",
    "^\s*using\s+ikvm\.",
    "^\s*using\s+kotlin\.",
    "^\s*using\s+com\.sun\.",
    "^\s*using\s+sun\."
)

Get-ChildItem -Path $modelsDir -Recurse -Filter "*.cs" | ForEach-Object {
    $lines = Get-Content $_.FullName
    foreach ($p in $patterns) {
        $hits = $lines | Select-String -Pattern $p
        if ($hits) {
            foreach ($h in $hits) {
                Write-Host "FAIL: $($_.Name):$($h.LineNumber) -> $($h.Line.Trim())"
                $failed = $true
            }
        }
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "Mihon.ExtensionsBridge.Models is no longer IKVM-free. RensaioBackend cannot safely reference it."
    exit 1
}

Write-Host "OK: Mihon.ExtensionsBridge.Models is IKVM-free."
exit 0
