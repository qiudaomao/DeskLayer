// Forum sign-in for the community features — the Linux twin of the win
// CommunityLogin (device-code flow: open the forum login in the browser,
// poll for the token). Same two hardening points as win: the poll runs the
// code's full advertised lifetime (a brand-new signup can take minutes),
// and starting a new sign-in cancels the previous loop first.

using System.Diagnostics;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.LinuxApp.Ui;

public static class CommunityLogin
{
    public sealed class Session
    {
        private readonly CancellationTokenSource cts = new();
        public CancellationToken Token => cts.Token;
        public void Cancel() => cts.Cancel();
    }

    public static Session Begin(Action<string?> onStatus, Action<CommunityUser?> onDone)
    {
        var session = new Session();
        _ = Run(session, onStatus, onDone);
        return session;
    }

    private static async Task Run(Session session, Action<string?> onStatus, Action<CommunityUser?> onDone)
    {
        onStatus(L.T("Waiting for the browser sign-in…"));
        var login = await CommunityClient.BeginLogin();
        if (login == null)
        {
            onStatus(L.T("Couldn't reach the store."));
            onDone(null);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { login.LoginUrl }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            onStatus(L.T("Couldn't open the browser."));
            onDone(null);
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(login.ExpiresInSeconds);
        while (!session.Token.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(2000, session.Token); }
            catch (TaskCanceledException) { onDone(null); return; }

            var poll = await CommunityClient.PollToken(login);
            if (poll.Pending) continue;
            if (poll.Token is { } token)
            {
                CommunityClient.Token = token;
                onStatus(null);
                onDone(poll.User ?? await CommunityClient.Me());
                return;
            }
            onStatus(poll.Error);
        }
        if (!session.Token.IsCancellationRequested)
        {
            onStatus(L.T("Sign-in expired — try again."));
            onDone(null);
        }
    }
}
