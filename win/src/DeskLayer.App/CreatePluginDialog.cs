// Describe a plugin, and a model writes it — the WPF port of the mac
// CreatePluginSheet. The endpoint settings live here rather than in a
// Preferences window because the app has none — store URLs are configured
// the same way. Any OpenAI-compatible endpoint works; the key is stored
// DPAPI-encrypted (the mac uses the login Keychain).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskLayer.Core;
using DeskLayer.Core.Llm;
using DeskLayer.Core.Model;

namespace DeskLayer.App;

public sealed class CreatePluginDialog : Window
{
    private readonly PluginAuthorSession author;
    private readonly PluginRegistry registry;

    private readonly ComboBox baseFrom = new();
    private readonly StackPanel resultChoice = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 64,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top,
    };
    private readonly TextBox baseUrl = new();
    private readonly PasswordBox apiKey = new();
    private readonly Grid modelRow = new();
    private readonly Button fetchModels = new() { Content = L.T(L.T("Fetch Models")), Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel steps = new();
    private readonly ScrollViewer stepsScroll;
    private readonly TextBlock errorText = new()
    {
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button showInstalled = new() { Visibility = Visibility.Collapsed };
    private readonly Button action = new();
    private readonly Expander endpoint = new() { Margin = new Thickness(0, 10, 0, 0) };

    private bool replacesBase = true;
    /// Set when the user wants a model the endpoint didn't list.
    private bool typesModel;

    /// "Show <installed>" was clicked: the Manager selects that plugin.
    public event Action<string>? ShowInstalled;

    public CreatePluginDialog(PluginAuthorSession author, PluginRegistry registry,
                              bool dark, string? preselectedPluginId)
    {
        this.author = author;
        this.registry = registry;

        Title = L.T("Create Plugin");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Resources = Theme.Load(dark);
        Background = (Brush)FindResource("WindowBg");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock { Style = (Style)FindResource("SectionText"), Text = L.T("Create Plugin") });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Describe what you want. The model is given DeskLayer's plugin API and writes the JavaScript; nothing is installed until it passes validation."),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = L.T("Start from") });
        baseFrom.Items.Add(L.T("A new plugin"));
        foreach (var plugin in registry.Plugins) baseFrom.Items.Add(plugin.Id);
        baseFrom.SelectedIndex = 0;
        // Selecting a plugin in the library first is the natural way to say
        // "change this one".
        if (preselectedPluginId != null && registry.Plugin(preselectedPluginId) != null)
            baseFrom.SelectedItem = preselectedPluginId;
        baseFrom.SelectionChanged += (_, _) => { author.ClearResult(); RenderResultChoice(); };
        panel.Children.Add(baseFrom);
        panel.Children.Add(resultChoice);

        panel.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = L.T("What should it do?") });
        panel.Children.Add(prompt);

        // Endpoint settings, collapsed once configured.
        endpoint.Header = new TextBlock { Text = L.T("Endpoint"), FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") };
        var endpointPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        endpointPanel.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = L.T("Base URL"), Margin = new Thickness(2, 2, 0, 3) });
        baseUrl.Text = author.Settings.BaseUrl;
        baseUrl.LostFocus += (_, _) => { author.Settings.BaseUrl = baseUrl.Text.Trim(); author.Settings.Save(); };
        endpointPanel.Children.Add(baseUrl);

        endpointPanel.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = L.T("API key"), Margin = new Thickness(2, 8, 0, 3) });
        apiKey.Password = author.ApiKey;
        apiKey.Background = (Brush)FindResource("FieldBg");
        apiKey.Foreground = (Brush)FindResource("TextPrimary");
        apiKey.BorderBrush = (Brush)FindResource("CardBorder");
        apiKey.Padding = new Thickness(8, 5, 8, 5);
        apiKey.PasswordChanged += (_, _) => author.ApiKey = apiKey.Password;
        endpointPanel.Children.Add(apiKey);

        endpointPanel.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = L.T("Model"), Margin = new Thickness(2, 8, 0, 3) });
        fetchModels.Click += (_, _) => { typesModel = false; author.FetchModels(); };
        endpointPanel.Children.Add(modelRow);
        endpointPanel.Children.Add(new TextBlock
        {
            Text = L.T("Any OpenAI-compatible endpoint. The key is stored encrypted for this Windows account."),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        endpoint.Content = endpointPanel;
        // Nudge the user to the settings when there is nothing to call.
        endpoint.IsExpanded = author.ApiKey.Length == 0 || !author.Settings.IsConfigured;
        panel.Children.Add(endpoint);

        stepsScroll = new ScrollViewer
        {
            Content = steps,
            Height = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(stepsScroll);
        panel.Children.Add(errorText);

        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        showInstalled.Click += (_, _) =>
        {
            if (author.InstalledPluginId is { } id)
            {
                ShowInstalled?.Invoke(id);
                Close();
            }
        };
        buttons.Children.Add(showInstalled);

        var close = new Button { Content = L.T("Close") };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        buttons.Children.Add(close);

        action.Margin = new Thickness(8, 0, 0, 0);
        action.Click += (_, _) =>
        {
            if (author.IsRunning)
            {
                author.Cancel();
                return;
            }
            var text = prompt.Text.Trim();
            if (text.Length == 0) return;
            author.Settings.BaseUrl = baseUrl.Text.Trim();
            author.Start(text, Subject());
        };
        Grid.SetColumn(action, 3);
        buttons.Children.Add(action);
        panel.Children.Add(buttons);

        Content = panel;

        author.Changed += AuthorChanged;
        Closed += (_, _) => author.Changed -= AuthorChanged;
        RenderResultChoice();
        RenderModelRow();
        Render();
    }

    private string? BasePluginId => baseFrom.SelectedIndex <= 0 ? null : baseFrom.SelectedItem as string;

    private PluginAuthorSession.Subject Subject()
    {
        var base_ = BasePluginId;
        if (base_ == null) return PluginAuthorSession.Subject.New;
        // Store plugins are copied, never replaced — the session enforces the
        // same rule, this just keeps the UI honest about it.
        if (!replacesBase || author.IsStoreInstalled(base_)) return PluginAuthorSession.Subject.Copy(base_);
        return PluginAuthorSession.Subject.Replace(base_);
    }

    private void AuthorChanged() => Dispatcher.BeginInvoke(Render);

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
            resultChoice.Children.Add(new TextBlock
            {
                Text = L.T("{0} was installed from a store, so the rewrite is saved as a separate plugin.", base_),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var replace = new RadioButton
        {
            Content = L.T("Replace {0}", base_),
            IsChecked = replacesBase,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 12,
        };
        var copy = new RadioButton
        {
            Content = L.T("Keep both, make a copy"),
            IsChecked = !replacesBase,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        replace.Checked += (_, _) => replacesBase = true;
        copy.Checked += (_, _) => replacesBase = false;
        resultChoice.Children.Add(replace);
        resultChoice.Children.Add(copy);
        resultChoice.Children.Add(new TextBlock
        {
            Text = L.T("Replacing keeps the current version as {0}.js.bak.", base_),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 4, 0, 0),
        });
    }

    /// Fetched models in a picker, or a plain text box — with whatever is
    /// currently selected kept in the list so a hand-typed name doesn't
    /// vanish when the picker appears.
    private void RenderModelRow()
    {
        modelRow.Children.Clear();
        modelRow.ColumnDefinitions.Clear();
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement editor;
        if (author.Settings.CachedModels.Count == 0 || typesModel)
        {
            var box = new TextBox { Text = author.Settings.Model };
            box.LostFocus += (_, _) => { author.Settings.Model = box.Text.Trim(); author.Settings.Save(); };
            editor = box;
        }
        else
        {
            var choices = new List<string>(author.Settings.CachedModels);
            var current = author.Settings.Model.Trim();
            if (current.Length > 0 && !choices.Contains(current)) choices.Insert(0, current);
            var picker = new ComboBox { ItemsSource = choices, SelectedItem = current.Length > 0 ? current : choices.FirstOrDefault() };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is string model) { author.Settings.Model = model; author.Settings.Save(); }
            };
            editor = picker;
        }
        modelRow.Children.Add(editor);

        if (author.Settings.CachedModels.Count > 0)
        {
            var toggle = new Button
            {
                Content = typesModel ? "☰" : "✎",
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 4, 8, 4),
                ToolTip = typesModel ? L.T("Choose from the fetched models") : L.T("Type a model name instead"),
            };
            toggle.Click += (_, _) => { typesModel = !typesModel; RenderModelRow(); };
            Grid.SetColumn(toggle, 1);
            modelRow.Children.Add(toggle);
        }
        Grid.SetColumn(fetchModels, 2);
        modelRow.Children.Add(fetchModels);
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
                    : (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            });
            var text = new StackPanel { MaxWidth = 380 };
            text.Children.Add(new TextBlock { Text = step.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            if (step.Detail is { } detail)
                text.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap,
                });
            row.Children.Add(text);
            steps.Children.Add(row);
        }
        stepsScroll.Visibility = author.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (author.Steps.Count > 0) stepsScroll.ScrollToBottom();

        // Error.
        if (author.Error is { } error)
        {
            errorText.Text = "⚠ " + error;
            errorText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            errorText.Visibility = Visibility.Visible;
        }
        else errorText.Visibility = Visibility.Collapsed;

        // Buttons.
        if (author.InstalledPluginId is { } installed)
        {
            showInstalled.Content = L.T("Show {0}", installed);
            showInstalled.Visibility = Visibility.Visible;
        }
        else showInstalled.Visibility = Visibility.Collapsed;

        action.Content = author.IsRunning ? L.T("Stop") : (BasePluginId == null ? L.T("Create") : L.T("Rewrite"));
        action.Style = author.IsRunning ? null : (Style)FindResource("AccentButton");
        fetchModels.Content = author.IsFetchingModels ? L.T("Fetching…") : L.T("Fetch Models");
        fetchModels.IsEnabled = !author.IsFetchingModels && author.Settings.ModelsUrl != null;
        baseFrom.IsEnabled = !author.IsRunning;
        prompt.IsEnabled = !author.IsRunning;

        if (!author.IsFetchingModels) RenderModelRow();
    }
}
