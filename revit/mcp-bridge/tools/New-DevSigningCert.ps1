# Generates (or reuses) a self-signed code-signing certificate for local dev use, and trusts it on this
# machine so Windows/Revit treat DLLs signed with it as coming from a verified publisher -- eliminating the
# "unverified publisher, Load Once / Always Load / Do Not Load" prompt during iterative add-in rebuilds.
#
# NOT the PRD §12 production signing plan: that needs a CA-issued, publicly-trusted, timestamped certificate
# for a real signed-installer distribution to end users, whose trust doesn't depend on being manually
# imported onto each machine first. This cert is trusted ONLY on the machine it's imported into -- exactly
# right for one dev VM, useless (and not intended) for distribution.
#
# SECURITY NOTE (independent PR review): this installs a self-signed cert into LocalMachine\Root, a
# machine-wide trust anchor. Root is genuinely necessary here -- TrustedPublisher alone can't satisfy
# Authenticode's chain-validation step for a self-signed leaf (still fails CERT_E_UNTRUSTEDROOT) -- but the
# blast radius is narrower than "any certificate authority": this cert has a code-signing-only EKU (no CA
# basic-constraint), so it can validate other LEAF code-signing certs signed by this same key, not act as
# an actual issuing CA for arbitrary new certs. -KeyExportPolicy NonExportable below keeps that key from
# being copied off this machine even by another local admin process. Still a real, if narrow, trust
# expansion -- don't run this on a machine you don't otherwise fully trust already.

$ErrorActionPreference = "Stop"

# Deliberately no commas: .NET's X509Certificate2.Subject re-quotes CN values containing a comma (the DN
# RDN separator) when round-tripped through ToString(), which broke an exact-match comparison against this
# same literal in an earlier version of this pair of scripts -- keep it simple rather than working around
# .NET's quoting rules in every place that reads this subject back. Parentheses are NOT DN-special and are
# fine.
$subject = "CN=MCPBridge Dev Signing (local machine only)"

# Independent PR review finding: no expiry filter here meant a re-run after the cert had expired would
# "successfully reuse" an already-unusable cert instead of minting a fresh one.
$existing = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if ($existing) {
    Write-Output "Reusing existing dev signing cert: $($existing.Thumbprint) (expires $($existing.NotAfter))"
    $cert = $existing
} else {
    $cert = New-SelfSignedCertificate `
        -Subject $subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyExportPolicy NonExportable `
        -CertStoreLocation Cert:\LocalMachine\My `
        -NotAfter (Get-Date).AddYears(1)
    Write-Output "Created new dev signing cert: $($cert.Thumbprint) (expires $($cert.NotAfter))"
}

# Trust it locally: Root makes the (self-signed) chain valid at all; TrustedPublisher is the store Windows
# actually checks to decide "is this publisher trusted to run code without prompting," which Root alone
# does not satisfy for Authenticode/SmartScreen-style checks.
$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
$rootStore.Open("ReadWrite")
if (-not ($rootStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    $rootStore.Add($cert)
    Write-Output "Added to LocalMachine\Root"
}
$rootStore.Close()

$pubStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", "LocalMachine")
$pubStore.Open("ReadWrite")
if (-not ($pubStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    $pubStore.Add($cert)
    Write-Output "Added to LocalMachine\TrustedPublisher"
}
$pubStore.Close()

Write-Output "Thumbprint: $($cert.Thumbprint)"
