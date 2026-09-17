using System.Diagnostics;

namespace Chameleon.Tunnel;

/// <summary>Запуск внешних команд (route/netsh/tun2socks) с логированием.</summary>
internal static class ProcessRunner
{
    public static async Task<int> RunAsync(string file, string args, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"$ {file} {args}");
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process();
        p.StartInfo = psi;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) log?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) log?.Invoke(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    /// <summary>Запускает команду и возвращает её stdout целиком.</summary>
    public static async Task<string> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process();
        p.StartInfo = psi;
        p.Start();
        string output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }

    /// <summary>Запускает долгоживущий процесс (tun2socks) и возвращает его.</summary>
    public static Process Start(string file, string args, Action<string>? log, string? workingDirectory = null)
    {
        log?.Invoke($"$ {file} {args}");
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) log?.Invoke("[tun2socks] " + e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) log?.Invoke("[tun2socks] " + e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }
}