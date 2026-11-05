using System.ComponentModel;
using System.Runtime.InteropServices;
using SamaLcd.Core;

namespace SamaLcd;

/// <summary>Receives display power changes even while the app is in the tray.</summary>
internal sealed class DisplayPowerNotifications : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DataLengthOffset = 16; // after the GUID
    private const int DataOffset = DataLengthOffset + sizeof(int);
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    private readonly Action _onWake;
    private readonly IntPtr _registration;
    private bool _displayWasOff;

    public DisplayPowerNotifications(Action onWake)
    {
        _onWake = onWake;
        CreateHandle(new CreateParams());
        _registration = RegisterPowerSettingNotification(Handle, in SessionDisplayStatus, 0);
        if (_registration == IntPtr.Zero)
            AppLog.Error("display.power_registration", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast && m.WParam == (IntPtr)PbtPowerSettingChange &&
            m.LParam != IntPtr.Zero && Marshal.PtrToStructure<Guid>(m.LParam) == SessionDisplayStatus &&
            Marshal.ReadInt32(m.LParam, DataLengthOffset) == sizeof(int))
        {
            int state = Marshal.ReadInt32(m.LParam, DataOffset);
            AppLog.Info("display.power_changed", new { state }); // 0 = off, 1 = on, 2 = dimmed
            if (state == 0)
            {
                _displayWasOff = true;
            }
            else if (state == 1 && _displayWasOff)
            {
                _displayWasOff = false;
                _onWake();
            }

            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr registration);
}
