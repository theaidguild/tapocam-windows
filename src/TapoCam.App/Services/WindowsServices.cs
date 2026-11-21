using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace TapoCam.App.Services;

internal static class WindowsServices
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TapoCam", "settings.json");

    public static string CreateRuntimeDirectory()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapoCam");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
            directory.SetAccessControl(security);
        else
            directory.Create(security);
        return path;
    }

    public static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Não foi possível identificar o usuário do Windows.");
    }

    public static void WritePrivateText(string path, string value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            using (var stream = new FileInfo(temporary).Create(
                       FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(value);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static string? FindTool(string name)
    {
        var tool = FindTool(name, ToolSearchDirectories(Environment.GetEnvironmentVariable("PATH")));
        return tool ?? FindWingetPackageTool(name,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages"));
    }

    public static string? FindTool(string name, IEnumerable<string> directories)
    {
        foreach (var entry in directories)
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string? FindWingetPackageTool(string name, string packagesDirectory)
    {
        if (!string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Directory.Exists(packagesDirectory))
            return null;
        foreach (var package in Directory.EnumerateDirectories(packagesDirectory, "Gyan.FFmpeg.Essentials_*"))
        foreach (var version in Directory.EnumerateDirectories(package))
        {
            var candidate = Path.Combine(version, "bin", name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // winget's Links folders are checked explicitly because a process started before or
    // during a winget install (e.g. from the installer) still has the old PATH.
    public static IEnumerable<string> ToolSearchDirectories(string? path) =>
        new[] { Path.Combine(AppContext.BaseDirectory, "tools") }
            .Concat((path ?? "").Split(Path.PathSeparator))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links"));

    public static string RequireTool(string name) =>
        FindTool(name) ?? throw new FileNotFoundException(
            $"Não foi possível encontrar {name}. Reinstale o TapoCam ou, para o FFmpeg, execute: winget install Gyan.FFmpeg.Essentials");

    public static bool LaunchAtSignIn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("TapoCam") is string;
        }
    }

    public static bool IsInstallerManaged
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\TapoCam");
            var installPath = key?.GetValue("InstallPath") as string;
            var processPath = Environment.ProcessPath;
            return key?.GetValue("InstallerManaged") is int value && value == 1 &&
                installPath is not null && processPath is not null &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)),
                    Path.GetDirectoryName(Path.GetFullPath(processPath)), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetLaunchAtSignIn(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue("TapoCam", $"\"{Environment.ProcessPath}\" --minimized", RegistryValueKind.String);
        else
            key.DeleteValue("TapoCam", throwOnMissingValue: false);
    }

    public static void Open(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
    }

    public static void ShowInExplorer(string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        info.ArgumentList.Add("/select," + Path.GetFullPath(path));
        using var process = Process.Start(info);
    }

    public static void Recycle(string path) => FileSystem.DeleteFile(
        path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    public static void Notify(string title, string body)
    {
        try { new ToastContentBuilder().AddText(title).AddText(body).Show(); }
        catch (Exception) { /* Notification delivery is best effort. */ }
    }

    // Must be called on the dispatcher thread: Windows associates the state with the calling thread.
    public static void SetRecordingPowerState(bool recording) =>
        _ = SetThreadExecutionState(recording ? 0x80000001u : 0x80000000u);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
