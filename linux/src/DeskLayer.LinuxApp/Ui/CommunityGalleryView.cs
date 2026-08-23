// The community gallery — the Linux twin of the win CommunityGalleryView.
// Replaces the desktop overview in the centre column while "Community" is
// selected in the sidebar: a paged, sortable, searchable grid of thumbnail
// tiles. Clicking a tile opens the detail dialog (full preview, cheers,
// comments, Install). Reads are anonymous; the header offers device-code
// sign-in for cheering/commenting.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.LinuxApp.Ui;

public sealed class CommunityGalleryView : Grid
{
    private readonly Window owner;
    private readonly Func<GalleryPlugin, Task<string?>> install;
    private readonly Func<string, bool> isInstalled;

    private readonly WrapPanel tiles = new() { Margin = new Thickness(4) };
    private readonly TextBox search = new() { Width = 200, Watermark = L.T("Search") };
    private readonly TextBlock accountText = new()
    {
        Foreground = Brushes.Gray, FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
    };
    private readonly Button signIn = new() { Content = L.T("Sign in…"), IsVisible = false };
    private CommunityLogin.Session? loginSession;
    private readonly TextBlock pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Button prev = new() { Content = L.T("Previous") };
    private readonly Button next = new() { Content = L.T("Next"), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock status = new() { Margin = new Thickness(4, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly Dictionary<GallerySort, Button> sortButtons = new();

    private GallerySort sort = GallerySort.Cheers;
    private bool verifiedOnly;
    private int page = 1;
    private int pages = 1;
    private DispatcherTimer? searchDebounce;
    private int loadToken;

    public CommunityGalleryView(Window owner,
                                Func<GalleryPlugin, Task<string?>> install,
                                Func<string, bool> isInstalled)
    {
        this.owner = owner;
        this.install = install;
        this.isInstalled = isInstalled;

        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        Margin = new Thickness(12);

        // --- header: title + account, then sort chips / verified / search ---
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var titleRow = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        var accountRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        signIn.Click += (_, _) => StartSignIn();
        var refresh = new Button { Content = "⟳", Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += (_, _) => Load(page);
        accountRow.Children.Add(accountText);
        accountRow.Children.Add(signIn);
        accountRow.Children.Add(refresh);
        DockPanel.SetDock(accountRow, Dock.Right);
        titleRow.Children.Add(accountRow);
        titleRow.Children.Add(new TextBlock
        {
            Text = L.T("Community"), FontSize = 17, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(titleRow);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (value, label) in new[]
        {
            (GallerySort.Cheers, L.T("Top Cheered")),
            (GallerySort.Downloads, L.T("Most Downloaded")),
            (GallerySort.Latest, L.T("Latest")),
        })
        {
            var v = value;
            var chip = new Button { Content = label };
            chip.Click += (_, _) => { if (sort != v) { sort = v; UpdateChips(); Load(1); } };
            sortButtons[value] = chip;
            controls.Children.Add(chip);
        }
        var verifiedChip = new Button { Content = L.T("Verified only") };
        verifiedChip.Click += (_, _) => { verifiedOnly = !verifiedOnly; StyleChip(verifiedChip, verifiedOnly); Load(1); };
        controls.Children.Add(verifiedChip);
        search.Margin = new Thickness(8, 0, 0, 0);
        search.TextChanged += (_, _) =>
        {
            searchDebounce?.Stop();
            searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            searchDebounce.Tick += (_, _) => { searchDebounce!.Stop(); Load(1); };
            searchDebounce.Start();
        };
        controls.Children.Add(search);
        header.Children.Add(controls);
        header.Children.Add(status);
        SetRow(header, 0);
        Children.Add(header);

        // --- tile grid ---
        var scroller = new ScrollViewer { Content = tiles };
        SetRow(scroller, 1);
        Children.Add(scroller);

        // --- paging ---
        var paging = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        prev.Click += (_, _) => { if (page > 1) Load(page - 1); };
        next.Click += (_, _) => { if (page < pages) Load(page + 1); };
        paging.Children.Add(prev);
        paging.Children.Add(pageLabel);
        paging.Children.Add(next);
        SetRow(paging, 2);
        Children.Add(paging);

        UpdateChips();
        Load(1);
        _ = RefreshAccount();
        DetachedFromVisualTree += (_, _) => loginSession?.Cancel();
    }

    private async Task RefreshAccount()
    {
        var user = await CommunityClient.Me();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (user != null)
            {
                accountText.Text = L.T("Signed in as {0}", user.Username);
                signIn.IsVisible = false;
            }
            else
            {
                accountText.Text = "";
                signIn.IsVisible = true;
            }
        });
    }

    private void StartSignIn()
    {
        signIn.IsEnabled = false;
        loginSession?.Cancel();   // never race two poll loops
        loginSession = CommunityLogin.Begin(
            message => Dispatcher.UIThread.Post(() => { if (message != null) accountText.Text = message; }),
            _ => Dispatcher.UIThread.Post(async () => { signIn.IsEnabled = true; await RefreshAccount(); }));
    }

    private void UpdateChips()
    {
        foreach (var (value, chip) in sortButtons) StyleChip(chip, value == sort);
    }

    private static void StyleChip(Button chip, bool on)
    {
        // ClearValue, not null: a null local value blanks the theme brush.
        if (on)
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            chip.Foreground = Brushes.White;
        }
        else
        {
            chip.ClearValue(TemplatedControl.BackgroundProperty);
            chip.ClearValue(TemplatedControl.ForegroundProperty);
        }
    }

    /// Reloads a page. The load token guards against an earlier slow request
    /// landing after a newer one.
    private async void Load(int requested)
    {
        var token = ++loadToken;
        status.Text = L.T("Loading…");
        prev.IsEnabled = next.IsEnabled = false;
        var result = await CommunityClient.Gallery(sort, requested, 24,
            query: string.IsNullOrWhiteSpace(search.Text) ? null : search.Text,
            verifiedOnly: verifiedOnly);
        if (token != loadToken) return;   // superseded

        if (result == null)
        {
            status.Text = L.T("Couldn't reach the community store.");
            return;
        }
        page = result.Page;
        pages = Math.Max(result.Pages, 1);
        status.Text = result.Total == 0 ? L.T("No plugins match.") : "";
        pageLabel.Text = L.T("Page {0} of {1}", page, pages);
        prev.IsEnabled = page > 1;
        next.IsEnabled = page < pages;

        tiles.Children.Clear();
        foreach (var plugin in result.Plugins) tiles.Children.Add(BuildTile(plugin));
    }

    private Control BuildTile(GalleryPlugin plugin)
    {
        var stack = new StackPanel();

        // Thumbnail (16:10-ish) or placeholder — never the full-size preview.
        var thumbHost = new Border
        {
            Height = 124,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x90)),
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = "🧩", FontSize = 34, Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (plugin.Thumbnail is { Length: > 0 } thumbUrl)
            WebImage.Into(thumbUrl, bitmap => thumbHost.Child = new Image
            {
                Source = bitmap, Stretch = Stretch.UniformToFill,
            });
        stack.Children.Add(thumbHost);

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.Name, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (plugin.Verified)
            titleRow.Children.Add(new TextBlock
            {
                Text = "  ✓", FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        body.Children.Add(titleRow);
        if (plugin.Author is { Length: > 0 } author)
            body.Children.Add(new TextBlock
            {
                Text = L.T("by {0}", author), FontSize = 11, Foreground = Brushes.Gray,
            });
        body.Children.Add(new TextBlock
        {
            Text = $"♥ {plugin.Cheers}    ↓ {plugin.Downloads}",
            FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(body);

        var card = new Border
        {
            Width = 220,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x90)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x90)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = stack,
        };
        var tile = new Button
        {
            Margin = new Thickness(6),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = card,
        };
        tile.Click += (_, _) => OpenDetail(plugin);
        return tile;
    }

    private void OpenDetail(GalleryPlugin plugin)
    {
        var dialog = new CommunityDetailDialog(plugin, install, isInstalled);
        _ = dialog.ShowDialog(owner);
    }
}
