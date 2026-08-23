// Plugin store browsing — Core's PluginStoreRegistry does all the work
// (catalogs, mirrors, 24h cache, install with origin recording); this pane
// is the Avalonia shell: preset/one-click add, per-store plugin lists,
// install into the plugins folder the engine and Manager both scan.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class StoresPane : DockPanel
{
    private readonly PluginStoreRegistry stores = new(_ => { });
    private readonly PluginRegistry registry;
    private readonly StackPanel list = new() { Spacing = 10, Margin = new Thickness(14) };
    private readonly TextBlock status = new() { Foreground = Brushes.Gray, Margin = new Thickness(14, 6) };

    public StoresPane(PluginRegistry registry)
    {
        this.registry = registry;

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 10),
        };
        foreach (var preset in PresetStore.All)
        {
            var button = new Button { Content = $"Add {preset.Name}" };
            button.Click += async (_, _) =>
            {
                status.Text = $"adding {preset.Name}…";
                await stores.AddStore(preset.Url, preset.Mirrors);
                Refresh();
            };
            toolbar.Children.Add(button);
        }
        var urlBox = new TextBox { Watermark = "https://…/catalog.json", MinWidth = 220 };
        var addUrl = new Button { Content = "Add Store" };
        addUrl.Click += async (_, _) =>
        {
            var url = (urlBox.Text ?? "").Trim();
            if (url.Length == 0) return;
            status.Text = "adding…";
            var ok = await stores.AddStore(url);
            status.Text = ok ? "" : "couldn't read a catalog from that URL";
            Refresh();
        };
        var refresh = new Button { Content = "⟳ Refresh" };
        refresh.Click += async (_, _) =>
        {
            status.Text = "refreshing…";
            await stores.RefreshAll(force: true);
            Refresh();
        };
        toolbar.Children.Add(urlBox);
        toolbar.Children.Add(addUrl);
        toolbar.Children.Add(refresh);

        SetDock(toolbar, Dock.Top);
        SetDock(status, Dock.Top);
        Children.Add(toolbar);
        Children.Add(status);
        Children.Add(new ScrollViewer { Content = list });

        Refresh();
        _ = InitialFetch();
    }

    private async Task InitialFetch()
    {
        await stores.RefreshAll(force: false);
        await Dispatcher.UIThread.InvokeAsync(Refresh);
    }

    private void Refresh()
    {
        status.Text = "";
        list.Children.Clear();
        if (stores.Stores.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No stores yet — add the Official Store above, or browse the Community tab.",
                Foreground = Brushes.Gray,
            });
            return;
        }
        foreach (var entry in stores.Stores)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = entry.DisplayName, FontWeight = FontWeight.Bold, FontSize = 15 });
            header.Children.Add(new TextBlock
            {
                Text = entry.LastError ?? $"{entry.Catalog?.Plugins.Count ?? 0} plugins",
                Foreground = entry.LastError != null ? Brushes.Orange : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var removeStore = new Button { Content = "✕", FontSize = 10 };
            var url = entry.Url;
            removeStore.Click += (_, _) => { stores.RemoveStore(url); Refresh(); };
            header.Children.Add(removeStore);
            list.Children.Add(header);

            foreach (var plugin in entry.Catalog?.Plugins ?? Array.Empty<StorePlugin>())
                list.Children.Add(PluginRow(plugin, entry.DisplayName));
        }
    }

    private Control PluginRow(StorePlugin plugin, string storeName)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        var installed = registry.Plugin(plugin.Name) != null;
        row.Children.Add(new TextBlock
        {
            Text = $"{plugin.Name} {plugin.Version ?? ""}",
            Width = 220, VerticalAlignment = VerticalAlignment.Center,
        });
        if (installed)
        {
            row.Children.Add(new TextBlock { Text = "installed ✓", Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center });
            var update = new Button { Content = "Reinstall / Update" };
            update.Click += async (_, _) => await Install(plugin, storeName, update);
            row.Children.Add(update);
        }
        else
        {
            var install = new Button { Content = "Install" };
            install.Click += async (_, _) => await Install(plugin, storeName, install);
            row.Children.Add(install);
        }
        if (plugin.Description is { Length: > 0 } d)
            row.Children.Add(new TextBlock
            {
                Text = d, Foreground = Brushes.Gray, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return row;
    }

    private async Task Install(StorePlugin plugin, string storeName, Button button)
    {
        button.IsEnabled = false;
        button.Content = "Installing…";
        var error = await stores.Install(plugin, storeName, PluginRegistry.PluginsDirectory);
        registry.Rescan();
        button.Content = error ?? "Installed ✓";
        if (error == null)
        {
            await Task.Delay(800);
            Refresh();
        }
        else
        {
            button.IsEnabled = true;
        }
    }
}
