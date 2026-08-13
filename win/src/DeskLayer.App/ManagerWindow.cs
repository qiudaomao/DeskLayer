// The Manager — a single macOS-style view (mirroring ManagerRootView.swift):
// left sidebar with the installed-plugin library and store categories (each
// row with an inline add/install button, a "+" menu and folder button at the
// bottom), center desktop preview drawn over the user's real wallpaper, and
// a right inspector whose content follows the selection — placed item,
// installed plugin, store, or store-listed plugin. Built in code against
// Theme.Load(); edits go through the LayoutStore (reconciled live).

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLayer.Core;
using DeskLayer.Core.Js;
using DeskLayer.Core.Llm;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

public sealed class ManagerWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly LayoutStore store;
    private readonly PluginRegistry registry;
    private readonly PluginStoreRegistry storeRegistry;
    private readonly PluginUpdater updater;
    private readonly System.Drawing.Rectangle screenBounds;
    private readonly PluginAuthorSession author;

    private readonly StackPanel sidebarPanel = new();
    private readonly ContextMenu plusMenu = new();
    private readonly Canvas overview = new() { ClipToBounds = true };
    /// The centre column swaps between the desktop overview and the community
    /// gallery; this holds whichever is showing.
    private readonly ContentControl centerHost = new();
    private UIElement? overviewCard;
    private bool showingGallery;
    private readonly StackPanel inspector = new() { Margin = new Thickness(14) };

    // One selection across the whole window; the inspector shows whichever
    // kind is set (they're mutually exclusive — the mac ManagerSelection).
    private Guid? selectedItemId;
    private string? selectedPluginId;
    private string? selectedStoreUrl;
    private (string StoreUrl, string Name)? selectedStorePlugin;

    /// Collapsed sidebar groups, remembered for the session.
    private readonly HashSet<string> collapsed = new();

    /// Remembered across reopen (theme toggle). Seeded from the Windows theme.
    public static bool PreferDark = Theme.SystemPrefersDark();
    private readonly bool dark = Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_DARK") is { } d ? d == "1" : PreferDark;
    private readonly Action? reopenToggled;

    /// Captures a running plugin's rendered card as PNG (wired to the
    /// engine by Program); null when the engine isn't available.
    private readonly Func<string, Task<byte[]?>>? capturePreview;

    public ManagerWindow(LayoutStore store, PluginRegistry registry,
                         PluginStoreRegistry storeRegistry, PluginUpdater updater,
                         System.Drawing.Rectangle screenBounds, Action? reopenToggled = null,
                         Func<string, Task<byte[]?>>? capturePreview = null)
    {
        this.capturePreview = capturePreview;
        this.store = store;
        this.registry = registry;
        this.storeRegistry = storeRegistry;
        this.updater = updater;
        this.screenBounds = screenBounds;
        this.reopenToggled = reopenToggled;
        author = new PluginAuthorSession(registry, storeRegistry, _ => { });

        Title = "DeskLayer";
        Width = 1080;
        Height = 640;
        Resources = Theme.Load(dark);
        Background = (Brush)FindResource("WindowBg");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");

        // Title bar follows the theme (DWMWA_USE_IMMERSIVE_DARK_MODE).
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var on = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        };

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.Children.Add(Place(BuildSidebar(), 0));
        overviewCard = BuildOverview();
        centerHost.Content = overviewCard;
        grid.Children.Add(Place(centerHost, 1));
        grid.Children.Add(Place(Card(new ScrollViewer
        {
            Content = inspector,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        }), 2));

        // No manual theme toggle: the Manager follows the Windows light/dark
        // setting (Theme.SystemPrefersDark, read on open). It used to float a
        // toggle over the centre card's top-right corner, which collided with
        // the Community pane's refresh button — and an app that tracks the
        // system theme doesn't need it.
        Content = grid;

        store.OnChange += RefreshFromStore;
        registry.DidChange += RegistryChanged;
        storeRegistry.DidChange += StoresChanged;
        Loaded += (_, _) => { RefreshSidebar(); RefreshOverview(); RefreshInspector(); };
        Closed += (_, _) =>
        {
            store.OnChange -= RefreshFromStore;
            registry.DidChange -= RegistryChanged;
            storeRegistry.DidChange -= StoresChanged;
        };

        // Debug: render the Manager to a PNG (proves what WPF draws even when
        // a headless screen-capture can't composite the window). Optional
        // pre-dump selection hooks exercise the inspector's detail views;
        // DESKLAYER_WINDOW_POS pins the window for synthetic-click tests, and
        // DESKLAYER_DUMP_AFTER re-dumps N seconds later so a scripted
        // interaction's result can be captured.
        if (Environment.GetEnvironmentVariable("DESKLAYER_WINDOW_POS") is { Length: > 0 } pos &&
            pos.Split(',') is { Length: 2 } parts &&
            double.TryParse(parts[0], out var left) && double.TryParse(parts[1], out var top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        var dump = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_MANAGER");
        if (!string.IsNullOrEmpty(dump))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_ITEM") is { Length: > 0 } itemSel
                        && store.Layout.Items.FirstOrDefault(i => i.PluginId == itemSel) is { } itemMatch)
                        SelectItem(itemMatch.Id);
                    else if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_PLUGIN") is { Length: > 0 } pluginSel)
                        SelectPlugin(pluginSel);
                    else if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_STORE") is { Length: > 0 } storeSel
                             && storeRegistry.Stores.FirstOrDefault(s => s.DisplayName == storeSel) is { } entry)
                        SelectStore(entry.Url);
                    // "<store display name>::<plugin name>" opens a store
                    // plugin's inspector — the only way to reach the community
                    // cheer/verified/discuss UI without clicking the sidebar.
                    else if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_STORE_PLUGIN") is { Length: > 0 } spSel
                             && spSel.Split("::") is { Length: 2 } parts
                             && storeRegistry.Stores.FirstOrDefault(s => s.DisplayName == parts[0]) is { } spEntry)
                        SelectStorePlugin(spEntry.Url, parts[1]);
                    else if (Environment.GetEnvironmentVariable("DESKLAYER_SHOW_GALLERY") == "1")
                        ShowGallery();
                    UpdateLayout();
                    DumpToPng(dump);
                    if (int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_DUMP_AFTER"), out var seconds) && seconds > 0)
                    {
                        var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                        again.Tick += (_, _) => { again.Stop(); UpdateLayout(); DumpToPng(dump); };
                        again.Start();
                    }
                }));
        // Debug: open the first color well in the inspector and dump the
        // picker (a Popup is its own window, invisible to a Manager dump).
        var dumpPicker = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_PICKER");
        if (!string.IsNullOrEmpty(dumpPicker))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_ITEM") is { Length: > 0 } sel
                        && store.Layout.Items.FirstOrDefault(i => i.PluginId == sel) is { } match)
                        SelectItem(match.Id);
                    UpdateLayout();
                    if (inspector.Children.OfType<ColorField>().FirstOrDefault() is not { } field) return;
                    var popup = field.OpenPicker();
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (popup.Child is FrameworkElement card) DumpElementToPng(card, dumpPicker);
                        popup.IsOpen = false;
                    }));
                }));

        // Debug: the ＋ menu's labels, as text — a ContextMenu is its own
        // window and never appears in a Manager dump.
        var dumpMenu = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_MENU");
        if (!string.IsNullOrEmpty(dumpMenu))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    RebuildPlusMenu(plusMenu);
                    var lines = plusMenu.Items.OfType<MenuItem>()
                        .Select(i => (i.IsEnabled ? "" : "(disabled) ") + i.Header);
                    File.WriteAllText(dumpMenu, string.Join("\n", lines) + "\n");
                }));

        // Debug: the inspector column on its own, at full height — the panel
        // scrolls, so anything below the fold never reaches a window dump.
        var dumpInspector = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_INSPECTOR");
        if (!string.IsNullOrEmpty(dumpInspector))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_ITEM") is { Length: > 0 } sel
                        && store.Layout.Items.FirstOrDefault(i => i.PluginId == sel) is { } match)
                        SelectItem(match.Id);
                    UpdateLayout();
                    DumpElementToPng(inspector, dumpInspector);
                }));

        var dumpCreate = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_CREATE");
        if (!string.IsNullOrEmpty(dumpCreate))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    var dialog = new CreatePluginDialog(author, registry, dark, selectedPluginId) { Owner = this };
                    dialog.Show();
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        dialog.UpdateLayout();
                        DumpElementToPng(dialog, dumpCreate);
                        dialog.Close();
                    }));
                }));
    }

    private static void DumpElementToPng(FrameworkElement window, string path)
    {
        try
        {
            var w = (int)Math.Max(window.ActualWidth, 100);
            var h = (int)Math.Max(window.ActualHeight, 100);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { /* best effort */ }
    }

    /// Device pixels per point on the primary display (2.0 at 200% scale) —
    /// the frame editor, default sizes, and resize limits all speak points.
    private double DpiScale
    {
        get
        {
            var dip = SystemParameters.PrimaryScreenWidth;
            return dip > 0 ? Math.Clamp(screenBounds.Width / dip, 0.5, 4.0) : 1.0;
        }
    }

    private void RegistryChanged() => Dispatcher.BeginInvoke(() =>
    {
        infoCache.Clear();
        RefreshSidebar();
        RefreshInspector();
    });

    /// Declared metadata (size, resize policy, limits) per plugin. ExtractInfo
    /// boots a throwaway Jint engine, too heavy to re-run on every overview
    /// refresh — cached until the registry rescans.
    private readonly Dictionary<string, PluginMetadata.PluginInfo> infoCache = new();

    private PluginMetadata.PluginInfo InfoFor(string pluginId)
    {
        if (infoCache.TryGetValue(pluginId, out var cached)) return cached;
        var info = new PluginMetadata.PluginInfo(null, null, null, null, null, null);
        if (registry.Plugin(pluginId) is { } plugin)
        {
            try { info = PluginMetadata.ExtractInfo(File.ReadAllText(plugin.SourcePath)); }
            catch (IOException) { }
        }
        infoCache[pluginId] = info;
        return info;
    }
    private void StoresChanged() => Dispatcher.BeginInvoke(() => { RefreshSidebar(); RefreshInspector(); });
    private void RefreshFromStore() => Dispatcher.BeginInvoke(() => { RefreshOverview(); RefreshInspector(); });

    private void DumpToPng(string path)
    {
        try
        {
            UpdateLayout();
            var w = (int)Math.Max(ActualWidth, Width);
            var h = (int)Math.Max(ActualHeight, Height);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { /* best effort */ }
    }

    // ---- selection (mutually exclusive, mac ManagerSelection) ----

    private void SelectItem(Guid id)
    {
        ExitGallery();
        selectedItemId = id;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshOverview();
        RefreshInspector();
    }

    private void SelectPlugin(string id)
    {
        ExitGallery();
        selectedItemId = null;
        selectedPluginId = id;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshOverview();
        RefreshInspector();
    }

    private void SelectStore(string url)
    {
        ExitGallery();
        selectedItemId = null;
        selectedPluginId = null;
        selectedStoreUrl = url;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshInspector();
    }

    private void SelectStorePlugin(string storeUrl, string name)
    {
        ExitGallery();
        selectedItemId = null;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = (storeUrl, name);
        RefreshSidebar();
        RefreshInspector();
    }

    // ---- shared styled helpers ----

    private Border Card(UIElement child) => new()
    {
        Style = (Style)FindResource("Card"),
        Child = child,
    };
    private TextBlock Section(string text) => new() { Style = (Style)FindResource("SectionText"), Text = text };
    private TextBlock Caption(string text) => new() { Style = (Style)FindResource("CaptionText"), Text = text };

    private static UIElement Place(UIElement child, int column)
    {
        var wrap = new Grid { Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0) };
        wrap.Children.Add(child);
        Grid.SetColumn(wrap, column);
        return wrap;
    }

    private TextBlock Glyph(string glyph, double size = 13) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)FindResource("TextSecondary"),
    };

    /// A borderless glyph button (the mac's .borderless icon buttons).
    private Border IconButton(string glyph, string tooltip, Action onClick, double size = 13)
    {
        var icon = Glyph(glyph, size);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var host = new Border
        {
            Width = 24,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = icon,
        };
        host.MouseEnter += (_, _) => host.Background = (Brush)FindResource("Hover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return host;
    }

    /// A mac LabeledContent row: secondary label left, value right-aligned.
    private Grid LabeledRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new TextBlock { Text = label, Foreground = (Brush)FindResource("TextSecondary"), FontSize = 12 };
        var right = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    private Border Divider() => new()
    {
        Height = 1,
        Background = (Brush)FindResource("CardBorder"),
        Margin = new Thickness(0, 10, 0, 10),
    };

    // ======================================================================
    //  Sidebar (library + stores + bottom "+" bar)
    // ======================================================================

    private UIElement BuildSidebar()
    {
        var dock = new DockPanel();

        // Bottom bar: "+" menu (add / create / stores) and the folder button.
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 6) };
        var menu = plusMenu;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        var plus = IconButton("", L.T("Add a plugin or a plugin store"),
            () => { RebuildPlusMenu(menu); menu.IsOpen = true; });
        menu.PlacementTarget = plus;
        bottom.Children.Add(plus);
        bottom.Children.Add(IconButton("", L.T("Open plugins folder"), OpenPluginsFolder));

        var bottomWrap = new StackPanel();
        bottomWrap.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorder") });
        bottomWrap.Children.Add(bottom);
        DockPanel.SetDock(bottomWrap, Dock.Bottom);
        dock.Children.Add(bottomWrap);

        sidebarPanel.Margin = new Thickness(8, 10, 8, 6);
        dock.Children.Add(new ScrollViewer
        {
            Content = sidebarPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        return Card(dock);
    }

    private void RebuildPlusMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var import = new MenuItem { Header = L.T("Add Plugin…") };
        import.Click += (_, _) => ImportPlugins();
        menu.Items.Add(import);
        var create = new MenuItem { Header = L.T("Create Plugin…") };
        create.Click += (_, _) => OpenCreatePlugin();
        menu.Items.Add(create);
        menu.Items.Add(new Separator());
        // The app ships no plugins, so the first thing a new user needs is a
        // store — offer ours by name.
        foreach (var preset in PresetStore.All)
        {
            var added = storeRegistry.Stores.Any(s => s.Url == preset.Url);
            var item = new MenuItem
            {
                Header = added ? L.T("{0} (added)", preset.Name) : L.T("Add {0}", preset.Name),
                IsEnabled = !added,
            };
            var p = preset;
            item.Click += async (_, _) => await storeRegistry.AddStore(p.Url, p.Mirrors);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var addStore = new MenuItem { Header = L.T("Add Plugin Store…") };
        addStore.Click += (_, _) => OpenAddStoreDialog();
        menu.Items.Add(addStore);
    }

    /// The sidebar row that opens the community gallery in the centre column.
    private UIElement CommunityEntry()
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Background = showingGallery ? (Brush)FindResource("Accent") : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✦  " + L.T("Community"),
                FontWeight = FontWeights.SemiBold,
                Foreground = showingGallery ? Brushes.White : (Brush)FindResource("TextPrimary"),
            },
        };
        row.MouseLeftButtonUp += (_, _) => ShowGallery();
        return row;
    }

    private void ShowGallery()
    {
        if (showingGallery) return;
        showingGallery = true;
        centerHost.Content = new CommunityGalleryView(dark, this, InstallFromGallery,
            name => registry.Plugin(name) != null,
            refreshStoreCatalogs: force => _ = storeRegistry.RefreshAll(force));
        RefreshSidebar();   // repaint the entry as selected
    }

    /// Leaves the gallery, restoring the desktop overview (called when the
    /// user selects anything in the library).
    private void ExitGallery()
    {
        if (!showingGallery) return;
        showingGallery = false;
        centerHost.Content = overviewCard;
        RefreshOverview();
    }

    /// Installs a gallery plugin by adapting it to the store install path.
    ///
    /// The community store entry is registered first (hidden from the sidebar
    /// — browsing lives in the pane) and the recorded origin is that entry's
    /// display name. Both matter for updates: StoreSourceFor finds a plugin's
    /// update source by scanning registered stores' catalogs and matching the
    /// origin, so a gallery install without the entry had no update path at
    /// all ("No update URL declared").
    private async Task<string?> InstallFromGallery(DeskLayer.Core.Community.GalleryPlugin plugin)
    {
        var communityUrl = DeskLayer.Core.Community.CommunityClient.CatalogUrl;
        if (storeRegistry.Stores.All(s => s.Url != communityUrl))
            await storeRegistry.AddStore(communityUrl);
        var entry = storeRegistry.Stores.FirstOrDefault(s => s.Url == communityUrl);

        var storePlugin = new StorePlugin
        {
            Name = plugin.Name,
            Description = plugin.Description,
            Url = plugin.Url,
            Version = plugin.Version,
            Author = plugin.Author,
        };
        var error = await storeRegistry.Install(storePlugin,
            entry?.DisplayName ?? "DeskLayer Community", PluginRegistry.PluginsDirectory);
        registry.Rescan();
        return error;
    }

    private void RefreshSidebar()
    {
        sidebarPanel.Children.Clear();

        // Community — browse everything published; swaps the centre column
        // for the gallery (mac parity: a sidebar entry, not a store category).
        sidebarPanel.Children.Add(CommunityEntry());

        // Installed — everything on disk, whichever store it came from.
        if (registry.Plugins.Count > 0)
        {
            sidebarPanel.Children.Add(GroupHeader(L.T("Installed"), registry.Plugins.Count, "installed", trailing: null));
            if (!collapsed.Contains("installed"))
                foreach (var plugin in registry.Plugins)
                {
                    var id = plugin.Id;
                    sidebarPanel.Children.Add(SidebarRow(
                        glyph: "",
                        title: id,
                        isSelected: selectedPluginId == id,
                        onSelect: () => SelectPlugin(id),
                        trailing: IconButton("", L.T("Add {0} to the desktop", id), () => AddToDesktop(id))));
                }
        }

        // One category per store, mac StoreSection-style. The community store
        // is browsed through the dedicated Community pane, so it isn't also
        // listed here as a category (avoids two ways in for the same thing).
        foreach (var entry in storeRegistry.Stores)
        {
            if (entry.Url == DeskLayer.Core.Community.CommunityClient.CatalogUrl) continue;
            var url = entry.Url;
            var key = "store:" + url;
            var refresh = IconButton("", L.T("Refresh {0}", entry.DisplayName),
                async () => await storeRegistry.RefreshAll(true));
            sidebarPanel.Children.Add(GroupHeader(entry.DisplayName, entry.Catalog?.Plugins.Count ?? 0, key,
                trailing: refresh, onSelect: () => SelectStore(url), isSelected: selectedStoreUrl == url));
            if (collapsed.Contains(key)) continue;

            if (entry.Catalog?.Plugins is { Count: > 0 } plugins)
            {
                foreach (var plugin in plugins)
                {
                    var name = plugin.Name;
                    var installed = registry.Plugin(name) != null;
                    var p = plugin;
                    var storeName = entry.DisplayName;
                    Border trailing = installed
                        ? IconButton("", L.T("Add {0} to the desktop", name), () => AddToDesktop(name))
                        : IconButton("", L.T("Install {0}", name), async () =>
                        {
                            // Installing straight from the row: the detail
                            // pane says nothing extra for a plugin the user
                            // already decided to install.
                            await storeRegistry.Install(p, storeName, PluginRegistry.PluginsDirectory);
                            registry.Rescan();
                        });
                    sidebarPanel.Children.Add(SidebarRow(
                        glyph: installed ? "" : "",
                        title: name,
                        isSelected: selectedStorePlugin is { } sel && sel.StoreUrl == url && sel.Name == name,
                        onSelect: () => SelectStorePlugin(url, name),
                        trailing: trailing,
                        secondary: !installed));
                }
            }
            else if (entry.LastError is { } error)
            {
                sidebarPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ " + error,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 2, 4, 4),
                });
            }
            else
            {
                sidebarPanel.Children.Add(new TextBlock
                {
                    Text = L.T("Loading…"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(24, 2, 4, 4),
                });
            }
        }

        if (sidebarPanel.Children.Count == 0)
            sidebarPanel.Children.Add(new TextBlock
            {
                Text = L.T("No plugins yet.\nUse + below to add a store."),
                Foreground = (Brush)FindResource("TextSecondary"),
                FontSize = 12,
                Margin = new Thickness(8, 8, 8, 8),
                TextWrapping = TextWrapping.Wrap,
            });
    }

    /// A collapsible group header: chevron, bold caption, count — and for
    /// stores a refresh button plus select-on-click.
    private UIElement GroupHeader(string title, int count, string collapseKey,
        Border? trailing, Action? onSelect = null, bool isSelected = false)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 2),
            Background = Brushes.Transparent,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chevron = Glyph(collapsed.Contains(collapseKey) ? "" : "", 9);
        chevron.Margin = new Thickness(4, 0, 6, 0);
        row.Children.Add(chevron);

        var label = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(isSelected ? "TextPrimary" : "TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Inlines.Add(new Run(title));
        label.Inlines.Add(new Run($"   {count}") { Foreground = (Brush)FindResource("TextSecondary"), FontWeight = FontWeights.Normal });
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (trailing != null)
        {
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }

        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // Chevron edge toggles; the title selects a store (which also
            // shows its details) or toggles the plain Installed group.
            var x = e.GetPosition(row).X;
            if (onSelect != null && x > 18) onSelect();
            else
            {
                if (!collapsed.Remove(collapseKey)) collapsed.Add(collapseKey);
                RefreshSidebar();
            }
        };
        return row;
    }

    private Border SidebarRow(string glyph, string title, bool isSelected, Action onSelect,
        Border? trailing, bool secondary = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Glyph(glyph, 12);
        icon.Margin = new Thickness(2, 0, 8, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(secondary ? "TextSecondary" : "TextPrimary"),
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (trailing != null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 4, 5),
            Margin = new Thickness(12, 0, 0, 1),
            Background = isSelected ? (Brush)FindResource("SelectedBg") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        if (!isSelected)
        {
            row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; onSelect(); };
        return row;
    }

    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", PluginRegistry.PluginsDirectory) { UseShellExecute = true });
    }

    private void ImportPlugins()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = L.T("Plugin scripts (*.js)") + "|*.js",
            Multiselect = true,
            Title = L.T("Choose plugin .js files to import"),
        };
        if (dialog.ShowDialog(this) != true) return;
        Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
        foreach (var path in dialog.FileNames)
        {
            var destination = Path.Combine(PluginRegistry.PluginsDirectory, Path.GetFileName(path));
            try { File.Copy(path, destination, overwrite: false); }
            catch (IOException) { }
        }
        registry.Rescan();
    }

    private void OpenCreatePlugin()
    {
        var dialog = new CreatePluginDialog(author, registry, dark, selectedPluginId)
        {
            Owner = this,
        };
        dialog.ShowInstalled += id => SelectPlugin(id);
        dialog.ShowDialog();
    }

    /// Renames the plugin file and repoints every placed item, so items keep
    /// rendering instead of pointing at a file that no longer exists.
    private void OpenRenameDialog(string pluginId)
    {
        var dialog = new Window
        {
            Title = L.T("Rename Plugin"),
            Owner = this,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Resources = Theme.Load(dark),
            Background = (Brush)FindResource("WindowBg"),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Section(L.T("Rename Plugin")));
        panel.Children.Add(new TextBlock
        {
            Text = L.T("The file is renamed too. Items on your desktop follow it."),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var name = new TextBox { Text = pluginId };
        panel.Children.Add(name);
        var error = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = L.T("Cancel") };
        cancel.Click += (_, _) => dialog.Close();
        var apply = new Button { Content = L.T("Rename"), Style = (Style)FindResource("AccentButton"), Margin = new Thickness(8, 0, 0, 0) };
        void Apply()
        {
            var result = registry.Rename(pluginId, name.Text);
            if (!result.IsOK)
            {
                error.Text = result.Message ?? L.T("Couldn't rename that plugin.");
                error.Visibility = Visibility.Visible;
                return;
            }
            if (result.Outcome == PluginRegistry.RenameOutcome.Renamed && result.Name is { } renamed)
            {
                // Preferences keyed by id travel with the plugin, or the
                // rename would silently turn auto-update off.
                if (updater.IsAutoUpdate(pluginId))
                {
                    updater.SetAutoUpdate(pluginId, false);
                    updater.SetAutoUpdate(renamed, true);
                }
                store.Update(layout => layout with
                {
                    Items = layout.Items
                        .Select(i => i.PluginId == pluginId ? i with { PluginId = renamed } : i)
                        .ToList(),
                });
                infoCache.Remove(pluginId);
                SelectPlugin(renamed);
            }
            dialog.Close();
        }
        apply.Click += (_, _) => Apply();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void OpenAddStoreDialog()
    {
        var dialog = new Window
        {
            Title = L.T("Add Plugin Store"),
            Owner = this,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Resources = Theme.Load(dark),
            Background = (Brush)FindResource("WindowBg"),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Section(L.T("Add Plugin Store")));
        panel.Children.Add(new TextBlock
        {
            Text = L.T("A store is a JSON catalog listing plugins you can install. It becomes its own category in the library."),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var url = new TextBox { Text = "", ToolTip = "https://example.com/plugins.json" };
        panel.Children.Add(url);
        var error = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = L.T("Cancel") };
        cancel.Click += (_, _) => dialog.Close();
        var add = new Button { Content = L.T("Add"), Style = (Style)FindResource("AccentButton"), Margin = new Thickness(8, 0, 0, 0) };
        add.Click += async (_, _) =>
        {
            var text = url.Text.Trim();
            if (text.Length == 0) return;
            add.IsEnabled = false;
            var ok = await storeRegistry.AddStore(text);
            add.IsEnabled = true;
            if (ok) dialog.Close();
            else
            {
                error.Text = L.T("Couldn't read a plugin catalog from that URL.");
                error.Visibility = Visibility.Visible;
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    // ======================================================================
    //  Desktop preview (real wallpaper as background)
    // ======================================================================

    private UIElement BuildOverview()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = Section(L.T("Desktop"));
        DockPanel.SetDock(header, Dock.Top);
        var host = new Grid();
        overview.Background = WallpaperBrush() ?? (Brush)FindResource("OverviewBg");
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = overview,
            ClipToBounds = true,
        };
        host.Children.Add(frame);
        host.SizeChanged += (_, _) => RefreshOverview();
        panel.Children.Add(header);
        panel.Children.Add(host);
        return Card(panel);
    }

    /// The user's actual wallpaper image, so the preview looks like the real
    /// desktop (the mac Manager shows the desktop picture the same way).
    private Brush? WallpaperBrush()
    {
        try
        {
            var path = WallpaperRestore.CapturedPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 960;
            image.EndInit();
            image.Freeze();
            return new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch { return null; }
    }

    private void RefreshOverview()
    {
        overview.Children.Clear();
        var scale = OverviewScale();
        overview.Width = screenBounds.Width * scale;
        overview.Height = screenBounds.Height * scale;

        foreach (var item in store.Layout.Items)
        {
            var frame = item.NormalizedFrame;
            var isSelected = item.Id == selectedItemId;
            var content = new Grid();
            content.Children.Add(new TextBlock
            {
                Text = item.PluginId,
                Foreground = Brushes.White,
                FontSize = 10,
                Margin = new Thickness(5, 3, 5, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
            // Resize grip — visible on the selected item only, and never on
            // a plugin that declares resizable: false.
            var grip = new Border
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(5, 0, 4, 0),
                Cursor = Cursors.SizeNWSE,
                Visibility = isSelected && GripUsable(InfoFor(item.PluginId))
                    ? Visibility.Visible : Visibility.Collapsed,
            };
            content.Children.Add(grip);
            var rect = new Border
            {
                Tag = item.Id,
                Width = Math.Max(28, frame.W * screenBounds.Width * scale),
                Height = Math.Max(20, frame.H * screenBounds.Height * scale),
                Background = new SolidColorBrush(isSelected
                    ? Color.FromArgb(0xE0, 0x0A, 0x84, 0xFF)
                    : Color.FromArgb(0xAA, 0x3A, 0x3A, 0x44)),
                BorderBrush = item.Target == RenderTarget.FloatingWindow
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))
                    : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.SizeAll,
                Opacity = item.IsEnabled ? 1 : 0.4,
                Child = content,
            };
            Canvas.SetLeft(rect, frame.X * screenBounds.Width * scale);
            Canvas.SetTop(rect, (1 - frame.Y - frame.H) * screenBounds.Height * scale);
            WireDrag(rect, item.Id);
            WireResize(rect, grip, item.Id);
            overview.Children.Add(rect);
        }
    }

    /// A grip is worth showing only if some axis is the user's to set: a
    /// fixed-size plugin has none, and a fully content-sized one snaps back.
    private static bool GripUsable(PluginMetadata.PluginInfo info) =>
        info.Resizable && !(info.AutoSizeWidth && info.AutoSizeHeight);

    /// Recolors the preview rects for a selection change without rebuilding
    /// them — rebuilding mid-mousedown would destroy the border being
    /// dragged and kill the drag.
    private void HighlightOverviewSelection()
    {
        foreach (var child in overview.Children.OfType<Border>())
        {
            var isSelected = child.Tag is Guid id && id == selectedItemId;
            child.Background = new SolidColorBrush(isSelected
                ? Color.FromArgb(0xE0, 0x0A, 0x84, 0xFF)
                : Color.FromArgb(0xAA, 0x3A, 0x3A, 0x44));
            var resizable = isSelected && child.Tag is Guid gripId &&
                store.Layout.Items.FirstOrDefault(i => i.Id == gripId) is { } gripItem &&
                GripUsable(InfoFor(gripItem.PluginId));
            if (child.Child is Grid content && content.Children.Count > 1)
                content.Children[1].Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private double OverviewScale()
    {
        var available = overview.Parent is FrameworkElement parent &&
                        parent.Parent is FrameworkElement grand && grand.ActualWidth > 40
            ? new Size(grand.ActualWidth - 24, grand.ActualHeight - 24)
            : new Size(440, 280);
        return Math.Min(available.Width / screenBounds.Width, available.Height / screenBounds.Height);
    }

    /// Select in place — sidebar and inspector refresh, but the overview
    /// rects are only recolored, so a drag that begins on the same click
    /// keeps its Border alive.
    private void SelectItemInPlace(Guid itemId)
    {
        selectedItemId = itemId;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshInspector();
        HighlightOverviewSelection();
    }

    private void WireDrag(Border rect, Guid itemId)
    {
        Point grab = default;
        var dragging = false;
        rect.MouseLeftButtonDown += (_, e) =>
        {
            SelectItemInPlace(itemId);
            grab = e.GetPosition(rect);
            dragging = true;
            rect.CaptureMouse();
            e.Handled = true;
        };
        rect.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(overview);
            Canvas.SetLeft(rect, Math.Clamp(p.X - grab.X, 0, overview.Width - rect.Width));
            Canvas.SetTop(rect, Math.Clamp(p.Y - grab.Y, 0, overview.Height - rect.Height));
        };
        rect.MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            rect.ReleaseMouseCapture();
            var scale = OverviewScale();
            var x = Canvas.GetLeft(rect) / scale / screenBounds.Width;
            var top = Canvas.GetTop(rect) / scale / screenBounds.Height;
            store.Update(layout => layout with
            {
                Items = layout.Items.Select(item => item.Id == itemId
                    ? item with { NormalizedFrame = item.NormalizedFrame with { X = x, Y = 1 - top - item.NormalizedFrame.H } }
                    : item).ToList(),
            });
        };
    }

    /// Bottom-right grip: resizing keeps the top-left corner put (the mac
    /// frame model — y is the top edge, height grows downward).
    private void WireResize(Border rect, Border grip, Guid itemId)
    {
        Point start = default;
        Size startSize = default;
        var resizing = false;
        PluginMetadata.PluginInfo info = new(null, null, null, null, null, null);
        grip.MouseLeftButtonDown += (_, e) =>
        {
            SelectItemInPlace(itemId);
            info = store.Layout.Items.FirstOrDefault(i => i.Id == itemId) is { } item
                ? InfoFor(item.PluginId)
                : new PluginMetadata.PluginInfo(null, null, null, null, null, null);
            if (!info.Resizable) return;
            start = e.GetPosition(overview);
            startSize = new Size(rect.Width, rect.Height);
            resizing = true;
            grip.CaptureMouse();
            e.Handled = true; // don't start the move-drag underneath
        };
        grip.MouseMove += (_, e) =>
        {
            if (!resizing) return;
            var p = e.GetPosition(overview);
            var scale = OverviewScale();
            // The drag proposal in points, resolved against the plugin's
            // declared policy: aspect follows the dominant axis, limits snap.
            var proposedW = (startSize.Width + (p.X - start.X)) / scale / DpiScale;
            var proposedH = (startSize.Height + (p.Y - start.Y)) / scale / DpiScale;
            var edited = Math.Abs(p.X - start.X) >= Math.Abs(p.Y - start.Y)
                ? PluginMetadata.PluginInfo.SizeAxis.Width
                : PluginMetadata.PluginInfo.SizeAxis.Height;
            var (w, h) = info.ResolvedSize(proposedW, proposedH, edited);
            // Content-driven axes ignore the drag — the render snaps them back.
            if (info.AutoSizeWidth) w = rect.Width / scale / DpiScale;
            if (info.AutoSizeHeight) h = rect.Height / scale / DpiScale;
            var maxW = Math.Max(24, overview.Width - Canvas.GetLeft(rect));
            var maxH = Math.Max(18, overview.Height - Canvas.GetTop(rect));
            rect.Width = Math.Clamp(w * DpiScale * scale, 24, maxW);
            rect.Height = Math.Clamp(h * DpiScale * scale, 18, maxH);
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!resizing) return;
            resizing = false;
            grip.ReleaseMouseCapture();
            e.Handled = true;
            var scale = OverviewScale();
            var (wPts, hPts) = info.ResolvedSize(rect.Width / scale / DpiScale, rect.Height / scale / DpiScale, null);
            var w = wPts * DpiScale / screenBounds.Width;
            var h = hPts * DpiScale / screenBounds.Height;
            var top = Canvas.GetTop(rect) / scale / screenBounds.Height;
            store.Update(layout => layout with
            {
                Items = layout.Items.Select(item => item.Id == itemId
                    ? item with
                    {
                        NormalizedFrame = item.NormalizedFrame with { W = w, H = h, Y = 1 - top - h },
                    }
                    : item).ToList(),
            });
        };
    }

    /// Places a new item centred on the screen, adopting the plugin's
    /// declared point size (converted to a screen fraction) — the mac's
    /// addToDesktop.
    private void AddToDesktop(string pluginId)
    {
        double w = 0.2, h = 0.2;
        var info = InfoFor(pluginId);
        var pointsWide = screenBounds.Width / DpiScale;
        var pointsHigh = screenBounds.Height / DpiScale;
        if (info.Width is { } pw && pointsWide > 0) w = Math.Min(pw / pointsWide, 1);
        if (info.Height is { } ph && pointsHigh > 0) h = Math.Min(ph / pointsHigh, 1);
        var item = new LayoutItem
        {
            Id = Guid.NewGuid(),
            PluginId = pluginId,
            DisplayUuid = "PRIMARY",
            NormalizedFrame = new NormalizedFrame(0.5 - w / 2, 0.5 - h / 2, w, h),
            ZOrder = (store.Layout.Items.Select(i => i.ZOrder).DefaultIfEmpty(0).Max()) + 1,
        };
        store.Update(layout => layout with { Items = layout.Items.Append(item).ToList() });
        SelectItem(item.Id);
    }

    // ======================================================================
    //  Inspector — follows the selection kind (mac InspectorView)
    // ======================================================================

    private void RefreshInspector()
    {
        inspector.Children.Clear();
        if (selectedStorePlugin is { } storePluginRef)
        {
            RenderStorePluginDetail(storePluginRef.StoreUrl, storePluginRef.Name);
            return;
        }
        if (selectedStoreUrl is { } storeUrl)
        {
            RenderStoreDetail(storeUrl);
            return;
        }
        if (selectedPluginId is { } pluginId)
        {
            RenderPluginDetail(pluginId);
            return;
        }
        var item = store.Layout.Items.FirstOrDefault(i => i.Id == selectedItemId);
        if (item != null)
        {
            RenderItemDetail(item);
            return;
        }
        inspector.Children.Add(new TextBlock
        {
            Text = L.T("No Selection"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondary"),
        });
        inspector.Children.Add(new TextBlock
        {
            Text = L.T("Select an item on the desktop, or a plugin in the sidebar."),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
    }

    // ---- placed item ----

    private void RenderItemDetail(LayoutItem item)
    {
        void Commit(Func<LayoutItem, LayoutItem> mutate) => store.Update(layout => layout with
        {
            Items = layout.Items.Select(i => i.Id == item.Id ? mutate(i) : i).ToList(),
        });

        inspector.Children.Add(new TextBlock
        {
            Text = item.PluginId,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        if (storeRegistry.OriginOf(item.PluginId) is { } origin)
            inspector.Children.Add(new TextBlock
            {
                Text = L.T("from {0}", origin),
                Foreground = (Brush)FindResource("TextSecondary"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
            });

        var enabled = new CheckBox { Content = L.T("Enabled"), IsChecked = item.IsEnabled, Margin = new Thickness(0, 12, 0, 0) };
        enabled.Checked += (_, _) => Commit(i => i with { IsEnabled = true });
        enabled.Unchecked += (_, _) => Commit(i => i with { IsEnabled = false });
        inspector.Children.Add(enabled);

        inspector.Children.Add(Caption(L.T("Show as")));
        var target = new ComboBox { ItemsSource = new[] { L.T("Wallpaper"), L.T("Floating Window") }, SelectedIndex = item.Target == RenderTarget.Wallpaper ? 0 : 1 };
        target.SelectionChanged += (_, _) => Commit(i => i with { Target = target.SelectedIndex == 0 ? RenderTarget.Wallpaper : RenderTarget.FloatingWindow });
        inspector.Children.Add(target);

        if (item.Target == RenderTarget.FloatingWindow)
        {
            var clickThrough = new CheckBox
            {
                Content = L.T("Click-through"),
                IsChecked = item.ClickThrough,
                Margin = new Thickness(0, 12, 0, 0),
                ToolTip = L.T("On: clicks pass through to windows beneath. Off: the window accepts mouse events and can be dragged."),
            };
            clickThrough.Checked += (_, _) => Commit(i => i with { ClickThrough = true });
            clickThrough.Unchecked += (_, _) => Commit(i => i with { ClickThrough = false });
            inspector.Children.Add(clickThrough);
        }

        inspector.Children.Add(Caption(L.T("Z-order")));
        var zOrder = new TextBox { Text = item.ZOrder.ToString() };
        zOrder.LostFocus += (_, _) => { if (int.TryParse(zOrder.Text, out var z)) Commit(i => i with { ZOrder = z }); };
        inspector.Children.Add(zOrder);

        inspector.Children.Add(Caption(L.T("Background")));
        inspector.Children.Add(new ColorField(item.BackgroundColor, allowNone: true,
            hex => Commit(i => i with { BackgroundColor = hex })));

        AddFrameEditor(item, Commit);

        var plugin = registry.Plugin(item.PluginId);
        AddPropertyAndPermissionEditors(item, plugin, Commit);
        AddUpdateControls(item.PluginId, plugin);

        var delete = new Button { Content = L.T("Remove from Desktop"), Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 18, 0, 0) };
        delete.Click += (_, _) =>
        {
            selectedItemId = null;
            store.Update(layout => layout with { Items = layout.Items.Where(i => i.Id != item.Id).ToList() });
        };
        inspector.Children.Add(delete);
    }

    /// The mac FrameEditor: percent is what's stored, points are what an
    /// author actually thinks in — X/Y edit the top-left corner against the
    /// item's display, height grows downward.
    private void AddFrameEditor(LayoutItem item, Action<Func<LayoutItem, LayoutItem>> commit)
    {
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Frame (points)"), Margin = new Thickness(2, 18, 0, 2) });
        // Point-denominated screen size: normalized fractions multiply out to
        // points here and to device pixels in the engine.
        double sw = screenBounds.Width / DpiScale, sh = screenBounds.Height / DpiScale;
        var frame = item.NormalizedFrame;
        var info = InfoFor(item.PluginId);

        var x = new TextBox { Text = Math.Round(frame.X * sw).ToString("0") };
        var y = new TextBox { Text = Math.Round((1 - frame.Y - frame.H) * sh).ToString("0") };
        // An axis the plugin sizes from its own content isn't the user's to
        // set: the next render would snap it straight back.
        var w = new TextBox
        {
            Text = Math.Round(frame.W * sw).ToString("0"),
            IsEnabled = info.Resizable && !info.AutoSizeWidth,
        };
        var h = new TextBox
        {
            Text = Math.Round(frame.H * sh).ToString("0"),
            IsEnabled = info.Resizable && !info.AutoSizeHeight,
        };

        void CommitFrame(PluginMetadata.PluginInfo.SizeAxis? edited)
        {
            if (!double.TryParse(x.Text, out var px) || !double.TryParse(y.Text, out var py) ||
                !double.TryParse(w.Text, out var pw) || !double.TryParse(h.Text, out var ph)) return;
            px = Math.Clamp(px, 0, sw);
            py = Math.Clamp(py, 0, sh);
            // Declared limits and aspect: out-of-range input snaps to the
            // limit, and the untouched axis follows the edited one — so the
            // fields never show a size the item can't take (mac FrameEditor).
            (pw, ph) = info.ResolvedSize(pw, ph, edited);
            x.Text = Math.Round(px).ToString("0");
            y.Text = Math.Round(py).ToString("0");
            w.Text = Math.Round(pw).ToString("0");
            h.Text = Math.Round(ph).ToString("0");
            // Back to bottom-left for storage; y is the top edge and stays put.
            var bottom = Math.Max(sh - py - ph, 0);
            commit(i => i with
            {
                NormalizedFrame = new NormalizedFrame(
                    Math.Min(px / sw, 1), Math.Min(bottom / sh, 1),
                    Math.Min(pw / sw, 1), Math.Min(ph / sh, 1)),
            });
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        void AddField(string label, TextBox box, int column, PluginMetadata.PluginInfo.SizeAxis? axis)
        {
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(2, row == 0 ? 0 : 8, 0, 2),
            });
            box.LostFocus += (_, _) => CommitFrame(axis);
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitFrame(axis); };
            cell.Children.Add(box);
            Grid.SetColumn(cell, column);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        AddField(L.T("X"), x, 0, null);
        AddField(L.T("Y (from top)"), y, 2, null);
        row = 1;
        AddField(L.T("Width"), w, 0, PluginMetadata.PluginInfo.SizeAxis.Width);
        AddField(L.T("Height"), h, 2, PluginMetadata.PluginInfo.SizeAxis.Height);
        inspector.Children.Add(grid);

        var autoNote = (info.AutoSizeWidth, info.AutoSizeHeight) switch
        {
            (true, true) => L.T("Width and height follow this plugin's content."),
            (true, false) => L.T("Width follows this plugin's content."),
            (false, true) => L.T("Height follows this plugin's content."),
            _ => null,
        };
        if (!info.Resizable)
            inspector.Children.Add(new TextBlock
            {
                Text = L.T("This plugin declares a fixed size (resizable: false)."),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(2, 4, 0, 0),
            });
        else if (autoNote != null)
            inspector.Children.Add(new TextBlock
            {
                Text = autoNote,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 0, 0),
            });
        else if (LimitsSummary(info) is { } limits)
            inspector.Children.Add(new TextBlock
            {
                Text = L.T("Limits: {0} pt", limits),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(2, 4, 0, 0),
            });
    }

    /// "W 100–300  H ≥ 80", or null when the plugin declares no limits.
    /// How the plugin may be resized, in the mac's words: the aspect policy
    /// first, then whichever axes size themselves from their content. A
    /// fixed-size plugin says only that — auto sizing is what the plugin
    /// does with a size, and it never gets one from the user.
    private static string ResizeSummary(PluginMetadata.PluginInfo info)
    {
        if (!info.Resizable) return L.T("fixed size");
        var parts = new List<string> { info.KeepsAspect ? L.T("keeps aspect") : L.T("free") };
        if (info.AutoSizeWidth && info.AutoSizeHeight) parts.Add(L.T("auto-sizes"));
        else if (info.AutoSizeHeight) parts.Add(L.T("auto height"));
        else if (info.AutoSizeWidth) parts.Add(L.T("auto width"));
        return string.Join(", ", parts);
    }

    private static string? LimitsSummary(PluginMetadata.PluginInfo info)
    {
        static string? Range(double? min, double? max) => (min, max) switch
        {
            ({ } lo, { } hi) => $"{(int)lo}–{(int)hi}",
            ({ } lo, null) => $"≥ {(int)lo}",
            (null, { } hi) => $"≤ {(int)hi}",
            _ => null,
        };
        var parts = new[]
        {
            Range(info.MinWidth, info.MaxWidth) is { } wRange ? $"W {wRange}" : null,
            Range(info.MinHeight, info.MaxHeight) is { } hRange ? $"H {hRange}" : null,
        }.Where(p => p != null).ToArray();
        return parts.Length == 0 ? null : string.Join("  ", parts);
    }

    private void AddPropertyAndPermissionEditors(LayoutItem item, InstalledPlugin? plugin, Action<Func<LayoutItem, LayoutItem>> commit)
    {
        if (plugin == null) return;
        IReadOnlyList<PluginProperty>? declared = null;
        IReadOnlySet<string>? permissions = null;
        try
        {
            using var probe = PluginInstance.Boot(item.PluginId, File.ReadAllText(plugin.SourcePath), item.PropertyOverrides);
            declared = probe?.Properties;
            permissions = probe?.Permissions;
        }
        catch (IOException) { }

        if (permissions is { Count: > 0 })
        {
            inspector.Children.Add(Caption(L.T("Permissions requested")));
            inspector.Children.Add(new TextBlock
            {
                Text = "⚠ " + string.Join(", ", permissions.OrderBy(p => p)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (permissions?.Contains("ssh") == true)
            AddSshEditor(item, commit);

        AddDeclaredPropertyEditors(item, declared, commit);
    }

    /// The mac SSHEditor: one or more remote hosts per item. Alias mode is
    /// the common case — Windows' bundled OpenSSH reads the same
    /// ~/.ssh/config, so an alias resolves host, user, port, and key.
    private void AddSshEditor(LayoutItem item, Action<Func<LayoutItem, LayoutItem>> commit)
    {
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("SSH Destinations"), Margin = new Thickness(2, 18, 0, 6) });
        var aliases = SshConfigFile.Aliases();
        var hosts = item.SshHosts.Count > 0 ? item.SshHosts.ToList() : new List<SshConfig> { new() };
        void Push(List<SshConfig> updated) => commit(i => i with { SshHosts = updated });
        List<SshConfig> With(int index, SshConfig replacement)
        {
            var copy = hosts.ToList();
            copy[index] = replacement;
            return copy;
        }

        for (var index = 0; index < hosts.Count; index++)
        {
            var idx = index;
            var host = hosts[idx];

            // Name row, with remove when there is more than one host.
            var header = new Grid { Margin = new Thickness(0, idx == 0 ? 0 : 10, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBox { Text = host.Name, FontWeight = FontWeights.SemiBold };
            name.LostFocus += (_, _) =>
            {
                var trimmed = name.Text.Trim();
                if (trimmed.Length > 0 && trimmed != host.Name) Push(With(idx, host with { Name = trimmed }));
            };
            header.Children.Add(name);
            if (hosts.Count > 1)
            {
                var remove = IconButton("", L.T("Remove this server"), () =>
                {
                    var copy = hosts.ToList();
                    copy.RemoveAt(idx);
                    Push(copy);
                });
                remove.Margin = new Thickness(6, 0, 0, 0);
                remove.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(remove, 1);
                header.Children.Add(remove);
            }
            inspector.Children.Add(header);

            var usesAlias = new CheckBox
            {
                Content = L.T("Use ~/.ssh/config alias"),
                IsChecked = host.UsesAlias,
                Margin = new Thickness(0, 8, 0, 0),
            };
            usesAlias.Checked += (_, _) => Push(With(idx, host with { UsesAlias = true }));
            usesAlias.Unchecked += (_, _) => Push(With(idx, host with { UsesAlias = false }));
            inspector.Children.Add(usesAlias);

            if (host.UsesAlias)
            {
                inspector.Children.Add(Caption(L.T("Alias")));
                if (aliases.Count == 0)
                {
                    var aliasBox = new TextBox { Text = host.Host };
                    aliasBox.LostFocus += (_, _) =>
                    {
                        if (aliasBox.Text.Trim() != host.Host)
                            Push(With(idx, host with { Host = aliasBox.Text.Trim() }));
                    };
                    inspector.Children.Add(aliasBox);
                    inspector.Children.Add(new TextBlock
                    {
                        Text = L.T("No hosts found in ~/.ssh/config."),
                        FontSize = 10,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                }
                else
                {
                    var choices = new List<string>(aliases);
                    if (host.Host.Length > 0 && !choices.Contains(host.Host)) choices.Insert(0, host.Host);
                    var picker = new ComboBox
                    {
                        ItemsSource = choices,
                        SelectedItem = host.Host.Length > 0 ? host.Host : null,
                    };
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (picker.SelectedItem is not string alias || alias == host.Host) return;
                        var updated = host with { Host = alias };
                        // Adopt the alias as the name unless the user set one.
                        if (host.Name.Length == 0 || host.Name.StartsWith("server", StringComparison.Ordinal) || host.Name == "default")
                            updated = updated with { Name = alias };
                        Push(With(idx, updated));
                    };
                    inspector.Children.Add(picker);
                    inspector.Children.Add(new TextBlock
                    {
                        Text = L.T("ssh resolves the host name, user, port, and key."),
                        FontSize = 10,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                }
            }
            else
            {
                inspector.Children.Add(Caption(L.T("Host")));
                var hostBox = new TextBox { Text = host.Host };
                hostBox.LostFocus += (_, _) =>
                {
                    if (hostBox.Text.Trim() != host.Host) Push(With(idx, host with { Host = hostBox.Text.Trim() }));
                };
                inspector.Children.Add(hostBox);

                inspector.Children.Add(Caption(L.T("Port")));
                var portBox = new TextBox { Text = host.Port.ToString() };
                portBox.LostFocus += (_, _) =>
                {
                    if (int.TryParse(portBox.Text, out var port) && port != host.Port && port is > 0 and < 65536)
                        Push(With(idx, host with { Port = port }));
                };
                inspector.Children.Add(portBox);

                inspector.Children.Add(Caption(L.T("User")));
                var userBox = new TextBox { Text = host.User };
                userBox.LostFocus += (_, _) =>
                {
                    if (userBox.Text.Trim() != host.User) Push(With(idx, host with { User = userBox.Text.Trim() }));
                };
                inspector.Children.Add(userBox);

                inspector.Children.Add(Caption(L.T("Auth")));
                var auth = new ComboBox
                {
                    ItemsSource = new[] { L.T("SSH agent"), L.T("Identity key") },
                    SelectedIndex = host.Auth == SshAuth.Key ? 1 : 0,
                };
                auth.SelectionChanged += (_, _) =>
                {
                    var picked = auth.SelectedIndex == 1 ? SshAuth.Key : SshAuth.None;
                    if (picked != host.Auth) Push(With(idx, host with { Auth = picked }));
                };
                inspector.Children.Add(auth);

                if (host.Auth == SshAuth.Key)
                {
                    var keyRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    keyRow.Children.Add(new TextBlock
                    {
                        Text = host.KeyPath.Length == 0 ? L.T("No key chosen") : Path.GetFileName(host.KeyPath),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = host.KeyPath.Length == 0 ? null : host.KeyPath,
                    });
                    var choose = new Button { Content = L.T("Choose…"), Padding = new Thickness(8, 4, 8, 4) };
                    choose.Click += (_, _) =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = L.T("Choose an SSH identity (private key) file"),
                            InitialDirectory = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
                        };
                        if (dialog.ShowDialog(this) == true)
                            Push(With(idx, host with { KeyPath = dialog.FileName }));
                    };
                    Grid.SetColumn(choose, 1);
                    keyRow.Children.Add(choose);
                    inspector.Children.Add(keyRow);
                }
                else
                {
                    inspector.Children.Add(new TextBlock
                    {
                        Text = L.T("Uses your ssh agent. (Password auth isn't supported on Windows.)"),
                        FontSize = 10,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                }
            }
        }

        var addServer = new Button { Content = L.T("+ Add Server"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        addServer.Click += (_, _) =>
        {
            var n = hosts.Count + 1;
            while (hosts.Any(h => h.Name == $"server{n}")) n++;
            var copy = hosts.ToList();
            copy.Add(new SshConfig { Name = $"server{n}" });
            Push(copy);
        };
        inspector.Children.Add(addServer);
        if (hosts.Count > 1)
            inspector.Children.Add(new TextBlock
            {
                Text = L.T("Plugins target a server by name: ssh(cmd, \"{0}\").", hosts[1].Name),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 4, 0, 0),
            });
    }

    private void AddDeclaredPropertyEditors(LayoutItem item, IReadOnlyList<PluginProperty>? declared, Action<Func<LayoutItem, LayoutItem>> commit)
    {

        if (declared is { Count: > 0 })
        {
            inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Properties"), Margin = new Thickness(2, 18, 0, 6) });
            foreach (var property in declared)
            {
                var name = property.Name;
                var valueType = property.ValueType;
                void CommitValue(PropertyValue value) => commit(i =>
                {
                    var overrides = new Dictionary<string, PropertyValue>(
                        i.PropertyOverrides.ToDictionary(kv => kv.Key, kv => kv.Value)) { [name] = value };
                    return i with { PropertyOverrides = overrides };
                });

                // A boolean is a toggle, like the mac's Toggle(property.name)
                // and the Enabled checkbox above: it carries its own label, so
                // it needs no caption — the control is the type hint.
                if (valueType is "boolean" or "bool")
                {
                    var toggle = new CheckBox
                    {
                        Content = name,
                        IsChecked = property.Value.BoolValue ?? false,
                        Margin = new Thickness(0, 10, 0, 0),
                    };
                    // Wire the handlers only after seeding: assigning
                    // IsChecked raises Checked, so attaching them first would
                    // write an override for every true boolean just by
                    // selecting the item — the mac's isSeeded gate, and the
                    // rule the colour picker learned the hard way.
                    toggle.Checked += (_, _) => CommitValue(PropertyValue.Bool(true));
                    toggle.Unchecked += (_, _) => CommitValue(PropertyValue.Bool(false));
                    inspector.Children.Add(toggle);
                    continue;
                }

                inspector.Children.Add(Caption(L.T("{0} ({1})", property.Name, L.T(property.ValueType))));

                if (valueType == "color")
                {
                    inspector.Children.Add(new ColorField(property.Value.StringValue, allowNone: false,
                        hex => { if (hex != null) CommitValue(PropertyValue.Color(hex)); }));
                    continue;
                }

                var box = new TextBox { Text = property.Value.StringValue };
                box.LostFocus += (_, _) =>
                {
                    if (PropertyValue.Coerce(box.Text, valueType) is { } coerced) CommitValue(coerced);
                };
                inspector.Children.Add(box);
            }
        }
    }

    private void AddUpdateControls(string pluginId, InstalledPlugin? plugin)
    {
        if (plugin == null) return;
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Updates"), Margin = new Thickness(2, 18, 0, 6) });

        string? updateUrl = null;
        try { updateUrl = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).updateUrl; }
        catch (IOException) { }

        if (updateUrl != null)
        {
            var auto = new CheckBox { Content = L.T("Auto-update on launch"), IsChecked = updater.IsAutoUpdate(pluginId) };
            auto.Checked += (_, _) => updater.SetAutoUpdate(pluginId, true);
            auto.Unchecked += (_, _) => updater.SetAutoUpdate(pluginId, false);
            inspector.Children.Add(auto);

            var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            var check = new Button { Content = L.T("Check for Update"), Margin = new Thickness(0, 8, 0, 0) };
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                status.Text = L.T("Checking…");
                try
                {
                    var result = await updater.Check(pluginId, File.ReadAllText(plugin.SourcePath), plugin.SourcePath);
                    status.Text = result.Message;
                    if (result.Outcome == UpdateOutcome.Updated) registry.Rescan();
                }
                catch (Exception ex) { status.Text = L.T("Update failed: {0}", ex.Message); }
                finally { check.IsEnabled = true; }
            };
            inspector.Children.Add(check);
            inspector.Children.Add(status);
            return;
        }

        // A store-installed plugin has no updateURL of its own — the catalog
        // is its source of truth, so update straight from it rather than
        // leaving the user with no button at all (mac PluginAboutView rule).
        var source = StoreSourceFor(pluginId);
        if (source == null)
        {
            inspector.Children.Add(new TextBlock
            {
                Text = L.T("No update URL declared"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
            });
            return;
        }

        inspector.Children.Add(LabeledRow(L.T("Store"), source.Value.StoreName));
        string? installedVersion = null;
        try { installedVersion = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).version; }
        catch (IOException) { }
        var listed = source.Value.Plugin.Version;
        var newer = listed != null &&
            (installedVersion == null || PluginUpdater.CompareVersions(listed, installedVersion) > 0);

        var status2 = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (newer)
        {
            status2.Text = L.T("The store lists {0}.", listed);
            status2.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        }
        var button = new Button
        {
            Content = newer && listed != null ? L.T("Update to {0}", listed) : L.T("Reinstall from Store"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        var sp = source.Value;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var error = await storeRegistry.Install(sp.Plugin, sp.StoreName, PluginRegistry.PluginsDirectory);
            registry.Rescan();
            status2.Text = error ?? L.T("Installed {0}", sp.Plugin.Version ?? "").TrimEnd();
            button.IsEnabled = true;
        };
        inspector.Children.Add(button);
        inspector.Children.Add(status2);
    }

    /// A store that offers this plugin — the recorded origin wins, any store
    /// listing the same name will do otherwise.
    private (string StoreName, StorePlugin Plugin)? StoreSourceFor(string pluginId)
    {
        var origin = storeRegistry.OriginOf(pluginId);
        (string, StorePlugin)? first = null;
        foreach (var entry in storeRegistry.Stores)
        {
            var listed = entry.Catalog?.Plugins.FirstOrDefault(p => p.Name == pluginId);
            if (listed == null) continue;
            if (entry.DisplayName == origin) return (entry.DisplayName, listed);
            first ??= (entry.DisplayName, listed);
        }
        return first;
    }

    // ---- installed plugin (library selection) ----

    private void RenderPluginDetail(string pluginId)
    {
        var plugin = registry.Plugin(pluginId);
        var usageCount = store.Layout.Items.Count(i => i.PluginId == pluginId);
        var info = InfoFor(pluginId);
        string? source = null;
        if (plugin != null)
        {
            try { source = File.ReadAllText(plugin.SourcePath); }
            catch (IOException) { }
        }

        inspector.Children.Add(new TextBlock { Text = pluginId, FontSize = 16, FontWeight = FontWeights.SemiBold });
        var origin = storeRegistry.OriginOf(pluginId);
        inspector.Children.Add(new TextBlock
        {
            Text = origin != null ? L.T("from {0}", origin) : L.T("User Installed"),
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 8),
        });

        inspector.Children.Add(LabeledRow(L.T("Version"), info.Version ?? "—"));
        if (info.Author != null) inspector.Children.Add(LabeledRow(L.T("Author"), info.Author));
        if (info.Description != null)
            inspector.Children.Add(new TextBlock
            {
                Text = info.Description,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        inspector.Children.Add(LabeledRow(L.T("On desktop"),
            usageCount == 0 ? L.T("not placed") : L.T("{0} items", usageCount)));

        inspector.Children.Add(Divider());
        AddUpdateControls(pluginId, plugin);

        // Capabilities (mac "Capabilities" section).
        inspector.Children.Add(Divider());
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Capabilities"), Margin = new Thickness(2, 0, 0, 6) });
        IReadOnlySet<string>? permissions = null;
        IReadOnlyList<PluginProperty>? declared = null;
        if (source != null)
        {
            try
            {
                using var probe = PluginInstance.Boot(pluginId, source, new Dictionary<string, PropertyValue>());
                permissions = probe?.Permissions;
                declared = probe?.Properties;
            }
            catch { }
        }
        inspector.Children.Add(LabeledRow(L.T("Permissions"),
            permissions is { Count: > 0 } ? string.Join(", ", permissions.OrderBy(p => p)) : L.T("none")));
        if (info.Width is { } dw && info.Height is { } dh)
            inspector.Children.Add(LabeledRow(L.T("Default size"), $"{(int)dw} × {(int)dh}"));
        inspector.Children.Add(LabeledRow(L.T("Resize"), ResizeSummary(info)));
        if (LimitsSummary(info) is { } limits)
            inspector.Children.Add(LabeledRow(L.T("Limits"), limits));

        // Properties — read-only here: values are edited per placed item.
        inspector.Children.Add(Divider());
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Properties"), Margin = new Thickness(2, 0, 0, 6) });
        if (declared is { Count: > 0 })
            foreach (var property in declared)
                inspector.Children.Add(LabeledRow(property.Name, property.Value.StringValue));
        else
            inspector.Children.Add(new TextBlock { Text = L.T("No properties declared"), FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") });

        // Source: reveal + add-to-desktop + rewrite-with-AI + uninstall.
        inspector.Children.Add(Divider());
        var add = new Button { Content = L.T("Add to Desktop"), Style = (Style)FindResource("AccentButton") };
        add.Click += (_, _) => AddToDesktop(pluginId);
        inspector.Children.Add(add);

        if (plugin != null)
        {
            var reveal = new Button { Content = L.T("Show in Explorer"), Margin = new Thickness(0, 8, 0, 0) };
            reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{plugin.SourcePath}\"") { UseShellExecute = true });
            inspector.Children.Add(reveal);

            var rewrite = new Button { Content = L.T("Rewrite with AI…"), Margin = new Thickness(0, 8, 0, 0) };
            rewrite.Click += (_, _) => OpenCreatePlugin();
            inspector.Children.Add(rewrite);

            var share = new Button { Content = L.T("Share to Community…"), Margin = new Thickness(0, 8, 0, 0) };
            share.Click += (_, _) =>
            {
                string pluginSource;
                try { pluginSource = File.ReadAllText(plugin.SourcePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                var dialog = new PublishDialog(dark, pluginId, pluginSource, info, permissions,
                    capturePreview: () => capturePreview?.Invoke(pluginId) ?? Task.FromResult<byte[]?>(null),
                    hasRunningInstance: () => store.Layout.Items.Any(i => i.PluginId == pluginId && i.IsEnabled),
                    addInstanceToDesktop: () => AddToDesktop(pluginId)) { Owner = this };
                dialog.ShowDialog();
            };
            inspector.Children.Add(share);

            // Store plugins keep their catalog name: an update looks the
            // plugin up by name, and a renamed copy would be installed
            // alongside it rather than over it.
            var fromStore = storeRegistry.OriginOf(pluginId);
            var rename = new Button
            {
                Content = L.T("Rename…"),
                Margin = new Thickness(0, 8, 0, 0),
                IsEnabled = fromStore == null,
            };
            rename.Click += (_, _) => OpenRenameDialog(pluginId);
            inspector.Children.Add(rename);
            if (fromStore is { } storeName)
                inspector.Children.Add(new TextBlock
                {
                    Text = L.T("Plugins from {0} keep their name so updates can find them.", storeName),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 0, 0),
                });

            var uninstall = new Button { Content = L.T("Uninstall"), Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 8, 0, 0) };
            uninstall.Click += (_, _) =>
            {
                var message = usageCount > 0
                    ? L.T("{0} items on your desktop use it and will stop rendering.", usageCount)
                    : L.T("The plugin file is deleted.");
                if (MessageBox.Show(this, message, L.T("Uninstall {0}?", pluginId),
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                try
                {
                    File.Delete(plugin.SourcePath);
                    if (plugin.AssetsDirectory != null) Directory.Delete(plugin.AssetsDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                selectedPluginId = null;
                registry.Rescan();
            };
            inspector.Children.Add(uninstall);
        }
    }

    // ---- store (category selection) ----

    private void RenderStoreDetail(string storeUrl)
    {
        var entry = storeRegistry.Stores.FirstOrDefault(s => s.Url == storeUrl);
        if (entry == null) { selectedStoreUrl = null; RefreshInspector(); return; }

        inspector.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 16, FontWeight = FontWeights.SemiBold });
        inspector.Children.Add(new TextBlock
        {
            Text = L.T("Plugin Store"),
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 8),
        });

        var total = entry.Catalog?.Plugins.Count ?? 0;
        var installed = entry.Catalog?.Plugins.Count(p => registry.Plugin(p.Name) != null) ?? 0;
        inspector.Children.Add(LabeledRow(L.T("Plugins"), total.ToString()));
        inspector.Children.Add(LabeledRow(L.T("Installed"), installed.ToString()));
        if (entry.LastError is { } error)
            inspector.Children.Add(new TextBlock
            {
                Text = "⚠ " + error,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

        if (entry.Catalog?.Website is { } website)
        {
            inspector.Children.Add(Divider());
            var link = new Button { Content = website, HorizontalContentAlignment = HorizontalAlignment.Left };
            link.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(website) { UseShellExecute = true }); }
                catch { }
            };
            inspector.Children.Add(link);
        }

        inspector.Children.Add(Divider());
        inspector.Children.Add(Caption(L.T("Catalog URL")));
        inspector.Children.Add(new TextBlock
        {
            Text = entry.Url,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (entry.FetchedAt is { } fetched)
            inspector.Children.Add(LabeledRow(L.T("Updated"), fetched.LocalDateTime.ToString("g")));

        var refresh = new Button { Content = L.T("Refresh"), Margin = new Thickness(0, 8, 0, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await storeRegistry.RefreshAll(true);
            refresh.IsEnabled = true;
        };
        inspector.Children.Add(refresh);

        inspector.Children.Add(Divider());
        var remove = new Button { Content = L.T("Remove Store"), Style = (Style)FindResource("DangerButton") };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    L.T("Its catalog disappears from the library. Installed plugins are untouched."),
                    L.T("Remove {0}?", entry.DisplayName), MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;
            storeRegistry.RemoveStore(entry.Url);
            selectedStoreUrl = null;
        };
        inspector.Children.Add(remove);
        inspector.Children.Add(new TextBlock
        {
            Text = L.T("Removing a store only drops its listing. Plugins you already installed from it stay on disk."),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }

    // ---- store-listed plugin (maybe not installed) ----

    private void RenderStorePluginDetail(string storeUrl, string name)
    {
        var entry = storeRegistry.Stores.FirstOrDefault(s => s.Url == storeUrl);
        var plugin = entry?.Catalog?.Plugins.FirstOrDefault(p => p.Name == name);
        var isInstalled = registry.Plugin(name) != null;

        inspector.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold });
        inspector.Children.Add(new TextBlock
        {
            Text = L.T("from {0}", entry?.DisplayName ?? L.T("Store")),
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 8),
        });

        if (plugin?.Description is { } description)
            inspector.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        if (plugin?.Version is { } version) inspector.Children.Add(LabeledRow(L.T("Version"), version));
        if (plugin?.Author is { } author2) inspector.Children.Add(LabeledRow(L.T("Author"), author2));

        // Community-store extras: cheer/comment counts from the forum, the
        // staff verified badge, and a deep link into the discussion.
        if (plugin?.Cheers is { } cheers)
            inspector.Children.Add(LabeledRow(L.T("Community"), L.T("{0} cheers · {1} comments", cheers, plugin.Comments ?? 0)));
        if (plugin?.Verified == true)
            inspector.Children.Add(new TextBlock
            {
                Text = "✓ " + L.T("Verified by staff"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
        if (plugin?.TopicUrl is { } topic)
        {
            var discuss = new Button { Content = L.T("Discuss on the Forum"), Margin = new Thickness(0, 8, 0, 0) };
            discuss.Click += (_, _) => Process.Start(new ProcessStartInfo(topic) { UseShellExecute = true });
            inspector.Children.Add(discuss);
        }

        inspector.Children.Add(Divider());
        if (isInstalled)
        {
            inspector.Children.Add(new TextBlock
            {
                Text = "✓ Installed",
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            });
            var show = new Button { Content = L.T("Show Installed Plugin") };
            show.Click += (_, _) => SelectPlugin(name);
            inspector.Children.Add(show);
            return;
        }
        if (plugin == null || entry == null) return;

        var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var install = new Button { Content = L.T("Install"), Style = (Style)FindResource("AccentButton") };
        var installAdd = new Button { Content = L.T("Install & Add to Desktop"), Margin = new Thickness(0, 8, 0, 0) };
        async Task Install(bool thenPlace)
        {
            install.IsEnabled = false;
            installAdd.IsEnabled = false;
            status.Text = L.T("Installing…");
            var error = await storeRegistry.Install(plugin, entry.DisplayName, PluginRegistry.PluginsDirectory);
            registry.Rescan();
            status.Text = error ?? L.T("Installed");
            install.IsEnabled = true;
            installAdd.IsEnabled = true;
            // Placing selects the new item — only once the install landed.
            if (thenPlace && error == null) AddToDesktop(name);
        }
        install.Click += async (_, _) => await Install(thenPlace: false);
        installAdd.Click += async (_, _) => await Install(thenPlace: true);
        inspector.Children.Add(install);
        inspector.Children.Add(installAdd);
        inspector.Children.Add(status);
    }
}
