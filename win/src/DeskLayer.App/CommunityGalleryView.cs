// The community gallery — the Windows twin of the mac CommunityGalleryView.
// Replaces the desktop overview in the centre column while "Community" is
// selected in the sidebar: a paged, sortable, searchable grid of every
// published plugin, with thumbnail tiles. Clicking a tile opens a detail
// dialog with the full preview, cheers, comments, and Install.
//
// The grid loads the small `thumbnail` URL, never the full-size preview; a
// missing thumbnail shows a placeholder (older plugins have none). All reads
// are anonymous; cheering and commenting happen in the detail dialog and need
// a signed-in forum account.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.App;

public sealed class CommunityGalleryView : Grid
{
    private readonly bool dark;
    private readonly Window owner;
    private readonly Func<GalleryPlugin, Task<string?>> install;
    private readonly Func<string, bool> isInstalled;

    private readonly WrapPanel tiles = new() { Margin = new Thickness(4) };
    private readonly TextBox search = new() { Width = 200 };
    private TextBlock accountText = null!;
    private Button signIn = null!;
    private CommunityLogin.Session? loginSession;
    private readonly TextBlock pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button prev;
    private readonly Button next;
    private readonly TextBlock status = new() { Margin = new Thickness(4, 20, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<GallerySort, Button> sortButtons = new();

    private GallerySort sort = GallerySort.Cheers;
    private bool verifiedOnly;
    private int page = 1;
    private int pages = 1;
    private System.Windows.Threading.DispatcherTimer? searchDebounce;
    private int loadToken;

    private readonly Action<bool> refreshStores;

    public CommunityGalleryView(bool dark, Window owner,
                                Func<GalleryPlugin, Task<string?>> install,
                                Func<string, bool> isInstalled,
                                Action<bool>? refreshStoreCatalogs = null)
    {
        refreshStores = force => refreshStoreCatalogs?.Invoke(force);
        this.dark = dark;
        this.owner = owner;
        this.install = install;
        this.isInstalled = isInstalled;

        // Own copy of the theme, like the dialogs: the view is built before it
        // is attached to the Manager's visual tree, so FindResource in this
        // constructor would otherwise miss every window-level brush.
        Resources = Theme.Load(dark);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // paging
        Margin = new Thickness(12);

        // --- header: title, sort chips, verified chip, search ---
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        // Title row: heading on the left, account + refresh on the right.
        var titleRow = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        var accountRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        accountText = new TextBlock
        {
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        signIn = new Button { Content = L.T("Sign in…"), Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed };
        signIn.Click += (_, _) => StartSignIn();
        var refresh = new Button { Content = "⟳", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Refresh") };
        refresh.Click += (_, _) => { Load(page); refreshStores(true); };
        accountRow.Children.Add(accountText);
        accountRow.Children.Add(signIn);
        accountRow.Children.Add(refresh);
        DockPanel.SetDock(accountRow, Dock.Right);
        titleRow.Children.Add(accountRow);
        titleRow.Children.Add(new TextBlock
        {
            Text = L.T("Community"),
            Style = (Style)FindResource("SectionText"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(titleRow);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label) in new[]
        {
            (GallerySort.Cheers, L.T("Top Cheered")),
            (GallerySort.Downloads, L.T("Most Downloaded")),
            (GallerySort.Latest, L.T("Latest")),
        })
        {
            var chip = Chip(label, () => { if (sort != value) { sort = value; UpdateChips(); Load(1); } });
            sortButtons[value] = chip;
            controls.Children.Add(chip);
        }
        var verifiedChip = Chip(L.T("Verified only"), null);
        verifiedChip.Click += (_, _) => { verifiedOnly = !verifiedOnly; StyleChip(verifiedChip, verifiedOnly); Load(1); };
        controls.Children.Add(verifiedChip);

        search.Margin = new Thickness(12, 0, 0, 0);
        search.VerticalContentAlignment = VerticalAlignment.Center;
        search.TextChanged += (_, _) =>
        {
            // Debounce: reload 400ms after the last keystroke, not per key.
            searchDebounce?.Stop();
            searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            searchDebounce.Tick += (_, _) => { searchDebounce!.Stop(); Load(1); };
            searchDebounce.Start();
        };
        controls.Children.Add(new TextBlock
        {
            Text = "🔎", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0), Opacity = 0.6,
        });
        controls.Children.Add(search);
        header.Children.Add(controls);
        header.Children.Add(status);
        SetRow(header, 0);
        Children.Add(header);

        // --- grid ---
        var scroller = new ScrollViewer
        {
            Content = tiles,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        SetRow(scroller, 1);
        Children.Add(scroller);

        // --- paging ---
        var paging = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        prev = new Button { Content = L.T("Previous") };
        prev.Click += (_, _) => { if (page > 1) Load(page - 1); };
        next = new Button { Content = L.T("Next"), Margin = new Thickness(8, 0, 0, 0) };
        next.Click += (_, _) => { if (page < pages) Load(page + 1); };
        paging.Children.Add(prev);
        paging.Children.Add(pageLabel);
        paging.Children.Add(next);
        SetRow(paging, 2);
        Children.Add(paging);

        UpdateChips();
        Loaded += async (_, _) =>
        {
            if (tiles.Children.Count == 0) Load(1);
            // Keep the registered community catalog fresh too — the
            // inspector's update check compares against it (stale-only, so
            // this is free when the 24h cache still holds).
            refreshStores(false);
            await RefreshAccount();
        };
        Unloaded += (_, _) => loginSession?.Cancel();
    }

    /// Shows who's signed in, or offers sign-in. Called on load and after a
    /// sign-in completes.
    private async Task RefreshAccount()
    {
        var user = await CommunityClient.Me();
        if (user != null)
        {
            accountText.Text = L.T("Signed in as {0}", user.Username);
            signIn.Visibility = Visibility.Collapsed;
        }
        else
        {
            accountText.Text = "";
            signIn.Visibility = Visibility.Visible;
        }
    }

    private void StartSignIn()
    {
        signIn.IsEnabled = false;
        loginSession?.Cancel();   // never race two poll loops
        loginSession = CommunityLogin.Begin(
            status => Dispatcher.Invoke(() => { if (status != null) accountText.Text = status; }),
            _ => Dispatcher.Invoke(async () => { signIn.IsEnabled = true; await RefreshAccount(); }));
    }

    private Button Chip(string label, Action? onClick)
    {
        var chip = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        if (onClick != null) chip.Click += (_, _) => onClick();
        return chip;
    }

    private void UpdateChips()
    {
        foreach (var (value, chip) in sortButtons) StyleChip(chip, value == sort);
    }

    private void StyleChip(Button chip, bool on)
    {
        chip.Background = on ? (Brush)FindResource("Accent") : (Brush)FindResource("FieldBg");
        chip.Foreground = on ? Brushes.White : (Brush)FindResource("TextPrimary");
    }

    /// Reloads a page. A load token guards against an earlier slow request
    /// landing after a newer one (stale search results overwriting fresh).
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

    private UIElement BuildTile(GalleryPlugin plugin)
    {
        var card = new Border
        {
            Width = 220,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var stack = new StackPanel();

        // Thumbnail (16:10-ish) or placeholder.
        var thumbHost = new Border
        {
            Height = 124,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = (Brush)FindResource("FieldBg"),
            ClipToBounds = true,
        };
        if (plugin.Thumbnail is { Length: > 0 } thumbUrl && LoadImage(thumbUrl) is { } source)
            thumbHost.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
        else
            thumbHost.Child = new TextBlock
            {
                Text = "🧩",
                FontSize = 34,
                Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        stack.Children.Add(thumbHost);

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (plugin.Verified)
            titleRow.Children.Add(new TextBlock
            {
                Text = "  ✓",
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        body.Children.Add(titleRow);
        if (plugin.Author is { Length: > 0 } author)
            body.Children.Add(new TextBlock
            {
                Text = L.T("by {0}", author),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
            });
        body.Children.Add(new TextBlock
        {
            Text = $"♥ {plugin.Cheers}    ↓ {plugin.Downloads}",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(body);
        card.Child = stack;

        // A real Button, presented as the card: reachable by keyboard, screen
        // readers, and UI automation — a Border with a mouse handler is none
        // of those. The template IS the card, so nothing changes visually.
        // The template must be complete before it is assigned: WPF seals a
        // template on assignment, and mutating VisualTree afterwards throws.
        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        };
        var tile = new Button
        {
            Margin = new Thickness(6),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = template,
            Content = card,
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, plugin.Name);
        tile.Click += (_, _) => OpenDetail(plugin);
        return tile;
    }

    /// Loads a remote image without blocking, cached, tolerant of failure
    /// (a broken URL falls back to the tile's placeholder).
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

    private void OpenDetail(GalleryPlugin plugin)
    {
        var dialog = new CommunityDetailDialog(dark, plugin, install, isInstalled) { Owner = owner };
        dialog.ShowDialog();
    }
}
