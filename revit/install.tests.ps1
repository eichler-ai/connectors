# Pester (v5) tests for install.ps1's pure parts: the release manifest, per-component change
# detection and the broker stage-and-swap (howto-seed-plan.md §1, step 5). Runs anywhere pwsh runs
# (CI's ubuntu job, a Mac with PowerShell, the VM), because these functions touch only the paths
# they are given. The deploy loop and Revit detection stay live-only.
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
        New-Payload (Join-Path $stage 'shim-2027') @{ 'MCPBridge.Shim.dll' = 'shim'; 'MCPBridge.addin' = '<m/>' }
        New-Payload (Join-Path $stage 'server') @{ 'mcp-server.exe' = 'exe' }
        New-Payload (Join-Path $stage 'unrelated') @{ 'readme.txt' = 'no' }
        $corpus = [ordered]@{ documents = 23; hash = 'abc'; verified_on = @('2025', '2027') }
        $m = New-PackageManifest $stage 'v1.2.3' $corpus
        $m.version | Should -Be 'v1.2.3'
        $m.schema_version | Should -Be 1
        @($m.components.Keys) | Should -Be @('addin-2025', 'addin-2027', 'server', 'shim-2027')
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
    It 'reports staged, not pending, when the exe already is the new image and the old one still runs from a locked .old (issue #192 live test)' {
        New-Payload $app @{ 'mcp-server.exe' = 'new-exe'; 'mcp-server.exe.old' = 'old-exe' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 4242 }) }
        # A locked .old: the move onto it fails (a real Remove-Item on it is non-terminating and silent).
        Mock Move-Item { throw 'locked' } -ParameterFilter { $Destination -like '*.old' }
        Install-BrokerStaged $payload $app | Should -Be 'staged'
        Should -Invoke Move-Item -Times 0 -Exactly
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'reports swapped and clears .old when the exe already is the new image and nothing is running' {
        New-Payload $app @{ 'mcp-server.exe' = 'new-exe'; 'mcp-server.exe.old' = 'old-exe' }
        Install-BrokerStaged $payload $app | Should -Be 'swapped'
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
    }
    It 'stages a DIFFERENT new image even while an older broker still holds .old, parking the current exe under a unique name (v0.1.1 -> v0.1.2 live)' {
        New-Payload $app @{ 'mcp-server.exe' = 'current-exe'; 'mcp-server.exe.old' = 'older-still-running' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 4242 }) }
        # .old is mapped by a running process: deleting it is refused (silently, as the real cmdlet does).
        Mock Remove-Item { } -ParameterFilter { $Path -like '*mcp-server.exe.old' }
        Install-BrokerStaged $payload $app | Should -Be 'staged'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Get-Content (Join-Path $app 'mcp-server.exe.old') -Raw | Should -Be 'older-still-running'
        $parked = Get-ChildItem $app -Filter 'mcp-server.exe.old-*'
        $parked.Count | Should -Be 1
        Get-Content $parked[0].FullName -Raw | Should -Be 'current-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'Remove-StaleBrokerImages sweeps every parked image (.old and .old-xxxxxxxx) and nothing else' {
        New-Payload $app @{ 'mcp-server.exe' = 'exe'; 'mcp-server.exe.old' = 'a'; 'mcp-server.exe.old-1a2b3c4d' = 'b'; 'mcp-server.exe.new' = 'keep' }
        Remove-StaleBrokerImages $app
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.old-1a2b3c4d') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeTrue
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'exe'
    }
    It 'Remove-StaleBrokerImages never throws when an image is still held' {
        New-Payload $app @{ 'mcp-server.exe.old' = 'held' }
        Mock Remove-Item { throw 'The process cannot access the file' }
        { Remove-StaleBrokerImages $app } | Should -Not -Throw
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeTrue
    }
    It 'Complete-PendingServerMarker moves the deferred server hash from the sidecar into the marker and removes the sidecar' {
        New-Payload $app @{ 'mcp-server.exe.new.sha256' = 'newhash' }
        $markerPath = Join-Path $app 'installed-version.json'
        @{ version = 'v0.1.2'; components = @{ server = 'oldhash'; 'addin-2027' = 'a27' } } | ConvertTo-Json | Set-Content $markerPath
        Complete-PendingServerMarker $app $markerPath
        $m = Get-Content $markerPath -Raw | ConvertFrom-Json
        $m.components.server | Should -Be 'newhash'
        $m.components.'addin-2027' | Should -Be 'a27'
        $m.version | Should -Be 'v0.1.2'
        Test-Path (Join-Path $app 'mcp-server.exe.new.sha256') | Should -BeFalse
    }
    It 'Complete-PendingServerMarker is a no-op without a sidecar and never throws on a bad marker' {
        $markerPath = Join-Path $app 'installed-version.json'
        New-Payload $app @{ 'installed-version.json' = '{ "version": "v1", "components": { "server": "keep" } }' }
        Complete-PendingServerMarker $app $markerPath
        (Get-Content $markerPath -Raw | ConvertFrom-Json).components.server | Should -Be 'keep'
        New-Payload $app @{ 'mcp-server.exe.new.sha256' = 'x'; 'installed-version.json' = '{not json' }
        { Complete-PendingServerMarker $app $markerPath } | Should -Not -Throw
    }
    It 'Complete-PendingBrokerSwap recovers the moved-aside state (no exe, .old and .new present)' {
        New-Payload $app @{ 'mcp-server.exe.old' = 'old-exe'; 'mcp-server.exe.new' = 'new-exe' }
        Complete-PendingBrokerSwap $app | Should -BeTrue
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse
    }
    It 'Complete-PendingBrokerSwap stages a pending .new even while a broker runs, and swaps outright when none does' {
        New-Payload $app @{ 'mcp-server.exe' = 'old-exe'; 'mcp-server.exe.new' = 'new-exe' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 1 }) }
        Complete-PendingBrokerSwap $app | Should -Be 'staged'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'new-exe'
        Get-Content (Join-Path $app 'mcp-server.exe.old') -Raw | Should -Be 'old-exe'   # the running image, parked
        Test-Path (Join-Path $app 'mcp-server.exe.new') | Should -BeFalse

        New-Payload $app @{ 'mcp-server.exe.new' = 'newer-exe' }
        Mock Get-BrokerProcess { @() }
        Complete-PendingBrokerSwap $app | Should -Be 'swapped'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'newer-exe'
        Get-ChildItem $app -Filter 'mcp-server.exe.old*' | Should -BeNullOrEmpty
    }
    It 'Complete-PendingBrokerSwap parks the current exe under a unique name when .old is still held' {
        New-Payload $app @{ 'mcp-server.exe' = 'current'; 'mcp-server.exe.old' = 'held'; 'mcp-server.exe.new' = 'newest' }
        Mock Get-BrokerProcess { @([pscustomobject]@{ Id = 1 }) }
        Mock Remove-Item { } -ParameterFilter { $Path -like '*mcp-server.exe.old' }
        Complete-PendingBrokerSwap $app | Should -Be 'staged'
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'newest'
        (Get-ChildItem $app -Filter 'mcp-server.exe.old-*').Count | Should -Be 1
    }
    It 'is a no-op when nothing is pending, but still removes a stale .old image' {
        New-Payload $app @{ 'mcp-server.exe' = 'exe'; 'mcp-server.exe.old' = 'stale' }
        Complete-PendingBrokerSwap $app | Should -BeFalse
        Test-Path (Join-Path $app 'mcp-server.exe.old') | Should -BeFalse
        Get-Content (Join-Path $app 'mcp-server.exe') -Raw | Should -Be 'exe'
    }
}

