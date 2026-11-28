$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ((Test-Path (Join-Path $here 'fvs-dev-root-ca.cer')) -and ($args -notcontains '-IReallyWantANewRoot')) {
    Write-Host 'REFUSED: the publisher root already exists and is pinned in the app. Nothing to do: the build renews the signing certificate by itself (UPDATEPIN_02).'
    exit 1
}
$bytes = New-Object byte[] 24; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes) -replace '[/+=]', ''
$secure = ConvertTo-SecureString $password -AsPlainText -Force

$root = New-SelfSignedCertificate -Type Custom -KeySpec Signature -KeyExportPolicy Exportable `
    -Subject 'CN=FVS Local Development Root CA, O=Alon Reich, C=IL' -KeyAlgorithm RSA -KeyLength 4096 `
    -HashAlgorithm SHA256 -KeyUsage CertSign, CRLSign -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') -CertStoreLocation 'Cert:\CurrentUser\My'

$leaf = New-SelfSignedCertificate -Type CodeSigningCert -KeySpec Signature -KeyExportPolicy Exportable `
    -Subject 'CN=Alon Reich (FVS local development), O=Alon Reich, C=IL' -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(3) -Signer $root -CertStoreLocation 'Cert:\CurrentUser\My'

Export-Certificate -Cert $root -FilePath (Join-Path $here 'fvs-dev-root-ca.cer') | Out-Null
Export-PfxCertificate -Cert $leaf -FilePath (Join-Path $here 'fvs-codesign.pfx') -Password $secure -ChainOption BuildChain | Out-Null
Set-Content -Path (Join-Path $here 'fvs-codesign.password.txt') -Value $password -NoNewline -Encoding ascii

Remove-Item "Cert:\CurrentUser\My\$($leaf.Thumbprint)" -DeleteKey
Remove-Item "Cert:\CurrentUser\My\$($root.Thumbprint)" -DeleteKey
Remove-Item (Join-Path $here 'fvs-dev-root-ca.pem'), (Join-Path $here 'fvs-dev-root-ca.key.pem'), (Join-Path $here 'fvs-codesign.pem') -ErrorAction SilentlyContinue

Write-Host "Created. Next: run ssl-certificate\install-dev-root.cmd once, then build.cmd."
