using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NeeView
{
    public class WebImageHostService : IDisposable
    {
        private const int Port = 28228;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private TcpListener? _listener;
        private bool _disposedValue;


        public bool IsRunning => _listener is not null;


        public void Start()
        {
            ThrowIfDisposed();

            if (_listener is not null) return;

            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);

                Trace.WriteLine($"WebImageHostService: Started: {string.Join(", ", GetAccessUrls())}");
            }
            catch (Exception ex)
            {
                _listener = null;
                Trace.WriteLine($"WebImageHostService: Start failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_listener is null) return;

            _cancellationTokenSource.Cancel();
            _listener.Stop();
            _listener = null;
        }

        public static string[] GetAccessUrls()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(e => e.OperationalStatus == OperationalStatus.Up)
                .SelectMany(e => e.GetIPProperties().UnicastAddresses)
                .Select(e => e.Address)
                .Where(e => e.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(e))
                .Select(e => $"http://{e}:{Port}/")
                .Prepend($"http://localhost:{Port}/")
                .Distinct()
                .ToArray();
        }

        private async Task AcceptLoopAsync()
        {
            if (_listener is null) return;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                    _ = Task.Run(() => ProcessClientAsync(client, _cancellationTokenSource.Token));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"WebImageHostService: Accept failed: {ex.Message}");
                }
            }
        }

        private static async Task ProcessClientAsync(TcpClient client, CancellationToken token)
        {
            using var _ = client;

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(token);
                if (requestLine is null)
                {
                    return;
                }

                string? line;
                do
                {
                    line = await reader.ReadLineAsync(token);
                }
                while (!string.IsNullOrEmpty(line));

                var path = GetRequestPath(requestLine);
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await WriteResponseAsync(stream, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(CreateIndexHtml()), token);
                        break;

                    case "/current.jpg":
                    case "/current.jpeg":
                        await WriteCurrentImageAsync(stream, token);
                        break;

                    case "/command/prev":
                        await MovePageAsync(isNext: false, stream, token);
                        break;

                    case "/command/next":
                        await MovePageAsync(isNext: true, stream, token);
                        break;

                    default:
                        await WriteNotFoundAsync(stream, token);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                Trace.WriteLine($"WebImageHostService: Client closed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Request failed: {ex}");
            }
        }

        private static async Task MovePageAsync(bool isNext, Stream stream, CancellationToken token)
        {
            try
            {
                await AppDispatcher.InvokeAsync(() =>
                {
                    if (isNext)
                    {
                        BookOperation.Current.Control.MoveNext(null);
                    }
                    else
                    {
                        BookOperation.Current.Control.MovePrev(null);
                    }
                });

                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":true}"""), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot move page: {ex.Message}");
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "409 Conflict");
            }
        }

        private static string GetRequestPath(string requestLine)
        {
            var values = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2) return "/";

            var path = values[1];
            var queryIndex = path.IndexOf('?');
            return queryIndex >= 0 ? path[..queryIndex] : path;
        }

        private static async Task WriteCurrentImageAsync(Stream stream, CancellationToken token)
        {
            try
            {
                using var imageStream = new MemoryStream();
                await ExportCurrentViewAsync(imageStream, token);
                await WriteResponseAsync(stream, "image/jpeg", imageStream.ToArray(), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot export current image: {ex.Message}");
                await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No current image."), token, statusCode: "404 Not Found");
            }
        }

        private static async Task ExportCurrentViewAsync(Stream stream, CancellationToken token)
        {
            var source = AppDispatcher.Invoke(ExportImageSourceFactory.Create);
            var options = new ExportImageParameter()
            {
                HasBackground = true,
                IsOriginalSize = false,
                IsDotKeep = false,
                QualityLevel = 85,
            };

            using var exporter = new ViewImageExporter(source);
            await exporter.ExportAsync(stream, decrypt: true, BitmapImageFormat.Jpeg, options, token);
        }

        private static async Task WriteNotFoundAsync(Stream stream, CancellationToken token)
        {
            await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found."), token, statusCode: "404 Not Found");
        }

        private static async Task WriteResponseAsync(Stream stream, string contentType, byte[] content, CancellationToken token, string cacheControl = "no-cache", string statusCode = "200 OK")
        {
            var header =
                $"HTTP/1.1 {statusCode}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {content.Length}\r\n" +
                $"Cache-Control: {cacheControl}\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, token);
            await stream.WriteAsync(content, token);
        }

        private static string CreateIndexHtml()
        {
            return """
                <!doctype html>
                <html lang="ja">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>NeeView Web Image Host</title>
                  <style>
                    html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; background: #111; color: #eee; font-family: system-ui, sans-serif; }
                    body { display: grid; }
                    img { width: 100%; height: 100%; min-width: 0; min-height: 0; max-width: 100vw; max-height: 100%; object-fit: contain; display: block; touch-action: none; user-select: none; }
                  </style>
                </head>
                <body>
                  <img id="image" alt="Current NeeView page">
                  <script>
                    const image = document.getElementById('image');
                    let touchStartX = 0;
                    let touchStartY = 0;
                    let touchStartTime = 0;
                    function refresh() {
                      image.src = '/current.jpg?t=' + Date.now();
                    }
                    async function move(path) {
                      await fetch(path, { method: 'POST', cache: 'no-store' });
                      setTimeout(refresh, 250);
                    }
                    image.addEventListener('touchstart', event => {
                      if (event.changedTouches.length !== 1) return;
                      const touch = event.changedTouches[0];
                      touchStartX = touch.clientX;
                      touchStartY = touch.clientY;
                      touchStartTime = Date.now();
                    }, { passive: true });
                    image.addEventListener('touchend', event => {
                      if (event.changedTouches.length !== 1) return;
                      const touch = event.changedTouches[0];
                      const dx = touch.clientX - touchStartX;
                      const dy = touch.clientY - touchStartY;
                      const elapsed = Date.now() - touchStartTime;
                      const distance = Math.hypot(dx, dy);
                      if (distance < 12 && elapsed < 500) {
                        if (touch.clientX < window.innerWidth * 0.7) {
                          move('/command/next');
                        } else {
                          move('/command/prev');
                        }
                        return;
                      }
                      if (distance >= 50 && dy > 0 && Math.abs(dy) > Math.abs(dx) * 1.3) {
                        refresh();
                      }
                    }, { passive: true });
                    setInterval(refresh, 15000);
                    refresh();
                  </script>
                </body>
                </html>
                """;
        }

        private void ThrowIfDisposed()
        {
            if (_disposedValue) throw new ObjectDisposedException(GetType().FullName);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    _cancellationTokenSource.Dispose();
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
