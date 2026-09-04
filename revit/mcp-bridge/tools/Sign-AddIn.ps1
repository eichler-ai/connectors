# Signs MCPBridge.AddIn.dll -- and MCPBridge.Shim.dll, the manifest-named assembly Revit's trust prompt
# actually keys on (self-update-architecture.md §4.6) -- with the local dev signing cert (see
# New-DevSigningCert.ps1) so Revit's unverified-publisher "Load Once / Always Load / Do Not Load" prompt
# stops appearing on every rebuild. One cert for both, so the shim inherits the add-in's publisher trust.
# Dev-only: the cert is trusted solely on this machine. See New-DevSigningCert.ps1's own doc comment for
# why this is not the PRD §12 production signing plan.
#
# Exit codes: 0 = signed successfully, OR no cert configured (an intentionally supported unsigned-build
# mode, not an error -- see MCPBridge.AddIn.csproj's MCPBridgeSignDevBuild target, which does NOT
# ContinueOnError, so this distinction is what keeps "cert not set up on this machine" from failing
# everyone else's build while still failing loudly on a genuine signing bug. Non-zero = a real failure
# while a cert WAS found and signing was actually attempted (locked file, bad key, network-only timestamp
# failure with no working server, etc.) -- these should fail the build, not be silently warned away.

param(
    [Parameter(Mandatory = $true)]
    [string]$DllPath
)

$ErrorActionPreference = "Stop"

$subject = "CN=MCPBridge Dev Signing (local machine only)"

# Independent PR review finding: no expiry filter here meant a cert past NotAfter would still be picked up
# and fail signing with a confusing certificate-validity error instead of the clear "no cert configured"
# message this same finding's exit-code split is meant to produce.
$cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if (-not $cert) {
    Write-Output "No (unexpired) dev signing cert found (subject: $subject) -- building unsigned. Run New-DevSigningCert.ps1 to set one up on this machine."
    exit 0
}

if (-not (Test-Path $DllPath)) {
    throw "File not found: $DllPath"
}

# Independent PR review finding: skip re-signing (and the network round trip to a public timestamp
# server) on a genuine incremental no-op build, where MSBuild's own Build target ran but didn't actually
# recompile -- AfterTargets="Build" fires unconditionally regardless of whether Build's own inner compile
# step did anything, and this target has no Inputs/Outputs of its own to short-circuit that. Checking the
# file's OWN current signature, rather than an MSBuild timestamp, is the correct test: a real recompile
# always emits fresh, unsigned bytes (the compiler has no way to preserve a prior signature), so this
# check only ever skips when nothing on disk actually changed.
$currentSignature = Get-AuthenticodeSignature -FilePath $DllPath
if ($currentSignature.Status -eq "Valid" -and $currentSignature.SignerCertificate.Thumbprint -eq $cert.Thumbprint) {
    Write-Output "$DllPath already validly signed with the current dev cert -- skipping (no recompile happened)."
    exit 0
}

# Independent PR review finding: the previous version of this loop caught every exception from
# Set-AuthenticodeSignature -- including a locked file, a bad/inaccessible key, or any other real signing
# failure -- and mislabeled all of them "Timestamp server $server failed", which would have hidden the
# actual cause of a genuine bug behind a misleading message pointing at the wrong subsystem. This version
# keeps the same per-server retry (a real network hiccup against one public timestamp server shouldn't
# fail the whole build if the other one works) but reports and finally throws the REAL underlying error,
# not an assumption about which part failed.
$timestampServers = @("http://timestamp.digicert.com", "http://timestamp.sectigo.com")
$lastError = $null
$signed = $false

foreach ($server in $timestampServers) {
    try {
        Set-AuthenticodeSignature -FilePath $DllPath -Certificate $cert -TimestampServer $server -HashAlgorithm SHA256 -ErrorAction Stop | Out-Null
    } catch {
        Write-Output "Set-AuthenticodeSignature threw while signing via $server`: $($_.Exception.Message)"
        $lastError = $_
        continue
    }

    # Set-AuthenticodeSignature embeds the signature before requesting the timestamp countersignature, so
    # a timestamp-only failure can leave the file signed-but-untimestamped even though the cmdlet's own
    # returned Status already reflects that failure -- re-check via Get-AuthenticodeSignature rather than
    # trusting the prior call's return value alone, so "signed" here always means genuinely, verifiably
    # signed AND timestamped, not just "the cmdlet didn't throw."
    $verify = Get-AuthenticodeSignature -FilePath $DllPath
    if ($verify.Status -eq "Valid") {
        Write-Output "Signed $DllPath (timestamped via $server): Valid"
        $signed = $true
        break
    }

    Write-Output "Signature status after attempting $server`: $($verify.Status) -- $($verify.StatusMessage)"
    $lastError = $verify.StatusMessage
}

if (-not $signed) {
    if ($lastError) {
        throw "Failed to sign $DllPath with a valid timestamp against any configured timestamp server. Last error: $lastError"
    }
    throw "Failed to sign $DllPath with a valid timestamp against any configured timestamp server."
}
