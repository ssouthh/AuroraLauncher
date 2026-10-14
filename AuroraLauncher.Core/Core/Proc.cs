using System.Diagnostics;
using System.Text;

namespace AuroraLauncher.Core;

/// <summary>Запуск внешних процессов с захватом вывода (установщики Forge/NeoForge, java -version).</summary>
public static class Proc
{
    public static async Task<(int code, string output)> RunCapturedAsync(string exe, IEnumerable<string> args,
        string? workDir, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (sb) sb.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (sb) sb.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    public static void OpenInExplorer(string dir)
    {
        Directory.CreateDirectory(dir);
        _ = Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
        => _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
