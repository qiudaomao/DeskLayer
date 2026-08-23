// The Linux Manager — same structure as the win/mac Manager (reference:
// win/src/DeskLayer.App/ManagerWindow.cs): left sidebar with the installed
// library and store categories (inline add/install buttons, "+" menu and
// folder button at the bottom), centre desktop overview with draggable/
// resizable item rects (swaps to the community gallery), and a right
// inspector whose content follows the selection — placed item, installed
// plugin, store, or store-listed plugin.
//
// Architecture differs from mac/win on purpose: the wallpaper engine runs
// as a separate systemd service; both processes edit the wire-format
// layout.json (engine watches it), plus the tray's `.paused` sentinel.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class ManagerWindow : Window
{
    private readonly LayoutStore store = new();
    private readonly PluginRegistry registry = new(watch: true);
    private readonly PluginStoreRegistry storeRegistry = new(_ => { });
    private readonly PluginUpdater updater = new(_ => { });
    private readonly DeskLayer.Core.Llm.PluginAuthorSession author;

    private readonly StackPanel sidebarPanel = new() { Margin = new Thickness(8, 10, 8, 6) };
    private readonly ContentControl centerHost = new();
    private readonly ContentControl inspectorHost = new();
    private readonly Canvas overview = new() { ClipToBounds = true };
    private readonly Grid overviewArea = new();
    private Control? overviewCard;
    private bool showingGallery;
    private readonly ItemInspector itemInspector;

    private Guid? selectedItemId;
    private string? selectedPluginId;
    private string? selectedStoreUrl;
    private (string StoreUrl, string Name)? selectedStorePlugin;
    private readonly HashSet<string> collapsed = new();

    private readonly Dictionary<string, PluginMetadata.PluginInfo> infoCache = new();
    private PixelSize screenPx = new(1366, 768);
    private double scaling = 1.0;

    public ManagerWindow()
    {
        Title = "DeskLayer";
        Width = 1080;
        Height = 640;

        author = new DeskLayer.Core.Llm.PluginAuthorSession(registry, storeRegistry, _ => { });
        itemInspector = new ItemInspector(store, registry, storeRegistry,
            InfoFor, () => (screenPx.Width / scaling, screenPx.Height / scaling),
            UpdateControls, onRemoved: () => { selectedItemId = null; RefreshAll(); });

        var grid = new Grid
        {
            Margin = new Thickness(10),
            ColumnDefinitions = new ColumnDefinitions("236,10,*,10,300"),
        };
        grid.Children.Add(Place(Card(BuildSidebar()), 0));
        overviewCard = BuildOverview();
        centerHost.Content = overviewCard;
        grid.Children.Add(Place(centerHost, 2));
        grid.Children.Add(Place(Card(new ScrollViewer { Content = inspectorHost }), 4));
        Content = grid;

        store.OnChange += () => Dispatcher.UIThread.Post(() => { RefreshOverview(); RefreshInspector(); });
        registry.DidChange += () => Dispatcher.UIThread.Post(() => { infoCache.Clear(); RefreshAll(); });
        storeRegistry.DidChange += () => Dispatcher.UIThread.Post(() => { RefreshSidebar(); RefreshInspector(); });

        Opened += (_, _) =>
        {
            if ((Screens.Primary ?? Screens.All.FirstOrDefault()) is { } screen)
            {
                screenPx = screen.Bounds.Size;
                scaling = screen.Scaling;
            }
            RefreshAll();
            ApplyDebugHooks();
        };
        _ = storeRegistry.RefreshAll(force: false);
    }

    private void RefreshAll()
    {
        RefreshSidebar();
        RefreshOverview();
        RefreshInspector();
    }

    /// Headless-verification hooks: DESKLAYER_MANAGER_TAB=community opens
    /// the gallery, DESKLAYER_MANAGER_SELECT=<n> selects the nth item.
    private void ApplyDebugHooks()
    {
        var tab = Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_TAB")?.ToLowerInvariant();
        if (tab == "community")
            ShowGallery();
        else if (tab == "create")
            OpenCreatePlugin(null);
        else if (int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_SELECT"), out var n)
            && n >= 0 && n < store.Layout.Items.Count)
            SelectItem(store.Layout.Items[n].Id);
    }

    private static Control Place(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Border Card(Control child) => new()
    {
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x90)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x90)),
        BorderThickness = new Thickness(1),
        Child = child,
        ClipToBounds = true,
    };

    /// Declared metadata per plugin — ExtractInfo boots a throwaway Jint
    /// engine, too heavy per overview refresh, so cached until rescans.
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

    // ======================================================================
    //  Selection (mutually exclusive, the mac ManagerSelection)
    // ======================================================================

    private void SelectItem(Guid id)
    {
        ExitGallery();
        selectedItemId = id;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshAll();
    }

    /// Select without rebuilding the overview rects — rebuilding
    /// mid-pointer-down would destroy the border being dragged.
    private void SelectItemInPlace(Guid id)
    {
        selectedItemId = id;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshInspector();
        HighlightOverviewSelection();
    }

    private void SelectPlugin(string id)
    {
        ExitGallery();
        selectedPluginId = id;
        selectedItemId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        RefreshAll();
    }

    private void SelectStore(string url)
    {
        ExitGallery();
        selectedStoreUrl = url;
        selectedItemId = null;
        selectedPluginId = null;
        selectedStorePlugin = null;
        RefreshAll();
    }

    private void SelectStorePlugin(string url, string name)
    {
        ExitGallery();
        selectedStorePlugin = (url, name);
        selectedItemId = null;
        selectedPluginId = null;
        selectedStoreUrl = null;
        RefreshAll();
    }

    // ======================================================================
    //  Sidebar
    // ======================================================================

    private Control BuildSidebar()
    {
        var dock = new DockPanel();

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 4, 8, 6) };
        var plus = new Button { Content = "＋" };
        plus.Click += (_, _) => OpenPlusMenu(plus);
        bottom.Children.Add(plus);
        var folder = new Button { Content = "📁" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", ArgumentList = { PluginRegistry.PluginsDirectory }, UseShellExecute = false,
            });
        };
        bottom.Children.Add(folder);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);

        dock.Children.Add(new ScrollViewer { Content = sidebarPanel });
        return dock;
    }

    private void OpenPlusMenu(Control target)
    {
        var flyout = new MenuFlyout();
        var import = new MenuItem { Header = L.T("Add Plugin…") };
        import.Click += async (_, _) => await ImportPlugins();
        flyout.Items.Add(import);
        var create = new MenuItem { Header = L.T("Create Plugin…") };
        create.Click += (_, _) => OpenCreatePlugin(null);
        flyout.Items.Add(create);
        flyout.Items.Add(new Separator());
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
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var addStore = new MenuItem { Header = L.T("Add Plugin Store…") };
        addStore.Click += async (_, _) =>
        {
            var url = await PromptDialog.Ask(this, L.T("Add Plugin Store"), "https://…/catalog.json");
            if (!string.IsNullOrWhiteSpace(url)) await storeRegistry.AddStore(url.Trim());
        };
        flyout.Items.Add(addStore);
        flyout.ShowAt(target);
    }

    private async Task ImportPlugins()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = L.T("Add Plugin"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("DeskLayer plugin") { Patterns = new[] { "*.js" } },
            },
        });
        foreach (var file in files)
        {
            var path = file.Path.IsFile ? file.Path.LocalPath : null;
            if (path == null) continue;
            Directory.CreateDirectory(PluginRegistry.PluginsDirectory);
            try { File.Copy(path, Path.Combine(PluginRegistry.PluginsDirectory, Path.GetFileName(path)), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        registry.Rescan();
    }

    private void RefreshSidebar()
    {
        sidebarPanel.Children.Clear();
        sidebarPanel.Children.Add(CommunityEntry());

        if (registry.Plugins.Count > 0)
        {
            sidebarPanel.Children.Add(GroupHeader(L.T("Installed"), registry.Plugins.Count, "installed"));
            if (!collapsed.Contains("installed"))
                foreach (var plugin in registry.Plugins)
                {
                    var id = plugin.Id;
                    sidebarPanel.Children.Add(SidebarRow(id,
                        isSelected: selectedPluginId == id,
                        onSelect: () => SelectPlugin(id),
                        trailing: SmallButton("＋", () => AddToDesktop(id))));
                }
        }

        // One category per store; the community store is browsed through the
        // dedicated pane, so it isn't also listed here.
        foreach (var entry in storeRegistry.Stores)
        {
            if (entry.Url == DeskLayer.Core.Community.CommunityClient.CatalogUrl) continue;
            var url = entry.Url;
            var key = "store:" + url;
            sidebarPanel.Children.Add(GroupHeader(entry.DisplayName, entry.Catalog?.Plugins.Count ?? 0, key,
                onSelect: () => SelectStore(url), isSelected: selectedStoreUrl == url));
            if (collapsed.Contains(key)) continue;

            if (entry.Catalog?.Plugins is { Count: > 0 } plugins)
            {
                foreach (var plugin in plugins)
                {
                    var name = plugin.Name;
                    var installed = registry.Plugin(name) != null;
                    var p = plugin;
                    var storeName = entry.DisplayName;
                    var trailing = installed
                        ? SmallButton("＋", () => AddToDesktop(name))
                        : SmallButton("⤓", async () =>
                        {
                            await storeRegistry.Install(p, storeName, PluginRegistry.PluginsDirectory);
                            registry.Rescan();
                        });
                    sidebarPanel.Children.Add(SidebarRow(name,
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
                    Text = "⚠ " + error, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                    Margin = new Thickness(24, 2, 4, 4),
                });
            }
            else
            {
                sidebarPanel.Children.Add(new TextBlock
                {
                    Text = L.T("Loading…"), FontSize = 11, Foreground = Brushes.Gray,
                    Margin = new Thickness(24, 2, 4, 4),
                });
            }
        }

        if (sidebarPanel.Children.Count == 1)
            sidebarPanel.Children.Add(new TextBlock
            {
                Text = L.T("No plugins yet.\nUse + below to add a store."),
                Foreground = Brushes.Gray, FontSize = 12,
                Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap,
            });
    }

    private Control CommunityEntry()
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Background = showingGallery ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "✦  " + L.T("Community"),
                FontWeight = FontWeight.SemiBold,
            },
        };
        if (showingGallery && row.Child is TextBlock active) active.Foreground = Brushes.White;
        row.PointerPressed += (_, _) => ShowGallery();
        return row;
    }

    private Control GroupHeader(string title, int count, string collapseKey,
        Action? onSelect = null, bool isSelected = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 2), Background = Brushes.Transparent };
        var chevron = new TextBlock
        {
            Text = collapsed.Contains(collapseKey) ? "▸" : "▾",
            FontSize = 9, Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray,
        };
        row.Children.Add(chevron);
        var label = new TextBlock
        {
            Text = $"{title}   {count}", FontSize = 11, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!isSelected) label.Foreground = Brushes.Gray;
        row.Children.Add(label);
        row.Cursor = new Cursor(StandardCursorType.Hand);
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            // Chevron edge toggles; the title selects a store or toggles the
            // plain Installed group (the win split).
            if (onSelect != null && e.GetPosition(row).X > 18) onSelect();
            else
            {
                if (!collapsed.Remove(collapseKey)) collapsed.Add(collapseKey);
                RefreshSidebar();
            }
        };
        return row;
    }

    private static Button SmallButton(string glyph, Action onClick)
    {
        var button = new Button { Content = glyph, FontSize = 11, Padding = new Thickness(5, 1) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Control SidebarRow(string title, bool isSelected, Action onSelect,
        Control? trailing, bool secondary = false)
    {
        var grid = new DockPanel();
        if (trailing != null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            grid.Children.Add(trailing);
        }
        var titleBlock = new TextBlock
        {
            Text = title, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (secondary) titleBlock.Foreground = Brushes.Gray;
        grid.Children.Add(titleBlock);
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 4, 4),
            Margin = new Thickness(12, 0, 0, 1),
            Background = isSelected ? new SolidColorBrush(Color.FromArgb(0x50, 0x0A, 0x84, 0xFF)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        row.PointerPressed += (_, _) => onSelect();
        return row;
    }

    // ======================================================================
    //  Community gallery (swaps into the centre column)
    // ======================================================================

    private void ShowGallery()
    {
        if (showingGallery) return;
        showingGallery = true;
        selectedItemId = null;
        selectedPluginId = null;
        selectedStoreUrl = null;
        selectedStorePlugin = null;
        centerHost.Content = Card(new CommunityGalleryView(this, InstallFromGallery,
            name => registry.Plugin(name) != null));
        RefreshSidebar();
        RefreshInspector();
    }

    private void ExitGallery()
    {
        if (!showingGallery) return;
        showingGallery = false;
        centerHost.Content = overviewCard;
        RefreshOverview();
    }

    /// Gallery installs go through the store path with the community entry
    /// registered first — the recorded origin is that entry's display name,
    /// which is how updates later find their source (win parity).
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

    // ======================================================================
    //  Desktop overview (draggable/resizable item rects)
    // ======================================================================

    private Control BuildOverview()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = new TextBlock
        {
            Text = L.T("Desktop"), FontSize = 15, FontWeight = FontWeight.Bold,
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);
        overview.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1e, 0x26));
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x90)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = overview,
            ClipToBounds = true,
        };
        overviewArea.Children.Add(frame);
        overviewArea.SizeChanged += (_, _) => RefreshOverview();
        panel.Children.Add(header);
        panel.Children.Add(overviewArea);
        return Card(panel);
    }

    private double OverviewScale()
    {
        var available = overviewArea.Bounds.Width > 40
            ? new Size(overviewArea.Bounds.Width - 24, overviewArea.Bounds.Height - 24)
            : new Size(440, 280);
        return Math.Min(available.Width / screenPx.Width, available.Height / screenPx.Height);
    }

    private sealed record OverviewParts(Border Rect, Image Snapshot, Border SelectionWash, Border Grip, TextBlock Label);
    private readonly Dictionary<Guid, OverviewParts> overviewParts = new();
    private DispatcherTimer? snapshotTimer;

    private void RefreshOverview()
    {
        if (showingGallery) return;
        overview.Children.Clear();
        overviewParts.Clear();
        var scale = OverviewScale();
        overview.Width = screenPx.Width * scale;
        overview.Height = screenPx.Height * scale;

        foreach (var item in store.Layout.Items)
        {
            var frame = item.NormalizedFrame;
            var isSelected = item.Id == selectedItemId;
            var content = new Grid();
            // Live engine snapshot as the rect's face — the mac behavior.
            var snapshot = new Image { Stretch = Stretch.Fill };
            content.Children.Add(snapshot);
            var wash = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x48, 0x0A, 0x84, 0xFF)),
                CornerRadius = new CornerRadius(4),
                IsVisible = isSelected,
            };
            content.Children.Add(wash);
            // Name label — only until the live snapshot takes over as the
            // face (it would overlap the plugin's own rendering).
            var label = new TextBlock
            {
                Text = item.PluginId, Foreground = Brushes.White, FontSize = 10,
                Margin = new Thickness(5, 3), TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            content.Children.Add(label);
            // Resize grip — selected items only, never on resizable: false.
            var grip = new Border
            {
                Width = 12, Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(5, 0, 4, 0),
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                IsVisible = isSelected && GripUsable(InfoFor(item.PluginId)),
            };
            content.Children.Add(grip);
            var rect = new Border
            {
                Tag = item.Id,
                Width = Math.Max(28, frame.W * screenPx.Width * scale),
                Height = Math.Max(20, frame.H * screenPx.Height * scale),
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x3A, 0x3A, 0x44)),
                BorderBrush = SelectionBorder(isSelected),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Cursor = new Cursor(StandardCursorType.SizeAll),
                ClipToBounds = true,
                Opacity = item.IsEnabled ? 1 : 0.4,
                Child = content,
            };
            Canvas.SetLeft(rect, frame.X * screenPx.Width * scale);
            Canvas.SetTop(rect, (1 - frame.Y - frame.H) * screenPx.Height * scale);
            WireDrag(rect, item.Id);
            WireResize(rect, grip, item.Id);
            overview.Children.Add(rect);
            overviewParts[item.Id] = new OverviewParts(rect, snapshot, wash, grip, label);
        }
        RefreshSnapshots();
        snapshotTimer ??= StartSnapshotTimer();
    }

    private static IBrush SelectionBorder(bool isSelected) => new SolidColorBrush(isSelected
        ? Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF)
        : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    /// Repaints the rect faces from the engine's .snapshots/ files — in
    /// place, so a drag in progress is never interrupted.
    private DispatcherTimer StartSnapshotTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { if (!showingGallery && IsVisible) RefreshSnapshots(); };
        timer.Start();
        return timer;
    }

    private void RefreshSnapshots()
    {
        foreach (var (id, parts) in overviewParts)
        {
            var path = Path.Combine(WallpaperEngine.SnapshotsDirectory, $"{id}.png");
            try
            {
                if (!File.Exists(path)) continue;
                // Through bytes, not the path: Bitmap(path) would hold the
                // file open and block the engine's atomic replace.
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                parts.Snapshot.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                parts.Label.IsVisible = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private static bool GripUsable(PluginMetadata.PluginInfo info) =>
        info.Resizable && !(info.AutoSizeWidth && info.AutoSizeHeight);

    private void HighlightOverviewSelection()
    {
        foreach (var (id, parts) in overviewParts)
        {
            var isSelected = id == selectedItemId;
            parts.Rect.BorderBrush = SelectionBorder(isSelected);
            parts.SelectionWash.IsVisible = isSelected;
            parts.Grip.IsVisible = isSelected &&
                store.Layout.Items.FirstOrDefault(i => i.Id == id) is { } item &&
                GripUsable(InfoFor(item.PluginId));
        }
    }

    private void WireDrag(Border rect, Guid itemId)
    {
        Point grab = default;
        var dragging = false;
        rect.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(rect).Properties.IsLeftButtonPressed) return;
            SelectItemInPlace(itemId);
            grab = e.GetPosition(rect);
            dragging = true;
            e.Pointer.Capture(rect);
            e.Handled = true;
        };
        rect.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(overview);
            Canvas.SetLeft(rect, Math.Clamp(p.X - grab.X, 0, overview.Width - rect.Width));
            Canvas.SetTop(rect, Math.Clamp(p.Y - grab.Y, 0, overview.Height - rect.Height));
        };
        rect.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            var scale = OverviewScale();
            var x = Canvas.GetLeft(rect) / scale / screenPx.Width;
            var top = Canvas.GetTop(rect) / scale / screenPx.Height;
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
        var info = new PluginMetadata.PluginInfo(null, null, null, null, null, null);
        grip.PointerPressed += (_, e) =>
        {
            SelectItemInPlace(itemId);
            info = InfoFor(store.Layout.Items.FirstOrDefault(i => i.Id == itemId)?.PluginId ?? "");
            if (!info.Resizable) return;
            start = e.GetPosition(overview);
            startSize = new Size(rect.Width, rect.Height);
            resizing = true;
            e.Pointer.Capture(grip);
            e.Handled = true;   // don't start the move-drag underneath
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!resizing) return;
            var p = e.GetPosition(overview);
            var scale = OverviewScale();
            var proposedW = (startSize.Width + (p.X - start.X)) / scale / scaling;
            var proposedH = (startSize.Height + (p.Y - start.Y)) / scale / scaling;
            var edited = Math.Abs(p.X - start.X) >= Math.Abs(p.Y - start.Y)
                ? PluginMetadata.PluginInfo.SizeAxis.Width
                : PluginMetadata.PluginInfo.SizeAxis.Height;
            var (w, h) = info.ResolvedSize(proposedW, proposedH, edited);
            // Content-driven axes ignore the drag — the render snaps them back.
            if (info.AutoSizeWidth) w = rect.Width / scale / scaling;
            if (info.AutoSizeHeight) h = rect.Height / scale / scaling;
            var maxW = Math.Max(24, overview.Width - Canvas.GetLeft(rect));
            var maxH = Math.Max(18, overview.Height - Canvas.GetTop(rect));
            rect.Width = Math.Clamp(w * scaling * scale, 24, maxW);
            rect.Height = Math.Clamp(h * scaling * scale, 18, maxH);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!resizing) return;
            resizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            var scale = OverviewScale();
            var (wPts, hPts) = info.ResolvedSize(rect.Width / scale / scaling, rect.Height / scale / scaling, null);
            var w = wPts * scaling / screenPx.Width;
            var h = hPts * scaling / screenPx.Height;
            var top = Canvas.GetTop(rect) / scale / screenPx.Height;
            store.Update(layout => layout with
            {
                Items = layout.Items.Select(item => item.Id == itemId
                    ? item with { NormalizedFrame = item.NormalizedFrame with { W = w, H = h, Y = 1 - top - h } }
                    : item).ToList(),
            });
        };
    }

    /// Places a new item centred, adopting the plugin's declared point size
    /// as a screen fraction — the mac addToDesktop.
    private void AddToDesktop(string pluginId)
    {
        double w = 0.2, h = 0.2;
        var info = InfoFor(pluginId);
        double pointsWide = screenPx.Width / scaling, pointsHigh = screenPx.Height / scaling;
        if (info.Width is { } pw && pointsWide > 0) w = Math.Min(pw / pointsWide, 1);
        if (info.Height is { } ph && pointsHigh > 0) h = Math.Min(ph / pointsHigh, 1);
        var item = new LayoutItem
        {
            Id = Guid.NewGuid(),
            PluginId = pluginId,
            DisplayUuid = "linux-primary",
            NormalizedFrame = new NormalizedFrame(0.5 - w / 2, 0.5 - h / 2, w, h),
            ZOrder = store.Layout.Items.Select(i => i.ZOrder).DefaultIfEmpty(0).Max() + 1,
        };
        store.Update(layout => layout with { Items = layout.Items.Append(item).ToList() });
        SelectItem(item.Id);
    }

    // ======================================================================
    //  Inspector — follows the selection kind (mac InspectorView)
    // ======================================================================

    private void RefreshInspector()
    {
        if (selectedStorePlugin is { } sp) { inspectorHost.Content = StorePluginDetail(sp.StoreUrl, sp.Name); return; }
        if (selectedStoreUrl is { } su) { inspectorHost.Content = StoreDetail(su); return; }
        if (selectedPluginId is { } pid) { inspectorHost.Content = PluginDetail(pid); return; }
        if (store.Layout.Items.FirstOrDefault(i => i.Id == selectedItemId) is { } item)
        {
            itemInspector.Show(item.Id);
            inspectorHost.Content = itemInspector;
            return;
        }
        var empty = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        empty.Children.Add(new TextBlock { Text = L.T("No Selection"), FontWeight = FontWeight.SemiBold, Foreground = Brushes.Gray });
        empty.Children.Add(new TextBlock
        {
            Text = L.T("Select an item on the desktop, or a plugin in the sidebar."),
            FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        });
        inspectorHost.Content = empty;
    }

    private static TextBlock Secondary(string text, int size = 11) => new()
    {
        Text = text, FontSize = size, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
    };

    private static Control LabeledRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var caption = Secondary(label);
        caption.Width = 90;
        row.Children.Add(caption);
        row.Children.Add(new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    private static Control SectionCaption(string text) => new TextBlock
    {
        Text = text, FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 12, 0, 2),
    };

    /// Two-click destructive button (Avalonia has no MessageBox; the second
    /// click within one selection is the confirmation).
    private static Button ConfirmButton(string label, string confirmLabel, Action action)
    {
        var armed = false;
        var button = new Button { Content = label };
        button.Click += (_, _) =>
        {
            if (!armed) { armed = true; button.Content = confirmLabel; return; }
            action();
        };
        return button;
    }

    // ---- installed plugin (library selection) ----

    private Control PluginDetail(string pluginId)
    {
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        var plugin = registry.Plugin(pluginId);
        var usageCount = store.Layout.Items.Count(i => i.PluginId == pluginId);
        var info = InfoFor(pluginId);
        string? source = null;
        if (plugin != null)
        {
            try { source = File.ReadAllText(plugin.SourcePath); }
            catch (IOException) { }
        }

        panel.Children.Add(new TextBlock { Text = pluginId, FontSize = 16, FontWeight = FontWeight.SemiBold });
        var origin = storeRegistry.OriginOf(pluginId);
        panel.Children.Add(Secondary(origin != null ? L.T("from {0}", origin) : L.T("User Installed")));

        panel.Children.Add(LabeledRow(L.T("Version"), info.Version ?? "—"));
        if (info.Author != null) panel.Children.Add(LabeledRow(L.T("Author"), info.Author));
        if (info.Description != null) panel.Children.Add(Secondary(info.Description));
        panel.Children.Add(LabeledRow(L.T("On desktop"),
            usageCount == 0 ? L.T("not placed") : L.T("{0} items", usageCount)));

        if (UpdateControls(pluginId) is { } updates) panel.Children.Add(updates);

        // Capabilities (mac "Capabilities" section).
        panel.Children.Add(SectionCaption(L.T("Capabilities")));
        IReadOnlySet<string>? permissions = null;
        IReadOnlyList<PluginProperty>? declared = null;
        if (source != null)
        {
            try
            {
                using var probe = DeskLayer.Core.Js.PluginInstance.Boot(pluginId, source, new Dictionary<string, PropertyValue>());
                permissions = probe?.Permissions;
                declared = probe?.Properties;
            }
            catch { }
        }
        panel.Children.Add(LabeledRow(L.T("Permissions"),
            permissions is { Count: > 0 } ? string.Join(", ", permissions.OrderBy(p => p)) : L.T("none")));
        if (info.Width is { } dw && info.Height is { } dh)
            panel.Children.Add(LabeledRow(L.T("Default size"), $"{(int)dw} × {(int)dh}"));
        panel.Children.Add(LabeledRow(L.T("Resize"), ResizeSummary(info)));

        // Properties — read-only here: values are edited per placed item.
        panel.Children.Add(SectionCaption(L.T("Properties")));
        if (declared is { Count: > 0 })
            foreach (var property in declared)
                panel.Children.Add(LabeledRow(property.Name, property.Value.StringValue));
        else
            panel.Children.Add(Secondary(L.T("No properties declared")));

        var add = new Button { Content = L.T("Add to Desktop"), Margin = new Thickness(0, 12, 0, 0) };
        add.Click += (_, _) => AddToDesktop(pluginId);
        panel.Children.Add(add);

        if (plugin != null)
        {
            var reveal = new Button { Content = L.T("Show in Files") };
            reveal.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { Path.GetDirectoryName(plugin.SourcePath) ?? PluginRegistry.PluginsDirectory },
                UseShellExecute = false,
            });
            panel.Children.Add(reveal);

            var rewrite = new Button { Content = L.T("Rewrite with AI…") };
            rewrite.Click += (_, _) => OpenCreatePlugin(pluginId);
            panel.Children.Add(rewrite);

            var uninstall = ConfirmButton(L.T("Uninstall"),
                usageCount > 0 ? L.T("Really uninstall? {0} items stop rendering", usageCount) : L.T("Really uninstall?"),
                () =>
                {
                    try
                    {
                        File.Delete(plugin.SourcePath);
                        if (plugin.AssetsDirectory != null) Directory.Delete(plugin.AssetsDirectory, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    selectedPluginId = null;
                    registry.Rescan();
                });
            panel.Children.Add(uninstall);
        }
        return panel;
    }

    private static string ResizeSummary(PluginMetadata.PluginInfo info)
    {
        if (!info.Resizable) return L.T("fixed size");
        var parts = new List<string> { info.KeepsAspect ? L.T("keeps aspect") : L.T("free") };
        if (info.AutoSizeWidth && info.AutoSizeHeight) parts.Add(L.T("auto-sizes"));
        else if (info.AutoSizeHeight) parts.Add(L.T("auto height"));
        else if (info.AutoSizeWidth) parts.Add(L.T("auto width"));
        return string.Join(", ", parts);
    }

    // ---- updates (shared by placed-item and plugin inspectors) ----

    private Control? UpdateControls(string pluginId)
    {
        var plugin = registry.Plugin(pluginId);
        if (plugin == null) return null;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(SectionCaption(L.T("Updates")));

        string? updateUrl = null;
        try { updateUrl = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).updateUrl; }
        catch (IOException) { }

        if (updateUrl != null)
        {
            var auto = new CheckBox { Content = L.T("Auto-update on launch"), IsChecked = updater.IsAutoUpdate(pluginId) };
            auto.IsCheckedChanged += (_, _) => updater.SetAutoUpdate(pluginId, auto.IsChecked == true);
            panel.Children.Add(auto);
            var status = Secondary("");
            var check = new Button { Content = L.T("Check for Update") };
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
            panel.Children.Add(check);
            panel.Children.Add(status);
            return panel;
        }

        // No updateURL — the catalog is the source of truth (win/mac rule).
        var storeSource = StoreSourceFor(pluginId);
        if (storeSource == null)
        {
            panel.Children.Add(Secondary(L.T("No update URL declared")));
            return panel;
        }
        panel.Children.Add(LabeledRow(L.T("Store"), storeSource.Value.StoreName));
        string? installedVersion = null;
        try { installedVersion = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).version; }
        catch (IOException) { }
        var listed = storeSource.Value.Plugin.Version;
        var newer = listed != null &&
            (installedVersion == null || PluginUpdater.CompareVersions(listed, installedVersion) > 0);
        var status2 = Secondary(newer ? L.T("The store lists {0}.", listed!) : "");
        if (newer) status2.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        var button = new Button
        {
            Content = newer && listed != null ? L.T("Update to {0}", listed) : L.T("Reinstall from Store"),
        };
        var sp = storeSource.Value;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var error = await storeRegistry.Install(sp.Plugin, sp.StoreName, PluginRegistry.PluginsDirectory);
            registry.Rescan();
            status2.Text = error ?? L.T("Installed {0}", sp.Plugin.Version ?? "").TrimEnd();
            button.IsEnabled = true;
        };
        panel.Children.Add(button);
        panel.Children.Add(status2);
        return panel;
    }

    private void OpenCreatePlugin(string? preselectedPluginId)
    {
        var dialog = new CreatePluginDialog(author, registry, preselectedPluginId);
        dialog.ShowInstalled += id => SelectPlugin(id);
        _ = dialog.ShowDialog(this);
    }

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

    // ---- store (category selection) ----

    private Control StoreDetail(string storeUrl)
    {
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        var entry = storeRegistry.Stores.FirstOrDefault(s => s.Url == storeUrl);
        if (entry == null) return panel;

        panel.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 16, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Secondary(L.T("Plugin Store")));

        var total = entry.Catalog?.Plugins.Count ?? 0;
        var installed = entry.Catalog?.Plugins.Count(p => registry.Plugin(p.Name) != null) ?? 0;
        panel.Children.Add(LabeledRow(L.T("Plugins"), total.ToString()));
        panel.Children.Add(LabeledRow(L.T("Installed"), installed.ToString()));
        if (entry.LastError is { } error)
        {
            var warn = Secondary("⚠ " + error);
            warn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            panel.Children.Add(warn);
        }

        panel.Children.Add(SectionCaption(L.T("Catalog URL")));
        panel.Children.Add(Secondary(entry.Url));
        if (entry.FetchedAt is { } fetched)
            panel.Children.Add(LabeledRow(L.T("Updated"), fetched.LocalDateTime.ToString("g")));

        var refresh = new Button { Content = L.T("Refresh"), Margin = new Thickness(0, 8, 0, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await storeRegistry.RefreshAll(true);
            refresh.IsEnabled = true;
        };
        panel.Children.Add(refresh);

        panel.Children.Add(ConfirmButton(L.T("Remove Store"), L.T("Really remove?"), () =>
        {
            storeRegistry.RemoveStore(entry.Url);
            selectedStoreUrl = null;
            RefreshAll();
        }));
        panel.Children.Add(Secondary(L.T("Removing a store only drops its listing. Plugins you already installed from it stay on disk."), 10));
        return panel;
    }

    // ---- store-listed plugin (maybe not installed) ----

    private Control StorePluginDetail(string storeUrl, string name)
    {
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        var entry = storeRegistry.Stores.FirstOrDefault(s => s.Url == storeUrl);
        var plugin = entry?.Catalog?.Plugins.FirstOrDefault(p => p.Name == name);
        var isInstalled = registry.Plugin(name) != null;

        panel.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Secondary(L.T("from {0}", entry?.DisplayName ?? L.T("Store"))));

        if (plugin?.Description is { } description) panel.Children.Add(Secondary(description));
        if (plugin?.Version is { } version) panel.Children.Add(LabeledRow(L.T("Version"), version));
        if (plugin?.Author is { } author) panel.Children.Add(LabeledRow(L.T("Author"), author));
        if (plugin?.Cheers is { } cheers)
            panel.Children.Add(LabeledRow(L.T("Community"), L.T("{0} cheers · {1} comments", cheers, plugin.Comments ?? 0)));
        if (plugin?.Verified == true)
        {
            var verified = Secondary("✓ " + L.T("Verified by staff"));
            verified.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            panel.Children.Add(verified);
        }
        if (plugin?.TopicUrl is { } topic)
        {
            var discuss = new Button { Content = L.T("Discuss on the Forum"), Margin = new Thickness(0, 8, 0, 0) };
            discuss.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", ArgumentList = { topic }, UseShellExecute = false,
            });
            panel.Children.Add(discuss);
        }

        if (isInstalled)
        {
            var mark = Secondary("✓ " + L.T("Installed"), 12);
            mark.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            mark.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(mark);
            var show = new Button { Content = L.T("Show Installed Plugin") };
            show.Click += (_, _) => SelectPlugin(name);
            panel.Children.Add(show);
            return panel;
        }
        if (plugin == null || entry == null) return panel;

        var status = Secondary("");
        var install = new Button { Content = L.T("Install"), Margin = new Thickness(0, 8, 0, 0) };
        var installAdd = new Button { Content = L.T("Install & Add to Desktop") };
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
            if (thenPlace && error == null) AddToDesktop(name);
        }
        install.Click += async (_, _) => await Install(thenPlace: false);
        installAdd.Click += async (_, _) => await Install(thenPlace: true);
        panel.Children.Add(install);
        panel.Children.Add(installAdd);
        panel.Children.Add(status);
        return panel;
    }
}

