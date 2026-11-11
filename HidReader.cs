using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class HidReader : NativeWindow, IDisposable
{
    private readonly string? devicePath;
    private nint frameDevice;

    public string? DetectedInstance { get; private set; }
    private readonly int length;
    private readonly Action<string> scanned;
    private readonly StringBuilder buffer = new();
    private readonly Hook keyboardHook;
    private readonly Hook mouseHook;
    private nint keyboard;
    private nint mouse;
    private long lastKey;
    private bool invalid;
    private bool locked;
    private bool firstInputLogged;

    public HidReader(string? instance, int length, Action<string> scanned)
    {
        if (instance is not null)
            devicePath = @"\\?\" + instance.Replace('\\', '#') + "#";
        this.length = length;
        this.scanned = scanned;
        keyboardHook = FilterKeyboard;
        mouseHook = FilterMouse;

        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        try
        {
            var device = new RawDevice { Page = 1, Usage = 6, Flags = 0x100, Target = Handle };
            if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>()))
                throw new Win32Exception();
            keyboard = SetWindowsHookEx(13, keyboardHook, GetModuleHandle(null), 0);
            if (keyboard == 0)
                throw new Win32Exception();
            mouse = SetWindowsHookEx(14, mouseHook, GetModuleHandle(null), 0);
            if (mouse == 0)
                throw new Win32Exception();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetLocked(bool value)
    {
        locked = value;
        firstInputLogged = false;
        Reset();
    }

    private void Reset()
    {
        buffer.Clear();
        invalid = false;
    }

    private nint FilterKeyboard(int code, nint message, nint data) =>
        locked && code >= 0 ? 1 : CallNextHookEx(0, code, message, data);

    private nint FilterMouse(int code, nint message, nint data) =>
        locked && code >= 0 ? 1 : CallNextHookEx(0, code, message, data);

    protected override void WndProc(ref Message message)
    {
        if (locked && message.Msg == 0x00FF)
            ReadKey(message.LParam);
        base.WndProc(ref message);
    }

    private unsafe void ReadKey(nint input)
    {
        uint headerSize = (uint)sizeof(RawHeader);
        uint size = (uint)sizeof(RawKey);
        RawKey key = default;
        uint read = GetRawInputData(input, 0x10000003, &key, ref size, headerSize);
        if (read != sizeof(RawKey) || key.Header.Type != 1 || key.Header.Device == 0
            || (key.Flags & 1) != 0)
            return;

        if (devicePath is not null && !MatchesDevice(key.Header.Device))
            return;

        if (devicePath is not null && !firstInputLogged)
        {
            firstInputLogged = true;
            AgentLog.Write("Primera tecla recibida de la lectora HID configurada.");
        }

        long now = Environment.TickCount64;
        if (frameDevice != key.Header.Device || now - lastKey > 1500)
            Reset();
        frameDevice = key.Header.Device;
        lastKey = now;

        int value = key.VirtualKey;
        if (value == 13)
        {
            string? badge = !invalid && buffer.Length == length ? buffer.ToString() : null;
            if (badge is null && locked)
                AgentLog.Write($"Trama HID rechazada. Longitud recibida: {buffer.Length}; esperada: {length}.");
            Reset();
            if (badge is not null)
            {
                if (devicePath is null)
                {
                    string? name = GetDeviceName(key.Header.Device);
                    if (name is null || !name.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase)
                        || name[4..].Split('#') is not [var bus, var hardware, var instance, _])
                        return;

                    DetectedInstance = string.Join('\\', bus, hardware, instance);
                }

                scanned(badge);
            }
            return;
        }
        if (value is 16 or 160 or 161)
            return;

        if (invalid || buffer.Length >= length)
            invalid = true;
        else if (value is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            buffer.Append((char)value);
        else if (value is >= 0x60 and <= 0x69)
            buffer.Append((char)('0' + value - 0x60));
        else
            invalid = true;
    }

    public unsafe bool IsConnected()
    {
        uint count = 0;
        uint size = (uint)sizeof(DeviceListEntry);
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0 || count > 4096)
            return false;

        var devices = new DeviceListEntry[(int)count];
        fixed (DeviceListEntry* entries = devices)
        {
            uint read = GetRawInputDeviceList(entries, ref count, size);
            if (read > devices.Length)
                return false;

            for (int i = 0; i < read; i++)
                if (devices[i].Type == 1 && MatchesDevice(devices[i].Device))
                    return true;
        }
        return false;
    }

    private bool MatchesDevice(nint device) =>
        devicePath is not null && GetDeviceName(device)?.StartsWith(devicePath,
            StringComparison.OrdinalIgnoreCase) == true;

    private static unsafe string? GetDeviceName(nint device)
    {
        uint count = 0;
        if (GetRawInputDeviceInfo(device, 0x20000007, null, ref count) != 0
            || count is 0 or > 4096)
            return null;
        char* path = stackalloc char[(int)count + 1];
        uint result = GetRawInputDeviceInfo(device, 0x20000007, path, ref count);
        if (result == uint.MaxValue || result > count)
            return null;
        return new string(path, 0, (int)result);
    }

    public void Dispose()
    {
        locked = false;
        if (keyboard != 0)
            UnhookWindowsHookEx(keyboard);
        if (mouse != 0)
            UnhookWindowsHookEx(mouse);
        var device = new RawDevice { Page = 1, Usage = 6, Flags = 1 };
        RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>());
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice
    {
        public ushort Page, Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader
    {
        public uint Type, Size;
        public nint Device, Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKey
    {
        public RawHeader Header;
        public ushort ScanCode, Flags, Reserved, VirtualKey;
        public uint Message, ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceListEntry
    {
        public nint Device;
        public uint Type;
    }

    [DllImport("user32.dll")]
    private static extern unsafe uint GetRawInputDeviceList(DeviceListEntry* devices,
        ref uint count, uint entrySize);

    private delegate nint Hook(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(ref RawDevice device, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern unsafe uint GetRawInputData(nint input, uint command, RawKey* key,
        ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern unsafe uint GetRawInputDeviceInfo(nint device, uint command, char* data,
        ref uint size);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int type, Hook hook, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
}
