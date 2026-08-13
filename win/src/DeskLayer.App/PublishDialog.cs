// "Share to Community…" — publish an installed plugin to the community store
// (store.byteplayer.app) from the inspector.
//
// Sign-in is the store's device-code flow: the dialog opens the forum login
// in the default browser and polls for the token, so the app needs no URL
// scheme and no embedded web view. The token is stored once (DPAPI) and
// reused; publishing then creates a forum showcase topic where people
// comment and cheer, and the plugin appears in the community catalog.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.App;

public sealed class PublishDialog : Window
{
    private readonly string source;
    private readonly TextBox name;
    private readonly TextBox version;
    private readonly TextBox description;
    private readonly TextBlock accountText;
    private readonly Button signIn;
    private readonly Button publish;
    private readonly TextBlock status;
    private readonly Button viewTopic;
    private readonly string? permissions;
    private string? topicUrl;
    private CommunityLogin.Session? loginSession;
    private readonly Func<Task<byte[]?>> capture;
    private readonly Func<bool> hasInstance;
    private readonly Action addInstance;
    private readonly Image previewImage;
    private readonly TextBlock previewStatus;
    private readonly Button retake;
    private readonly Button addAndCapture;
    private byte[]? previewBytes;

    public PublishDialog(bool dark, string pluginId, string pluginSource,
                         DeskLayer.Core.PluginMetadata.PluginInfo info, IReadOnlyCollection<string>? grantedPermissions,
                         Func<Task<byte[]?>> capturePreview, Func<bool> hasRunningInstance, Action addInstanceToDesktop)
    {
        capture = capturePreview;
        hasInstance = hasRunningInstance;
        addInstance = addInstanceToDesktop;
        source = pluginSource;
        permissions = grantedPermissions is { Count: > 0 } ? string.Join(", ", grantedPermissions.OrderBy(p => p)) : null;

        Title = L.T("Share to Community");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources = Theme.Load(dark);
        Background = (Brush)FindResource("WindowBg");
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Share to Community"),
            Style = (Style)FindResource("SectionText"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Publishes to the community store and opens a forum topic where people can comment and cheer."),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Account row: resolved async after the window shows.
        accountText = new TextBlock
        {
            Text = L.T("Checking sign-in…"),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        signIn = new Button
        {
            Content = L.T("Sign in with the forum…"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(8, 0, 0, 0),
        };
        signIn.Click += (_, _) => SignIn();
        var accountRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        accountRow.Children.Add(accountText);
        accountRow.Children.Add(signIn);
        panel.Children.Add(accountRow);

        TextBlock Caption(string text) => new()
        {
            Text = text,
            FontSize = 10,
            Foreground = (Brush)FindResource("CaptionText"),
            Margin = new Thickness(2, 8, 0, 3),
        };

        panel.Children.Add(Caption(L.T("Name")));
        name = new TextBox { Text = pluginId };
        panel.Children.Add(name);
        panel.Children.Add(Caption(L.T("Version")));
        version = new TextBox { Text = info.Version ?? "1.0.0" };
        panel.Children.Add(version);
        panel.Children.Add(Caption(L.T("Description")));
        description = new TextBox
        {
            Text = info.Description ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(description);
        if (permissions != null)
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Declared permissions ({0}) are listed on the store page.", permissions),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0),
            });

        panel.Children.Add(Caption(L.T("Preview")));
        previewImage = new Image
        {
            MaxHeight = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(previewImage);
        previewStatus = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(previewStatus);
        var previewButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        retake = new Button { Content = L.T("Capture Again"), Visibility = Visibility.Collapsed };
        retake.Click += async (_, _) => await Capture();
        addAndCapture = new Button { Content = L.T("Add to Desktop & Capture"), Visibility = Visibility.Collapsed };
        addAndCapture.Click += async (_, _) =>
        {
            addAndCapture.IsEnabled = false;
            addInstance();
            await Capture();
            addAndCapture.IsEnabled = true;
        };
        previewButtons.Children.Add(retake);
        previewButtons.Children.Add(addAndCapture);
        panel.Children.Add(previewButtons);

        status = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(status);

        viewTopic = new Button
        {
            Content = L.T("View Discussion"),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        viewTopic.Click += (_, _) =>
        {
            if (topicUrl != null)
                Process.Start(new ProcessStartInfo(topicUrl) { UseShellExecute = true });
        };
        panel.Children.Add(viewTopic);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var close = new Button { Content = L.T("Close") };
        close.Click += (_, _) => Close();
        publish = new Button
        {
            Content = L.T("Publish"),
            Style = (Style)FindResource("AccentButton"),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = false,
        };
        publish.Click += async (_, _) => await PublishNow();
        buttons.Children.Add(close);
        buttons.Children.Add(publish);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += async (_, _) =>
        {
            var account = RefreshAccount();
            if (hasInstance()) await Capture();
            else
            {
                previewStatus.Text = L.T("Add an instance to your desktop to capture a preview.");
                addAndCapture.Visibility = Visibility.Visible;
            }
            await account;
        };
        Closed += (_, _) => loginSession?.Cancel();
    }

    private async Task RefreshAccount()
    {
        var user = await CommunityClient.Me();
        if (user != null)
        {
            accountText.Text = L.T("Signed in as {0}", user.Username);
            signIn.Visibility = Visibility.Collapsed;
            publish.IsEnabled = true;
        }
        else
        {
            accountText.Text = L.T("Publishing uses your forum account.");
            signIn.Visibility = Visibility.Visible;
            publish.IsEnabled = false;
        }
    }

    private void SignIn()
    {
        signIn.IsEnabled = false;
        loginSession?.Cancel();   // never race two poll loops
        loginSession = CommunityLogin.Begin(
            status => Dispatcher.Invoke(() => Show(status)),
            _ => Dispatcher.Invoke(async () => { signIn.IsEnabled = true; await RefreshAccount(); }));
    }

    private async Task PublishNow()
    {
        var proposedName = name.Text.Trim();
        var proposedVersion = version.Text.Trim();
        if (proposedName.Length == 0 || proposedVersion.Length == 0)
        {
            Show(L.T("Name and version are required."));
            return;
        }
        publish.IsEnabled = false;
        Show(L.T("Publishing…"));
        var result = await CommunityClient.Publish(new PublishRequest(
            proposedName, proposedVersion,
            description.Text.Trim().Length == 0 ? null : description.Text.Trim(),
            source, permissions,
            PreviewPngBase64: previewBytes is { Length: > 0 and <= 2 * 1024 * 1024 }
                ? Convert.ToBase64String(previewBytes) : null,
            ThumbnailPngBase64: previewBytes is { Length: > 0 } && Thumbnail(previewBytes) is { } thumb
                ? Convert.ToBase64String(thumb) : null));
        if (result.Error != null)
        {
            Show(result.Error);
            publish.IsEnabled = true;
            await RefreshAccount();   // a 401 cleared the token; show sign-in again
            return;
        }
        topicUrl = result.TopicUrl;
        Show(L.T("Published! People can now install it from the Community Store."));
        viewTopic.Visibility = topicUrl != null ? Visibility.Visible : Visibility.Collapsed;
        // Publishing the same bytes again would only 409; leave the button off.
    }

    /// Captures the running card via the engine (which waits for a fresh
    /// frame) and shows the result. The publish payload uses the same bytes.
    private async Task Capture()
    {
        retake.Visibility = Visibility.Collapsed;
        previewStatus.Text = L.T("Capturing…");
        var bytes = await capture();
        if (bytes is { Length: > 0 })
        {
            previewBytes = bytes;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new System.IO.MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            previewImage.Source = image;
            previewImage.Visibility = Visibility.Visible;
            previewStatus.Text = L.T("This is what the store page will show.");
            addAndCapture.Visibility = Visibility.Collapsed;
            retake.Visibility = Visibility.Visible;
        }
        else
        {
            previewStatus.Text = hasInstance()
                ? L.T("Couldn't capture this plugin — it will publish without a preview.")
                : L.T("Add an instance to your desktop to capture a preview.");
            if (!hasInstance()) addAndCapture.Visibility = Visibility.Visible;
            else retake.Visibility = Visibility.Visible;
        }
    }

    /// A ~480px-wide PNG of the preview for the gallery grid. Null if the
    /// source can't be decoded; the store falls back to a placeholder.
    private static byte[]? Thumbnail(byte[] pngBytes)
    {
        try
        {
            var source = new System.Windows.Media.Imaging.BitmapImage();
            using (var input = new System.IO.MemoryStream(pngBytes))
            {
                source.BeginInit();
                source.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                source.StreamSource = input;
                source.EndInit();
            }
            const double maxWidth = 480;
            if (source.PixelWidth <= maxWidth)
            {
                // Already small enough — the preview bytes are the thumbnail.
                return pngBytes.Length <= 256 * 1024 ? pngBytes : null;
            }
            var scale = maxWidth / source.PixelWidth;
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                source, new System.Windows.Media.ScaleTransform(scale, scale));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
            using var output = new System.IO.MemoryStream();
            encoder.Save(output);
            var bytes = output.ToArray();
            return bytes.Length <= 256 * 1024 ? bytes : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }

    private void Show(string? text)
    {
        status.Text = text ?? "";
        status.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
