using NetLX.Infrastructure.Platform;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Prepare_MovesPre14DataFolderOnce()
    {
        var parent = Directory.CreateTempSubdirectory("netlx-paths-").FullName;
        try
        {
            var legacy = Directory.CreateDirectory(Path.Combine(parent, "PortStrider", "profiles")).FullName;
            File.WriteAllText(Path.Combine(legacy, "index.json"), "[]");

            var target = AppPaths.Prepare(parent);

            Assert.Equal(Path.Combine(parent, AppPaths.FolderName), target);
            Assert.True(File.Exists(Path.Combine(target, "profiles", "index.json")));
            Assert.False(Directory.Exists(Path.Combine(parent, "PortStrider")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Prepare_KeepsExistingDataWhenBothFoldersExist()
    {
        var parent = Directory.CreateTempSubdirectory("netlx-paths-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, "PortStrider"));
            var current = Directory.CreateDirectory(Path.Combine(parent, AppPaths.FolderName)).FullName;
            File.WriteAllText(Path.Combine(current, "keep.txt"), "x");

            AppPaths.Prepare(parent);

            Assert.True(File.Exists(Path.Combine(current, "keep.txt")));
            Assert.True(Directory.Exists(Path.Combine(parent, "PortStrider")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
