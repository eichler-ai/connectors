using System;
using System.IO;
using MCPBridge.Core.Workspace;
using Xunit;

namespace MCPBridge.Core.Tests.Workspace;

public class WorkspacePathsTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspacePathsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mcpbridge-workspace-tests-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Local_LayoutIsCorrectRelativeToInjectableRoot()
    {
        var paths = WorkspacePaths.Local("doc-abc123", "instance-1", _tempRoot);
        var expectedRoot = Path.Combine(_tempRoot, "RevitMCPExchange", "doc-abc123");

        Assert.Equal(expectedRoot, paths.DocumentRoot);
        Assert.Equal(Path.Combine(expectedRoot, "imports"), paths.Imports);
        Assert.Equal(Path.Combine(expectedRoot, "exports"), paths.Exports);
        Assert.Equal(Path.Combine(expectedRoot, "logs"), paths.Logs);
        Assert.Equal(Path.Combine(expectedRoot, "scripts"), paths.Scripts);
        Assert.Equal(Path.Combine(expectedRoot, "tmp", "instance-1"), paths.Tmp());
    }

    [Fact]
    public void Tmp_CanBeGivenADifferentInstanceIdThanTheOneConstructedWith()
    {
        var paths = WorkspacePaths.Local("doc-abc123", "instance-1", _tempRoot);

        var other = paths.Tmp("instance-2");

        Assert.Equal(Path.Combine(paths.DocumentRoot, "tmp", "instance-2"), other);
    }

    [Fact]
    public void AccessingAPathProperty_ActuallyCreatesTheDirectoryOnDisk()
    {
        var paths = WorkspacePaths.Local("doc-xyz", "instance-1", _tempRoot);

        var imports = paths.Imports;

        Assert.True(Directory.Exists(imports));
    }

    [Fact]
    public void EnsureDirectoriesExist_CreatesAllFiveDirectories()
    {
        var paths = WorkspacePaths.Local("doc-xyz2", "instance-1", _tempRoot);

        paths.EnsureDirectoriesExist();
        _ = paths.Tmp();

        Assert.True(Directory.Exists(paths.Imports));
        Assert.True(Directory.Exists(paths.Exports));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(Directory.Exists(paths.Scripts));
        Assert.True(Directory.Exists(paths.Tmp()));
    }

    [Fact]
    public void RepeatedAccess_IsIdempotent_NoExceptionSamePaths()
    {
        var paths = WorkspacePaths.Local("doc-repeat", "instance-1", _tempRoot);

        var first = paths.Exports;
        var second = paths.Exports;

        Assert.Equal(first, second);
        Assert.True(Directory.Exists(first));

        // Calling again after the directory already exists must not throw.
        var third = paths.Exports;
        Assert.Equal(first, third);
    }

    [Fact]
    public void TryPromoteDocumentRoot_RenamesFolderInPlace()
    {
        var oldPaths = WorkspacePaths.Local("tmp-old123", "instance-1", _tempRoot);
        oldPaths.EnsureDirectoriesExist();
        File.WriteAllText(Path.Combine(oldPaths.Exports, "view.png"), "fake image bytes");

        var promoted = WorkspacePaths.TryPromoteDocumentRoot("tmp-old123", "doc-new456", _tempRoot);

        Assert.True(promoted);
        var newPaths = WorkspacePaths.Local("doc-new456", "instance-1", _tempRoot);
        Assert.True(File.Exists(Path.Combine(newPaths.DocumentRoot, "exports", "view.png")));
        Assert.False(Directory.Exists(oldPaths.DocumentRoot));
    }

    [Fact]
    public void TryPromoteDocumentRoot_MissingSource_ReturnsFalse_DoesNotThrow()
    {
        var promoted = WorkspacePaths.TryPromoteDocumentRoot("tmp-nonexistent", "doc-whatever", _tempRoot);

        Assert.False(promoted);
    }

    [Fact]
    public void TryPromoteDocumentRoot_DestinationAlreadyExists_ReturnsFalse_DoesNotThrow()
    {
        var oldPaths = WorkspacePaths.Local("tmp-collide-old", "instance-1", _tempRoot);
        oldPaths.EnsureDirectoriesExist();
        var newPaths = WorkspacePaths.Local("doc-collide-new", "instance-1", _tempRoot);
        newPaths.EnsureDirectoriesExist();

        var promoted = WorkspacePaths.TryPromoteDocumentRoot("tmp-collide-old", "doc-collide-new", _tempRoot);

        Assert.False(promoted);
        Assert.True(Directory.Exists(oldPaths.DocumentRoot));
    }

    [Fact]
    public void RegisterAlias_ResolveAlias_RoundTrips_AndPassesThroughUnknownIds()
    {
        var oldId = "tmp-" + Guid.NewGuid();
        var newId = "doc-" + Guid.NewGuid();

        Assert.Equal(oldId, WorkspacePaths.ResolveAlias(oldId)); // no alias yet -> unchanged

        WorkspacePaths.RegisterAlias(oldId, newId);

        Assert.Equal(newId, WorkspacePaths.ResolveAlias(oldId));
        Assert.Equal(newId, WorkspacePaths.ResolveAlias(newId)); // resolving the new id itself is a no-op
    }
}
