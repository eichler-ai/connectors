# Signs MCPBridge.AddIn.dll with the local dev signing cert (see New-DevSigningCert.ps1) so Revit's
# unverified-publisher "Load Once / Always Load / Do Not Load" prompt stops appearing on every rebuild.
# Dev-only: the cert is trusted solely on this machine. See New-DevSigningCert.ps1's own doc comment for
# why this is not the PRD §12 production signing plan.

param(
    [Parameter(Mandatory = $true)]
    [string]$DllPath
)

$ErrorActionPreference = "Stop"

$subject = "CN=MCPBridge Dev Signing (local machine only)"
$cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1

if (-not $cert) {
    throw "No dev signing cert found (subject: $subject). Run New-DevSigningCert.ps1 first."
}

if (-not (Test-Path $DllPath)) {
    throw "File not found: $DllPath"
}

# Timestamped so the signature stays valid after the cert itself expires (PRD §12's own requirement,
# applies here too even though this is a dev-only cert).
$timestampServers = @("http://timestamp.digicert.com", "http://timestamp.sectigo.com")
$signed = $false
foreach ($server in $timestampServers) {
    try {
        $result = Set-AuthenticodeSignature -FilePath $DllPath -Certificate $cert -TimestampServer $server -HashAlgorithm SHA256
        if ($result.Status -eq "Valid") {
            Write-Output "Signed $DllPath (timestamped via $server): $($result.Status)"
            $signed = $true
            break
        } else {
            Write-Output "Signing via $server returned status $($result.Status): $($result.StatusMessage)"
        }
    } catch {
        Write-Output "Timestamp server $server failed: $($_.Exception.Message)"
    }
}

if (-not $signed) {
    throw "Failed to sign $DllPath with a valid timestamp against any configured timestamp server."
}
