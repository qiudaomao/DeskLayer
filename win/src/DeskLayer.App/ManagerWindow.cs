// The Manager — the Windows counterpart of the mac SwiftUI 3-pane manager:
//   library | desktop overview | inspector
// v1 scope: add plugins to the desktop, drag items on a virtual overview,
// edit the core item fields, delete. Every commit goes through the
// LayoutStore (non-quiet), which rebuilds the runtime; live no-respawn
// edits arrive with the mac applyLiveEdits parity pass.
//
// Built in code (no XAML) like the rest of the app. Normal activatable
// window — the only one the app shows in the taskbar.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

public sealed class ManagerWindow : Window
{
    private readonly LayoutStore store;
    private readonly PluginRegistry registry;
    private readonly System.Drawing.Rectangle screenBounds;

    private readonly ListBox library = new();
    private readonly Canvas overview = new() { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x33, 0x44)), ClipToBounds = true };
    private readonly StackPanel inspector = new() { Margin = new Thickness(12) };
    private Guid? selectedItemId;

    public ManagerWindow(LayoutStore store, PluginRegistry registry, System.Drawing.Rectangle screenBounds)
    {
        this.store = store;
        this.registry = registry;
        this.screenBounds = screenBounds;

        Title = "DeskLayer Manager";
        Width = 980;
        Height = 560;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        root.Children.Add(Panelize(BuildLibrary(), 0));
        root.Children.Add(Panelize(BuildOverview(), 1));
        root.Children.Add(Panelize(new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 2));
        Content = root;

        store.OnChange += RefreshFromStore;
        registry.DidChange += () => Dispatcher.BeginInvoke(RefreshLibrary);
        Loaded += (_, _) => { RefreshLibrary(); RefreshOverview(); RefreshInspector(); };
        Closed += (_, _) => store.OnChange -= RefreshFromStore;
    }

    private void RefreshFromStore() => Dispatcher.BeginInvoke(() => { RefreshOverview(); RefreshInspector(); });

    private static Border Panelize(UIElement child, int column)
    {
        var border = new Border
        {
            Margin = new Thickness(6),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Child = child,
        };
        Grid.SetColumn(border, column);
        return border;
    }

    // ---- library ----

    private UIElement BuildLibrary()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var header = new TextBlock
        {
            Text = "Plugins",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 4, 4, 8),
        };
        DockPanel.SetDock(header, Dock.Top);

        var add = new Button { Content = "Add to desktop", Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        add.Click += (_, _) => AddSelectedPlugin();
        DockPanel.SetDock(add, Dock.Bottom);

        var create = new Button { Content = "New plugin…", Margin = new Thickness(4, 4, 4, 0), Padding = new Thickness(8, 4, 8, 4) };
        create.Click += (_, _) => CreatePluginTemplate();
        DockPanel.SetDock(create, Dock.Bottom);

        library.Background = Brushes.Transparent;
        library.BorderThickness = new Thickness(0);
        library.Foreground = Brushes.White;

        panel.Children.Add(header);
        panel.Children.Add(add);
        panel.Children.Add(create);
        panel.Children.Add(library);
        return panel;
    }

    private void RefreshLibrary()
    {
        library.Items.Clear();
        foreach (var plugin in registry.Plugins)
            library.Items.Add(plugin.Id);
    }

    /// Writes a starter plugin into the plugins directory; the registry's
    /// watcher picks it up. (The mac's LLM-assisted authoring stays mac-only
    /// for now; authors edit the file with any editor and hot reload does
    /// the rest.)
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
                version: "1.0.0",
                author: "You",
                description: "A starter card.",
                width: 220, height: 90,
                properties,
                render
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

    // ---- overview ----

    private UIElement BuildOverview()
    {
        var host = new Grid { Margin = new Thickness(10) };
        host.Children.Add(overview);
        host.SizeChanged += (_, _) => RefreshOverview();
        return host;
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
                Width = Math.Max(24, frame.W * screenBounds.Width * scale),
                Height = Math.Max(18, frame.H * screenBounds.Height * scale),
                Background = new SolidColorBrush(item.Id == selectedItemId
                    ? Color.FromArgb(0xC0, 0x0A, 0x84, 0xFF)
                    : Color.FromArgb(0x90, 0x55, 0x60, 0x70)),
                BorderBrush = item.Target == RenderTarget.FloatingWindow ? Brushes.Orange : Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = item.PluginId,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    Margin = new Thickness(4, 2, 4, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Tag = item.Id,
                Cursor = Cursors.SizeAll,
                Opacity = item.IsEnabled ? 1 : 0.4,
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
            ? new Size(parent.ActualWidth - 20, parent.ActualHeight - 20)
            : new Size(420, 260);
        return Math.Min(available.Width / screenBounds.Width, available.Height / screenBounds.Height);
    }

    private void WireDrag(Border rect, Guid itemId)
    {
        Point grabOffset = default;
        var dragging = false;

        rect.MouseLeftButtonDown += (_, e) =>
        {
            selectedItemId = itemId;
            RefreshInspector();
            grabOffset = e.GetPosition(rect);
            dragging = true;
            rect.CaptureMouse();
            e.Handled = true;
        };
        rect.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(overview);
            Canvas.SetLeft(rect, Math.Clamp(p.X - grabOffset.X, 0, overview.Width - rect.Width));
            Canvas.SetTop(rect, Math.Clamp(p.Y - grabOffset.Y, 0, overview.Height - rect.Height));
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
                Foreground = Brushes.Gray,
            });
            return;
        }

        TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 2),
        };

        void Commit(Func<LayoutItem, LayoutItem> mutate) => store.Update(layout => layout with
        {
            Items = layout.Items.Select(i => i.Id == item.Id ? mutate(i) : i).ToList(),
        });

        inspector.Children.Add(new TextBlock
        {
            Text = item.PluginId,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
        });

        var enabled = new CheckBox { Content = "Enabled", IsChecked = item.IsEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
        enabled.Checked += (_, _) => Commit(i => i with { IsEnabled = true });
        enabled.Unchecked += (_, _) => Commit(i => i with { IsEnabled = false });
        inspector.Children.Add(enabled);

        inspector.Children.Add(Label("Target"));
        var target = new ComboBox { ItemsSource = new[] { "wallpaper", "floatingWindow" }, SelectedIndex = item.Target == RenderTarget.Wallpaper ? 0 : 1 };
        target.SelectionChanged += (_, _) => Commit(i => i with
        {
            Target = target.SelectedIndex == 0 ? RenderTarget.Wallpaper : RenderTarget.FloatingWindow,
        });
        inspector.Children.Add(target);

        var clickThrough = new CheckBox { Content = "Click-through", IsChecked = item.ClickThrough, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
        clickThrough.Checked += (_, _) => Commit(i => i with { ClickThrough = true });
        clickThrough.Unchecked += (_, _) => Commit(i => i with { ClickThrough = false });
        inspector.Children.Add(clickThrough);

        inspector.Children.Add(Label("Z-order"));
        var zOrder = new TextBox { Text = item.ZOrder.ToString() };
        zOrder.LostFocus += (_, _) => { if (int.TryParse(zOrder.Text, out var z)) Commit(i => i with { ZOrder = z }); };
        inspector.Children.Add(zOrder);

        inspector.Children.Add(Label("Background (CSS color, empty = none)"));
        var background = new TextBox { Text = item.BackgroundColor ?? "" };
        background.LostFocus += (_, _) => Commit(i => i with
        {
            BackgroundColor = background.Text.Length == 0 ? null : background.Text,
        });
        inspector.Children.Add(background);

        inspector.Children.Add(Label("Size (fraction of screen)"));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var width = new TextBox { Text = item.NormalizedFrame.W.ToString("0.###"), Width = 70 };
        var height = new TextBox { Text = item.NormalizedFrame.H.ToString("0.###"), Width = 70, Margin = new Thickness(6, 0, 0, 0) };
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

        // Declared plugin properties — edits become per-item overrides,
        // coerced by the declared valueType exactly like the runtime does.
        var plugin = registry.Plugin(item.PluginId);
        if (plugin != null)
        {
            IReadOnlyList<PluginProperty>? declared = null;
            IReadOnlySet<string>? permissions = null;
            try
            {
                using var probe = PluginInstance.Boot(item.PluginId,
                    File.ReadAllText(plugin.SourcePath), item.PropertyOverrides);
                declared = probe?.Properties;
                permissions = probe?.Permissions;
            }
            catch (IOException) { }

            // Permissions the plugin declares (host powers it can use).
            // These are the plugin's own request, not a per-item toggle —
            // shown so the user knows what the widget can reach.
            if (permissions is { Count: > 0 })
            {
                inspector.Children.Add(new TextBlock
                {
                    Text = "Permissions requested",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 16, 0, 2),
                });
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
                inspector.Children.Add(new TextBlock
                {
                    Text = "Properties",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 16, 0, 0),
                });
                foreach (var property in declared)
                {
                    inspector.Children.Add(Label($"{property.Name} ({property.ValueType})"));
                    var box = new TextBox { Text = property.Value.StringValue };
                    var propertyName = property.Name;
                    var valueType = property.ValueType;
                    box.LostFocus += (_, _) =>
                    {
                        var coerced = PropertyValue.Coerce(box.Text, valueType);
                        if (coerced == null) return;
                        Commit(i =>
                        {
                            var overrides = new Dictionary<string, PropertyValue>(
                                i.PropertyOverrides.ToDictionary(kv => kv.Key, kv => kv.Value))
                            {
                                [propertyName] = coerced.Value,
                            };
                            return i with { PropertyOverrides = overrides };
                        });
                    };
                    inspector.Children.Add(box);
                }
            }
        }

        var delete = new Button
        {
            Content = "Remove from desktop",
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
        };
        delete.Click += (_, _) =>
        {
            selectedItemId = null;
            store.Update(layout => layout with { Items = layout.Items.Where(i => i.Id != item.Id).ToList() });
        };
        inspector.Children.Add(delete);
    }
}
