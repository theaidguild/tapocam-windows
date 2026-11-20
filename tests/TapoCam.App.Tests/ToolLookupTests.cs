using TapoCam.App.Services;

namespace TapoCam.App.Tests;

public sealed class ToolLookupTests
{
    [Fact]
    public void FindsToolInWingetLinksWhenPathIsStale()
    {
        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links");
        var directories = WindowsServices.ToolSearchDirectories(@"C:\Windows\System32").ToList();
        Assert.True(directories.IndexOf(links) > directories.IndexOf(@"C:\Windows\System32"));

        var folder = Directory.CreateTempSubdirectory("TapoCam-tests-");
        try
        {
            var tool = Path.Combine(folder.FullName, "ffmpeg.exe");
            File.WriteAllText(tool, "");
            Assert.Equal(tool, WindowsServices.FindTool("ffmpeg.exe", ["relative", folder.FullName]));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void FindsWingetPortableToolInsideVersionedPackageDirectory()
    {
        var folder = Directory.CreateTempSubdirectory("TapoCam-tests-");
        try
        {
            var package = Path.Combine(folder.FullName, "Gyan.FFmpeg.Essentials_TestSource");
            var bin = Path.Combine(package, "ffmpeg-test-essentials_build", "bin");
            Directory.CreateDirectory(bin);
            var tool = Path.Combine(bin, "ffmpeg.exe");
            File.WriteAllText(tool, "");

            Assert.Equal(tool, WindowsServices.FindWingetPackageTool("ffmpeg.exe", folder.FullName));
            Assert.Null(WindowsServices.FindWingetPackageTool("go2rtc.exe", folder.FullName));
        }
        finally { folder.Delete(recursive: true); }
    }
}
