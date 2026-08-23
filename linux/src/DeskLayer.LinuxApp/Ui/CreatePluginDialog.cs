// Describe a plugin, and a model writes it — the Linux twin of the win
// CreatePluginDialog (itself the WPF port of the mac CreatePluginSheet).
// The endpoint settings live here rather than in a Preferences window
// because the app has none. Any OpenAI-compatible endpoint works; the key
// is stored via LlmSettings.ApiKey (0600 file on Linux, pending Secret
// Service — mac uses the Keychain, win DPAPI).
//
// All the actual work is Core's PluginAuthorSession: it prompts with the
// shared plugin API docs, runs the tool-call loop, validates, and installs
// nothing until the result passes validation.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core;
using DeskLayer.Core.Llm;
using DeskLayer.Core.Model;

namespace DeskLayer.LinuxApp.Ui;

public sealed class CreatePluginDialog : Window
{
    private readonly PluginAuthorSession author;
    private readonly PluginRegistry registry;

    private readonly ComboBox baseFrom = new() { MinWidth = 220 };
    private readonly StackPanel resultChoice = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 64,
    };
    private readonly TextBox baseUrl = new();
    private readonly TextBox apiKey = new() { PasswordChar = '•' };
    private readonly DockPanel modelRow = new();
    private readonly Button fetchModels = new() { Content = L.T("Fetch Models"), Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel steps = new();
    private readonly ScrollViewer stepsScroll;
    private readonly TextBlock errorText = new()
    {
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
        IsVisible = false,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
    };
    private readonly Button showInstalled = new() { IsVisible = false };
    private readonly Button action = new();
    private readonly Expander endpoint = new() { Margin = new Thickness(0, 10, 0, 0) };

    private bool replacesBase = true;
    /// Set when the user wants a model the endpoint didn't list.
    private bool typesModel;

    /// "Show <installed>" was clicked: the Manager selects that plugin.
    public event Action<string>? ShowInstalled;

    public CreatePluginDialog(PluginAuthorSession author, PluginRegistry registry,
                              string? preselectedPluginId)
    {
        this.author = author;
        this.registry = registry;

        Title = L.T("Create Plugin");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 4 };

        panel.Children.Add(new TextBlock { Text = L.T("Create Plugin"), FontSize = 15, FontWeight = FontWeight.Bold });
        panel.Children.Add(Secondary(L.T("Describe what you want. The model is given DeskLayer's plugin API and writes the JavaScript; nothing is installed until it passes validation.")));

        panel.Children.Add(Caption(L.T("Start from")));
        baseFrom.Items.Add(new ComboBoxItem { Content = L.T("A new plugin") });
        foreach (var plugin in registry.Plugins)
            baseFrom.Items.Add(new ComboBoxItem { Content = plugin.Id, Tag = plugin.Id });
        baseFrom.SelectedIndex = 0;
        // Selecting a plugin in the library first is the natural way to say
        // "change this one".
        if (preselectedPluginId != null && registry.Plugin(preselectedPluginId) != null)
            for (var i = 0; i < baseFrom.Items.Count; i++)
                if (baseFrom.Items[i] is ComboBoxItem { Tag: string id } && id == preselectedPluginId)
                {
                    baseFrom.SelectedIndex = i;
                    break;
                }
        baseFrom.SelectionChanged += (_, _) => { author.ClearResult(); RenderResultChoice(); Render(); };
        panel.Children.Add(baseFrom);
        panel.Children.Add(resultChoice);

        panel.Children.Add(Caption(L.T("What should it do?")));
        panel.Children.Add(prompt);

        // Endpoint settings, collapsed once configured.
        endpoint.Header = new TextBlock { Text = L.T("Endpoint"), FontSize = 11, Foreground = Brushes.Gray };
        var endpointPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Spacing = 3 };
        endpointPanel.Children.Add(Caption(L.T("Base URL")));
        baseUrl.Text = author.Settings.BaseUrl;
        baseUrl.LostFocus += (_, _) => { author.Settings.BaseUrl = (baseUrl.Text ?? "").Trim(); author.Settings.Save(); };
        endpointPanel.Children.Add(baseUrl);

        endpointPanel.Children.Add(Caption(L.T("API key")));
        apiKey.Text = author.ApiKey;
        apiKey.LostFocus += (_, _) => author.ApiKey = apiKey.Text ?? "";
        endpointPanel.Children.Add(apiKey);

        endpointPanel.Children.Add(Caption(L.T("Model")));
        fetchModels.Click += (_, _) => { typesModel = false; author.FetchModels(); };
        endpointPanel.Children.Add(modelRow);
        endpointPanel.Children.Add(Secondary(L.T("Any OpenAI-compatible endpoint. The key is stored with owner-only file permissions."), 10));
        endpoint.Content = endpointPanel;
        // Nudge the user to the settings when there is nothing to call.
        endpoint.IsExpanded = author.ApiKey.Length == 0 || !author.Settings.IsConfigured;
        panel.Children.Add(endpoint);

        stepsScroll = new ScrollViewer
        {
            Content = steps,
            Height = 120,
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false,
        };
        panel.Children.Add(stepsScroll);
        panel.Children.Add(errorText);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        showInstalled.Click += (_, _) =>
        {
            if (author.InstalledPluginId is { } id)
            {
                ShowInstalled?.Invoke(id);
                Close();
            }
        };
        buttons.Children.Add(showInstalled);

        action.Margin = new Thickness(8, 0, 0, 0);
        action.Click += (_, _) =>
        {
            if (author.IsRunning)
            {
                author.Cancel();
                return;
            }
            var text = (prompt.Text ?? "").Trim();
            if (text.Length == 0) return;
            author.Settings.BaseUrl = (baseUrl.Text ?? "").Trim();
            author.ApiKey = apiKey.Text ?? "";
            author.Start(text, Subject());
        };
        DockPanel.SetDock(action, Dock.Right);
        buttons.Children.Add(action);
        var close = new Button { Content = L.T("Close") };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(new Border());   // filler
        panel.Children.Add(buttons);

        Content = panel;

        author.Changed += AuthorChanged;
        Closed += (_, _) => author.Changed -= AuthorChanged;
        RenderResultChoice();
        RenderModelRow();
        Render();
    }

    private static TextBlock Secondary(string text, int size = 11) => new()
    {
        Text = text, FontSize = size, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(2, 6, 0, 0),
    };

    private string? BasePluginId =>
        baseFrom.SelectedItem is ComboBoxItem { Tag: string id } ? id : null;

    private PluginAuthorSession.Subject Subject()
    {
        var base_ = BasePluginId;
        if (base_ == null) return PluginAuthorSession.Subject.New;
        // Store plugins are copied, never replaced — the session enforces the
        // same rule, this just keeps the UI honest about it.
        if (!replacesBase || author.IsStoreInstalled(base_)) return PluginAuthorSession.Subject.Copy(base_);
        return PluginAuthorSession.Subject.Replace(base_);
    }

    private void AuthorChanged() => Dispatcher.UIThread.Post(Render);

    /// Replace-or-copy, shown only when editing an existing plugin.
    private void RenderResultChoice()
    {
        resultChoice.Children.Clear();
        var base_ = BasePluginId;
        if (base_ == null) return;

        if (author.IsStoreInstalled(base_))
        {
            // Replacing in place would be undone by the store's next update,
            // silently. Only the copy is offered.
            resultChoice.Children.Add(Secondary(
                L.T("{0} was installed from a store, so the rewrite is saved as a separate plugin.", base_), 10));
            return;
        }

        var replace = new RadioButton { Content = L.T("Replace {0}", base_), IsChecked = replacesBase, FontSize = 12 };
        var copy = new RadioButton { Content = L.T("Keep both, make a copy"), IsChecked = !replacesBase, FontSize = 12 };
        replace.IsCheckedChanged += (_, _) => { if (replace.IsChecked == true) replacesBase = true; };
        copy.IsCheckedChanged += (_, _) => { if (copy.IsChecked == true) replacesBase = false; };
        resultChoice.Children.Add(replace);
        resultChoice.Children.Add(copy);
        resultChoice.Children.Add(Secondary(L.T("Replacing keeps the current version as {0}.js.bak.", base_), 10));
    }

    /// Fetched models in a picker, or a plain text box — with whatever is
    /// currently selected kept in the list so a hand-typed name doesn't
    /// vanish when the picker appears.
    private void RenderModelRow()
    {
        modelRow.Children.Clear();

        DockPanel.SetDock(fetchModels, Dock.Right);
        modelRow.Children.Add(fetchModels);

        if (author.Settings.CachedModels.Count > 0)
        {
            var toggle = new Button
            {
                Content = typesModel ? "☰" : "✎",
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 4),
            };
            toggle.Click += (_, _) => { typesModel = !typesModel; RenderModelRow(); };
            DockPanel.SetDock(toggle, Dock.Right);
            modelRow.Children.Add(toggle);
        }

        if (author.Settings.CachedModels.Count == 0 || typesModel)
        {
            var box = new TextBox { Text = author.Settings.Model };
            box.LostFocus += (_, _) => { author.Settings.Model = (box.Text ?? "").Trim(); author.Settings.Save(); };
            modelRow.Children.Add(box);
        }
        else
        {
            var choices = new List<string>(author.Settings.CachedModels);
            var current = author.Settings.Model.Trim();
            if (current.Length > 0 && !choices.Contains(current)) choices.Insert(0, current);
            var picker = new ComboBox
            {
                ItemsSource = choices,
                SelectedItem = current.Length > 0 ? current : choices.FirstOrDefault(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is string model) { author.Settings.Model = model; author.Settings.Save(); }
            };
            modelRow.Children.Add(picker);
        }
    }

    /// Everything that changes while a run progresses.
    private void Render()
    {
        // Steps list.
        steps.Children.Clear();
        foreach (var step in author.Steps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = step.IsError ? "⚠" : "✓",
                FontSize = 11,
                Foreground = step.IsError
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))
                    : Brushes.Gray,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            });
            var text = new StackPanel { MaxWidth = 400 };
            text.Children.Add(new TextBlock { Text = step.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            if (step.Detail is { } detail)
                text.Children.Add(Secondary(detail, 10));
            row.Children.Add(text);
            steps.Children.Add(row);
        }
        stepsScroll.IsVisible = author.Steps.Count > 0;
        if (author.Steps.Count > 0) stepsScroll.ScrollToEnd();

        // Error.
        if (author.Error is { } error)
        {
            errorText.Text = "⚠ " + error;
            errorText.IsVisible = true;
        }
        else errorText.IsVisible = false;

        // Buttons.
        if (author.InstalledPluginId is { } installed)
        {
            showInstalled.Content = L.T("Show {0}", installed);
            showInstalled.IsVisible = true;
        }
        else showInstalled.IsVisible = false;

        action.Content = author.IsRunning ? L.T("Stop") : (BasePluginId == null ? L.T("Create") : L.T("Rewrite"));
        fetchModels.Content = author.IsFetchingModels ? L.T("Fetching…") : L.T("Fetch Models");
        fetchModels.IsEnabled = !author.IsFetchingModels && author.Settings.ModelsUrl != null;
        baseFrom.IsEnabled = !author.IsRunning;
        prompt.IsEnabled = !author.IsRunning;

        if (!author.IsFetchingModels) RenderModelRow();
    }
}
