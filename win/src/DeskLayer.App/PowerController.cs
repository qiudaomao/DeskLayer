// Merges Windows power/session signals into one RenderPolicy — the twin of
// the mac PowerStateController. Pauses on session lock and system suspend;
// throttles under battery saver. No thermal input (Windows has no reliable
// public thermal API — the port-plan decision).
//
// Signals come from a hidden message-only window (WM_POWERBROADCAST +
// WTS session notifications), so this must be created on the UI thread.

using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskLayer.App;

public enum PolicyKind { Run, Throttled, Paused }

public readonly record struct RenderPolicy(PolicyKind Kind, double MaxFps)
{
    public static readonly RenderPolicy Run = new(PolicyKind.Run, double.PositiveInfinity);
    public static RenderPolicy Throttled(double maxFps) => new(PolicyKind.Throttled, maxFps);
    public static readonly RenderPolicy Paused = new(PolicyKind.Paused, 0);
}

public sealed class PowerController : IDisposable
{
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    // QUNS states: 6 = presentation mode, 8 = running a D3D full-screen app.
    // Both mean something is covering the desktop full-screen; a wallpaper
    // renderer behind them is pure waste, so pause.
    private const int QunsPresentationMode = 6;
    private const int QunsRunningD3DFullScreen = 8;

    private static bool ForegroundFullScreen()
    {
        return SHQueryUserNotificationState(out var state) == 0
            && state is QunsPresentationMode or QunsRunningD3DFullScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int NotifyForThisSession = 0;

    private readonly MessageWindow window;
    private bool suspended;
    private bool locked;

    /// Current merged policy; read by the render thread each tick.
    public RenderPolicy Policy { get; private set; } = RenderPolicy.Run;
    /// Fired when the policy leaves Paused — the runtime must re-assert
    /// itself (a slept display often returns as unlock, not resume).
    public event Action? DidWake;

    public PowerController(Action<string> log)
    {
        window = new MessageWindow(this, log);
        Recompute();
    }

    /// Re-evaluate (battery-saver has no notification; poll it). Returns the
    /// current policy.
    public RenderPolicy Refresh()
    {
        Recompute();
        return Policy;
    }

    private void Recompute()
    {
        var batterySaver = false;
        if (GetSystemPowerStatus(out var status))
            batterySaver = (status.SystemStatusFlag & 0x1) != 0; // battery-saver on

        var next = suspended || locked || ForegroundFullScreen()
            ? RenderPolicy.Paused
            : batterySaver ? RenderPolicy.Throttled(10) : RenderPolicy.Run;

        if (next == Policy) return;
        var wasPaused = Policy.Kind == PolicyKind.Paused;
        Policy = next;
        if (wasPaused && next.Kind != PolicyKind.Paused) DidWake?.Invoke();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly PowerController owner;

        public MessageWindow(PowerController owner, Action<string> log)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "DeskLayerPower", Parent = new IntPtr(-3) /*HWND_MESSAGE*/ });
            if (!WTSRegisterSessionNotification(Handle, NotifyForThisSession))
                log("WTS session notifications unavailable; lock/unlock pause disabled");
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmPowerBroadcast:
                    var evt = m.WParam.ToInt32();
                    if (evt == PbtApmSuspend) { owner.suspended = true; owner.Recompute(); }
                    else if (evt is PbtApmResumeSuspend or PbtApmResumeAutomatic) { owner.suspended = false; owner.Recompute(); }
                    break;
                case WmWtsSessionChange:
                    var change = m.WParam.ToInt32();
                    if (change == WtsSessionLock) { owner.locked = true; owner.Recompute(); }
                    else if (change == WtsSessionUnlock) { owner.locked = false; owner.Recompute(); }
                    break;
            }
            base.WndProc(ref m);
        }

        public void Teardown()
        {
            WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }
    }

    public void Dispose() => window.Teardown();
}
