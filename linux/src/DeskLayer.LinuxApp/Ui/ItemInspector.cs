// The item inspector — the Linux take on the mac/win right pane: about &
// capabilities, typed property editors (bool/number/color/string), SSH
// destinations for ssh-permission plugins, background color, frame and
// z-order. Every commit goes through LayoutStore; the engine service picks
// it up from the watched layout.json.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskLayer.Core;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class ItemInspector : StackPanel
{
    private readonly LayoutStore store;
    private readonly PluginRegistry registry;
    private readonly Action refreshList;
    private Guid itemId;

    public ItemInspector(LayoutStore store, PluginRegistry registry, Action refreshList)
    {
        this.store = store;
        this.registry = registry;
        this.refreshList = refreshList;
        Spacing = 8;
        Margin = new Thickness(16);
    }

    private LayoutItem? Item() => store.Layout.Items.FirstOrDefault(i => i.Id == itemId);

    private void Mutate(Func<LayoutItem, LayoutItem> change)
    {
        var id = itemId;
        store.Update(l => l with { Items = l.Items.Select(i => i.Id == id ? change(i) : i).ToList() });
    }

    public void Show(Guid id)
    {
        itemId = id;
        Children.Clear();
        var item = Item();
        if (item == null) return;

        Children.Add(new TextBlock { Text = item.PluginId, FontSize = 18, FontWeight = FontWeight.Bold });

        // ---- About ----
        var plugin = registry.Plugin(item.PluginId);
        var source = plugin != null && File.Exists(plugin.SourcePath)
            ? File.ReadAllText(plugin.SourcePath) : null;
        var info = source != null ? PluginMetadata.ExtractInfo(source) : null;
        if (info != null)
        {
            var about = new TextBlock
            {
                Text = string.Join("   ", new[]
                {
                    info.Version is { } v ? $"v{v}" : null,
                    info.Author,
                    info.Width is { } w && info.Height is { } h ? $"{w:0}×{h:0} pt" : null,
                }.Where(s => s != null)),
                Foreground = Brushes.Gray,
            };
            Children.Add(about);
            if (info.Description is { Length: > 0 } description)
                Children.Add(new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    FontSize = 12,
                });
        }

        var enabled = new CheckBox { Content = "Enabled", IsChecked = item.IsEnabled };
        enabled.IsCheckedChanged += (_, _) =>
        {
            Mutate(i => i with { IsEnabled = enabled.IsChecked == true });
            refreshList();
        };
        Children.Add(enabled);

        BuildFrameRow(item);
        BuildZRow(item);
        BuildBackgroundRow(item);
        BuildProperties(item, source);
        BuildSsh(item, source);

        var remove = new Button { Content = "Remove from Desktop", Margin = new Thickness(0, 16, 0, 0) };
        remove.Click += (_, _) =>
        {
            var id = itemId;
            store.Update(l => l with { Items = l.Items.Where(i => i.Id != id).ToList() });
            Children.Clear();
            refreshList();
        };
        Children.Add(remove);
    }

    private void Header(string text) => Children.Add(new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 10, 0, 0),
    });

    // ---- frame / z / background ----

    private void BuildFrameRow(LayoutItem item)
    {
        Header("Frame (normalized 0–1)");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var boxes = new[]
        {
            Num(item.NormalizedFrame.X), Num(item.NormalizedFrame.Y),
            Num(item.NormalizedFrame.W), Num(item.NormalizedFrame.H),
        };
        var labels = new[] { "x", "y", "w", "h" };
        for (var i = 0; i < 4; i++)
        {
            row.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(boxes[i]);
        }
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) =>
        {
            if (Parse(boxes[0]) is { } x && Parse(boxes[1]) is { } y
                && Parse(boxes[2]) is { } w && Parse(boxes[3]) is { } h)
                Mutate(i => i with { NormalizedFrame = new NormalizedFrame(x, y, Math.Max(0.01, w), Math.Max(0.01, h)) });
        };
        row.Children.Add(apply);
        Children.Add(row);
    }

    private void BuildZRow(LayoutItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var label = new TextBlock { Text = $"Z-order: {item.ZOrder}", VerticalAlignment = VerticalAlignment.Center };
        var up = new Button { Content = "▲" };
        var down = new Button { Content = "▼" };
        up.Click += (_, _) => { Mutate(i => i with { ZOrder = i.ZOrder + 1 }); Show(itemId); };
        down.Click += (_, _) => { Mutate(i => i with { ZOrder = i.ZOrder - 1 }); Show(itemId); };
        row.Children.Add(label);
        row.Children.Add(up);
        row.Children.Add(down);
        Children.Add(row);
    }

    private void BuildBackgroundRow(LayoutItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "Background", VerticalAlignment = VerticalAlignment.Center, Width = 90 });
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = BrushFor(item.BackgroundColor),
        };
        var box = new TextBox { Text = item.BackgroundColor ?? "", Watermark = "#00000000 or empty", MinWidth = 140 };
        void Commit()
        {
            var text = (box.Text ?? "").Trim();
            Mutate(i => i with { BackgroundColor = text.Length == 0 ? null : text });
            swatch.Background = BrushFor(text.Length == 0 ? null : text);
        }
        box.LostFocus += (_, _) => Commit();
        row.Children.Add(box);
        row.Children.Add(swatch);
        Children.Add(row);
    }

    // ---- properties ----

    private void BuildProperties(LayoutItem item, string? source)
    {
        var declared = Probe(item.PluginId)?.Properties ?? (IReadOnlyList<PluginProperty>)Array.Empty<PluginProperty>();
        if (declared.Count == 0) return;
        Header("Properties");
        foreach (var property in declared)
        {
            var current = item.PropertyOverrides.TryGetValue(property.Name, out var over) ? over : property.Value;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = property.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center,
            });
            var name = property.Name;
            var valueType = property.ValueType;
            switch (valueType)
            {
                case "bool":
                {
                    var check = new CheckBox { IsChecked = current.BoolValue == true };
                    check.IsCheckedChanged += (_, _) =>
                        CommitOverride(name, PropertyValue.Bool(check.IsChecked == true));
                    row.Children.Add(check);
                    break;
                }
                case "color":
                {
                    var swatch = new Border
                    {
                        Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                        BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                        Background = BrushFor(current.StringValue),
                    };
                    var box = new TextBox { Text = current.StringValue, MinWidth = 120 };
                    box.LostFocus += (_, _) =>
                    {
                        var text = (box.Text ?? "").Trim();
                        if (Rendering.Css.TryParse(text, out _))
                        {
                            CommitOverride(name, PropertyValue.Color(text));
                            swatch.Background = BrushFor(text);
                        }
                    };
                    row.Children.Add(box);
                    row.Children.Add(swatch);
                    break;
                }
                case "number":
                {
                    var box = new TextBox { Text = current.StringValue, MinWidth = 90 };
                    box.LostFocus += (_, _) =>
                    {
                        if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var n))
                            CommitOverride(name, PropertyValue.Number(n));
                    };
                    row.Children.Add(box);
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = current.StringValue, MinWidth = 180 };
                    box.LostFocus += (_, _) => CommitOverride(name, PropertyValue.String(box.Text ?? ""));
                    row.Children.Add(box);
                    break;
                }
            }
            Children.Add(row);
        }
    }

    private void CommitOverride(string name, PropertyValue value) => Mutate(i => i with
    {
        PropertyOverrides = new Dictionary<string, PropertyValue>(i.PropertyOverrides) { [name] = value },
    });

    // ---- ssh destinations ----

    private void BuildSsh(LayoutItem item, string? source)
    {
        var permissions = Probe(item.PluginId)?.Permissions;
        if (permissions == null || !permissions.Contains("ssh")) return;
        Header("SSH Destinations");
        foreach (var host in item.SshHosts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{host.Name} → {(host.UsesAlias ? $"alias {host.Host}" : $"{host.User}@{host.Host}:{host.Port}")}",
                VerticalAlignment = VerticalAlignment.Center,
            });
            var removeHost = new Button { Content = "✕", FontSize = 10 };
            var hostId = host.Id;
            removeHost.Click += (_, _) =>
            {
                Mutate(i => i with { SshHosts = i.SshHosts.Where(h => h.Id != hostId).ToList() });
                Show(itemId);
            };
            row.Children.Add(removeHost);
            Children.Add(row);
        }
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var nameBox = new TextBox { Watermark = "name", Width = 90 };
        var aliasBox = new TextBox { Watermark = "~/.ssh/config alias", Width = 150 };
        var add = new Button { Content = "Add alias" };
        add.Click += (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            var alias = (aliasBox.Text ?? "").Trim();
            if (name.Length == 0 || alias.Length == 0) return;
            Mutate(i => i with
            {
                SshHosts = i.SshHosts.Append(new SshConfig { Name = name, Host = alias, UsesAlias = true }).ToList(),
            });
            Show(itemId);
        };
        addRow.Children.Add(nameBox);
        addRow.Children.Add(aliasBox);
        addRow.Children.Add(add);
        Children.Add(addRow);
        Children.Add(new TextBlock
        {
            Text = "Aliases resolve through this machine's ~/.ssh/config.",
            FontSize = 11, Foreground = Brushes.Gray,
        });
    }

    // ---- helpers ----

    private DeskLayer.Core.Js.PluginInstance? probeCache;
    private string? probeFor;

    private DeskLayer.Core.Js.PluginInstance? Probe(string pluginId)
    {
        if (probeFor == pluginId) return probeCache;
        probeCache?.Dispose();
        probeCache = null;
        probeFor = pluginId;
        var plugin = registry.Plugin(pluginId);
        if (plugin == null || !File.Exists(plugin.SourcePath)) return null;
        try { probeCache = DeskLayer.Core.Js.PluginInstance.Boot(pluginId, File.ReadAllText(plugin.SourcePath)); }
        catch { }
        return probeCache;
    }

    private static IBrush? BrushFor(string? css)
    {
        if (css == null || !Rendering.Css.TryParse(css, out var c)) return Brushes.Transparent;
        return new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
    }

    private static TextBox Num(double value) => new()
    {
        Text = value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
        Width = 64,
    };

    private static double? Parse(TextBox box) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
