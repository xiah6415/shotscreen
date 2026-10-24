# Creates the self-signed code-signing certificate that release builds of 拾光 for Windows are signed with
# (the counterpart of scripts/make-signing-cert.sh on macOS). Run once on the PC that makes releases:
#   powershell -ExecutionPolicy Bypass -File windows/scripts/make-signing-cert.ps1
#
# The updater only installs a new version signed with the same certificate as the running app, so keep a backup:
# export it with its private key (certmgr.msc → Personal → Certificates → "Glean Local Signing" → Export) and store the
# .pfx somewhere safe. Losing it means users have to download the next version by hand once.
$subject = 'CN=Glean Local Signing'
$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject }
if ($existing) {
    Write-Output "Already have '$subject' (thumbprint $($existing[0].Thumbprint)); nothing to do."
    exit 0
}
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(20)
Write-Output "Created '$subject' (thumbprint $($cert.Thumbprint)) in Cert:\CurrentUser\My. Back it up (see the comment at the top)."
