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
        Assert.Equal(Path.Combine(expectedRoot, "tmp", "instance-1"), paths.Tmp());
    }

    [Fact]
    public void AccessingAPathProperty_ActuallyCreatesTheDirectoryOnDisk()
    {
        var paths = WorkspacePaths.Local("doc-xyz", "instance-1", _tempRoot);

        var imports = paths.Imports;

        Assert.True(Directory.Exists(imports));
    }

    [Fact]
    public void EnsureDirectoriesExist_CreatesImportsAndExports()
    {
        var paths = WorkspacePaths.Local("doc-xyz2", "instance-1", _tempRoot);

        paths.EnsureDirectoriesExist();

        Assert.True(Directory.Exists(paths.Imports));
        Assert.True(Directory.Exists(paths.Exports));
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
}
