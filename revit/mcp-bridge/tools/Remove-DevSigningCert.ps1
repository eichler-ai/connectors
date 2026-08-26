# One-off cleanup: removes any existing MCPBridge dev signing cert from LocalMachine\My, \Root, and
# \TrustedPublisher, so New-DevSigningCert.ps1 can be re-run cleanly (e.g. after fixing the subject string).
Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object { $_.Subject -like "*MCPBridge*" } | Remove-Item
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like "*MCPBridge*" } | Remove-Item
Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Subject -like "*MCPBridge*" } | Remove-Item
Write-Output "Cleanup complete."
