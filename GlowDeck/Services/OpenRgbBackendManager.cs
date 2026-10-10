using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace GlowDeck.Services;

public sealed class OpenRgbBackendManager : IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 6742;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Process? _managedProcess;
    private bool _disposed;

    public bool IsManagedBackendRunning => _managedProcess is { HasExited: false };

    public static string? FindOpenRgbExecutable()
    {
        var candidates = new[]
        {
            OpenRgbBackendInstaller.PortableExecutablePath,
            Path.Combine(
                AppContext.BaseDirectory,
                "Backends",
                "OpenRGB-1.0",
                "OpenRGB Windows 64-bit",
                "OpenRGB.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "OpenRGB",
                "OpenRGB.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "OpenRGB",
                "OpenRGB.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "OpenRGB",
                "OpenRGB.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<OpenRgbSdkClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                return await OpenRgbSdkClient.ConnectAsync(cancellationToken: cancellationToken);
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_managedProcess is not { HasExited: false })
                {
                    DisposeExitedProcess();
                    if (IsOpenRgbProcessRunning())
                    {
                        throw new InvalidOperationException(
                            "OpenRGB is already running without its SDK server. Close it or enable its SDK server before refreshing; GlowDeck will not open a second process against the same hardware.");
                    }

                    var executablePath = FindOpenRgbExecutable();
                    if (executablePath is null)
                    {
                        throw new FileNotFoundException(
                            "The OpenRGB backend is not installed. Choose 'Set up RGB backend' in GlowDeck to download the official portable release.",
                            OpenRgbBackendInstaller.PortableExecutablePath);
                    }

                    _managedProcess = StartBackend(executablePath);
                }

                try
                {
                    return await WaitForConnectionAsync(_managedProcess!, cancellationToken);
                }
                catch
                {
                    await StopManagedProcessAsync();
                    throw;
                }
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopManagedProcessAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static Process StartBackend(string executablePath)
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlowDeck",
            "OpenRGBConfig");
        Directory.CreateDirectory(configDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("The OpenRGB executable has no parent directory."),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add("--noautoconnect");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configDirectory);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the OpenRGB background backend.");
    }

    private static async Task<OpenRgbSdkClient> WaitForConnectionAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The OpenRGB backend exited during startup with code {process.ExitCode}.");
            }

            try
            {
                return await OpenRgbSdkClient.ConnectAsync(
                    Host,
                    Port,
                    cancellationToken);
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        throw new TimeoutException(
            $"The OpenRGB SDK server did not become ready at {Host}:{Port} within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    private static bool IsOpenRgbProcessRunning()
    {
        var processes = Process.GetProcessesByName("OpenRGB");
        try
        {
            return processes.Any(process => !process.HasExited);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void DisposeExitedProcess()
    {
        if (_managedProcess is not { HasExited: true } process)
        {
            return;
        }

        _managedProcess = null;
        process.Dispose();
    }

    private async Task StopManagedProcessAsync()
    {
        var process = _managedProcess;
        _managedProcess = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
