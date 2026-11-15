param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
    $thumbprint = [string]$configuration.certificateThumbprint
    $storeLocation = [string]$configuration.certificateStoreLocation
    if ($thumbprint -notmatch '^[A-F0-9]{40}$' -or $storeLocation -notin @('CurrentUser', 'LocalMachine')) {
        throw 'Invalid signing configuration.'
    }
    $FilePath = (Resolve-Path -LiteralPath $FilePath).Path
    $signToolPath = (Resolve-Path -LiteralPath $configuration.signToolPath).Path
    if (-not $VerifyOnly) {
        $certificate = Get-Item -LiteralPath "Cert:\$storeLocation\My\$thumbprint"
        if (-not $certificate.HasPrivateKey -or $certificate.Subject -eq $certificate.Issuer -or
            $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
            throw 'A current CA-issued code-signing certificate with an accessible private key is required. Self-signed certificates are not supported.'
        }
        $signArguments = @('sign', '/fd', 'SHA256', '/tr', [string]$configuration.timestampUrl, '/td', 'SHA256',
            '/s', 'My', '/sha1', $thumbprint, '/d', 'ClipDock')
        if ($storeLocation -eq 'LocalMachine') { $signArguments += '/sm' }
        & $signToolPath @signArguments $FilePath
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $FilePath (exit $LASTEXITCODE)." }
    }

    & $signToolPath verify /pa /all /tw $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $FilePath (exit $LASTEXITCODE)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw "A valid timestamped signature from the configured publisher is required: $FilePath."
    }
    if ($signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer) {
        throw 'Self-signed publisher certificates are not allowed.'
    }
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $trustedRoots = [System.Security.Cryptography.X509Certificates.X509Store]::new('AuthRoot', 'LocalMachine')
    try {
        $chain.ChainPolicy.RevocationMode = 'Online'
        $chain.ChainPolicy.RevocationFlag = 'ExcludeRoot'
        $null = $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        if (-not $chain.Build($signature.SignerCertificate)) {
            throw 'The publisher certificate does not have a valid, revocation-checked code-signing chain.'
        }
        $rootCertificate = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        $trustedRoots.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $trustedRoots.Certificates.Find('FindByThumbprint', $rootCertificate.Thumbprint, $false)
        if ($matches.Count -eq 0) {
            throw 'The signing chain does not terminate in the Windows third-party AuthRoot store. Verify the public CA before building a release; private trust roots are not supported.'
        }
    }
    finally { $trustedRoots.Close(); $chain.Dispose() }
    if (-not $VerifyOnly -and [System.IO.Path]::GetFileName($FilePath) -match '^uninst\.e(32|64)\.tmp$') {
        New-Item -ItemType Directory -Path $configuration.evidenceDirectory -Force | Out-Null
        $hash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-Item -LiteralPath $FilePath -Destination (Join-Path $configuration.evidenceDirectory "uninstaller-$hash.exe")
    }
    Write-Output "Verified: $FilePath"
}
catch {
    Write-Error $_
    exit 1
}
