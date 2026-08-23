// The placed-item inspector — the Linux twin of the win RenderItemDetail:
// origin, enabled, show-as, z-order, background color, frame in points
// (top-left Y, resize policy honored), typed property editors, SSH
// destinations, update controls, remove. Every commit goes through
// LayoutStore; the engine service picks it up from the watched layout.json.

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
    private readonly PluginStoreRegistry storeRegistry;
    private readonly Func<string, PluginMetadata.PluginInfo> infoFor;
    private readonly Func<(double W, double H)> screenPoints;
    private readonly Func<string, Control?> updateControls;
    private readonly Action onRemoved;
    private Guid itemId;

    public ItemInspector(LayoutStore store, PluginRegistry registry,
                         PluginStoreRegistry storeRegistry,
                         Func<string, PluginMetadata.PluginInfo> infoFor,
                         Func<(double W, double H)> screenPoints,
                         Func<string, Control?> updateControls,
                         Action onRemoved)
    {
        this.store = store;
        this.registry = registry;
        this.storeRegistry = storeRegistry;
        this.infoFor = infoFor;
        this.screenPoints = screenPoints;
        this.updateControls = updateControls;
        this.onRemoved = onRemoved;
        Spacing = 8;
        Margin = new Thickness(14);
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

        Children.Add(new TextBlock { Text = item.PluginId, FontSize = 16, FontWeight = FontWeight.SemiBold });
        if (storeRegistry.OriginOf(item.PluginId) is { } origin)
            Children.Add(new TextBlock
            {
                Text = L.T("from {0}", origin), Foreground = Brushes.Gray, FontSize = 11,
            });

        var enabled = new CheckBox { Content = L.T("Enabled"), IsChecked = item.IsEnabled };
        enabled.IsCheckedChanged += (_, _) => Mutate(i => i with { IsEnabled = enabled.IsChecked == true });
        Children.Add(enabled);

        // Show as — the win combo, with floating still pending on Linux.
        Header(L.T("Show as"));
        var target = new ComboBox
        {
            ItemsSource = new[] { L.T("Wallpaper"), L.T("Floating Window") },
            SelectedIndex = item.Target == RenderTarget.Wallpaper ? 0 : 1,
            IsEnabled = false,
        };
        Children.Add(target);
        Children.Add(new TextBlock
        {
            Text = L.T("Floating windows aren't supported on Linux yet."),
            FontSize = 10, Foreground = Brushes.Gray,
        });

        Header(L.T("Z-order"));
        var zOrder = new TextBox { Text = item.ZOrder.ToString() };
        zOrder.LostFocus += (_, _) => { if (int.TryParse(zOrder.Text, out var z)) Mutate(i => i with { ZOrder = z }); };
        Children.Add(zOrder);

        BuildBackgroundRow(item);
        BuildFrameEditor(item);
        BuildProperties(item);
        BuildSsh(item);
        if (updateControls(item.PluginId) is { } updates) Children.Add(updates);

        var remove = new Button { Content = L.T("Remove from Desktop"), Margin = new Thickness(0, 16, 0, 0) };
        remove.Click += (_, _) =>
        {
            var removeId = itemId;
            store.Update(l => l with { Items = l.Items.Where(i => i.Id != removeId).ToList() });
            Children.Clear();
            onRemoved();
        };
        Children.Add(remove);
    }

    private void Header(string text) => Children.Add(new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 0),
    });

    private void BuildBackgroundRow(LayoutItem item)
    {
        Header(L.T("Background"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = BrushFor(item.BackgroundColor),
        };
        var box = new TextBox { Text = item.BackgroundColor ?? "", Watermark = "#00000000 or empty", MinWidth = 140 };
        box.LostFocus += (_, _) =>
        {
            var text = (box.Text ?? "").Trim();
            Mutate(i => i with { BackgroundColor = text.Length == 0 ? null : text });
            swatch.Background = BrushFor(text.Length == 0 ? null : text);
        };
        row.Children.Add(box);
        row.Children.Add(swatch);
        Children.Add(row);
    }

    // ---- frame: stored normalized, edited in points; X/Y are the top-left
    // corner, height grows downward (the mac FrameEditor model) ----

    private void BuildFrameEditor(LayoutItem item)
    {
        Header(L.T("Frame (points)"));
        var (sw, sh) = screenPoints();
        var frame = item.NormalizedFrame;
        var info = infoFor(item.PluginId);

        var x = new TextBox { Text = Math.Round(frame.X * sw).ToString("0"), Width = 70 };
        var y = new TextBox { Text = Math.Round((1 - frame.Y - frame.H) * sh).ToString("0"), Width = 70 };
        // An axis the plugin sizes from its own content isn't the user's to
        // set: the next render would snap it straight back.
        var w = new TextBox
        {
            Text = Math.Round(frame.W * sw).ToString("0"), Width = 70,
            IsEnabled = info.Resizable && !info.AutoSizeWidth,
        };
        var h = new TextBox
        {
            Text = Math.Round(frame.H * sh).ToString("0"), Width = 70,
            IsEnabled = info.Resizable && !info.AutoSizeHeight,
        };

        void CommitFrame(PluginMetadata.PluginInfo.SizeAxis? edited)
        {
            if (!double.TryParse(x.Text, out var px) || !double.TryParse(y.Text, out var py) ||
                !double.TryParse(w.Text, out var pw) || !double.TryParse(h.Text, out var ph)) return;
            px = Math.Clamp(px, 0, sw);
            py = Math.Clamp(py, 0, sh);
            (pw, ph) = info.ResolvedSize(pw, ph, edited);
            x.Text = Math.Round(px).ToString("0");
            y.Text = Math.Round(py).ToString("0");
            w.Text = Math.Round(pw).ToString("0");
            h.Text = Math.Round(ph).ToString("0");
            var bottom = Math.Max(sh - py - ph, 0);
            Mutate(i => i with
            {
                NormalizedFrame = new NormalizedFrame(
                    Math.Min(px / sw, 1), Math.Min(bottom / sh, 1),
                    Math.Min(pw / sw, 1), Math.Min(ph / sh, 1)),
            });
        }

        Control Field(string label, TextBox box, PluginMetadata.PluginInfo.SizeAxis? axis)
        {
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.Gray });
            box.LostFocus += (_, _) => CommitFrame(axis);
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) CommitFrame(axis); };
            cell.Children.Add(box);
            return cell;
        }

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(Field(L.T("X"), x, null));
        row1.Children.Add(Field(L.T("Y (from top)"), y, null));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        row2.Children.Add(Field(L.T("Width"), w, PluginMetadata.PluginInfo.SizeAxis.Width));
        row2.Children.Add(Field(L.T("Height"), h, PluginMetadata.PluginInfo.SizeAxis.Height));
        Children.Add(row1);
        Children.Add(row2);

        var note = !info.Resizable
            ? L.T("This plugin declares a fixed size (resizable: false).")
            : (info.AutoSizeWidth, info.AutoSizeHeight) switch
            {
                (true, true) => L.T("Width and height follow this plugin's content."),
                (true, false) => L.T("Width follows this plugin's content."),
                (false, true) => L.T("Height follows this plugin's content."),
                _ => null,
            };
        if (note != null)
            Children.Add(new TextBlock
            {
                Text = note, FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            });
    }

    // ---- properties ----

    private void BuildProperties(LayoutItem item)
    {
        var declared = Probe(item.PluginId)?.Properties ?? (IReadOnlyList<PluginProperty>)Array.Empty<PluginProperty>();
        if (declared.Count == 0) return;
        Header(L.T("Properties"));
        foreach (var property in declared)
        {
            var current = item.PropertyOverrides.TryGetValue(property.Name, out var over) ? over : property.Value;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = property.Name, Width = 110, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var name = property.Name;
            switch (property.ValueType)
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
                    var box = new TextBox { Text = current.StringValue, MinWidth = 100 };
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
                    var box = new TextBox { Text = current.StringValue, MinWidth = 80 };
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
                    var box = new TextBox { Text = current.StringValue, MinWidth = 130 };
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

    private void BuildSsh(LayoutItem item)
    {
        var permissions = Probe(item.PluginId)?.Permissions;
        if (permissions == null || !permissions.Contains("ssh")) return;
        Header(L.T("SSH Destinations"));
        foreach (var host in item.SshHosts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{host.Name} → {(host.UsesAlias ? $"alias {host.Host}" : $"{host.User}@{host.Host}:{host.Port}")}",
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
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
        var nameBox = new TextBox { Watermark = L.T("name"), Width = 80 };
        var aliasBox = new TextBox { Watermark = "~/.ssh/config alias", Width = 120 };
        var add = new Button { Content = L.T("Add") };
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
            Text = L.T("Aliases resolve through this machine's ~/.ssh/config."),
            FontSize = 10, Foreground = Brushes.Gray,
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
}
