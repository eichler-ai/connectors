# One-off cleanup: removes any existing MCPBridge dev signing cert from LocalMachine\My, \Root, and
# \TrustedPublisher, so New-DevSigningCert.ps1 can be re-run cleanly (e.g. after fixing the subject string).
#
# Independent PR review findings applied here: matches "*MCPBridge Dev Signing*" rather than a bare
# "*MCPBridge*", narrowing (though not eliminating) the already-low risk of matching some unrelated,
# legitimately-installed certificate that happens to mention this project's name for other reasons.
# NOTE: this does NOT delete the private key material backing the \My cert -- the -DeleteKey switch on
# Remove-Item's certificate provider (present in some PowerShell/OS combinations) isn't available in this
# environment, and CNG/CAPI key-container cleanup is a bigger rabbit hole than a one-off dev cleanup
# script needs right now. This leaves a small, harmless orphaned key on disk per removal; not worth
# chasing unless it actually becomes a problem in practice.
Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object { $_.Subject -like "*MCPBridge Dev Signing*" } | Remove-Item
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like "*MCPBridge Dev Signing*" } | Remove-Item
Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Subject -like "*MCPBridge Dev Signing*" } | Remove-Item
Write-Output "Cleanup complete."
