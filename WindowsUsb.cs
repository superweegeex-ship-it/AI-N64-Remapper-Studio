using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace N64Studio;

sealed class WindowsUsb : ITransport
{
    readonly SafeFileHandle file;
    readonly IntPtr usb;
    readonly FileStream? hid;
    volatile InputFrame? input;
    public InputFrame? Input => input;

    public WindowsUsb()
    {
        Guid vendor = new("8B3E9D2E-7EEC-4994-AAE7-0C40DE84D36D");
        string[] paths = Paths(vendor).Where(p => p.Contains("vid_7318&pid_0002&mi_01", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length != 1) throw new IOException(paths.Length == 0
            ? "No N64 Remapper settings interface found. Check the USB data cable, controller and firmware."
            : "Multiple N64 Remappers found. Leave one plugged in.");
        file = Open(paths[0], 0xC0000000);
        try
        {
            Check(WinUsb_Initialize(file, out usb));
            uint timeout = 1800;
            Check(WinUsb_SetPipePolicy(usb, 0x02, 3, 4, ref timeout));
            Check(WinUsb_SetPipePolicy(usb, 0x82, 3, 4, ref timeout));
            Check(WinUsb_FlushPipe(usb, 0x82));
        }
        catch { Dispose(); throw; }

        try
        {
            HidD_GetHidGuid(out Guid hidGuid);
            string[] matches = Paths(hidGuid).Where(p => p.Contains("vid_7318&pid_0002&mi_00", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                SafeFileHandle handle = Open(matches[0], 0x80000000);
                try { hid = new FileStream(handle, FileAccess.Read, 64, true); }
                catch { handle.Dispose(); throw; }
                _ = Pump();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            // The configuration interface remains usable if Windows cannot open HID.
        }
    }

    async Task Pump()
    {
        var report = new byte[64];
        try
        {
            while (hid is not null)
            {
                int count = await hid.ReadAsync(report).ConfigureAwait(false);
                if (count == 0) break;
                if (InputFrame.Decode(report.AsSpan(0, count)) is { } frame) input = frame;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { input = null; }
    }

    public void Send(byte[] bytes)
    {
        Check(WinUsb_WritePipe(usb, 2, bytes, (uint)bytes.Length, out uint written, IntPtr.Zero));
        if (written != bytes.Length) throw new IOException("Incomplete USB write.");
    }

    public byte[] Receive()
    {
        byte[] bytes = new byte[64];
        Check(WinUsb_ReadPipe(usb, 0x82, bytes, 64, out uint read, IntPtr.Zero));
        return bytes[..(int)read];
    }

    public void Dispose()
    {
        hid?.Dispose();
        if (usb != IntPtr.Zero) WinUsb_Free(usb);
        file.Dispose();
    }

    static SafeFileHandle Open(string path, uint access)
    {
        SafeFileHandle handle = CreateFileW(path, access, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = new Win32Exception();
        handle.Dispose();
        throw error;
    }

    static string[] Paths(Guid guid)
    {
        for (int i = 0; i < 3; i++)
        {
            uint result = CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, null, 0);
            if (result != 0) throw new IOException($"Windows device discovery failed (0x{result:X}).");
            char[] buffer = new char[size];
            result = CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0);
            if (result == 0) return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != 0x1A) throw new IOException($"Windows device discovery failed (0x{result:X}).");
        }
        throw new IOException("USB devices changed during discovery. Try connecting again.");
    }

    static void Check(bool result) { if (!result) throw new Win32Exception(); }

    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string? device, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string? device, [Out] char[] buffer, uint size, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle handle, out IntPtr usb);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_FlushPipe(IntPtr usb, byte pipe);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] data, uint size, out uint written, IntPtr overlapped);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, [Out] byte[] data, uint size, out uint read, IntPtr overlapped);
}
