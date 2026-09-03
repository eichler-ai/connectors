# Pester (v5) tests for install.ps1's pure parts: the release manifest, per-component change
# detection and the broker stage-and-swap (howto-seed-plan.md §1, step 5). Runs anywhere pwsh runs
# (CI's ubuntu job, a Mac with PowerShell, the VM), because these functions touch only the paths
# they are given. The deploy loop, Revit detection and the scheduled-task watcher stay live-only.
#
#   pwsh -NoProfile -Command "Invoke-Pester -Path revit/install.tests.ps1 -Output Detailed"

BeforeAll {
    . (Join-Path $PSScriptRoot 'install.ps1') -LoadFunctionsOnly

    function New-Payload([string]$Dir, [hashtable]$Files) {
        New-Item -ItemType Directory -Force -Path $Dir | Out-Null
        foreach ($name in $Files.Keys) {
            $path = Join-Path $Dir $name
            New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
            [IO.File]::WriteAllText($path, [string]$Files[$name])
        }
    }
}

Describe 'Get-DirectoryContentHash' {
    It 'is stable across file order and timestamps, and changes with content or name' {
        $a = Join-Path $TestDrive 'a'
        $b = Join-Path $TestDrive 'b'
        New-Payload $a @{ 'MCPBridge.AddIn.dll' = 'dll-bytes'; 'sub/x.xml' = '<x/>' }
        New-Payload $b @{ 'sub/x.xml' = '<x/>'; 'MCPBridge.AddIn.dll' = 'dll-bytes' }
        (Get-ChildItem $b -Recurse -File) | ForEach-Object { $_.LastWriteTime = (Get-Date).AddDays(-30) }
        $ha = Get-DirectoryContentHash $a
        $hb = Get-DirectoryContentHash $b
        $ha | Should -Be $hb
        $ha | Should -Match '^[0-9a-f]{64}$'

        Set-Content (Join-Path $a 'MCPBridge.AddIn.dll') 'other-bytes'
        Get-DirectoryContentHash $a | Should -Not -Be $hb

        Rename-Item (Join-Path $b 'sub/x.xml') 'y.xml'
        Get-DirectoryContentHash $b | Should -Not -Be $hb
    }
}

Describe 'New-PackageManifest' {
    It 'hashes every addin-*/ and server/ component and carries the corpus version' {
        $stage = Join-Path $TestDrive 'stage'
        New-Payload (Join-Path $stage 'addin-2027') @{ 'MCPBridge.AddIn.dll' = '2027' }
        New-Payload (Join-Path $stage 'addin-2025') @{ 'MCPBridge.AddIn.dll' = '2025' }
        New-Payload (Join-Path $stage 'server') @{ 'mcp-server.exe' = 'exe' }
        New-Payload (Join-Path $stage 'unrelated') @{ 'readme.txt' = 'no' }
        $corpus = [ordered]@{ documents = 23; hash = 'abc'; verified_on = @('2025', '2027') }
        $m = New-PackageManifest $stage 'v1.2.3' $corpus
        $m.version | Should -Be 'v1.2.3'
        $m.schema_version | Should -Be 1
        @($m.components.Keys) | Should -Be @('addin-2025', 'addin-2027', 'server')
        $m.components['server'].sha256 | Should -Be (Get-DirectoryContentHash (Join-Path $stage 'server'))
        $m.howto_corpus.hash | Should -Be 'abc'

        # Round-trips through JSON into the shape Read-PackageManifest returns.
        $m | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json')
        $read = Read-PackageManifest $stage
        Get-ManifestComponentHash $read 'addin-2027' | Should -Be $m.components['addin-2027'].sha256
        Get-ManifestComponentHash $read 'addin-2026' | Should -BeNullOrEmpty
        $read.howto_corpus.documents | Should -Be 23
    }

    It 'returns null for a package without a manifest' {
        Read-PackageManifest $TestDrive | Should -BeNullOrEmpty
        Get-ManifestComponentHash $null 'server' | Should -BeNullOrEmpty
    }
}

Describe 'Test-ComponentUnchanged' {
    BeforeAll {
        $script:manifest = [pscustomobject]@{ components = [pscustomobject]@{ server = [pscustomobject]@{ sha256 = 'aaa' } } }
        $script:markerSame = [pscustomobject]@{ components = [pscustomobject]@{ server = 'aaa' } }
        $script:markerOther = [pscustomobject]@{ components = [pscustomobject]@{ server = 'bbb' } }
    }
    It 'skips only when the package hash, the installed hash and the files on disk all agree' {
        Test-ComponentUnchanged $manifest $markerSame 'server' $true | Should -BeTrue
    }
    It 'deploys when the hash changed' {
        Test-ComponentUnchanged $manifest $markerOther 'server' $true | Should -BeFalse
    }
    It 'repairs when the files are missing even though the hash matches' {
        Test-ComponentUnchanged $manifest $markerSame 'server' $false | Should -BeFalse
    }
    It 'deploys when the package has no manifest (the old behaviour) or the marker predates components' {
        Test-ComponentUnchanged $null $markerSame 'server' $true | Should -BeFalse
        Test-ComponentUnchanged $manifest ([pscustomobject]@{ version = 'v0' }) 'server' $true | Should -BeFalse
        Test-ComponentUnchanged $manifest $null 'server' $true | Should -BeFalse
    }
}

