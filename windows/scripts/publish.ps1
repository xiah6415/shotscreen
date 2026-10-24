# Builds the distributable 拾光 for Windows: one self-contained exe (no .NET install needed), signed with the
# "Glean Local Signing" certificate, plus its SHA-256 — the two files a GitHub Release carries for Windows.
#   powershell -ExecutionPolicy Bypass -File windows/scripts/publish.ps1             # signs if the certificate exists
#   powershell -ExecutionPolicy Bypass -File windows/scripts/publish.ps1 -RequireSignature
# Output: windows/dist/Glean-<VERSION>-win-x64.exe and .sha256
param([switch]$RequireSignature)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+(\.\d+)*$') { throw "VERSION '$version' must look like 1.2.3" }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

$dist = Join-Path $root 'windows\dist'
$publishDir = Join-Path $dist 'publish'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& $dotnet publish (Join-Path $root 'windows\src\SuYanShot') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$name = "Glean-$version-win-x64.exe"
$exe = Join-Path $dist $name
Copy-Item (Join-Path $publishDir 'Glean.exe') $exe -Force

# Updates are only accepted when they carry the same certificate as the installed app.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=Glean Local Signing' } | Select-Object -First 1
if ($cert) {
    $signature = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
    if (-not $signature.SignerCertificate) { throw "Signing failed: $($signature.StatusMessage)" }
    Write-Output "Signed with $($cert.Thumbprint)"
} elseif ($RequireSignature) {
    throw "No 'Glean Local Signing' certificate: run windows/scripts/make-signing-cert.ps1, or import the backed-up .pfx"
} else {
    Write-Warning "Not signed: this build cannot update itself or be offered as an update."
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash  $name" -Encoding ASCII
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Output "Built $exe ($size MB), sha256 $hash"
