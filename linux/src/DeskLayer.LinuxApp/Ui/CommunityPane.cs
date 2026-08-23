// Community gallery — Core's CommunityClient does the HTTP (same records
// as the win pane); this is the Avalonia shell: sort, search, verified
// filter, pager, install. Installs run through PluginStoreRegistry so
// origins record as "DeskLayer Community" and updates flow like mac/win.
//
// v1 scope: browse + install. Cheer/comment/publish (need sign-in UI) ride
// the next cycle — the client methods are already in Core.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core.Community;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class CommunityPane : DockPanel
{
    private readonly PluginRegistry registry;
    private readonly PluginStoreRegistry stores = new(_ => { });
    private readonly StackPanel list = new() { Spacing = 8, Margin = new Thickness(14) };
    private readonly TextBlock status = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox search = new() { Watermark = "Search", MinWidth = 160 };
    private readonly ComboBox sort = new();
    private readonly CheckBox verifiedOnly = new() { Content = "Verified only", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button prev = new() { Content = "◀" };
    private readonly Button next = new() { Content = "▶" };
    private readonly TextBlock pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private int page = 1;
    private int pages = 1;

    public CommunityPane(PluginRegistry registry)
    {
        this.registry = registry;

        foreach (var label in new[] { "Top Cheered", "Most Downloaded", "Latest" })
            sort.Items.Add(new ComboBoxItem { Content = label });
        sort.SelectedIndex = 0;
        sort.SelectionChanged += (_, _) => _ = Load(1);
        search.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) _ = Load(1); };
        verifiedOnly.IsCheckedChanged += (_, _) => _ = Load(1);
        prev.Click += (_, _) => _ = Load(page - 1);
        next.Click += (_, _) => _ = Load(page + 1);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 10),
        };
        toolbar.Children.Add(sort);
        toolbar.Children.Add(verifiedOnly);
        toolbar.Children.Add(search);
        toolbar.Children.Add(prev);
        toolbar.Children.Add(pageLabel);
        toolbar.Children.Add(next);
        toolbar.Children.Add(status);

        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(new ScrollViewer { Content = list });
        _ = Load(1);
    }

    private GallerySort SelectedSort => sort.SelectedIndex switch
    {
        1 => GallerySort.Downloads,
        2 => GallerySort.Latest,
        _ => GallerySort.Cheers,
    };

    private async Task Load(int requested)
    {
        status.Text = "loading…";
        var result = await CommunityClient.Gallery(SelectedSort, Math.Max(1, requested), 24,
            query: (search.Text ?? "").Trim(), verifiedOnly: verifiedOnly.IsChecked == true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            list.Children.Clear();
            if (result == null)
            {
                status.Text = "couldn't reach the community store";
                return;
            }
            page = result.Page;
            pages = Math.Max(1, result.Pages);
            pageLabel.Text = $"{page} / {pages}";
            prev.IsEnabled = page > 1;
            next.IsEnabled = page < pages;
            status.Text = $"{result.Total} plugins";
            foreach (var plugin in result.Plugins)
                list.Children.Add(Row(plugin));
        });
    }

    private Control Row(GalleryPlugin plugin)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var title = new StackPanel { Width = 250 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        name.Children.Add(new TextBlock { Text = plugin.Name, FontWeight = FontWeight.Bold });
        if (plugin.Verified)
            name.Children.Add(new TextBlock { Text = "✔", Foreground = Brushes.DeepSkyBlue });
        title.Children.Add(name);
        title.Children.Add(new TextBlock
        {
            Text = $"{plugin.Author ?? "?"}   👏 {plugin.Cheers}   ⤓ {plugin.Downloads}",
            Foreground = Brushes.Gray, FontSize = 12,
        });
        row.Children.Add(title);

        var installed = registry.Plugin(plugin.Name) != null;
        var action = new Button { Content = installed ? "Reinstall / Update" : "Install" };
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            action.Content = "Installing…";
            var store = new StorePlugin
            {
                Name = plugin.Name, Url = plugin.Url, Version = plugin.Version,
                Author = plugin.Author, Description = plugin.Description,
            };
            var error = await stores.Install(store, "DeskLayer Community", PluginRegistry.PluginsDirectory);
            registry.Rescan();
            action.Content = error ?? "Installed ✓";
            if (error != null) action.IsEnabled = true;
        };
        row.Children.Add(action);

        if (plugin.Description is { Length: > 0 } d)
            row.Children.Add(new TextBlock
            {
                Text = d, Foreground = Brushes.Gray, FontSize = 12, MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return row;
    }
}
