// The Manager — themed, macOS-flavored 3-pane library/overview/inspector plus
// a plugin Store tab. Built in code against Theme.Load() (a dark WPF resource
// dictionary) so every control gets the rounded, spaced, accented look. Edits
// go through the LayoutStore (reconciled live); the Store tab installs plugins
// and the inspector checks per-plugin updates.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskLayer.Core;
using DeskLayer.Core.Js;
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

    private readonly ListBox library = new();
    private readonly Canvas overview = new() { ClipToBounds = true };
    private readonly StackPanel inspector = new() { Margin = new Thickness(14) };
    private Guid? selectedItemId;

    private readonly ListBox storeList = new();
    private readonly StackPanel catalogPanel = new();

    /// Remembered across reopen (theme toggle). Seeded from the Windows theme.
    public static bool PreferDark = Theme.SystemPrefersDark();
    private readonly bool dark = Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_DARK") is { } d ? d == "1" : PreferDark;
    private readonly Action? reopenToggled;
    private readonly TabControl tabs = new() { Margin = new Thickness(14, 12, 14, 14) };

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

        Title = "DeskLayer";
        Width = 1040;
        Height = 620;
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

        tabs.Items.Add(new TabItem { Header = "Desktop", Content = BuildDesktopTab() });
        tabs.Items.Add(new TabItem { Header = "Store", Content = BuildStoreTab() });
        if (Environment.GetEnvironmentVariable("DESKLAYER_DUMP_TAB") == "store") tabs.SelectedIndex = 1;

        // Theme toggle, floated top-right over the tab strip.
        var themeToggle = new Button
        {
            Content = dark ? "☀ Light" : "☾ Dark",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 20, 0),
            Padding = new Thickness(10, 5, 10, 5),
        };
        themeToggle.Click += (_, _) =>
        {
            PreferDark = !dark;
            var reopen = reopenToggled;
            Close();
            reopen?.Invoke();
        };
        var root = new Grid();
        root.Children.Add(tabs);
        root.Children.Add(themeToggle);
        Content = root;

        store.OnChange += RefreshFromStore;
        registry.DidChange += () => Dispatcher.BeginInvoke(RefreshLibrary);
        storeRegistry.DidChange += () => Dispatcher.BeginInvoke(RefreshStoreList);
        Loaded += (_, _) => { RefreshLibrary(); RefreshOverview(); RefreshInspector(); RefreshStoreList(); };
        Closed += (_, _) => store.OnChange -= RefreshFromStore;

        // Debug: render the Manager to a PNG (proves what WPF draws even when
        // a headless screen-capture can't composite the window).
        var dump = Environment.GetEnvironmentVariable("DESKLAYER_DUMP_MANAGER");
        if (!string.IsNullOrEmpty(dump))
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => DumpToPng(dump)));
    }

    private void DumpToPng(string path)
    {
        try
        {
            UpdateLayout();
            var w = (int)Math.Max(ActualWidth, Width);
            var h = (int)Math.Max(ActualHeight, Height);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { /* best effort */ }
    }

    // ---- shared styled helpers ----

    private Border Card(UIElement child) => new()
    {
        Style = (Style)FindResource("Card"),
        Child = child,
    };
    private TextBlock Section(string text) => new() { Style = (Style)FindResource("SectionText"), Text = text };
    private TextBlock Caption(string text) => new() { Style = (Style)FindResource("CaptionText"), Text = text };
    private Button Accent(string text) => new() { Content = text, Style = (Style)FindResource("AccentButton") };

    private void RefreshFromStore() => Dispatcher.BeginInvoke(() => { RefreshOverview(); RefreshInspector(); });

    // ======================================================================
    //  Desktop tab
    // ======================================================================

    private UIElement BuildDesktopTab()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        grid.Children.Add(Place(BuildLibrary(), 0));
        grid.Children.Add(Place(BuildOverview(), 1));
        grid.Children.Add(Place(Card(new ScrollViewer
        {
            Content = inspector,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        }), 2));
        return grid;
    }

    private static UIElement Place(UIElement child, int column)
    {
        var wrap = new Grid { Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0) };
        wrap.Children.Add(child);
        Grid.SetColumn(wrap, column);
        return wrap;
    }

    private UIElement BuildLibrary()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = Section("Plugins");
        DockPanel.SetDock(header, Dock.Top);

        var add = Accent("Add to desktop");
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += (_, _) => AddSelectedPlugin();
        DockPanel.SetDock(add, Dock.Bottom);

        var create = new Button { Content = "New plugin…", Margin = new Thickness(0, 6, 0, 0) };
        create.Click += (_, _) => CreatePluginTemplate();
        DockPanel.SetDock(create, Dock.Bottom);

        panel.Children.Add(header);
        panel.Children.Add(add);
        panel.Children.Add(create);
        panel.Children.Add(library);
        return Card(panel);
    }

    private void RefreshLibrary()
    {
        var previous = library.SelectedItem as string;
        library.Items.Clear();
        foreach (var plugin in registry.Plugins) library.Items.Add(plugin.Id);
        if (previous != null) library.SelectedItem = previous;
    }

    private void CreatePluginTemplate()
    {
        var name = "NewPlugin";
        var index = 1;
        while (File.Exists(Path.Combine(PluginRegistry.PluginsDirectory, $"{name}.js")))
            name = $"NewPlugin{++index}";
        File.WriteAllText(Path.Combine(PluginRegistry.PluginsDirectory, $"{name}.js"), """
            let properties = [
                { "name": "fps", "valueType": "number", "value": "1" },
                { "name": "label", "valueType": "string", "value": "Hello" }
            ];
            const prop = n => properties.find(p => p.name === n).value;
            render = () => view([
                VStack([
                    Text(String(prop("label"))).fontSize(18).bold().textColor("white"),
                    Text("edit me in the plugins folder").fontSize(11).textColor("#ffffff99")
                ]).spacing(6).padding(14).background("#101418e6").cornerRadius(12)
            ]);
            plugin.export = {
                version: "1.0.0", author: "You", description: "A starter card.",
                width: 220, height: 90, properties, render
            };
            """);
        registry.Rescan();
    }

    private void AddSelectedPlugin()
    {
        if (library.SelectedItem is not string pluginId) return;
        var item = new LayoutItem
        {
            Id = Guid.NewGuid(),
            PluginId = pluginId,
            DisplayUuid = "PRIMARY",
            NormalizedFrame = new NormalizedFrame(0.40, 0.40, 0.20, 0.22),
            ZOrder = store.Layout.Items.Count,
        };
        selectedItemId = item.Id;
        store.Update(layout => layout with { Items = layout.Items.Append(item).ToList() });
    }

    private UIElement BuildOverview()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = Section("Desktop");
        DockPanel.SetDock(header, Dock.Top);
        var host = new Grid();
        overview.Background = (Brush)FindResource("OverviewBg");
        host.Children.Add(overview);
        host.SizeChanged += (_, _) => RefreshOverview();
        panel.Children.Add(header);
        panel.Children.Add(host);
        return Card(panel);
    }

    private void RefreshOverview()
    {
        overview.Children.Clear();
        var scale = OverviewScale();
        overview.Width = screenBounds.Width * scale;
        overview.Height = screenBounds.Height * scale;
        overview.HorizontalAlignment = HorizontalAlignment.Center;
        overview.VerticalAlignment = VerticalAlignment.Center;

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
        var available = overview.Parent is FrameworkElement parent && parent.ActualWidth > 40
            ? new Size(parent.ActualWidth - 24, parent.ActualHeight - 24)
            : new Size(440, 280);
        return Math.Min(available.Width / screenBounds.Width, available.Height / screenBounds.Height);
    }

    private void WireDrag(Border rect, Guid itemId)
    {
        Point grab = default;
        var dragging = false;
        rect.MouseLeftButtonDown += (_, e) =>
        {
            selectedItemId = itemId;
            RefreshInspector();
            RefreshOverview();
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

    // ---- inspector ----

    private void RefreshInspector()
    {
        inspector.Children.Clear();
        var item = store.Layout.Items.FirstOrDefault(i => i.Id == selectedItemId);
        if (item == null)
        {
            inspector.Children.Add(new TextBlock
            {
                Text = "Select an item",
                Foreground = (Brush)FindResource("TextSecondary"),
            });
            return;
        }

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

        inspector.Children.Add(Caption("Target"));
        var target = new ComboBox { ItemsSource = new[] { "wallpaper", "floatingWindow" }, SelectedIndex = item.Target == RenderTarget.Wallpaper ? 0 : 1 };
        target.SelectionChanged += (_, _) => Commit(i => i with { Target = target.SelectedIndex == 0 ? RenderTarget.Wallpaper : RenderTarget.FloatingWindow });
        inspector.Children.Add(target);

        var clickThrough = new CheckBox { Content = "Click-through", IsChecked = item.ClickThrough, Margin = new Thickness(0, 12, 0, 0) };
        clickThrough.Checked += (_, _) => Commit(i => i with { ClickThrough = true });
        clickThrough.Unchecked += (_, _) => Commit(i => i with { ClickThrough = false });
        inspector.Children.Add(clickThrough);

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
        AddUpdateControls(item, plugin);

        var delete = new Button { Content = "Remove from desktop", Style = (Style)FindResource("DangerButton"), Margin = new Thickness(0, 18, 0, 0) };
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

    private void AddUpdateControls(LayoutItem item, InstalledPlugin? plugin)
    {
        if (plugin == null) return;
        inspector.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = "Updates", Margin = new Thickness(2, 18, 0, 6) });

        var auto = new CheckBox { Content = "Auto-update on launch", IsChecked = updater.IsAutoUpdate(item.PluginId) };
        auto.Checked += (_, _) => updater.SetAutoUpdate(item.PluginId, true);
        auto.Unchecked += (_, _) => updater.SetAutoUpdate(item.PluginId, false);
        inspector.Children.Add(auto);

        var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "Check for update", Margin = new Thickness(0, 8, 0, 0) };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Checking…";
            try
            {
                var result = await updater.Check(item.PluginId, File.ReadAllText(plugin.SourcePath), plugin.SourcePath);
                status.Text = result.Message;
                if (result.Outcome == UpdateOutcome.Updated) registry.Rescan();
            }
            catch (Exception ex) { status.Text = "Update failed: " + ex.Message; }
            finally { check.IsEnabled = true; }
        };
        inspector.Children.Add(check);
        inspector.Children.Add(status);
    }

    // ======================================================================
    //  Store tab
    // ======================================================================

    private UIElement BuildStoreTab()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: stores + add.
        var left = new DockPanel { Margin = new Thickness(12) };
        var header = Section("Stores");
        DockPanel.SetDock(header, Dock.Top);

        var addRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(addRow, Dock.Bottom);
        var urlBox = new TextBox { };
        var addBtn = Accent("Add");
        addBtn.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(addBtn, Dock.Right);
        addBtn.Click += async (_, _) =>
        {
            var url = urlBox.Text.Trim();
            if (url.Length == 0) return;
            addBtn.IsEnabled = false;
            var ok = await storeRegistry.AddStore(url);
            addBtn.IsEnabled = true;
            if (ok) { urlBox.Clear(); RefreshStoreList(); }
        };
        addRow.Children.Add(addBtn);
        addRow.Children.Add(urlBox);

        var presets = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(presets, Dock.Bottom);
        foreach (var preset in PresetStore.All)
        {
            var b = new Button { Content = "+ " + preset.Name, Margin = new Thickness(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
            var p = preset;
            b.Click += async (_, _) => { await storeRegistry.AddStore(p.Url, p.Mirrors); RefreshStoreList(); };
            presets.Children.Add(b);
        }

        storeList.SelectionChanged += (_, _) => RefreshCatalog();
        left.Children.Add(header);
        left.Children.Add(addRow);
        left.Children.Add(presets);
        left.Children.Add(storeList);
        var leftCard = Card(left);
        Grid.SetColumn(leftCard, 0);

        // Right: catalog.
        var rightCard = Card(new ScrollViewer { Content = catalogPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4) });
        rightCard.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(rightCard, 1);

        grid.Children.Add(leftCard);
        grid.Children.Add(rightCard);
        return grid;
    }

    private static FrameworkElement Wrap(FrameworkElement child, double leftMargin = 0)
    {
        child.Margin = new Thickness(leftMargin, 0, 0, 0);
        return child;
    }

    private void RefreshStoreList()
    {
        var previous = (storeList.SelectedItem as StoreEntry)?.Url;
        storeList.Items.Clear();
        foreach (var entry in storeRegistry.Stores) storeList.Items.Add(entry);
        storeList.DisplayMemberPath = nameof(StoreEntry.DisplayName);
        if (previous != null)
            storeList.SelectedItem = storeRegistry.Stores.FirstOrDefault(s => s.Url == previous);
        else if (storeList.Items.Count > 0) storeList.SelectedIndex = 0;
        RefreshCatalog();
    }

    private void RefreshCatalog()
    {
        catalogPanel.Children.Clear();
        catalogPanel.Margin = new Thickness(12);
        if (storeList.SelectedItem is not StoreEntry entry)
        {
            catalogPanel.Children.Add(new TextBlock { Text = "Add a store to browse plugins.", Foreground = (Brush)FindResource("TextSecondary") });
            return;
        }
        catalogPanel.Children.Add(Section(entry.DisplayName));
        if (entry.LastError is { } err)
            catalogPanel.Children.Add(new TextBlock { Text = err, Foreground = (Brush)FindResource("Danger"), FontSize = 11, Margin = new Thickness(2, 0, 0, 8) });

        var plugins = entry.Catalog?.Plugins ?? Array.Empty<StorePlugin>();
        if (plugins.Count == 0)
            catalogPanel.Children.Add(new TextBlock { Text = "No plugins in this catalog.", Foreground = (Brush)FindResource("TextSecondary") });

        foreach (var plugin in plugins)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            var title = new TextBlock { FontWeight = FontWeights.SemiBold };
            title.Text = plugin.Name + (plugin.Version is { } v ? $"  v{v}" : "");
            info.Children.Add(title);
            if (plugin.Description is { } d)
                info.Children.Add(new TextBlock { Text = d, Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(info, 0);

            var installed = registry.Plugin(plugin.Name.Replace('/', '-')) != null;
            var install = new Button { Content = installed ? "Reinstall" : "Install", VerticalAlignment = VerticalAlignment.Center };
            if (!installed) install.Style = (Style)FindResource("AccentButton");
            var storeName = entry.DisplayName;
            var status = new TextBlock { Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11 };
            install.Click += async (_, _) =>
            {
                install.IsEnabled = false;
                status.Text = "Installing…";
                var error = await storeRegistry.Install(plugin, storeName, PluginRegistry.PluginsDirectory);
                install.IsEnabled = true;
                if (error == null) { status.Text = "Installed"; registry.Rescan(); }
                else status.Text = error;
            };
            Grid.SetColumn(install, 1);

            var card = Card(new Grid());
            var inner = (Grid)card.Child;
            inner.Margin = new Thickness(12, 10, 12, 10);
            var stack = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(info);
            top.Children.Add(install);
            stack.Children.Add(top);
            stack.Children.Add(status);
            inner.Children.Add(stack);
            catalogPanel.Children.Add(card);
        }
    }
}
