using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace TapoCam.Updater;

internal static class Program
{
    private const int ParentExitTimeoutMilliseconds = 60_000;
    private static string? _appPath;
    private static string? _updateDirectoryId;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || !int.TryParse(args[1], out var parentProcessId))
                throw new ArgumentException("Argumentos inválidos para iniciar a atualização.");

            var installerPath = Path.GetFullPath(args[0]);
            _appPath = Path.GetFullPath(args[2]);
            _updateDirectoryId = new DirectoryInfo(Path.GetDirectoryName(installerPath)!).Name;
            var appPath = _appPath;
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("O instalador baixado não foi encontrado.", installerPath);
            if (!File.Exists(appPath))
                throw new FileNotFoundException("O TapoCam não foi encontrado na pasta de instalação.", appPath);

            WaitForParentExit(parentProcessId, appPath);
            var exitCode = RunInstaller(installerPath);
            if (exitCode == 0)
            {
                StartApp(appPath, _updateDirectoryId);
                TryDelete(installerPath);
                return 0;
            }

            MessageBox.Show(
                $"A instalação da atualização falhou (código {exitCode}). O TapoCam será reaberto na versão instalada. Consulte o log em %LOCALAPPDATA%\\TapoCam\\update.log.",
                "Atualização do TapoCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            StartApp(appPath, _updateDirectoryId);
            return exitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Não foi possível concluir a atualização: {ex.Message}",
                "Atualização do TapoCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (_appPath is not null && _updateDirectoryId is not null && File.Exists(_appPath))
            {
                try { StartApp(_appPath, _updateDirectoryId); }
                catch (Exception launchException)
                {
                    MessageBox.Show(
                        $"Também não foi possível reabrir o TapoCam: {launchException.Message}",
                        "Atualização do TapoCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return 1;
        }
    }

    private static void WaitForParentExit(int processId, string appPath)
    {
        if (processId == Process.GetCurrentProcess().Id)
            throw new ArgumentException("O processo pai da atualização é inválido.");

        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }

        using (parent)
        {
            if (parent.HasExited)
                return;
            var parentPath = parent.MainModule?.FileName;
            if (!string.Equals(parentPath, appPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O processo que iniciou a atualização não é o TapoCam instalado.");
            if (!parent.WaitForExit(ParentExitTimeoutMilliseconds))
                throw new TimeoutException("O TapoCam não encerrou no tempo esperado.");
        }
    }

    private static int RunInstaller(string installerPath)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapoCam");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "update.log");
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!
        };
        startInfo.Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG=\"{logPath}\"";

        using var installer = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar o instalador.");
        installer.WaitForExit();
        return installer.ExitCode;
    }

    private static void StartApp(string appPath, string? updateDirectoryId = null)
    {
        var startInfo = new ProcessStartInfo(appPath) { UseShellExecute = true };
        if (updateDirectoryId is not null)
            startInfo.Arguments = $"--cleanup-update {updateDirectoryId} --update-helper-pid {Process.GetCurrentProcess().Id}";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível reiniciar o TapoCam.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
