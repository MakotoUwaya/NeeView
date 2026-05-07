using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace NeeView
{
    public class WebImageHostService : IDisposable
    {
        private const int Port = 28228;

        private static readonly SemaphoreSlim _thumbnailSemaphore = new(2);

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

                    case "/bookshelf":
                        await WriteBookshelfAsync(stream, token);
                        break;

                    case "/bookshelf/open":
                        await OpenBookshelfItemAsync(GetRequestTarget(requestLine), stream, token);
                        break;

                    case "/bookshelf/thumb":
                        await WriteBookshelfThumbnailAsync(GetRequestTarget(requestLine), stream, token);
                        break;

                    case "/bookshelf/nav":
                        await MoveBookshelfAsync(GetRequestTarget(requestLine), stream, token);
                        break;

                    case "/bookshelf/unread":
                        await MarkBookshelfUnreadAsync(GetRequestTarget(requestLine), stream, token);
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

        private static async Task WriteBookshelfAsync(Stream stream, CancellationToken token)
        {
            try
            {
                var model = AppDispatcher.Invoke(CreateBookshelfModel);
                var json = JsonSerializer.Serialize(model);
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot create bookshelf model: {ex.Message}");
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"Place":"","CanMoveUp":false,"CanMovePrevious":false,"CanMoveNext":false,"Items":[]}"""), token, "no-store");
            }
        }

        private static BookshelfModel CreateBookshelfModel()
        {
            var bookshelf = BookshelfFolderList.Current;
            var collection = bookshelf.FolderCollection;
            var items = collection?.Items
                .Select((item, index) => new BookshelfItemModel(
                    index,
                    item.DisplayName ?? item.Name ?? item.TargetPath.FileName,
                    item.TargetPath.SimplePath,
                    item.IsDirectoryMaybe(),
                    item.IsVisible,
                    item.CanThumbnail(),
                    BookHistoryCollection.Current.Contains(item.EntityPath.SimplePath)))
                .ToArray() ?? Array.Empty<BookshelfItemModel>();

            return new BookshelfModel(
                collection?.PlaceDisplayString ?? "",
                bookshelf.CanMoveToParent(),
                bookshelf.CanMoveToPrevious(),
                bookshelf.CanMoveToNext(),
                items);
        }

        private static async Task OpenBookshelfItemAsync(string requestTarget, Stream stream, CancellationToken token)
        {
            if (!TryGetQueryValue(requestTarget, "index", out var indexValue) || !int.TryParse(indexValue, out var index))
            {
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "400 Bad Request");
                return;
            }

            try
            {
                var result = await AppDispatcher.InvokeAsync(() =>
                {
                    var bookshelf = BookshelfFolderList.Current;
                    var item = bookshelf.FolderCollection?.Items.ElementAtOrDefault(index);
                    if (item is null || item.IsEmpty())
                    {
                        return new BookshelfOpenResult(false, false, false);
                    }

                    var canOpenFolder = item.CanOpenFolder();
                    var canLoadBook = !item.Attributes.HasFlag(FolderItemAttribute.System) &&
                        !item.Attributes.HasFlag(FolderItemAttribute.Directory | FolderItemAttribute.Bookmark);

                    if (canLoadBook)
                    {
                        bookshelf.LoadItem(item);
                    }

                    if (canOpenFolder)
                    {
                        // Match the bookshelf double-click behavior: load as a book, then enter the folder when possible.
                        bookshelf.RequestPlace(item.TargetPath, null, FolderSetPlaceOption.Focus | FolderSetPlaceOption.UpdateHistory);
                        return new BookshelfOpenResult(true, canLoadBook, true);
                    }

                    return new BookshelfOpenResult(true, canLoadBook, false);
                });

                var json = JsonSerializer.Serialize(result);
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot open bookshelf item: {ex.Message}");
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "409 Conflict");
            }
        }

        private static async Task WriteBookshelfThumbnailAsync(string requestTarget, Stream stream, CancellationToken token)
        {
            if (!TryGetQueryValue(requestTarget, "index", out var indexValue) || !int.TryParse(indexValue, out var index))
            {
                await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad thumbnail request."), token, "no-store", "400 Bad Request");
                return;
            }

            if (!await _thumbnailSemaphore.WaitAsync(TimeSpan.FromMilliseconds(500), token))
            {
                await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Busy."), token, "no-store", "503 Service Unavailable");
                return;
            }

            try
            {
                var thumbnail = AppDispatcher.Invoke(() =>
                {
                    var item = BookshelfFolderList.Current.FolderCollection?.Items.ElementAtOrDefault(index);
                    return item?.CanThumbnail() == true ? item.Thumbnail : null;
                });

                if (thumbnail is null)
                {
                    await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No thumbnail."), token, "no-store", "404 Not Found");
                    return;
                }

                if (thumbnail is Thumbnail thumbnailImage && !thumbnailImage.IsValid)
                {
                    await thumbnailImage.InitializeFromCacheAsync(token);
                }

                var bytes = AppDispatcher.Invoke(() =>
                {
                    var imageSource = thumbnail is Thumbnail loadedThumbnail
                        ? loadedThumbnail.CreateImageSource()
                        : thumbnail.ImageSource;

                    if (imageSource is not BitmapSource bitmapSource) return null;

                    using var imageStream = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                    encoder.Save(imageStream);
                    return imageStream.ToArray();
                });

                if (bytes is null)
                {
                    await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No thumbnail."), token, "no-store", "404 Not Found");
                    return;
                }

                await WriteResponseAsync(stream, "image/png", bytes, token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot export bookshelf thumbnail: {ex.Message}");
                await WriteResponseAsync(stream, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No thumbnail."), token, "no-store", "404 Not Found");
            }
            finally
            {
                _thumbnailSemaphore.Release();
            }
        }

        private static async Task MarkBookshelfUnreadAsync(string requestTarget, Stream stream, CancellationToken token)
        {
            if (!TryGetQueryValue(requestTarget, "index", out var indexValue) || !int.TryParse(indexValue, out var index))
            {
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "400 Bad Request");
                return;
            }

            try
            {
                var path = await AppDispatcher.InvokeAsync(() =>
                {
                    var item = BookshelfFolderList.Current.FolderCollection?.Items.ElementAtOrDefault(index);
                    return item is null || item.IsEmpty() ? null : item.EntityPath.SimplePath;
                });

                if (path is not null)
                {
                    BookHistoryCollection.Current.Remove(path);
                }

                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":true}"""), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot mark bookshelf item unread: {ex.Message}");
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "409 Conflict");
            }
        }

        private static async Task MoveBookshelfAsync(string requestTarget, Stream stream, CancellationToken token)
        {
            if (!TryGetQueryValue(requestTarget, "action", out var action))
            {
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "400 Bad Request");
                return;
            }

            try
            {
                await AppDispatcher.InvokeAsync(() =>
                {
                    var bookshelf = BookshelfFolderList.Current;
                    switch (action)
                    {
                        case "up":
                            if (bookshelf.CanMoveToParent())
                            {
                                bookshelf.MoveToParent();
                            }
                            break;

                        case "previous":
                            if (bookshelf.CanMoveToPrevious())
                            {
                                bookshelf.MoveToPrevious();
                            }
                            break;

                        case "next":
                            if (bookshelf.CanMoveToNext())
                            {
                                bookshelf.MoveToNext();
                            }
                            break;
                    }
                });

                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":true}"""), token, "no-store");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WebImageHostService: Cannot move bookshelf: {ex.Message}");
                await WriteResponseAsync(stream, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"ok":false}"""), token, "no-store", "409 Conflict");
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
            var path = GetRequestTarget(requestLine);
            var queryIndex = path.IndexOf('?');
            return queryIndex >= 0 ? path[..queryIndex] : path;
        }

        private static string GetRequestTarget(string requestLine)
        {
            var values = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2) return "/";

            return values[1];
        }

        private static bool TryGetQueryValue(string requestTarget, string key, out string value)
        {
            value = "";

            var queryIndex = requestTarget.IndexOf('?');
            if (queryIndex < 0 || queryIndex + 1 >= requestTarget.Length) return false;

            foreach (var token in requestTarget[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = token.Split('=', 2);
                if (Uri.UnescapeDataString(pair[0]) == key)
                {
                    value = pair.Length >= 2 ? Uri.UnescapeDataString(pair[1]) : "";
                    return true;
                }
            }

            return false;
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
                    #backdrop { position: fixed; inset: 0; background: #0007; opacity: 0; pointer-events: none; transition: opacity 160ms ease-out; }
                    #backdrop.open { opacity: 1; pointer-events: auto; }
                    #sheet { position: fixed; inset: auto 0 0 0; max-height: 80vh; background: #181818; border-top: 1px solid #333; transform: translateY(100%); transition: transform 160ms ease-out; display: grid; grid-template-rows: auto minmax(0, 1fr); box-shadow: 0 -8px 24px #0009; }
                    #sheet.open { transform: translateY(0); }
                    #sheetHeader { padding: 8px 12px; border-bottom: 1px solid #303030; color: #ccc; font-size: 12px; white-space: nowrap; overflow-x: auto; overflow-y: hidden; -webkit-overflow-scrolling: touch; user-select: none; -webkit-user-select: none; }
                    #fullscreenButton { position: fixed; top: 10px; right: 10px; width: 34px; height: 34px; border: 1px solid #555; border-radius: 4px; background: #111b; color: #eee; font-size: 20px; line-height: 30px; display: grid; place-items: center; z-index: 2; }
                    #bookshelfItems { overflow: auto; -webkit-overflow-scrolling: touch; }
                    .bookshelfItem { min-height: 60px; padding: 8px 14px; border-bottom: 1px solid #282828; font-size: 16px; display: grid; grid-template-columns: 44px 1fr; gap: 12px; align-items: center; user-select: none; -webkit-user-select: none; -webkit-touch-callout: none; }
                    .bookshelfItem.current { background: #263247; }
                    .bookshelfMedia { position: relative; width: 44px; height: 44px; display: grid; place-items: center; }
                    .bookshelfIcon { color: #aaa; width: 44px; text-align: center; font-size: 22px; }
                    .bookshelfThumb { width: 44px; height: 44px; object-fit: contain; background: #111; }
                    .bookshelfReadMark { position: absolute; right: -3px; bottom: -3px; width: 16px; height: 16px; border-radius: 50%; background: #1f7a3a; color: white; font-size: 12px; line-height: 16px; text-align: center; box-shadow: 0 0 0 2px #181818; }
                    .bookshelfName { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                  </style>
                </head>
                <body>
                  <img id="image" alt="Current NeeView page">
                  <button id="fullscreenButton" type="button" aria-label="Fullscreen">&#9974;</button>
                  <div id="backdrop"></div>
                  <section id="sheet" aria-hidden="true">
                    <div id="sheetHeader">Bookshelf</div>
                    <div id="bookshelfItems"></div>
                  </section>
                  <script>
                    const image = document.getElementById('image');
                    const fullscreenButton = document.getElementById('fullscreenButton');
                    const backdrop = document.getElementById('backdrop');
                    const sheet = document.getElementById('sheet');
                    const sheetHeader = document.getElementById('sheetHeader');
                    const bookshelfItems = document.getElementById('bookshelfItems');
                    let touchStartX = 0;
                    let touchStartY = 0;
                    let touchStartTime = 0;
                    let sheetTouchStartX = 0;
                    let sheetTouchStartY = 0;
                    let longPressTimer = 0;
                    let suppressNextClick = false;
                    const thumbnailObserver = new IntersectionObserver(entries => {
                      for (const entry of entries) {
                        if (!entry.isIntersecting) continue;
                        const thumb = entry.target;
                        thumbnailObserver.unobserve(thumb);
                        thumb.src = thumb.dataset.src;
                      }
                    }, { root: bookshelfItems, rootMargin: '160px 0px' });
                    function refresh() {
                      image.src = '/current.jpg?t=' + Date.now();
                    }
                    async function move(path) {
                      await fetch(path, { method: 'POST', cache: 'no-store' });
                      setTimeout(refresh, 250);
                    }
                    function refreshAndCloseBookshelf() {
                      const closeAfterLoad = () => closeBookshelf();
                      image.addEventListener('load', closeAfterLoad, { once: true });
                      refresh();
                      setTimeout(closeBookshelf, 1200);
                    }
                    async function openBookshelf() {
                      const response = await fetch('/bookshelf?t=' + Date.now(), { cache: 'no-store' });
                      const model = await response.json();
                      sheetHeader.textContent = model.Place || 'Bookshelf';
                      bookshelfItems.replaceChildren();
                      for (const item of model.Items || []) {
                        const row = document.createElement('div');
                        row.className = 'bookshelfItem' + (item.IsCurrent ? ' current' : '');
                        const media = document.createElement('span');
                        media.className = 'bookshelfMedia';
                        const icon = document.createElement(item.HasThumbnail ? 'img' : 'span');
                        if (item.HasThumbnail) {
                          icon.className = 'bookshelfThumb';
                          icon.alt = '';
                          icon.dataset.src = '/bookshelf/thumb?index=' + item.Index + '&t=' + Date.now();
                          icon.addEventListener('error', () => {
                            const fallback = document.createElement('span');
                            fallback.className = 'bookshelfIcon';
                            fallback.innerHTML = item.IsDirectory ? '&#128193;' : '&#128196;';
                            icon.replaceWith(fallback);
                          });
                        } else {
                          icon.className = 'bookshelfIcon';
                          icon.innerHTML = item.IsDirectory ? '&#128193;' : '&#128196;';
                        }
                        media.append(icon);
                        if (item.HasThumbnail) {
                          thumbnailObserver.observe(icon);
                        }
                        if (item.IsRead) {
                          const mark = document.createElement('span');
                          mark.className = 'bookshelfReadMark';
                          mark.textContent = '✓';
                          media.append(mark);
                        }
                        const name = document.createElement('span');
                        name.className = 'bookshelfName';
                        name.textContent = item.Name || item.Path;
                        row.append(media, name);
                        row.addEventListener('contextmenu', event => event.preventDefault());
                        row.addEventListener('touchstart', event => {
                          if (event.changedTouches.length !== 1) return;
                          longPressTimer = window.setTimeout(async () => {
                            suppressNextClick = true;
                            if (item.IsRead) {
                              await fetch('/bookshelf/unread?index=' + item.Index, { method: 'POST', cache: 'no-store' });
                              setTimeout(openBookshelf, 150);
                            }
                          }, 650);
                        }, { passive: true });
                        row.addEventListener('touchmove', () => {
                          window.clearTimeout(longPressTimer);
                        }, { passive: true });
                        row.addEventListener('touchend', () => {
                          window.clearTimeout(longPressTimer);
                        }, { passive: true });
                        row.addEventListener('touchcancel', () => {
                          window.clearTimeout(longPressTimer);
                        }, { passive: true });
                        row.addEventListener('click', async () => {
                          if (suppressNextClick) {
                            suppressNextClick = false;
                            return;
                          }
                          const response = await fetch('/bookshelf/open?index=' + item.Index, { method: 'POST', cache: 'no-store' });
                          const result = await response.json();
                          if (result.OpenedFolder) {
                            setTimeout(openBookshelf, 300);
                            if (result.LoadedBook) {
                              setTimeout(refresh, 350);
                            }
                          } else if (result.LoadedBook) {
                            setTimeout(refreshAndCloseBookshelf, 350);
                          }
                        });
                        bookshelfItems.append(row);
                      }
                      backdrop.classList.add('open');
                      sheet.classList.add('open');
                      sheet.setAttribute('aria-hidden', 'false');
                      requestAnimationFrame(() => {
                        const current = bookshelfItems.querySelector('.bookshelfItem.current');
                        current?.scrollIntoView({ block: 'start' });
                      });
                    }
                    async function moveBookshelf(action) {
                      await fetch('/bookshelf/nav?action=' + action, { method: 'POST', cache: 'no-store' });
                      setTimeout(openBookshelf, 250);
                    }
                    async function toggleFullscreen() {
                      if (document.fullscreenElement) {
                        await document.exitFullscreen();
                      } else {
                        await document.documentElement.requestFullscreen();
                      }
                    }
                    function closeBookshelf() {
                      backdrop.classList.remove('open');
                      sheet.classList.remove('open');
                      sheet.setAttribute('aria-hidden', 'true');
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
                      } else if (distance >= 50 && dy < 0 && Math.abs(dy) > Math.abs(dx) * 1.3) {
                        openBookshelf();
                      }
                    }, { passive: true });
                    backdrop.addEventListener('click', closeBookshelf);
                    sheet.addEventListener('touchstart', event => {
                      if (event.changedTouches.length !== 1) return;
                      const touch = event.changedTouches[0];
                      sheetTouchStartX = touch.clientX;
                      sheetTouchStartY = touch.clientY;
                    }, { passive: true });
                    sheet.addEventListener('touchend', event => {
                      if (event.changedTouches.length !== 1) return;
                      const touch = event.changedTouches[0];
                      const dx = touch.clientX - sheetTouchStartX;
                      const dy = touch.clientY - sheetTouchStartY;
                      const distance = Math.hypot(dx, dy);
                      if (distance < 50) return;
                      if (dx > 0 && Math.abs(dx) > Math.abs(dy) * 1.3) {
                        moveBookshelf('up');
                      }
                    }, { passive: true });
                    fullscreenButton.addEventListener('click', toggleFullscreen);
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

        private record BookshelfModel(string Place, bool CanMoveUp, bool CanMovePrevious, bool CanMoveNext, BookshelfItemModel[] Items);

        private record BookshelfItemModel(int Index, string Name, string Path, bool IsDirectory, bool IsCurrent, bool HasThumbnail, bool IsRead);

        private record BookshelfOpenResult(bool Ok, bool LoadedBook, bool OpenedFolder);
    }
}