Describe 'Versioned add-in layout (self-update-architecture.md §4): pointer, version folders, shim' {
    BeforeEach {
        $script:app = Join-Path $TestDrive "app-$([guid]::NewGuid())"
        $script:addins = Join-Path $TestDrive "addins-$([guid]::NewGuid())"
        $script:payload = Join-Path $TestDrive "payload-$([guid]::NewGuid())"
        $script:shim = Join-Path $TestDrive "shim-$([guid]::NewGuid())"
        New-Payload $payload @{ 'MCPBridge.AddIn.dll' = 'addin-v2'; 'MCPBridge.addin' = '<addin/>'; 'Microsoft.CodeAnalysis.dll' = 'roslyn'; 'de/Microsoft.CodeAnalysis.resources.dll' = 'de' }
        New-Payload $shim @{ 'MCPBridge.Shim.dll' = 'shim-bytes'; 'MCPBridge.addin' = '<shim/>' }
    }

    It 'Write-AddinPointer writes {version} atomically, without a BOM, and Read-AddinPointer reads it back' {
        Write-AddinPointer $app 'v0.1.5'
        $path = Get-AddinPointerPath $app
        $path | Should -Be (Join-Path (Join-Path $app 'addin') 'current.json')
        (Read-AddinPointer $app).version | Should -Be 'v0.1.5'
        (Read-AddinPointer $app).PSObject.Properties['previous'] | Should -BeNullOrEmpty
        $bytes = [IO.File]::ReadAllBytes($path)
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -BeFalse
        # No temp file survives the rename.
        Get-ChildItem (Split-Path $path) -Filter 'current.json.tmp-*' | Should -BeNullOrEmpty
    }
    It 'Write-AddinPointer remembers the replaced version as previous, and keeps it across a same-version rewrite' {
        Write-AddinPointer $app 'v0.1.4'
        Write-AddinPointer $app 'v0.1.5'
        $p = Read-AddinPointer $app
        $p.version | Should -Be 'v0.1.5'
        $p.previous | Should -Be 'v0.1.4'
        Write-AddinPointer $app 'v0.1.5'
        (Read-AddinPointer $app).previous | Should -Be 'v0.1.4'
    }
    It 'Read-AddinPointer tolerates a UTF-8 BOM (Windows PowerShell Out-File -Encoding utf8) and returns null for junk or no version' {
        $path = Get-AddinPointerPath $app
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        [IO.File]::WriteAllText($path, '{"version":"v0.1.9"}', (New-Object System.Text.UTF8Encoding($true)))
        (Read-AddinPointer $app).version | Should -Be 'v0.1.9'
        [IO.File]::WriteAllText($path, '{not json')
        Read-AddinPointer $app | Should -BeNullOrEmpty
        [IO.File]::WriteAllText($path, '{"other":1}')
        Read-AddinPointer $app | Should -BeNullOrEmpty
        Read-AddinPointer (Join-Path $TestDrive 'no-such-app') | Should -BeNullOrEmpty
    }

    It 'Install-AddinVersionPayload lays the payload down verbatim under addin\<version>\<year>, touching nothing else' {
        Install-AddinVersionPayload $payload $app 'v0.1.5' '2027'
        $dir = Get-AddinVersionDir $app 'v0.1.5' '2027'
        Get-Content (Join-Path $dir 'MCPBridge.AddIn.dll') -Raw | Should -Be 'addin-v2'
        Test-Path (Join-Path $dir 'de/Microsoft.CodeAnalysis.resources.dll') | Should -BeTrue
        Test-Path $addins | Should -BeFalse
        Read-AddinPointer $app | Should -BeNullOrEmpty
    }

    It 'Install-AddinShim places the shim + manifest, reports unchanged for the same bytes, and held when a copy is refused' {
        Install-AddinShim $shim $addins | Should -Be 'placed'
        Test-ShimAddinInstalled $addins | Should -BeTrue
        Get-Content (Join-Path $addins 'MCPBridge.addin') -Raw | Should -Be '<shim/>'
        Install-AddinShim $shim $addins | Should -Be 'unchanged'
        Set-Content (Join-Path $shim 'MCPBridge.Shim.dll') 'shim-v2' -NoNewline
        Mock Copy-Item { throw 'The process cannot access the file' } -ParameterFilter { $Destination -like '*MCPBridge.Shim.dll' }
        Install-AddinShim $shim $addins | Should -Be 'held'
        Get-Content (Join-Path $addins 'MCPBridge.Shim.dll') -Raw | Should -Be 'shim-bytes'
    }

    It 'Test-VersionedAddinInstalled needs the shim, the pointer AND the pointed folder for this year' {
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeFalse
        Install-AddinVersionPayload $payload $app 'v0.1.5' '2027'
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeFalse   # no shim, no pointer
        Install-AddinShim $shim $addins | Out-Null
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeFalse   # no pointer
        Write-AddinPointer $app 'v0.1.5'
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeTrue
        Test-VersionedAddinInstalled $app $addins '2025' | Should -BeFalse   # no 2025 folder under v0.1.5
        Write-AddinPointer $app 'v0.1.6'
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeFalse   # pointer moved on, folder missing
    }

    It 'Remove-OwnedAddinFiles (uninstall) takes the shim + manifest and the folder they leave empty' {
        Install-AddinShim $shim $addins | Out-Null
        Remove-OwnedAddinFiles $addins
        Test-Path $addins | Should -BeFalse
    }
    It 'Remove-OwnedAddinFiles leaves a foreign file, and the folder holding it, alone' {
        Install-AddinShim $shim $addins | Out-Null
        New-Payload $addins @{ 'ThirdParty.dll' = 'theirs' }
        Remove-OwnedAddinFiles $addins
        Test-ShimAddinInstalled $addins | Should -BeFalse
        Test-Path (Join-Path $addins 'MCPBridge.Shim.dll') | Should -BeFalse
        Get-Content (Join-Path $addins 'ThirdParty.dll') -Raw | Should -Be 'theirs'
    }
    It 'Remove-OwnedAddinFiles is a no-op on a folder without our manifest' {
        New-Payload $addins @{ 'ThirdParty.dll' = 'theirs'; 'Other.addin' = '<o/>' }
        Remove-OwnedAddinFiles $addins
        @(Get-ChildItem $addins).Count | Should -Be 2
    }
    It 'Remove-OwnedAddinFiles leaves a held shim DLL in place (a running Revit) so uninstall can report it' {
        Install-AddinShim $shim $addins | Out-Null
        Mock Remove-Item { } -ParameterFilter { "$Path" -like '*MCPBridge.Shim.dll' }
        Remove-OwnedAddinFiles $addins
        Test-Path (Join-Path $addins 'MCPBridge.Shim.dll') | Should -BeTrue
        Test-Path (Join-Path $addins 'MCPBridge.addin') | Should -BeFalse
    }

    It 'Remove-StaleAddinVersions keeps the current and previous versions, deletes the rest, and skips a folder it cannot rename (mapped by a running Revit)' {
        foreach ($v in 'v0.1.2', 'v0.1.3', 'v0.1.4', 'v0.1.5', 'local-20260101000000') {
            New-Payload (Get-AddinVersionDir $app $v '2027') @{ 'MCPBridge.AddIn.dll' = $v }
        }
        New-Payload (Join-Path (Join-Path $app 'addin') 'v0.1.1.stale-deadbeef') @{ 'left.dll' = 'over' }
        Write-AddinPointer $app 'v0.1.4'
        Write-AddinPointer $app 'v0.1.5'
        Mock Move-Item { throw 'in use' } -ParameterFilter { $Path -like '*v0.1.3' }
        Remove-StaleAddinVersions $app
        $left = @(Get-ChildItem (Join-Path $app 'addin') -Directory | ForEach-Object Name | Sort-Object)
        $left | Should -Be @('v0.1.3', 'v0.1.4', 'v0.1.5')
        # The held folder is intact, not half-deleted.
        Get-Content (Join-Path (Get-AddinVersionDir $app 'v0.1.3' '2027') 'MCPBridge.AddIn.dll') -Raw | Should -Be 'v0.1.3'
        Test-Path (Get-AddinPointerPath $app) | Should -BeTrue
    }
    It 'Remove-StaleAddinVersions does nothing without a pointer, and never throws' {
        New-Payload (Get-AddinVersionDir $app 'v0.1.2' '2027') @{ 'MCPBridge.AddIn.dll' = 'x' }
        { Remove-StaleAddinVersions $app } | Should -Not -Throw
        Test-Path (Get-AddinVersionDir $app 'v0.1.2' '2027') | Should -BeTrue
        { Remove-StaleAddinVersions (Join-Path $TestDrive 'no-such-app') } | Should -Not -Throw
    }

    It 'end to end: fresh versioned install, then an update flips the pointer under a running Revit without touching the loaded folder' {
        # Fresh install: payload, pointer, then shim -- the order the shim needs (it reads the pointer).
        Install-AddinVersionPayload $payload $app 'v0.1.5' '2027'
        Write-AddinPointer $app 'v0.1.5'
        Install-AddinShim $shim $addins | Should -Be 'placed'
        Test-VersionedAddinInstalled $app $addins '2027' | Should -BeTrue

        # Update: a new version folder beside the running one, pointer flipped, old folder untouched.
        $payload2 = Join-Path $TestDrive "payload2-$([guid]::NewGuid())"
        New-Payload $payload2 @{ 'MCPBridge.AddIn.dll' = 'addin-v3'; 'MCPBridge.addin' = '<addin/>' }
        Install-AddinVersionPayload $payload2 $app 'v0.1.6' '2027'
        Write-AddinPointer $app 'v0.1.6'
        Install-AddinShim $shim $addins | Should -Be 'unchanged'
        (Read-AddinPointer $app).version | Should -Be 'v0.1.6'
        Get-Content (Join-Path (Get-AddinVersionDir $app 'v0.1.5' '2027') 'MCPBridge.AddIn.dll') -Raw | Should -Be 'addin-v2'
        Get-Content (Join-Path (Get-AddinVersionDir $app 'v0.1.6' '2027') 'MCPBridge.AddIn.dll') -Raw | Should -Be 'addin-v3'
        Remove-StaleAddinVersions $app
        Test-Path (Get-AddinVersionDir $app 'v0.1.5' '2027') | Should -BeTrue   # previous is retained
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
    It 'merges into a config whose mcpServers is null (does not throw)' {
        '{"mcpServers":null}' | Set-Content $cfg
        Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.revit.command | Should -Be 'C:\x\mcp-server.exe'
    }
    It 'backs up only once, preserving the pristine pre-install backup across re-installs' {
        '{"mcpServers":{"orig":true}}' | Set-Content $cfg
        Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        (Get-Content "$cfg.mcpbridge.bak" -Raw) | Should -Match 'orig'
        Add-DesktopMcpServer $cfg 'revit' 'C:\y\mcp-server.exe' @('--mode', 'local') | Should -BeTrue
        # The backup still holds the ORIGINAL config, not the first install's rewrite.
        (Get-Content "$cfg.mcpbridge.bak" -Raw) | Should -Match 'orig'
        (Get-Content "$cfg.mcpbridge.bak" -Raw) | Should -Not -Match 'revit'
    }
    It 'throws on a malformed config so the installer falls back to printed instructions' {
        'this is not json {' | Set-Content $cfg
        { Add-DesktopMcpServer $cfg 'revit' 'C:\x\mcp-server.exe' @('--mode', 'local') } | Should -Throw
    }
}

Describe 'Get-DesktopConfigPath' {
    BeforeEach {
        $script:savedLocal = $env:LOCALAPPDATA
        $script:savedApp = $env:APPDATA
        $script:root = Join-Path $TestDrive "env-$([guid]::NewGuid())"
        $env:LOCALAPPDATA = Join-Path $root 'Local'
        $env:APPDATA = Join-Path $root 'Roaming'
        New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA, $env:APPDATA | Out-Null
    }
    AfterEach {
        $env:LOCALAPPDATA = $savedLocal
        $env:APPDATA = $savedApp
    }
    It 'prefers the MSIX package config when a Claude package is present' {
        $pkg = Join-Path $env:LOCALAPPDATA 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude'
        New-Item -ItemType Directory -Force -Path $pkg | Out-Null
        Get-DesktopConfigPath | Should -Be (Join-Path $pkg 'claude_desktop_config.json')
    }
    It 'falls back to the standard %APPDATA% path when no Claude package exists' {
        Get-DesktopConfigPath | Should -Be (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    }
}

Describe 'Register-McpServer' {
    BeforeEach {
        $script:dir = Join-Path $TestDrive "reg-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $script:cfg = Join-Path $dir 'claude_desktop_config.json'
        $script:fakeExe = Join-Path $dir 'mcp-server.exe'
        New-Item -ItemType File -Force -Path $fakeExe | Out-Null
        # Isolate from the real machine: the Desktop config path is our TestDrive file, and the CLI half
        # is treated as absent (no `claude` on PATH) so no real client is touched.
        Mock Get-DesktopConfigPath { $script:cfg }
        Mock Get-Command { $null } -ParameterFilter { $Name -eq 'claude' }
    }
    It 'registers the Desktop server when none is configured' {
        Register-McpServer $fakeExe 6>$null
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.revit.command | Should -Be $fakeExe
    }
    It 'with -OnlyIfMissing leaves an already-registered Desktop entry untouched' {
        @{ mcpServers = @{ revit = @{ type = 'stdio'; command = 'OLD.exe'; args = @('--mode', 'local') } } } | ConvertTo-Json -Depth 6 | Set-Content $cfg
        Register-McpServer $fakeExe -OnlyIfMissing 6>$null
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.revit.command | Should -Be 'OLD.exe'
    }
    It 'with -OnlyIfMissing adds the Desktop entry when it is absent (repairs lost wiring)' {
        '{"mcpServers":{"other":{"command":"o.exe"}}}' | Set-Content $cfg
        Register-McpServer $fakeExe -OnlyIfMissing 6>$null
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        $j.mcpServers.revit.command | Should -Be $fakeExe
        $j.mcpServers.other.command | Should -Be 'o.exe'
    }
    It 'is a no-op when the server exe is absent' {
        Register-McpServer (Join-Path $dir 'no-such\mcp-server.exe') 6>$null
        Test-Path $cfg | Should -BeFalse
    }
}

Describe 'Register-McpServer -- Claude Code CLI wiring' {
    BeforeEach {
        $script:dir = Join-Path $TestDrive "cli-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $script:fakeExe = Join-Path $dir 'mcp-server.exe'
        New-Item -ItemType File -Force -Path $fakeExe | Out-Null
        # Desktop half is inert here (its own dir is absent -> Add returns $false); the CLI is "present".
        Mock Get-DesktopConfigPath { Join-Path $dir 'no-desktop\claude_desktop_config.json' }
        Mock Get-Command { [pscustomobject]@{ Name = 'claude' } } -ParameterFilter { $Name -eq 'claude' }
        # Default: `add`/`remove` succeed; `list` is overridden per-test to say present/absent.
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @('Added') } } -ParameterFilter { $CliArgs[0] -eq 'add' }
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @() } } -ParameterFilter { $CliArgs[0] -eq 'remove' }
    }
    It 'does NOT call `remove` when revit is absent (the fresh-install crash regression)' {
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @() } } -ParameterFilter { $CliArgs[0] -eq 'list' }
        Register-McpServer $fakeExe 6>$null
        Should -Invoke Invoke-ClaudeMcp -Times 0 -Exactly -ParameterFilter { $CliArgs[0] -eq 'remove' }
        Should -Invoke Invoke-ClaudeMcp -Times 1 -Exactly -ParameterFilter { $CliArgs[0] -eq 'add' }
    }
    It 'removes then re-adds when revit is already present (a normal update)' {
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @('revit: C:\old\mcp-server.exe --mode local') } } -ParameterFilter { $CliArgs[0] -eq 'list' }
        Register-McpServer $fakeExe 6>$null
        Should -Invoke Invoke-ClaudeMcp -Times 1 -Exactly -ParameterFilter { $CliArgs[0] -eq 'remove' }
        Should -Invoke Invoke-ClaudeMcp -Times 1 -Exactly -ParameterFilter { $CliArgs[0] -eq 'add' }
    }
    It 'with -OnlyIfMissing leaves an already-present CLI registration completely untouched' {
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @('revit: C:\x\mcp-server.exe --mode local') } } -ParameterFilter { $CliArgs[0] -eq 'list' }
        Register-McpServer $fakeExe -OnlyIfMissing 6>$null
        Should -Invoke Invoke-ClaudeMcp -Times 0 -Exactly -ParameterFilter { $CliArgs[0] -eq 'remove' }
        Should -Invoke Invoke-ClaudeMcp -Times 0 -Exactly -ParameterFilter { $CliArgs[0] -eq 'add' }
    }
    It 'reports a manual step (does not throw) when `add` fails' {
        Mock Invoke-ClaudeMcp { @{ ExitCode = 0; Output = @() } } -ParameterFilter { $CliArgs[0] -eq 'list' }
        Mock Invoke-ClaudeMcp { @{ ExitCode = 1; Output = @('some CLI error') } } -ParameterFilter { $CliArgs[0] -eq 'add' }
        { Register-McpServer $fakeExe 6>$null } | Should -Not -Throw
    }
}

