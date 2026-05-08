using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NeeView
{
    public class NgrokTunnelService : IDisposable
    {
        private const int TargetPort = 28228;
        private const string ApiUrl = "http://127.0.0.1:4040/api/tunnels";

        private static readonly Lazy<NgrokTunnelService> _instance = new(() => new NgrokTunnelService());
        public static NgrokTunnelService Current => _instance.Value;

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private Process? _process;
        private bool _disposed;


        public bool IsManaged => _process is { HasExited: false };


        public async Task<bool> IsApiReadyAsync(CancellationToken token)
        {
            try
            {
                using var response = await _httpClient.GetAsync(ApiUrl, token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return false;
            }
        }

        public async Task EnsureRunningAsync(CancellationToken token)
        {
            ThrowIfDisposed();

            if (await IsApiReadyAsync(token)) return;

            await _gate.WaitAsync(token);
            try
            {
                if (await IsApiReadyAsync(token)) return;

                StartProcess();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                while (!timeoutCts.IsCancellationRequested)
                {
                    if (_process is { HasExited: true })
                    {
                        throw new InvalidOperationException($"ngrok exited before becoming ready (exit code {_process.ExitCode}).");
                    }

                    if (await IsApiReadyAsync(timeoutCts.Token)) return;

                    try
                    {
                        await Task.Delay(300, timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                throw new TimeoutException("ngrok did not become ready within 15 seconds.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void StartProcess()
        {
            if (_process is { HasExited: false }) return;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ngrok",
                    Arguments = $"http {TargetPort} --log=stdout",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start ngrok process.");

                process.OutputDataReceived += OnOutput;
                process.ErrorDataReceived += OnError;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _process = process;
                Trace.WriteLine($"NgrokTunnelService: Started ngrok process (PID {process.Id}).");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException("ngrok command was not found in PATH. Install ngrok and try again.", ex);
            }
        }

        private static void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Trace.WriteLine($"ngrok: {e.Data}");
            }
        }

        private static void OnError(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Trace.WriteLine($"ngrok!: {e.Data}");
            }
        }

        public void Stop()
        {
            var process = _process;
            _process = null;
            if (process is null) return;

            try
            {
                process.OutputDataReceived -= OnOutput;
                process.ErrorDataReceived -= OnError;

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"NgrokTunnelService: Stop failed: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _gate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
