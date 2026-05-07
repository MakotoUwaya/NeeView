using NeeView.Properties;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QRCoder;

namespace NeeView
{
    public class ShowNgrokTunnelQrCodeCommand : CommandElement
    {
        public ShowNgrokTunnelQrCodeCommand()
        {
            this.Group = TextResources.GetString("CommandGroup.Other");
            this.IsShowMessage = false;
        }

        public override async void Execute(object? sender, CommandContext e)
        {
            try
            {
                var url = await NgrokTunnelTools.GetPublicUrlAsync(CancellationToken.None);
                if (url is null)
                {
                    ShowError("ngrok tunnel URL was not found. Start ngrok with \"ngrok http 28228\" and try again.");
                    return;
                }

                var image = QrCodeTools.CreateBitmapImage(url);
                var dialog = new NgrokTunnelQrCodeWindow(url, image)
                {
                    Owner = MainWindow.Current,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ShowNgrokTunnelQrCodeCommand: {ex}");
                ShowError(ex.Message);
            }
        }

        private static void ShowError(string message)
        {
            var dialog = new MessageDialog("ngrok tunnel QR code", message, MessageDialogIcon.Error)
            {
                Owner = MainWindow.Current,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            dialog.ShowDialog();
        }
    }

    internal static class NgrokTunnelTools
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        public static async Task<string?> GetPublicUrlAsync(CancellationToken token)
        {
            using var response = await _httpClient.GetAsync("http://127.0.0.1:4040/api/tunnels", token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            if (!document.RootElement.TryGetProperty("tunnels", out var tunnels)) return null;

            foreach (var tunnel in tunnels.EnumerateArray())
            {
                if (!tunnel.TryGetProperty("proto", out var proto) || proto.GetString() != "https") continue;
                if (tunnel.TryGetProperty("public_url", out var publicUrl))
                {
                    return publicUrl.GetString();
                }
            }

            foreach (var tunnel in tunnels.EnumerateArray())
            {
                if (tunnel.TryGetProperty("public_url", out var publicUrl))
                {
                    return publicUrl.GetString();
                }
            }

            return null;
        }
    }

    internal static class QrCodeTools
    {
        public static BitmapImage CreateBitmapImage(string text)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(data);
            var bytes = qrCode.GetGraphic(12);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

    internal class NgrokTunnelQrCodeWindow : Window
    {
        public NgrokTunnelQrCodeWindow(string url, BitmapImage qrCode)
        {
            this.Title = "ngrok tunnel QR code";
            this.SizeToContent = SizeToContent.WidthAndHeight;
            this.ResizeMode = ResizeMode.NoResize;
            this.ShowInTaskbar = false;

            var panel = new StackPanel()
            {
                Margin = new Thickness(24),
                Orientation = Orientation.Vertical,
            };

            panel.Children.Add(new Image()
            {
                Source = qrCode,
                Width = 320,
                Height = 320,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            panel.Children.Add(new TextBox()
            {
                Text = url,
                IsReadOnly = true,
                Margin = new Thickness(0, 16, 0, 0),
                MinWidth = 420,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Black,
                Background = System.Windows.Media.Brushes.White,
            });

            var buttons = new StackPanel()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            var copyButton = new Button()
            {
                Content = "Copy URL",
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
            };
            copyButton.Click += (s, e) => Clipboard.SetText(url);
            buttons.Children.Add(copyButton);

            var closeButton = new Button()
            {
                Content = "Close",
                MinWidth = 96,
            };
            closeButton.Click += (s, e) => Close();
            buttons.Children.Add(closeButton);

            panel.Children.Add(buttons);
            this.Content = panel;
        }
    }
}