Describe 'Self-copy source (issue #192): the installed install.ps1 must be the full script, never the irm|iex stub' {
    BeforeAll {
        $script:fullScript = Get-Content (Join-Path $PSScriptRoot 'install.ps1') -Raw
        $script:stub = 'irm https://raw.githubusercontent.com/eichler-ai/connectors/main/revit/install.ps1 | iex'
    }
    It 'recognises the real script and rejects the one-liner stub, empty text, and a truncated script' {
        Test-IsFullInstallerScript $fullScript | Should -BeTrue
        Test-IsFullInstallerScript $stub | Should -BeFalse
        Test-IsFullInstallerScript '' | Should -BeFalse
        Test-IsFullInstallerScript $fullScript.Substring(0, 5000) | Should -BeFalse
    }
    It 'rejects a download truncated PAST both markers (review of #193: markers alone let 73% missing through)' {
        # Both markers live in the first ~6 KB; a cut anywhere after them must still fail, at any length.
        foreach ($len in 20000, 40000, ($fullScript.Length - 200)) {
            Test-IsFullInstallerScript $fullScript.Substring(0, $len) | Should -BeFalse -Because "a $len-byte prefix is not the installer"
        }
    }
    It 'rejects a script with an intact tail but a corrupted middle (the sentinel alone cannot see that)' {
        # Anchored on a code line, not a byte offset: an offset can land inside a comment, where any
        # text is legal and the parser rightly sees nothing wrong.
        $at = $fullScript.IndexOf("`nfunction Copy-SelfIfNeeded(") + 1  # the definition line, not the string literal naming it
        $corrupt = $fullScript.Substring(0, $at) + "{{{`n" + $fullScript.Substring($at)
        Test-IsFullInstallerScript $corrupt | Should -BeFalse
    }
    It 'uses the invocation definition when it is the full script, without touching the network' {
        Mock Invoke-WebRequest { throw 'network must not be used' }
        Get-InstallerSourceForBootstrap $fullScript 'https://example.invalid/install.ps1' | Should -Be $fullScript
        Should -Invoke Invoke-WebRequest -Times 0 -Exactly
    }
    It 'fetches the canonical script when the definition is the piped one-liner (what iex actually yields)' {
        Mock Invoke-WebRequest { [pscustomobject]@{ Content = $fullScript } }
        Get-InstallerSourceForBootstrap $stub 'https://example.invalid/install.ps1' | Should -Be $fullScript
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
    }
    It 'throws rather than bootstrap from a download that is not the installer' {
        Mock Invoke-WebRequest { [pscustomobject]@{ Content = $stub } }
        { Get-InstallerSourceForBootstrap $stub 'https://example.invalid/install.ps1' } | Should -Throw '*did not look like the installer*'
    }
    It 'Copy-SelfIfNeeded refuses to install a stub as the self-copy and leaves the destination untouched' {
        $src = Join-Path $TestDrive 'stub.ps1'; $dst = Join-Path $TestDrive 'app\install.ps1'
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Set-Content $src $stub
        { Copy-SelfIfNeeded $src $dst } | Should -Throw '*not the full install.ps1*'
        Test-Path $dst | Should -BeFalse
    }
    It 'Copy-SelfIfNeeded copies the full script' {
        $src = Join-Path $TestDrive 'full.ps1'; $dst = Join-Path $TestDrive 'app2\install.ps1'
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Set-Content $src $fullScript -NoNewline
        Copy-SelfIfNeeded $src $dst
        (Get-Content $dst -Raw) | Should -Be $fullScript
    }
}
