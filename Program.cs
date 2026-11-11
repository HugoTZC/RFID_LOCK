using System.IO.Ports;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--service"])
        {
            ServiceBase.Run(new AwakeService());
            return 0;
        }

        if (args is not ["--agent"])
            return 1;

        using var instance = new Mutex(true, @"Local\RfidLock.Agent", out bool first);
        if (!first)
            return 0;

        try
        {
            var configuration = JsonSerializer.Deserialize<Configuration>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.json")));
            if (configuration is null || (string.IsNullOrWhiteSpace(configuration.HidInstance)
                    && (string.IsNullOrWhiteSpace(configuration.Port) || configuration.BaudRate <= 0))
                || (!string.IsNullOrWhiteSpace(configuration.HidInstance)
                    && (configuration.BadgeLength is < 1 or > 128
                        || configuration.Badges?.Any(id => id is null
                            || id.Length != configuration.BadgeLength
                            || id.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z'))) == true)) || configuration.Badges is not { Length: > 0 }
                || configuration.Badges.Any(id => string.IsNullOrWhiteSpace(id)
                    || id.Length > 128 || id.Any(c => c < 32 || c > 126)))
                throw new InvalidDataException("Revise el puerto, la velocidad y los gafetes de config.json.");

            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var agent = new LockAgent(configuration);
            Application.Run(agent);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "RFID Lock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed record Configuration(string? Port, int BaudRate, string[] Badges,
    string? HidInstance, int BadgeLength, Dictionary<string, string>? BadgeNames);

internal sealed class AwakeService : ServiceBase
{
    private const uint Continuous = 0x80000000;
    private readonly ManualResetEventSlim stop = new();
    private Thread? worker;

    public AwakeService()
    {
        ServiceName = "RfidLock";
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        stop.Reset();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = new Thread(() =>
        {
            bool success = Native.SetThreadExecutionState(Continuous | 3) != 0;
            started.SetResult(success);
            if (!success)
                return;
            stop.Wait();
            Native.SetThreadExecutionState(Continuous);
        }) { IsBackground = true };
        worker.Start();
        if (!started.Task.GetAwaiter().GetResult())
            throw new InvalidOperationException("Windows rechazó la solicitud de mantener el equipo despierto.");
    }

    protected override void OnStop()
    {
        stop.Set();
        worker?.Join();
    }

    protected override void OnShutdown() => OnStop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OnStop();
            stop.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class LockAgent : ApplicationContext
{
    private const uint IdleMilliseconds = 300_000;
    private readonly Configuration configuration;
    private readonly HashSet<string> badges;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly List<Form> covers = new();
    private readonly StringBuilder frame = new();
    private readonly HidReader? hid;
    private SerialPort? reader;
    private long retryAt;
    private long lastByteAt;
    private uint unlockedAt;
    private bool discardFrame;
    private bool locked;
    private bool inputBlocked;
    private bool waitingForReader = true;
    private IdleNotice? notice;
    private string? currentUser;

    public LockAgent(Configuration configuration)
    {
        this.configuration = configuration;
        badges = new HashSet<string>(configuration.Badges, StringComparer.Ordinal);
        try
        {
            if (!string.IsNullOrWhiteSpace(configuration.HidInstance))
                hid = new HidReader(configuration.HidInstance, configuration.BadgeLength, badge =>
                {
                    if (locked && badges.Contains(badge))
                        Unlock(badge);
                });
            CheckReader();
            timer.Tick += Tick;
            timer.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Lock()
    {
        locked = true;
        currentUser = null;
        notice?.Hide();
        ResetFrame();
        // Drain data received while the desktop was unlocked.
        TryReader(discard: true);
        if (!locked)
            return;

        ShowCovers();
        if (hid is null)
            inputBlocked = Native.BlockInput(true);
        else
            hid.SetLocked(true);
    }

    private void ShowCovers()
    {
        var bounds = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
        if (covers.Select(form => form.Bounds).SequenceEqual(bounds))
            return;

        CloseCovers();
        foreach (var rectangle in bounds)
        {
            var cover = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = rectangle,
                BackColor = Color.Black,
                TopMost = true,
                ShowInTaskbar = false
            };
            var message = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Text = "Bloqueado, escanee su gafete para desbloquear"
            };
            cover.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 22);
            cover.Controls.Add(message);
            covers.Add(cover);
            cover.Show();
        }
    }

    private void CloseCovers()
    {
        foreach (var cover in covers)
        {
            var font = cover.Font;
            cover.Dispose();
            font.Dispose();
        }
        covers.Clear();
    }

    private bool CheckReader()
    {
        bool available;
        if (hid is not null)
            available = hid.IsConnected();
        else
        {
            if (reader is not null && !SerialPort.GetPortNames().Contains(
                configuration.Port!, StringComparer.OrdinalIgnoreCase))
                CloseReader();
            if (reader is null)
                TryReader(discard: true);
            available = reader is not null;
        }

        if (!available)
        {
            if (locked)
                Unlock();
            waitingForReader = true;
            return false;
        }

        if (waitingForReader)
        {
            waitingForReader = false;
            Lock();
        }
        return true;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!CheckReader())
            return;

        if (locked)
        {
            ShowCovers();
            if (hid is null && !inputBlocked)
                inputBlocked = Native.BlockInput(true);
            TryReader(discard: false);
            return;
        }

        TryReader(discard: true);
        var input = new Native.LastInput { Size = (uint)Marshal.SizeOf<Native.LastInput>() };
        if (!Native.GetLastInputInfo(ref input))
            return;

        uint now = unchecked((uint)Environment.TickCount);
        uint idle = Math.Min(unchecked(now - unlockedAt), unchecked(now - input.Time));
        if (idle >= IdleMilliseconds)
            Lock();
        else if (idle >= 10_000 && currentUser is not null)
        {
            notice ??= new IdleNotice();
            notice.UpdateCountdown(currentUser, IdleMilliseconds - idle);
        }
        else
            notice?.Hide();
    }

    private void TryReader(bool discard)
    {
        if (hid is not null)
            return;

        try
        {
            if (reader is null)
            {
                if (Environment.TickCount64 < retryAt)
                    return;
                reader = new SerialPort(configuration.Port!, configuration.BaudRate)
                {
                    ReadTimeout = 50,
                    Encoding = Encoding.ASCII
                };
                reader.Open();
                reader.DiscardInBuffer();
            }

            if (discard)
            {
                reader.DiscardInBuffer();
                return;
            }

            // Bound the work per UI tick even if the port is continuously noisy.
            for (int remaining = Math.Min(reader.BytesToRead, 512); remaining > 0; remaining--)
            {
                int value = reader.ReadByte();
                long now = Environment.TickCount64;
                if (now - lastByteAt > 1500)
                    ResetFrame();
                lastByteAt = now;

                if (value is 13 or 10)
                {
                    string badge = frame.ToString();
                    bool valid = !discardFrame && badges.Contains(badge);
                    ResetFrame();
                    if (valid)
                    {
                        Unlock(badge);
                        return;
                    }
                }
                else if (value is >= 32 and <= 126 && frame.Length < 128 && !discardFrame)
                    frame.Append((char)value);
                else
                    discardFrame = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.TimeoutException)
        {
            CloseReader();
            ResetFrame();
            retryAt = Environment.TickCount64 + 2000;
            waitingForReader = true;
            if (locked)
                Unlock();
        }
    }

    private void ResetFrame()
    {
        frame.Clear();
        discardFrame = false;
    }

    private void ReleaseInput()
    {
        if (inputBlocked)
            Native.BlockInput(false);
        inputBlocked = false;
    }

    private void Unlock(string? badge = null)
    {
        currentUser = null;
        if (badge is not null)
        {
            currentUser = configuration.BadgeNames?.TryGetValue(badge, out string? name) == true
                && !string.IsNullOrWhiteSpace(name) ? name : "Usuario autorizado";
        }
        notice?.Hide();
        hid?.SetLocked(false);
        ReleaseInput();
        CloseCovers();
        unlockedAt = unchecked((uint)Environment.TickCount);
        locked = false;
    }

    private void CloseReader()
    {
        try
        {
            reader?.Dispose();
        }
        catch (IOException)
        {
            // The USB device may already have been removed.
        }
        finally
        {
            reader = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            notice?.Dispose();
            hid?.Dispose();
            ReleaseInput();
            CloseCovers();
            CloseReader();
        }
        base.Dispose(disposing);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInput
    {
        internal uint Size;
        internal uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool block);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInput input);

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);
}
