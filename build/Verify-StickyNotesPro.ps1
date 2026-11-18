<#
  StickyNotes Pro __VERSION__ – independent integrity verification
  ---------------------------------------------------------------
  Checks a StickyNotesPro.exe against the values published in the official
  "Certificate of Integrity" for this release:
    1. SHA-256 of the file
    2. Authenticode signature present and not altered (no HashMismatch)
    3. Signer certificate = the pinned StickyNotes Pro release certificate

  Usage:  powershell -ExecutionPolicy Bypass -File .\Verify-StickyNotesPro.ps1 [-Path <file>]
#>
param(
    [string]$Path = $(if (Test-Path "$PSScriptRoot\StickyNotesPro.exe") { "$PSScriptRoot\StickyNotesPro.exe" } else { "$env:ProgramFiles\StickyNotes Pro\StickyNotesPro.exe" })
)

$ExpectedSha256 = '__EXE_SHA256__'
$PinnedCertSha256 = '__PIN__'
$ok = $true

Write-Host ""
Write-Host "StickyNotes Pro __VERSION__ - integrity verification" -ForegroundColor Cyan
Write-Host "File: $Path"
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Write-Host "FAIL  file not found" -ForegroundColor Red; exit 2 }

# 1. SHA-256
$hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
if ($hash -eq $ExpectedSha256) { Write-Host "PASS  SHA-256 matches the published value" -ForegroundColor Green }
else { Write-Host "FAIL  SHA-256 $hash does not match the published value $ExpectedSha256" -ForegroundColor Red; $ok = $false }

# 2. Authenticode signature
$sig = Get-AuthenticodeSignature -LiteralPath $Path
switch ($sig.Status) {
    'Valid'        { Write-Host "PASS  Authenticode signature valid and trusted" -ForegroundColor Green }
    'UnknownError' { Write-Host "PASS  Authenticode signature intact (publisher certificate is self-signed / not in the Windows trust store)" -ForegroundColor Green }
    default        { Write-Host "FAIL  Authenticode status: $($sig.Status) - $($sig.StatusMessage)" -ForegroundColor Red; $ok = $false }
}

# 3. pinned signer certificate
if ($sig.SignerCertificate) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $pin = ([System.BitConverter]::ToString($sha.ComputeHash($sig.SignerCertificate.RawData))).Replace('-', '')
    if ($pin -eq $PinnedCertSha256) { Write-Host "PASS  signed by the StickyNotes Pro release certificate" -ForegroundColor Green }
    else { Write-Host "FAIL  signed by an unknown certificate ($($sig.SignerCertificate.Subject))" -ForegroundColor Red; $ok = $false }
    Write-Host ("      Subject    : " + $sig.SignerCertificate.Subject)
    Write-Host ("      Thumbprint : " + $sig.SignerCertificate.Thumbprint + "  (SHA-1)")
    Write-Host ("      SHA-256    : " + $pin)
    if ($sig.TimeStamperCertificate) { Write-Host ("      Timestamp  : " + $sig.TimeStamperCertificate.Subject) }
} else { Write-Host "FAIL  no signer certificate" -ForegroundColor Red; $ok = $false }

Write-Host ""
if ($ok) { Write-Host "RESULT: GENUINE - this file is the authentic, unmodified StickyNotes Pro __VERSION__ release." -ForegroundColor Green; exit 0 }
else     { Write-Host "RESULT: NOT GENUINE - do not run this file. Reinstall from the official setup." -ForegroundColor Red; exit 1 }
