// The Linux Manager v1 — a deliberately compact Avalonia take on the
// mac/win 3-pane manager (reference: win/src/DeskLayer.App/ManagerWindow.cs,
// 2.2k LOC; this v1 carries the daily-driver subset).
//
// Architecture differs from mac/win on purpose: the wallpaper engine runs
// as a separate systemd service, so the Manager is its own process editing
// the shared wire-format layout.json through Core's LayoutStore. The engine
// watches the file and reconciles; nothing needs IPC.
//
// v1 scope: item list, enable/disable, add installed plugin to desktop,
// remove, frame editing in points, z-order, property editing with
// type-coerced commits. Store browsing / community / LLM dialogs ride the
// next cycle (their Core clients are already cross-platform).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class ManagerWindow : Window
{
    private readonly LayoutStore store = new();
    private readonly PluginRegistry registry = new(watch: true);
    private readonly ListBox itemList = new();
    private readonly StackPanel editor = new() { Spacing = 8, Margin = new Thickness(16) };
    private readonly ComboBox addPlugin = new() { MinWidth = 160 };
    private Guid? selectedId;

    public ManagerWindow()
    {
        Title = "DeskLayer";
        Width = 860;
        Height = 560;

        var addButton = new Button { Content = "Add to Desktop" };
        addButton.Click += (_, _) => AddSelectedPlugin();

        var left = new DockPanel();
        var addRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
        };
        addRow.Children.Add(addPlugin);
        addRow.Children.Add(addButton);
        DockPanel.SetDock(addRow, Dock.Bottom);
        left.Children.Add(addRow);
        left.Children.Add(itemList);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("300,1,*") };
        split.Children.Add(left);
        var divider = new Border { Background = Brushes.Gray, Opacity = 0.3 };
        Grid.SetColumn(divider, 1);
        split.Children.Add(divider);
        var scroll = new ScrollViewer { Content = editor };
        Grid.SetColumn(scroll, 2);
        split.Children.Add(scroll);
        Content = split;

        itemList.SelectionChanged += (_, _) =>
        {
            if (itemList.SelectedItem is ListBoxItem { Tag: Guid id }) ShowEditor(id);
        };

        RefreshItems();
        RefreshPlugins();
        registry.DidChange += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPlugins);
    }

    private void RefreshItems()
    {
        var items = store.Layout.Items;
        itemList.Items.Clear();
        foreach (var item in items)
        {
            itemList.Items.Add(new ListBoxItem
            {
                Content = $"{item.PluginId}{(item.IsEnabled ? "" : "  (off)")}",
                Tag = item.Id,
            });
        }
        if (selectedId is { } id)
        {
            var index = items.ToList().FindIndex(i => i.Id == id);
            if (index >= 0) itemList.SelectedIndex = index;
        }
    }

    private void RefreshPlugins()
    {
        addPlugin.Items.Clear();
        foreach (var plugin in registry.Plugins)
            addPlugin.Items.Add(new ComboBoxItem { Content = plugin.Id, Tag = plugin.Id });
        if (addPlugin.Items.Count > 0 && addPlugin.SelectedIndex < 0) addPlugin.SelectedIndex = 0;
    }

    private void AddSelectedPlugin()
    {
        if (addPlugin.SelectedItem is not ComboBoxItem { Tag: string pluginId }) return;
        var item = new LayoutItem
        {
            Id = Guid.NewGuid(),
            PluginId = pluginId,
            DisplayUuid = "linux-primary",
            NormalizedFrame = new NormalizedFrame(0.4, 0.4, 0.2, 0.2),
            ZOrder = store.Layout.Items.Count == 0 ? 0 : store.Layout.Items.Max(i => i.ZOrder) + 1,
        };
        store.Update(l => l with { Items = l.Items.Append(item).ToList() });
        selectedId = item.Id;
        RefreshItems();
    }

    private LayoutItem? Selected() => store.Layout.Items.FirstOrDefault(i => i.Id == selectedId);

    private void Mutate(Func<LayoutItem, LayoutItem> change)
    {
        if (selectedId is not { } id) return;
        store.Update(l => l with
        {
            Items = l.Items.Select(i => i.Id == id ? change(i) : i).ToList(),
        });
    }

    private void ShowEditor(Guid id)
    {
        selectedId = id;
        editor.Children.Clear();
        var item = Selected();
        if (item == null) return;

        editor.Children.Add(new TextBlock { Text = item.PluginId, FontSize = 18, FontWeight = FontWeight.Bold });

        var enabled = new CheckBox { Content = "Enabled", IsChecked = item.IsEnabled };
        enabled.IsCheckedChanged += (_, _) =>
        {
            Mutate(i => i with { IsEnabled = enabled.IsChecked == true });
            RefreshItems();
        };
        editor.Children.Add(enabled);

        // Frame in normalized coordinates exposed as points of a nominal
        // 1366x768 reference (the engine multiplies by real screen size).
        editor.Children.Add(new TextBlock { Text = "Frame (normalized 0–1)", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
        var frameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fx = FrameBox(item.NormalizedFrame.X);
        var fy = FrameBox(item.NormalizedFrame.Y);
        var fw = FrameBox(item.NormalizedFrame.W);
        var fh = FrameBox(item.NormalizedFrame.H);
        foreach (var (label, box) in new[] { ("x", fx), ("y", fy), ("w", fw), ("h", fh) })
        {
            frameRow.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            frameRow.Children.Add(box);
        }
        var applyFrame = new Button { Content = "Apply" };
        applyFrame.Click += (_, _) =>
        {
            if (Parse(fx) is { } x && Parse(fy) is { } y && Parse(fw) is { } w && Parse(fh) is { } h)
                Mutate(i => i with { NormalizedFrame = new NormalizedFrame(x, y, Math.Max(0.01, w), Math.Max(0.01, h)) });
        };
        frameRow.Children.Add(applyFrame);
        editor.Children.Add(frameRow);

        var zRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        zRow.Children.Add(new TextBlock { Text = $"Z-order: {item.ZOrder}", VerticalAlignment = VerticalAlignment.Center });
        var zUp = new Button { Content = "▲" };
        var zDown = new Button { Content = "▼" };
        zUp.Click += (_, _) => { Mutate(i => i with { ZOrder = i.ZOrder + 1 }); ShowEditor(id); };
        zDown.Click += (_, _) => { Mutate(i => i with { ZOrder = i.ZOrder - 1 }); ShowEditor(id); };
        zRow.Children.Add(zUp);
        zRow.Children.Add(zDown);
        editor.Children.Add(zRow);

        // Properties: declared list comes from a probe boot of the plugin
        // source (no live engine in this process).
        var declared = ProbeProperties(item.PluginId);
        if (declared.Count > 0)
        {
            editor.Children.Add(new TextBlock { Text = "Properties", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var property in declared)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock
                {
                    Text = $"{property.Name} ({property.ValueType})",
                    Width = 180,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var current = item.PropertyOverrides.TryGetValue(property.Name, out var over)
                    ? over : property.Value;
                var box = new TextBox { Text = current.StringValue, MinWidth = 160 };
                var name = property.Name;
                var valueType = property.ValueType;
                box.LostFocus += (_, _) => CommitProperty(name, valueType, box.Text ?? "");
                row.Children.Add(box);
                editor.Children.Add(row);
            }
        }

        var remove = new Button { Content = "Remove from Desktop", Margin = new Thickness(0, 16, 0, 0) };
        remove.Click += (_, _) =>
        {
            store.Update(l => l with { Items = l.Items.Where(i => i.Id != id).ToList() });
            selectedId = null;
            editor.Children.Clear();
            RefreshItems();
        };
        editor.Children.Add(remove);
    }

    private void CommitProperty(string name, string valueType, string text)
    {
        object raw = valueType == "number" && double.TryParse(text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
            : valueType == "bool" ? text.Trim().ToLowerInvariant() is "true" or "1" or "yes"
            : text;
        var coerced = PropertyValue.Coerce(raw, valueType);
        if (coerced == null) return;
        Mutate(i => i with
        {
            PropertyOverrides = new Dictionary<string, PropertyValue>(i.PropertyOverrides)
            {
                [name] = coerced.Value,
            },
        });
    }

    private IReadOnlyList<PluginProperty> ProbeProperties(string pluginId)
    {
        var plugin = registry.Plugin(pluginId);
        if (plugin == null) return Array.Empty<PluginProperty>();
        try
        {
            using var probe = DeskLayer.Core.Js.PluginInstance.Boot(
                pluginId, File.ReadAllText(plugin.SourcePath));
            return probe?.Properties.ToList() ?? (IReadOnlyList<PluginProperty>)Array.Empty<PluginProperty>();
        }
        catch
        {
            return Array.Empty<PluginProperty>();
        }
    }

    private static TextBox FrameBox(double value) => new()
    {
        Text = value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
        Width = 64,
    };

    private static double? Parse(TextBox box) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}

public static class ManagerApp
{
    public static int Run(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);

    private sealed class App : Application
    {
        public override void Initialize() =>
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new ManagerWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
