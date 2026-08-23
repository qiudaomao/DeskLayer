// The Linux Manager — Avalonia take on the mac/win manager (reference:
// win/src/DeskLayer.App/ManagerWindow.cs, 2.2k LOC).
//
// Architecture differs from mac/win on purpose: the wallpaper engine runs
// as a separate systemd service, so the Manager is its own process editing
// the shared wire-format layout.json through Core's LayoutStore. The engine
// watches the file and reconciles; nothing needs IPC beyond two files
// (layout.json and the .paused sentinel).
//
// Tabs: Desktop (item list + typed inspector), Stores (catalog browsing +
// install), Community (gallery browsing + install). The app owns a
// StatusNotifier tray icon; closing the window hides it, Quit lives in the
// tray menu. LLM dialog / publish / floating windows ride the next cycle.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class ManagerWindow : Window
{
    private readonly LayoutStore store = new();
    private readonly PluginRegistry registry = new(watch: true);
    private readonly ListBox itemList = new();
    private readonly ItemInspector inspector;
    private readonly ComboBox addPlugin = new() { MinWidth = 160 };

    public ManagerWindow()
    {
        Title = "DeskLayer";
        Width = 960;
        Height = 620;

        inspector = new ItemInspector(store, registry, RefreshItems);

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Desktop", Content = BuildDesktopTab() },
                new TabItem { Header = "Stores", Content = new StoresPane(registry) },
                new TabItem { Header = "Community", Content = new CommunityPane(registry) },
            },
        };
        Content = tabs;

        // Deep-link hook (also how headless verification drives the tabs):
        // DESKLAYER_MANAGER_TAB=desktop|stores|community
        tabs.SelectedIndex = Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_TAB")?.ToLowerInvariant() switch
        {
            "stores" => 1,
            "community" => 2,
            _ => 0,
        };

        itemList.SelectionChanged += (_, _) =>
        {
            if (itemList.SelectedItem is ListBoxItem { Tag: Guid id }) inspector.Show(id);
        };

        RefreshItems();
        RefreshPlugins();
        registry.DidChange += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPlugins);

        // Headless-verification hook: pre-select the Nth desktop item so the
        // inspector's probe/editor path runs without synthetic input.
        if (int.TryParse(Environment.GetEnvironmentVariable("DESKLAYER_MANAGER_SELECT"), out var preselect)
            && preselect >= 0 && preselect < itemList.Items.Count)
            itemList.SelectedIndex = preselect;
    }

    private Control BuildDesktopTab()
    {
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
        var scroll = new ScrollViewer { Content = inspector };
        Grid.SetColumn(scroll, 2);
        split.Children.Add(scroll);
        return split;
    }

    private void RefreshItems()
    {
        var selected = itemList.SelectedItem is ListBoxItem { Tag: Guid id } ? id : (Guid?)null;
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
        if (selected is { } keep)
        {
            var index = items.ToList().FindIndex(i => i.Id == keep);
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
        RefreshItems();
        var index = store.Layout.Items.ToList().FindIndex(i => i.Id == item.Id);
        if (index >= 0) itemList.SelectedIndex = index;
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