/// A minimal text prompt (Avalonia ships no input dialog).
public static class PromptDialog
{
    public static async Task<string?> Ask(Window owner, string title, string watermark)
    {
        var box = new TextBox { Watermark = watermark, MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(owner);
    }
}

public static class ManagerApp
{
    public static int Run(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);

    private sealed class App : Application
    {
        private ManagerWindow? window;

        public override void Initialize() =>
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // The tray icon owns the app's lifetime; closing the window
                // just hides it (the mac/win menubar-app model).
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window = new ManagerWindow();
                desktop.MainWindow = window;
                window.Closing += (_, e) => { e.Cancel = true; window.Hide(); };
                SetupTray(desktop);
            }
            base.OnFrameworkInitializationCompleted();
        }

        private static string PausedSentinel =>
            Path.Combine(LayoutStore.DataDirectory, ".paused");

        private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var pause = new NativeMenuItem(PauseLabel());
            pause.Click += (_, _) =>
            {
                if (File.Exists(PausedSentinel)) File.Delete(PausedSentinel);
                else File.WriteAllText(PausedSentinel, "");
                pause.Header = PauseLabel();
            };

            var open = new NativeMenuItem("Open Manager");
            open.Click += (_, _) => ShowWindow();

            var restart = new NativeMenuItem("Restart Engine");
            restart.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "systemctl",
                ArgumentList = { "--user", "restart", "desklayer" },
                UseShellExecute = false,
            });

            var quit = new NativeMenuItem("Quit DeskLayer Manager");
            quit.Click += (_, _) => desktop.Shutdown();

            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(pause);
            menu.Items.Add(restart);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon
            {
                ToolTipText = "DeskLayer",
                Icon = TrayIconImage(),
                Menu = menu,
            };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }

        private void ShowWindow()
        {
            if (window == null) return;
            window.Show();
            window.Activate();
        }

        private static string PauseLabel() =>
            File.Exists(PausedSentinel) ? "Resume Wallpaper" : "Pause Wallpaper";

        // No bundled asset pipeline yet — draw the icon (rounded square,
        // "DL") with Skia and hand Avalonia the PNG.
        private static WindowIcon TrayIconImage()
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(64, 64));
            var canvas = surface.Canvas;
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using var back = new SkiaSharp.SKPaint
            {
                Color = new SkiaSharp.SKColor(0x2b, 0x6c, 0xb8), IsAntialias = true,
            };
            canvas.DrawRoundRect(new SkiaSharp.SKRect(4, 4, 60, 60), 14, 14, back);
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 30) { Embolden = true };
            using var text = new SkiaSharp.SKPaint(font) { Color = SkiaSharp.SKColors.White, IsAntialias = true };
            var width = text.MeasureText("DL");
            canvas.DrawText("DL", (64 - width) / 2f, 43, font, text);
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            return new WindowIcon(stream);
        }
    }
}
