param([int]$Flags = 512, [int]$Seconds = 8)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Raw {
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    public static bool Register(IntPtr hwnd, uint flags) {
        var d = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = flags, hwndTarget = hwnd };
        return RegisterRawInputDevices(new[] { d }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
    }
}
"@
$form = New-Object System.Windows.Forms.Form
$form.Text = 'fake game process'
$form.StartPosition = 'Manual'
$form.Bounds = New-Object System.Drawing.Rectangle 100, 100, 300, 200
$form.TopMost = $true
$form.Add_Shown({ [void][Raw]::Register($form.Handle, [uint32]$Flags) })
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $Seconds * 1000
$timer.Add_Tick({ $form.Close() })
$timer.Start()
[void]$form.ShowDialog()
