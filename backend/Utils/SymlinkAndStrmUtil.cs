using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NzbWebDAV.Utils;

public static class SymlinkAndStrmUtil
{
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private const int MaxStderrChars = 4096;

    public static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrms(string directoryPath)
    {
        return IsLinux
            ? GetAllSymlinksAndStrmsLinux(directoryPath)
            : GetAllSymlinksAndStrmsWindows(directoryPath);
    }

    private static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrmsLinux(string directoryPath)
    {
        var startInfo = CreateLinuxFindStartInfo(directoryPath);
        using var process = Process.Start(startInfo)!;

        // Drain stderr asynchronously. Leaving it unread can fill the OS pipe buffer and
        // deadlock find when a library tree produces many permission errors.
        var stderrBuilder = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrBuilder)
            {
                if (stderrBuilder.Length >= MaxStderrChars) return;
                if (stderrBuilder.Length > 0)
                    stderrBuilder.Append('\n');

                var remaining = MaxStderrChars - stderrBuilder.Length;
                stderrBuilder.Append(e.Data.Length <= remaining ? e.Data : e.Data[..remaining]);
            }
        };
        process.BeginErrorReadLine();

        while (ReadNullTerminated(process.StandardOutput) is { } filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (Path.GetExtension(fullPath).Equals(".strm", StringComparison.OrdinalIgnoreCase))
            {
                yield return new StrmInfo
                {
                    StrmPath = fullPath,
                    TargetUrl = File.ReadAllText(fullPath)
                };
                continue;
            }

            var target = new FileInfo(fullPath).LinkTarget;
            if (target is not null)
            {
                yield return new SymlinkInfo
                {
                    SymlinkPath = fullPath,
                    TargetPath = target
                };
            }
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string stderr;
            lock (stderrBuilder)
                stderr = stderrBuilder.ToString();

            throw new InvalidOperationException(
                $"Library symlink scan failed with exit code {process.ExitCode}" +
                (string.IsNullOrWhiteSpace(stderr) ? "." : $": {stderr}"));
        }
    }

    /// <summary>
    /// Builds the Linux traversal process without a command shell. Every value,
    /// including the user-selected root, is passed as a distinct argv entry.
    /// </summary>
    internal static ProcessStartInfo CreateLinuxFindStartInfo(string directoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "find",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -H preserves the old `cd root && find .` behavior when the selected
        // root itself is a symlink, without following symlinks found below it.
        startInfo.ArgumentList.Add("-H");
        // An absolute starting point cannot be interpreted as a find option even
        // when a caller supplies a relative directory beginning with '-'.
        startInfo.ArgumentList.Add(Path.GetFullPath(directoryPath));
        startInfo.ArgumentList.Add("(");
        startInfo.ArgumentList.Add("-type");
        startInfo.ArgumentList.Add("l");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("-name");
        startInfo.ArgumentList.Add("*.strm");
        startInfo.ArgumentList.Add(")");
        startInfo.ArgumentList.Add("-print0");
        return startInfo;
    }

    private static string? ReadNullTerminated(StreamReader reader)
    {
        var value = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
                return value.Length == 0 ? null : value.ToString();
            if (next == '\0')
                return value.ToString();
            value.Append((char)next);
        }
    }

    private static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrmsWindows(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .Select(GetSymlinkOrStrmInfo)
            .Where(x => x != null)
            .Select(x => x!);
    }

    public static ISymlinkOrStrmInfo? GetSymlinkOrStrmInfo(FileInfo x)
    {
        return IsStrm(x) ? new StrmInfo() { StrmPath = x.FullName, TargetUrl = File.ReadAllText(x.FullName) }
            : IsSymLink(x) ? new SymlinkInfo() { SymlinkPath = x.FullName, TargetPath = x.LinkTarget! }
            : null;
    }

    private static bool IsStrm(FileInfo x) =>
        x.Extension.Equals(".strm", StringComparison.CurrentCultureIgnoreCase);

    private static bool IsSymLink(FileInfo x) =>
        x.Attributes.HasFlag(FileAttributes.ReparsePoint) && x.LinkTarget is not null;

    public interface ISymlinkOrStrmInfo;

    public struct SymlinkInfo : ISymlinkOrStrmInfo
    {
        public required string SymlinkPath;
        public required string TargetPath;
    }

    public struct StrmInfo : ISymlinkOrStrmInfo
    {
        public required string StrmPath;
        public required string TargetUrl;
    }
}
