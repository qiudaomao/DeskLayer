// A community plugin's detail view — the Windows twin of the mac
// GalleryDetailSheet. Full-size preview, description, Install, and the social
// layer: a live cheer toggle and the forum comment thread with a compose box.
//
// Reads are anonymous. Cheering and commenting need a signed-in forum account
// (the same device-code token the publish dialog stores); when signed out,
// those controls invite sign-in rather than acting. The detail is read live
// (CommunityClient.Detail), so a fresh cheer never snaps back to a cached
// count, and every backend error — including Discourse's own, already
// localized — is shown verbatim.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.App;

public sealed class CommunityDetailDialog : Window
{
    private readonly GalleryPlugin plugin;
    private readonly string slug;
    private readonly Func<GalleryPlugin, Task<string?>> install;
    private readonly Func<string, bool> isInstalled;

    private readonly Button cheerButton;
    private readonly Button installButton;
    private readonly TextBlock installStatus;
    private readonly StackPanel commentList;
    private readonly TextBox compose;
    private readonly Button send;
    private readonly TextBlock composeHint;

    private CommunityUser? me;
    private bool cheered;
    private int cheers;

    public CommunityDetailDialog(bool dark, GalleryPlugin plugin,
                                 Func<GalleryPlugin, Task<string?>> install,
                                 Func<string, bool> isInstalled)
    {
        this.plugin = plugin;
        this.install = install;
        this.isInstalled = isInstalled;
        slug = plugin.Slug ?? plugin.Name;
        cheers = plugin.Cheers;

        Title = plugin.Name;
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources = Theme.Load(dark);
        Background = (Brush)FindResource("WindowBg");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock { Text = plugin.Name, FontSize = 18, FontWeight = FontWeights.SemiBold });
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        if (plugin.Author is { Length: > 0 } author)
            sub.Children.Add(new TextBlock { Text = L.T("by {0}", author), FontSize = 12, Foreground = (Brush)FindResource("TextSecondary") });
        if (plugin.Verified)
            sub.Children.Add(new TextBlock
            {
                Text = "   ✓ " + L.T("Verified by staff"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
            });
        panel.Children.Add(sub);

        // Full-size preview (not the thumbnail).
        if (plugin.Preview is { Length: > 0 } preview && LoadImage(preview) is { } source)
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("FieldBg"),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new Image { Source = source, Stretch = Stretch.Uniform, MaxHeight = 240, Margin = new Thickness(6) },
            });

        if (plugin.Description is { Length: > 0 } description)
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 0, 0, 12),
            });

        // Action row: Install + Cheer.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        installButton = new Button { Content = L.T("Install"), Style = (Style)FindResource("AccentButton") };
        installButton.Click += async (_, _) => await Install();
        actions.Children.Add(installButton);
        cheerButton = new Button { Margin = new Thickness(8, 0, 0, 0) };
        cheerButton.Click += async (_, _) => await ToggleCheer();
        actions.Children.Add(cheerButton);
        if (plugin.TopicUrl is { Length: > 0 } topic)
        {
            var discuss = new Button { Content = L.T("Open in Forum"), Margin = new Thickness(8, 0, 0, 0) };
            discuss.Click += (_, _) => Process.Start(new ProcessStartInfo(topic) { UseShellExecute = true });
            actions.Children.Add(discuss);
        }
        panel.Children.Add(actions);
        installStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(installStatus);
        UpdateCheerLabel();
        if (isInstalled(plugin.Name)) MarkInstalled();

        panel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorder"), Margin = new Thickness(0, 14, 0, 12) });

        // Comments.
        panel.Children.Add(new TextBlock { Text = L.T("Comments"), Style = (Style)FindResource("SectionText"), Margin = new Thickness(0, 0, 0, 8) });
        commentList = new StackPanel();
        panel.Children.Add(commentList);

        composeHint = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 4),
        };
        panel.Children.Add(composeHint);
        compose = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 54,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(compose);
        send = new Button
        {
            Content = L.T("Send"),
            Style = (Style)FindResource("AccentButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        send.Click += async (_, _) => await SendComment();
        panel.Children.Add(send);

        scroll.Content = panel;
        Content = scroll;

        Loaded += async (_, _) => await LoadSocial();
    }

    private async Task LoadSocial()
    {
        me = await CommunityClient.Me();
        var detail = await CommunityClient.Detail(slug);
        if (detail != null)
        {
            cheers = detail.Cheers;
            cheered = detail.Cheered ?? false;
        }
        UpdateCheerLabel();
        UpdateComposeState();
        await ReloadComments();
    }

    private async Task ReloadComments()
    {
        commentList.Children.Clear();
        var page = await CommunityClient.Comments(slug);
        if (page == null)
        {
            commentList.Children.Add(Muted(L.T("Couldn't load comments.")));
            return;
        }
        if (page.Comments.Count == 0)
        {
            commentList.Children.Add(Muted(L.T("No comments yet — be the first.")));
            return;
        }
        foreach (var comment in page.Comments) commentList.Children.Add(BuildComment(comment));
    }

    private UIElement BuildComment(CommunityComment comment)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("FieldBg"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = comment.Author, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        head.Children.Add(new TextBlock
        {
            Text = "  " + comment.CreatedAt.LocalDateTime.ToString("g"),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = comment.Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        box.Child = stack;
        return box;
    }

    private async Task ToggleCheer()
    {
        if (CommunityClient.Token == null)
        {
            installStatus.Text = L.T("Sign in from Share to Community… to cheer.");
            installStatus.Visibility = Visibility.Visible;
            return;
        }
        cheerButton.IsEnabled = false;
        var result = await CommunityClient.Cheer(slug);
        cheerButton.IsEnabled = true;
        if (!result.Ok)
        {
            installStatus.Text = result.Error;   // Discourse's own localized message
            installStatus.Visibility = Visibility.Visible;
            return;
        }
        cheered = result.Value!.Cheered;
        cheers = result.Value.Cheers;
        UpdateCheerLabel();
    }

    private void UpdateCheerLabel()
    {
        cheerButton.Content = (cheered ? "♥ " : "♡ ") + cheers;
        // The forum forbids liking your own post; don't offer it.
        var ownPlugin = me != null && plugin.Author != null &&
                        string.Equals(me.Username, plugin.Author, StringComparison.OrdinalIgnoreCase);
        cheerButton.IsEnabled = !ownPlugin;
        cheerButton.ToolTip = ownPlugin ? L.T("You can't cheer your own plugin.") : null;
    }

    private void UpdateComposeState()
    {
        var signedIn = CommunityClient.Token != null && me != null;
        compose.IsEnabled = signedIn;
        send.IsEnabled = signedIn;
        composeHint.Text = signedIn
            ? L.T("Commenting as {0}", me!.Username)
            : L.T("Sign in from Share to Community… to comment.");
    }

    private async Task SendComment()
    {
        send.IsEnabled = false;
        var result = await CommunityClient.PostComment(slug, compose.Text);
        send.IsEnabled = true;
        if (!result.Ok)
        {
            composeHint.Text = result.Error;   // verbatim, already localized
            return;
        }
        compose.Text = "";
        await ReloadComments();
    }

    private async Task Install()
    {
        installButton.IsEnabled = false;
        installStatus.Text = L.T("Installing…");
        installStatus.Visibility = Visibility.Visible;
        var error = await install(plugin);
        if (error != null)
        {
            installStatus.Text = error;
            installButton.IsEnabled = true;
            return;
        }
        MarkInstalled();
    }

    private void MarkInstalled()
    {
        installButton.IsEnabled = false;
        installButton.Content = "✓ " + L.T("Installed");
        installStatus.Visibility = Visibility.Collapsed;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindResource("TextSecondary"),
    };

    private static BitmapImage? LoadImage(string url)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(url, UriKind.Absolute);
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is UriFormatException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
