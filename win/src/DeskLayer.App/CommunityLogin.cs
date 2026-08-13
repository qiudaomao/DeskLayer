// Shared forum sign-in for the community features (the publish dialog and the
// gallery pane). Runs the store's device-code flow: open the forum login in
// the browser, then poll for the token. A brand-new user signing up can take
// minutes (email confirmation), so the poll runs the full advertised lifetime
// and a second click cancels the first loop before starting a new code —
// the two hardening points that were the root of the mac first-login race.

using System.Diagnostics;
using System.Threading;
using DeskLayer.Core;
using DeskLayer.Core.Community;

namespace DeskLayer.App;

public static class CommunityLogin
{
    /// A running sign-in. Cancel() stops the poll; a caller that starts a new
    /// sign-in must cancel the previous one first so two loops never race.
    public sealed class Session
    {
        private readonly CancellationTokenSource cts = new();
        public CancellationToken Token => cts.Token;
        public void Cancel() => cts.Cancel();
    }

    /// Begins the flow. `onStatus` reports progress/errors for the UI to show;
    /// `onDone(user)` fires on the owning thread's context when a token is
    /// stored (user) or the flow ended without one (null). Returns the
    /// session so the caller can cancel it (e.g. when its window closes).
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
        try { Process.Start(new ProcessStartInfo(login.LoginUrl) { UseShellExecute = true }); }
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
            // A transient poll error shouldn't abandon the whole sign-in while
            // the code is still valid — keep polling until the deadline.
            onStatus(poll.Error);
        }
        if (!session.Token.IsCancellationRequested)
        {
            onStatus(L.T("Sign-in expired — try again."));
            onDone(null);
        }
    }
}
