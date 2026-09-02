// A community plugin's detail view — the Linux twin of the win
// CommunityDetailDialog: full-size preview, description, Install, and the
// social layer (live cheer toggle, forum comment thread with a compose box).
// Reads are anonymous; cheering and commenting need the signed-in forum
// account, and when signed out those controls invite sign-in rather than
// acting. Detail is read live so a fresh cheer never snaps back to a cached
// count; backend errors (already localized by the store) show verbatim.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.LinuxApp.Ui;

public sealed class CommunityDetailDialog : Window
{
    private readonly GalleryPlugin plugin;
    private readonly string slug;
    private readonly Func<GalleryPlugin, Task<string?>> install;
    private readonly Action? onChanged;
    private readonly Button deleteButton = new() { IsVisible = false };
    private bool deleteArmed;

    private readonly Button cheerButton = new();
    private readonly Button installButton = new() { Content = L.T("Install") };
    private readonly TextBlock installStatus = new() { FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel commentList = new() { Spacing = 10 };
    private readonly TextBox compose = new() { Watermark = L.T("Write a comment…"), AcceptsReturn = true, MinHeight = 56 };
    private readonly Button send = new() { Content = L.T("Send") };
    private readonly TextBlock composeHint = new() { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

    private bool cheered;
    private int cheers;

    public CommunityDetailDialog(GalleryPlugin plugin,
                                 Func<GalleryPlugin, Task<string?>> install,
                                 Func<string, bool> isInstalled,
                                 Action? onChanged = null)
    {
        this.plugin = plugin;
        this.install = install;
        this.onChanged = onChanged;
        slug = plugin.Slug ?? plugin.Name;
        cheers = plugin.Cheers;

        Title = plugin.Name;
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 4 };

        panel.Children.Add(new TextBlock { Text = plugin.Name, FontSize = 18, FontWeight = FontWeight.SemiBold });
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (plugin.Author is { Length: > 0 } author)
            sub.Children.Add(new TextBlock { Text = L.T("by {0}", author), FontSize = 12, Foreground = Brushes.Gray });
        if (plugin.Verified)
            sub.Children.Add(new TextBlock
            {
                Text = "   ✓ " + L.T("Verified by staff"), FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
            });
        panel.Children.Add(sub);

        // Advisory only — staff Verified stays the strong signal.
        if (plugin.AiReview == "checked")
            panel.Children.Add(new TextBlock
            {
                Text = "⚙ " + L.T("Checked by AI — automated review only; still look over the source before trusting a plugin."),
                FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            });
        else if (plugin.AiReview == "pending")
            panel.Children.Add(new TextBlock
            {
                Text = L.T("AI review pending."), FontSize = 11, Foreground = Brushes.Gray,
            });
        else if (plugin.AiReview == "blocked")
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ " + L.T("Blocked by AI review{0}", plugin.AiReviewNote is { Length: > 0 } note ? $": {note}" : "."),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            });

        // Full-size preview (not the thumbnail).
        var previewHost = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x90)),
            Margin = new Thickness(0, 0, 0, 12),
            ClipToBounds = true,
            IsVisible = false,
        };
        panel.Children.Add(previewHost);
        if (plugin.Preview is { Length: > 0 } previewUrl)
            WebImage.Into(previewUrl, bitmap =>
            {
                previewHost.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                previewHost.IsVisible = true;
            });

        // Action row: Install + Cheer.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        installButton.Click += async (_, _) => await Install();
        cheerButton.Click += async (_, _) => await ToggleCheer();
        actions.Children.Add(installButton);
        actions.Children.Add(cheerButton);
        actions.Children.Add(installStatus);
        panel.Children.Add(actions);
        UpdateCheerLabel();
        if (isInstalled(plugin.Name)) MarkInstalled();

        if (plugin.Description is { Length: > 0 } description)
            panel.Children.Add(new TextBlock
            {
                Text = description, FontSize = 12, Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            });

        if (plugin.TopicUrl is { Length: > 0 } topic)
        {
            var discuss = new Button { Content = L.T("Discuss on the Forum"), Margin = new Thickness(0, 8, 0, 0) };
            discuss.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", ArgumentList = { topic }, UseShellExecute = false,
            });
            panel.Children.Add(discuss);
        }

        // Comments.
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Comments"), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 16, 0, 4),
        });
        panel.Children.Add(commentList);
        panel.Children.Add(compose);
        send.Click += async (_, _) => await SendComment();
        var composeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        composeRow.Children.Add(send);
        composeRow.Children.Add(composeHint);
        panel.Children.Add(composeRow);

        // Explicit close — tiling compositors give dialogs no titlebar X
        // (IsCancel makes Esc work too).
        var closeButton = new Button { Content = L.T("Close"), IsCancel = true };
        closeButton.Click += (_, _) => Close();
        // Owner (or staff) can unlist their plugin — two clicks, the backend
        // enforces ownership (403 "not your plugin" shows verbatim).
        deleteButton.Content = L.T("Delete from Community");
        deleteButton.Click += async (_, _) => await Unpublish();
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(closeButton, Dock.Right);
        footer.Children.Add(closeButton);
        footer.Children.Add(deleteButton);
        panel.Children.Add(footer);

        Content = new ScrollViewer { Content = panel };

        _ = LoadLive();
    }

    /// Live detail + comments + sign-in state (counts from the tile may be
    /// hours stale).
    private async Task LoadLive()
    {
        var me = await CommunityClient.Me();
        var detail = await CommunityClient.Detail(slug);
        var comments = await CommunityClient.Comments(slug);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (detail != null)
            {
                cheers = detail.Cheers;
                cheered = detail.Cheered ?? false;
                UpdateCheerLabel();
            }
            var signedIn = me != null;
            deleteButton.IsVisible = me != null &&
                (me.Username == plugin.Author || me.Admin || me.Moderator);
            compose.IsEnabled = signedIn;
            send.IsEnabled = signedIn;
            composeHint.Text = signedIn ? "" : L.T("Sign in from the Community pane to cheer or comment.");
            commentList.Children.Clear();
            foreach (var comment in comments?.Comments ?? (IReadOnlyList<CommunityComment>)Array.Empty<CommunityComment>())
            {
                var block = new StackPanel();
                block.Children.Add(new TextBlock
                {
                    Text = $"{comment.Author}   {comment.CreatedAt.LocalDateTime:g}",
                    FontSize = 11, Foreground = Brushes.Gray,
                });
                block.Children.Add(new TextBlock { Text = comment.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                commentList.Children.Add(block);
            }
            if (commentList.Children.Count == 0)
                commentList.Children.Add(new TextBlock
                {
                    Text = L.T("No comments yet."), FontSize = 12, Foreground = Brushes.Gray,
                });
        });
    }

    private void UpdateCheerLabel() =>
        cheerButton.Content = $"{(cheered ? "♥" : "♡")} {cheers}";

    private async Task ToggleCheer()
    {
        cheerButton.IsEnabled = false;
        var result = await CommunityClient.Cheer(slug);
        if (result.Ok && result.Value is { } value)
        {
            cheered = value.Cheered;
            cheers = value.Cheers;
            UpdateCheerLabel();
        }
        else
        {
            installStatus.Text = result.Error ?? L.T("Sign in from the Community pane to cheer or comment.");
        }
        cheerButton.IsEnabled = true;
    }

    private async Task SendComment()
    {
        var body = (compose.Text ?? "").Trim();
        if (body.Length == 0) return;
        send.IsEnabled = false;
        var result = await CommunityClient.PostComment(slug, body);
        if (result.Ok)
        {
            compose.Text = "";
            await LoadLive();
        }
        else
        {
            composeHint.Text = result.Error ?? L.T("Couldn't post the comment.");
        }
        send.IsEnabled = true;
    }

    private async Task Unpublish()
    {
        if (!deleteArmed)
        {
            deleteArmed = true;
            deleteButton.Content = L.T("Really delete {0}?", plugin.Name);
            return;
        }
        deleteButton.IsEnabled = false;
        var result = await CommunityClient.Unpublish(slug);
        if (result.Ok)
        {
            onChanged?.Invoke();
            Close();
            return;
        }
        deleteButton.IsEnabled = true;
        deleteArmed = false;
        deleteButton.Content = L.T("Delete from Community");
        installStatus.Text = result.Error ?? L.T("Couldn't delete the plugin.");
    }

    private async Task Install()
    {
        installButton.IsEnabled = false;
        installStatus.Text = L.T("Installing…");
        var error = await install(plugin);
        if (error == null) MarkInstalled();
        installStatus.Text = error ?? "";
    }

    private void MarkInstalled()
    {
        installButton.Content = "✓ " + L.T("Installed");
        installButton.IsEnabled = false;
    }
}
