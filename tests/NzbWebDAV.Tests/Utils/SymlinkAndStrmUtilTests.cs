using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class SymlinkAndStrmUtilTests
{
    [Fact]
    public void LinuxFindStartInfo_PassesHostileRootAsOneOpaqueArgument()
    {
        var hostileRoot = Path.Combine(
            Path.GetTempPath(),
            "library-\"-'-$()-; touch injected-line1\nline2");

        var startInfo = SymlinkAndStrmUtil.CreateLinuxFindStartInfo(hostileRoot);

        Assert.Equal("find", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(Path.GetFullPath(hostileRoot), startInfo.ArgumentList[1]);
        Assert.Equal(
            ["-H", Path.GetFullPath(hostileRoot), "(", "-type", "l", "-o", "-name", "*.strm", ")", "-print0"],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public void LinuxEnumeration_HandlesQuotesNewlinesAndShellMetacharactersLiterally()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"library-\"-'-$()-; touch injected-line1\nline2-{Guid.NewGuid():N}");
        var strmPath = Path.Combine(root, "movie.strm");
        var symlinkPath = Path.Combine(root, "episode-link.mkv");
        const string targetUrl = "http://localhost:8080/content/movie.mkv?token=a&part=1";
        const string linkTarget = "missing-target.mkv";

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(strmPath, targetUrl);
            File.CreateSymbolicLink(symlinkPath, linkTarget);

            var results = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(root).ToList();

            var strm = Assert.Single(results.OfType<SymlinkAndStrmUtil.StrmInfo>());
            Assert.Equal(strmPath, strm.StrmPath);
            Assert.Equal(targetUrl, strm.TargetUrl);

            var symlink = Assert.Single(results.OfType<SymlinkAndStrmUtil.SymlinkInfo>());
            Assert.Equal(symlinkPath, symlink.SymlinkPath);
            Assert.Equal(linkTarget, symlink.TargetPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