Describe 'Install-BrokerStaged' {
    BeforeEach {
        $script:payload = Join-Path $TestDrive "payload-$([guid]::NewGuid())"
        $script:app = Join-Path $TestDrive "app-$([guid]::NewGuid())"
        New-Payload $payload @{ 'mcp-server.exe' = 'new-exe' }
    }
    It 'places the exe on a first install' {
        Install-BrokerStaged $payload $app | Should -Be 'swapped'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'replaces an installed exe that nothing is running and leaves no .old behind' {
        New-Payload $app @{ 'mcp-server.exe' = 'old-exe' }
        Install-BrokerStaged $payload $app | Should -Be 'swapped'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'reports staged, keeping the running image beside the new exe, when a broker from this install is running' {
        New-Payload $app @{ 'mcp-server.exe' = 'old-exe' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 4242 }) }
        Install-BrokerStaged $payload $app | Should -Be 'staged'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Get-Content (Join-Path $app 'mcp-server.exe.old') -Raw | Should -Be 'old-exe'
    }
    It 'reports pending and keeps the .new when the first move is refused' {
        New-Payload $app @{ 'mcp-server.exe' = 'old-exe' }
        Mock Move-Item { throw 'locked' } -ParameterFilter { $Path -like '*mcp-server.exe' -and $Destination -like '*.old' }
        Install-BrokerStaged $payload $app | Should -Be 'pending'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'old-exe'
        Get-Content (Join-Path $app 'mcp-server.exe.new') -Raw | Should -Be 'new-exe'
    }
    It 'Complete-PendingBrokerSwap recovers the moved-aside state (no exe, .old and .new present)' {
        New-Payload $app @{ 'mcp-server.exe.old' = 'old-exe'; 'mcp-server.exe.new' = 'new-exe' }
        Complete-PendingBrokerSwap $app | Should -BeTrue
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'Complete-PendingBrokerSwap finishes a pending swap only when no broker is running' {
        New-Payload $app @{ 'mcp-server.exe' = 'old-exe'; 'mcp-server.exe.new' = 'new-exe' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 1 }) }
        Complete-PendingBrokerSwap $app | Should -BeFalse
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'old-exe'
        Mock Get-BrokerProcess { @() }
        Complete-PendingBrokerSwap $app | Should -BeTrue
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'is a no-op when nothing is pending, but still removes a stale .old image' {
        New-Payload $app @{ 'mcp-server.exe' = 'exe'; 'mcp-server.exe.old' = 'stale' }
        Complete-PendingBrokerSwap $app | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'exe'
    }
}

Describe 'Add-DesktopMcpServer / Remove-DesktopMcpServer' {
    BeforeEach {
        $script:cfgDir = Join-Path $TestDrive "claude-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
        $script:cfg = Join-Path $cfgDir 'claude_desktop_config.json'
    }
    It 'creates the config and adds a stdio server when none exists' {
        Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.revit.type | Should -Be 'stdio'
        $j.mcpServers.revit.command | Should -Be 'C:\x\mcp-server.exe'
        @($j.mcpServers.revit.args) | Should -Be @('--mode', 'local')
    }
    It 'merges without disturbing another server or a top-level key' {
        @{ theme = 'dark'; mcpServers = @{ other = @{ command = 'other.exe' } } } | ConvertTo-Json -Depth 5 | Set-Content $cfg
        Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.other.command | Should -Be 'other.exe'
        $j.mcpServers.revit.command | Should -Be 'C:\x\mcp-server.exe'
        $j.theme | Should -Be 'dark'
    }
    It 'backs up an existing config and writes UTF-8 with no BOM' {
        '{"mcpServers":{}}' | Set-Content $cfg
        Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        Test-Path "$cfg.mcpbridge.bak" | Should -BeTrue
        $bytes = [System.IO.File]::ReadAllBytes($cfg)
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -BeFalse
    }
    It 'is a no-op (returns false) when Claude Desktop is not installed (config dir absent)' {
        $absent = Join-Path $TestDrive 'no-such-dir\claude_desktop_config.json'
        Add-DesktopMcpServer $absent 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeFalse
        Test-Path $absent | Should -BeFalse
    }
    It 'Remove takes only our entry, leaving other servers intact' {
        @{ mcpServers = @{ other = @{ command = 'other.exe' }; revit = @{ command = 'r.exe' } } } | ConvertTo-Json -Depth 5 | Set-Content $cfg
        Remove-DesktopMcpServer $cfg 'revit' | Should -BeTrue
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.PSObject.Properties['revit'] | Should -BeNullOrEmpty
        $j.mcpServers.other.command | Should -Be 'other.exe'
    }
    It 'Remove is a no-op when the file, or the entry, is absent' {
        Remove-DesktopMcpServer $cfg 'revit' | Should -BeFalse
        '{"mcpServers":{}}' | Set-Content $cfg
        Remove-DesktopMcpServer $cfg 'revit' | Should -BeFalse
    }
}
