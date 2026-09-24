# Guard: RensaioBackend and RensaioTray must stay isolated from IKVM/CEF/Mihon runtime.
#
# The whole point of Rensaio.RPC is that the backend (and the tray that hosts it)
# never load IKVM, AndroidCompat, CEF, or the Mihon extension runtime. Only the
# IKVM-free Mihon.ExtensionsBridge.Models and the gRPC contracts may be referenced.
#
# This script fails the build if that invariant is broken — it is the compiler-level
# counterpart to verify-models-ikvm-free.ps1.
#
# Usage:  pwsh -File verify-backend-isolated.ps1

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$failed = $false

$projects = @(
    (Join-Path $repoRoot "RensaioBackend\RensaioBackend.csproj"),
    (Join-Path $repoRoot "RensaioTray\RensaioTray.csproj")
)

# 1) Forbidden project references (the Core runtime and its IKVM/CEF payload).
foreach ($proj in $projects) {
    if (-not (Test-Path $proj)) {
        Write-Host "FAIL: project not found: $proj"
        $failed = $true
        continue
    }

    # Only inspect actual reference elements, never comments or remarks (the
    # project files intentionally document the forbidden dependencies in comments).
    $refLines = (Get-Content $proj) |
        Where-Object { $_ -match "<(ProjectReference|PackageReference|Reference)\b" }

    foreach ($line in $refLines) {
        if ($line -match "Mihon\.ExtensionsBridge\.Core") {
            Write-Host "FAIL: $([IO.Path]::GetFileName($proj)) references Mihon.ExtensionsBridge.Core -> $($line.Trim())"
            $failed = $true
        }

        foreach ($forbidden in @("IKVM", "Android\.Compat", "CefSharp", "jcefmaven")) {
            if ($line -match $forbidden) {
                Write-Host "FAIL: $([IO.Path]::GetFileName($proj)) references '$forbidden' -> $($line.Trim())"
                $failed = $true
            }
        }
    }
}

# 2) Forbidden Java/Android/IKVM/Kotlin usings in backend and tray sources.
$patterns = @(
    "^\s*using\s+java\.",
    "^\s*using\s+javax\.",
    "^\s*using\s+android\.",
    "^\s*using\s+androidx\.",
    "^\s*using\s+ikvm\.",
    "^\s*using\s+kotlin\.",
    "^\s*using\s+jdk\.",
    "^\s*using\s+com\.sun\.",
    "^\s*using\s+sun\.",
    "^\s*using\s+static\s+(java|javax|android|androidx|ikvm|kotlin|jdk|com\.sun|sun)\.",
    "^\s*using\s+Mihon\.ExtensionsBridge\.Core\."
)

$sourceDirs = @(
    (Join-Path $repoRoot "RensaioBackend"),
    (Join-Path $repoRoot "RensaioTray")
)

foreach ($dir in $sourceDirs) {
    Get-ChildItem -Path $dir -Recurse -Filter "*.cs" -File |
        Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" } |
        ForEach-Object {
            $lines = Get-Content $_.FullName
            foreach ($p in $patterns) {
                $hits = $lines | Select-String -Pattern $p
                foreach ($h in $hits) {
                    Write-Host "FAIL: $($_.FullName.Substring($repoRoot.Path.Length + 1)):$($h.LineNumber) -> $($h.Line.Trim())"
                    $failed = $true
                }
            }
        }
}

if ($failed) {
    Write-Host ""
    Write-Host "RensaioBackend/RensaioTray are no longer isolated from the IKVM/CEF/Mihon runtime."
    Write-Host "All extension functionality must go through the Rensaio.RPC gRPC boundary."
    exit 1
}

Write-Host "OK: RensaioBackend and RensaioTray are isolated from IKVM/CEF/Mihon."
exit 0
