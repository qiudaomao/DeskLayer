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
    private readonly Canvas overview = new() { ClipToBounds = true };
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

    public ManagerWindow(LayoutStore store, PluginRegistry registry,
                         PluginStoreRegistry storeRegistry, PluginUpdater updater,
                         System.Drawing.Rectangle screenBounds, Action? reopenToggled = null)
    {
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
        grid.Children.Add(Place(BuildOverview(), 1));
        grid.Children.Add(Place(Card(new ScrollViewer
        {
            Content = inspector,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        }), 2));

        // Theme toggle, floated over the center card's top-right corner.
        var themeToggle = new Button
        {
            Content = dark ? "☀" : "☾",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 330, 0),
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = dark ? "Switch to light theme" : "Switch to dark theme",
        };
        themeToggle.Click += (_, _) =>
        {
            PreferDark = !dark;
            var reopen = reopenToggled;
            Close();
            reopen?.Invoke();
        };
        var root = new Grid();
        root.Children.Add(grid);
        root.Children.Add(themeToggle);
        Content = root;

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
        // pre-dump selection hooks exercise the inspector's detail views.
        var dump = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_MANAGER");
        if (!string.IsNullOrEmpty(dump))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_PLUGIN") is { Length: > 0 } pluginSel)
                        SelectPlugin(pluginSel);
                    else if (Environment.GetEnvironmentVariable("DESKLAYER_SELECT_STORE") is { Length: > 0 } storeSel
                             && storeRegistry.Stores.FirstOrDefault(s => s.DisplayName == storeSel) is { } entry)
                        SelectStore(entry.Url);
                    UpdateLayout();
                    DumpToPng(dump);
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

    private static void DumpElementToPng(Window window, string path)
    {
        try
        {
            var w = (int)Math.Max(window.ActualWidth, window.Width);
            var h = (int)Math.Max(window.ActualHeight, 200);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { /* best effort */ }
    }

    private void RegistryChanged() => Dispatcher.BeginInvoke(() => { RefreshSidebar(); RefreshInspector(); });
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
        selectedItemId = null;
        selectedPluginId = null;
        selectedStoreUrl = url;
        selectedStorePlugin = null;
        RefreshSidebar();
        RefreshInspector();
    }

    private void SelectStorePlugin(string storeUrl, string name)
    {
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
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var plus = IconButton("", "Add a plugin or a plugin store",
            () => { RebuildPlusMenu(menu); menu.IsOpen = true; });
        menu.PlacementTarget = plus;
        bottom.Children.Add(plus);
        bottom.Children.Add(IconButton("", "Open plugins folder", OpenPluginsFolder));

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
        var import = new MenuItem { Header = "Add Plugin…" };
        import.Click += (_, _) => ImportPlugins();
        menu.Items.Add(import);
        var create = new MenuItem { Header = "Create Plugin…" };
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
                Header = added ? $"{preset.Name} (added)" : $"Add {preset.Name}",
                IsEnabled = !added,
            };
            var p = preset;
            item.Click += async (_, _) => await storeRegistry.AddStore(p.Url, p.Mirrors);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var addStore = new MenuItem { Header = "Add Plugin Store…" };
        addStore.Click += (_, _) => OpenAddStoreDialog();
        menu.Items.Add(addStore);
    }

    private void RefreshSidebar()
    {
        sidebarPanel.Children.Clear();

        // Installed — everything on disk, whichever store it came from.
        if (registry.Plugins.Count > 0)
        {
            sidebarPanel.Children.Add(GroupHeader("Installed", registry.Plugins.Count, "installed", trailing: null));
            if (!collapsed.Contains("installed"))
                foreach (var plugin in registry.Plugins)
                {
                    var id = plugin.Id;
                    sidebarPanel.Children.Add(SidebarRow(
                        glyph: "",
                        title: id,
                        isSelected: selectedPluginId == id,
                        onSelect: () => SelectPlugin(id),
                        trailing: IconButton("", $"Add {id} to the desktop", () => AddToDesktop(id))));
                }
        }

        // One category per store, mac StoreSection-style.
        foreach (var entry in storeRegistry.Stores)
        {
            var url = entry.Url;
            var key = "store:" + url;
            var refresh = IconButton("", $"Refresh {entry.DisplayName}",
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
                        ? IconButton("", $"Add {name} to the desktop", () => AddToDesktop(name))
                        : IconButton("", $"Install {name}", async () =>
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
                    Text = "Loading…",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(24, 2, 4, 4),
                });
            }
        }

        if (sidebarPanel.Children.Count == 0)
            sidebarPanel.Children.Add(new TextBlock
            {
                Text = "No plugins yet.\nUse + below to add a store.",
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
            Filter = "Plugin scripts (*.js)|*.js",
            Multiselect = true,
            Title = "Choose plugin .js files to import",
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

    private void OpenAddStoreDialog()
    {
        var dialog = new Window
        {
            Title = "Add Plugin Store",
            Owner = this,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Resources = Theme.Load(dark),
            Background = (Brush)FindResource("WindowBg"),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Section("Add Plugin Store"));
        panel.Children.Add(new TextBlock
        {
            Text = "A store is a JSON catalog listing plugins you can install. It becomes its own category in the library.",
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
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close();
        var add = new Button { Content = "Add", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(8, 0, 0, 0) };
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
                error.Text = "Couldn't read a plugin catalog from that URL.";
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
        var header = Section("Desktop");
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
            var rect = new Border
            {
                Width = Math.Max(28, frame.W * screenBounds.Width * scale),
                Height = Math.Max(20, frame.H * screenBounds.Height * scale),
                Background = new SolidColorBrush(item.Id == selectedItemId
                    ? Color.FromArgb(0xE0, 0x0A, 0x84, 0xFF)
                    : Color.FromArgb(0xAA, 0x3A, 0x3A, 0x44)),
                BorderBrush = item.Target == RenderTarget.FloatingWindow
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))
                    : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.SizeAll,
                Opacity = item.IsEnabled ? 1 : 0.4,
                Child = new TextBlock
                {
                    Text = item.PluginId,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    Margin = new Thickness(5, 3, 5, 3),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(rect, frame.X * screenBounds.Width * scale);
            Canvas.SetTop(rect, (1 - frame.Y - frame.H) * screenBounds.Height * scale);
            WireDrag(rect, item.Id);
            overview.Children.Add(rect);
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

    private void WireDrag(Border rect, Guid itemId)
    {
        Point grab = default;
        var dragging = false;
        rect.MouseLeftButtonDown += (_, e) =>
        {
            SelectItem(itemId);
            // Selection rebuilt the overview; find our replacement border to
            // drag, or keep dragging this one (still visually attached).
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

    /// Places a new item centred on the screen, adopting the plugin's
    /// declared point size (converted to a screen fraction) — the mac's
    /// addToDesktop.
    private void AddToDesktop(string pluginId)
    {
        double w = 0.2, h = 0.2;
        var plugin = registry.Plugin(pluginId);
        if (plugin != null)
        {
            try
            {
                var info = PluginMetadata.ExtractInfo(File.ReadAllText(plugin.SourcePath));
                if (info.Width is { } pw && screenBounds.Width > 0) w = Math.Min(pw / screenBounds.Width, 1);
                if (info.Height is { } ph && screenBounds.Height > 0) h = Math.Min(ph / screenBounds.Height, 1);
            }
            catch (IOException) { }
        }
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
            Text = "No Selection",
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondary"),
        });
        inspector.Children.Add(new TextBlock
        {
            Text = "Select an item on the desktop, or a plugin in the sidebar.",
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
                Text = "from " + origin,
                Foreground = (Brush)FindResource("TextSecondary"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
            });

        var enabled = new CheckBox { Content = "Enabled", IsChecked = item.IsEnabled, Margin = new Thickness(0, 12, 0, 0) };
        enabled.Checked += (_, _) => Commit(i => i with { IsEnabled = true });
        enabled.Unchecked += (_, _) => Commit(i => i with { IsEnabled = false });
        inspector.Children.Add(enabled);

        inspector.Children.Add(Caption("Show as"));
        var target = new ComboBox { ItemsSource = new[] { "Wallpaper", "Floating Window" }, SelectedIndex = item.Target == RenderTarget.Wallpaper ? 0 : 1 };
        target.SelectionChanged += (_, _) => Commit(i => i with { Target = target.SelectedIndex == 0 ? RenderTarget.Wallpaper : RenderTarget.FloatingWindow });
        inspector.Children.Add(target);

        if (item.Target == RenderTarget.FloatingWindow)
        {
            var clickThrough = new CheckBox
            {
                Content = "Click-through",
                IsChecked = item.ClickThrough,
                Margin = new Thickness(0, 12, 0, 0),
                ToolTip = "On: clicks pass through to windows beneath. Off: the window accepts mouse events and can be dragged.",
            };
            clickThrough.Checked += (_, _) => Commit(i => i with { ClickThrough = true });
            clickThrough.Unchecked += (_, _) => Commit(i => i with { ClickThrough = false });
            inspector.Children.Add(clickThrough);
        }

        inspector.Children.Add(Caption("Z-order"));
        var zOrder = new TextBox { Text = item.ZOrder.ToString() };
        zOrder.LostFocus += (_, _) => { if (int.TryParse(zOrder.Text, out var z)) Commit(i => i with { ZOrder = z }); };
        inspector.Children.Add(zOrder);

        inspector.Children.Add(Caption("Background (CSS color, empty = none)"));
        var background = new TextBox { Text = item.BackgroundColor ?? "" };
        background.LostFocus += (_, _) => Commit(i => i with { BackgroundColor = background.Text.Length == 0 ? null : background.Text });
        inspector.Children.Add(background);

        inspector.Children.Add(Caption("Size (fraction of screen)"));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var width = new TextBox { Text = item.NormalizedFrame.W.ToString("0.###"), Width = 76 };
        var height = new TextBox { Text = item.NormalizedFrame.H.ToString("0.###"), Width = 76, Margin = new Thickness(8, 0, 0, 0) };
        void CommitSize()
        {
            if (double.TryParse(width.Text, out var w) && double.TryParse(height.Text, out var h) && w > 0.01 && h > 0.01)
                Commit(i => i with { NormalizedFrame = i.NormalizedFrame with { W = w, H = h } });
        }
        width.LostFocus += (_, _) => CommitSize();
        height.LostFocus += (_, _) => CommitSize();
        sizeRow.Children.Add(width);
        sizeRow.Children.Add(height);
        inspector.Children.Add(sizeRow);

        var plugin = registry.Plugin(item.PluginId);
        AddPropertyAndPermissionEditors(item, plugin, Commit);
        AddUpdateControls(item.PluginId, plugin);

        var delete = new Button { Content = "Remove from Desktop", Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 18, 0, 0) };
        delete.Click += (_, _) =>
        {
            selectedItemId = null;
            store.Update(layout => layout with { Items = layout.Items.Where(i => i.Id != item.Id).ToList() });
        };
        inspector.Children.Add(delete);
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
            inspector.Children.Add(Caption("Permissions requested"));
            inspector.Children.Add(new TextBlock
            {
                Text = "⚠ " + string.Join(", ", permissions.OrderBy(p => p)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (declared is { Count: > 0 })
        {
            inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = "Properties", Margin = new Thickness(2, 18, 0, 6) });
            foreach (var property in declared)
            {
                inspector.Children.Add(Caption($"{property.Name} ({property.ValueType})"));
                var box = new TextBox { Text = property.Value.StringValue };
                var name = property.Name;
                var valueType = property.ValueType;
                box.LostFocus += (_, _) =>
                {
                    var coerced = PropertyValue.Coerce(box.Text, valueType);
                    if (coerced == null) return;
                    commit(i =>
                    {
                        var overrides = new Dictionary<string, PropertyValue>(i.PropertyOverrides.ToDictionary(kv => kv.Key, kv => kv.Value)) { [name] = coerced.Value };
                        return i with { PropertyOverrides = overrides };
                    });
                };
                inspector.Children.Add(box);
            }
        }
    }

    private void AddUpdateControls(string pluginId, InstalledPlugin? plugin)
    {
        if (plugin == null) return;
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = "Updates", Margin = new Thickness(2, 18, 0, 6) });

        string? updateUrl = null;
        try { updateUrl = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).updateUrl; }
        catch (IOException) { }

        if (updateUrl != null)
        {
            var auto = new CheckBox { Content = "Auto-update on launch", IsChecked = updater.IsAutoUpdate(pluginId) };
            auto.Checked += (_, _) => updater.SetAutoUpdate(pluginId, true);
            auto.Unchecked += (_, _) => updater.SetAutoUpdate(pluginId, false);
            inspector.Children.Add(auto);

            var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            var check = new Button { Content = "Check for Update", Margin = new Thickness(0, 8, 0, 0) };
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                status.Text = "Checking…";
                try
                {
                    var result = await updater.Check(pluginId, File.ReadAllText(plugin.SourcePath), plugin.SourcePath);
                    status.Text = result.Message;
                    if (result.Outcome == UpdateOutcome.Updated) registry.Rescan();
                }
                catch (Exception ex) { status.Text = "Update failed: " + ex.Message; }
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
                Text = "No update URL declared",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
            });
            return;
        }

        inspector.Children.Add(LabeledRow("Store", source.Value.StoreName));
        string? installedVersion = null;
        try { installedVersion = PluginMetadata.Extract(File.ReadAllText(plugin.SourcePath)).version; }
        catch (IOException) { }
        var listed = source.Value.Plugin.Version;
        var newer = listed != null &&
            (installedVersion == null || PluginUpdater.CompareVersions(listed, installedVersion) > 0);

        var status2 = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (newer)
        {
            status2.Text = $"The store lists {listed}.";
            status2.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        }
        var button = new Button
        {
            Content = newer && listed != null ? $"Update to {listed}" : "Reinstall from Store",
            Margin = new Thickness(0, 4, 0, 0),
        };
        var sp = source.Value;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var error = await storeRegistry.Install(sp.Plugin, sp.StoreName, PluginRegistry.PluginsDirectory);
            registry.Rescan();
            status2.Text = error ?? $"Installed {sp.Plugin.Version ?? ""}".TrimEnd();
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
        PluginMetadata.PluginInfo info = new(null, null, null, null, null, null);
        string? source = null;
        if (plugin != null)
        {
            try { source = File.ReadAllText(plugin.SourcePath); }
            catch (IOException) { }
            if (source != null) info = PluginMetadata.ExtractInfo(source);
        }

        inspector.Children.Add(new TextBlock { Text = pluginId, FontSize = 16, FontWeight = FontWeights.SemiBold });
        var origin = storeRegistry.OriginOf(pluginId);
        inspector.Children.Add(new TextBlock
        {
            Text = origin != null ? $"from {origin}" : "User Installed",
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 8),
        });

        inspector.Children.Add(LabeledRow("Version", info.Version ?? "—"));
        if (info.Author != null) inspector.Children.Add(LabeledRow("Author", info.Author));
        if (info.Description != null)
            inspector.Children.Add(new TextBlock
            {
                Text = info.Description,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        inspector.Children.Add(LabeledRow("On desktop", usageCount == 0 ? "not placed" : $"{usageCount} item{(usageCount == 1 ? "" : "s")}"));

        inspector.Children.Add(Divider());
        AddUpdateControls(pluginId, plugin);

        // Capabilities (mac "Capabilities" section).
        inspector.Children.Add(Divider());
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = "Capabilities", Margin = new Thickness(2, 0, 0, 6) });
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
        inspector.Children.Add(LabeledRow("Permissions",
            permissions is { Count: > 0 } ? string.Join(", ", permissions.OrderBy(p => p)) : "none"));
        if (info.Width is { } dw && info.Height is { } dh)
            inspector.Children.Add(LabeledRow("Default size", $"{(int)dw} × {(int)dh}"));

        // Properties — read-only here: values are edited per placed item.
        inspector.Children.Add(Divider());
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = "Properties", Margin = new Thickness(2, 0, 0, 6) });
        if (declared is { Count: > 0 })
            foreach (var property in declared)
                inspector.Children.Add(LabeledRow(property.Name, property.Value.StringValue));
        else
            inspector.Children.Add(new TextBlock { Text = "No properties declared", FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") });

        // Source: reveal + add-to-desktop + rewrite-with-AI + uninstall.
        inspector.Children.Add(Divider());
        var add = new Button { Content = "Add to Desktop", Style = (Style)FindResource("AccentButton") };
        add.Click += (_, _) => AddToDesktop(pluginId);
        inspector.Children.Add(add);

        if (plugin != null)
        {
            var reveal = new Button { Content = "Show in Explorer", Margin = new Thickness(0, 8, 0, 0) };
            reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{plugin.SourcePath}\"") { UseShellExecute = true });
            inspector.Children.Add(reveal);

            var rewrite = new Button { Content = "Rewrite with AI…", Margin = new Thickness(0, 8, 0, 0) };
            rewrite.Click += (_, _) => OpenCreatePlugin();
            inspector.Children.Add(rewrite);

            var uninstall = new Button { Content = "Uninstall", Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 8, 0, 0) };
            uninstall.Click += (_, _) =>
            {
                var message = usageCount > 0
                    ? $"{usageCount} item{(usageCount == 1 ? "" : "s")} on your desktop use it and will stop rendering."
                    : "The plugin file is deleted.";
                if (MessageBox.Show(this, message, $"Uninstall {pluginId}?",
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
            Text = "Plugin Store",
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 8),
        });

        var total = entry.Catalog?.Plugins.Count ?? 0;
        var installed = entry.Catalog?.Plugins.Count(p => registry.Plugin(p.Name) != null) ?? 0;
        inspector.Children.Add(LabeledRow("Plugins", total.ToString()));
        inspector.Children.Add(LabeledRow("Installed", installed.ToString()));
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
        inspector.Children.Add(Caption("Catalog URL"));
        inspector.Children.Add(new TextBlock
        {
            Text = entry.Url,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (entry.FetchedAt is { } fetched)
            inspector.Children.Add(LabeledRow("Updated", fetched.LocalDateTime.ToString("g")));

        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 8, 0, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await storeRegistry.RefreshAll(true);
            refresh.IsEnabled = true;
        };
        inspector.Children.Add(refresh);

        inspector.Children.Add(Divider());
        var remove = new Button { Content = "Remove Store", Style = (Style)FindResource("DangerButton") };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Its catalog disappears from the library. Installed plugins are untouched.",
                    $"Remove {entry.DisplayName}?", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;
            storeRegistry.RemoveStore(entry.Url);
            selectedStoreUrl = null;
        };
        inspector.Children.Add(remove);
        inspector.Children.Add(new TextBlock
        {
            Text = "Removing a store only drops its listing. Plugins you already installed from it stay on disk.",
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
            Text = $"from {entry?.DisplayName ?? "store"}",
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
        if (plugin?.Version is { } version) inspector.Children.Add(LabeledRow("Version", version));
        if (plugin?.Author is { } author2) inspector.Children.Add(LabeledRow("Author", author2));

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
            var show = new Button { Content = "Show Installed Plugin" };
            show.Click += (_, _) => SelectPlugin(name);
            inspector.Children.Add(show);
            return;
        }
        if (plugin == null || entry == null) return;

        var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var install = new Button { Content = "Install", Style = (Style)FindResource("AccentButton") };
        var installAdd = new Button { Content = "Install & Add to Desktop", Margin = new Thickness(0, 8, 0, 0) };
        async Task Install(bool thenPlace)
        {
            install.IsEnabled = false;
            installAdd.IsEnabled = false;
            status.Text = "Installing…";
            var error = await storeRegistry.Install(plugin, entry.DisplayName, PluginRegistry.PluginsDirectory);
            registry.Rescan();
            status.Text = error ?? "Installed";
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
