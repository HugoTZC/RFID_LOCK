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

        bool setupRequested = args is ["--setup"];
        if (!setupRequested && args is not ["--agent"])
            return 1;

        using var instance = new Mutex(true, @"Local\RfidLock.Agent", out bool first);
        if (!first)
            return 0;

        try
        {
            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RfidLock", "config.json");
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var configuration = !setupRequested && File.Exists(configPath)
                ? JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configPath))
                : null;
            // Installation and legacy configuration files never arm the session lock.
            if (setupRequested || configuration is not { ProtectionEnabled: true, ConfigurationVerifiedAt: not null })
            {
                using var setup = new InitialSetup(configPath);
                if (setup.ShowDialog() != DialogResult.OK)
                    return 0;
                configuration = setup.SavedConfiguration!;
            }
            if ((string.IsNullOrWhiteSpace(configuration.HidInstance)
                    && (string.IsNullOrWhiteSpace(configuration.Port) || configuration.BaudRate <= 0))
                || (!string.IsNullOrWhiteSpace(configuration.HidInstance)
                    && (configuration.BadgeLength is < 1 or > 128
                        || configuration.Badges?.Any(id => id is null
                            || id.Length != configuration.BadgeLength
                            || id.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z'))) == true)) || configuration.Badges is not { Length: > 0 }
                || configuration.Badges.Any(id => string.IsNullOrWhiteSpace(id)
                    || id.Length > 128 || id.Any(c => c < 32 || c > 126)))
                throw new InvalidDataException("Revise el puerto, la velocidad y los gafetes de config.json.");

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
    string? HidInstance, int BadgeLength, Dictionary<string, string>? BadgeNames,
    bool ProtectionEnabled = false, DateTimeOffset? ConfigurationVerifiedAt = null, string? MachineName = null,
    string? ProtectedDatabaseConnection = null);

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
    private readonly DatabaseAudit audit;
    private readonly HashSet<string> badges;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly List<Form> covers = new();
    private readonly StringBuilder frame = new();
    private HidReader? hid;
    private SerialPort? reader;
    private long retryAt;
    private long lastByteAt;
    private long unlockedAt;
    private string? pendingHidBadge;
    private bool discardFrame;
    private bool locked;
    private bool inputBlocked;
    private bool waitingForReader = true;
    private int missingReaderChecks;
    private IdleNotice? notice;
    private string? currentUser;
    private string? currentBadge;

    public LockAgent(Configuration configuration)
    {
        this.configuration = configuration;
        badges = new HashSet<string>(configuration.Badges, StringComparer.Ordinal);
        audit = new DatabaseAudit(configuration);
        Native.SetThreadExecutionState(0x80000003);
        try
        {
            if (!string.IsNullOrWhiteSpace(configuration.HidInstance))
                hid = new HidReader(configuration.HidInstance, configuration.BadgeLength,
                    HandleHidBadge);
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

    private void Lock(string reason)
    {
        AgentLog.Write("Bloqueo: " + reason);
        audit.Record("screen_locked", reason: reason);
        locked = true;
        pendingHidBadge = null;
        currentBadge = null;
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
        {
            covers[0].Activate();
            AgentLog.Write("Captura HID activada en la cubierta.");
        }
    }

    private void ShowCovers()
    {
        var bounds = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
        if (covers.Select(form => form.Bounds).SequenceEqual(bounds))
            return;

        CloseCovers();
        if (hid is not null)
        {
            hid.Dispose();
            hid = null;
        }

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

        if (configuration.HidInstance is { Length: > 0 } instance)
        {
            hid = new HidReader(instance, configuration.BadgeLength, HandleHidBadge, covers[0]);
            hid.SetLocked(true);
            covers[0].Activate();
        }
    }

    private void CloseCovers()
    {
        if (covers.Count > 0 && hid is not null)
        {
            hid.Dispose();
            hid = null;
        }

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
            // Ignore brief Raw Input enumeration failures while a reader was available.
            if (hid is not null && !waitingForReader && ++missingReaderChecks < 5)
                return true;
            if (!waitingForReader)
                AgentLog.Write("Lectora no disponible.");
            if (locked)
                Unlock();
            waitingForReader = true;
            return false;
        }

        missingReaderChecks = 0;
        if (waitingForReader)
        {
            waitingForReader = false;
            if (currentBadge is null)
                Lock("lectora disponible");
        }
        return true;
    }

    private void Tick(object? sender, EventArgs e)
    {
        // Apply the scan after WM_INPUT has finished, before probing the reader.
        if (pendingHidBadge is { } badge)
        {
            pendingHidBadge = null;
            if (HandleBadge(badge))
                return;
        }

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

        TryReader(discard: false);
        if (locked || waitingForReader)
            return;

        var input = new Native.LastInput { Size = (uint)Marshal.SizeOf<Native.LastInput>() };
        if (!Native.GetLastInputInfo(ref input))
            return;

        uint now = unchecked((uint)Environment.TickCount);
        long idle = Math.Min(Environment.TickCount64 - unlockedAt, unchecked(now - input.Time));
        if (idle >= IdleMilliseconds)
            Lock("inactividad");
        else
            ShowIdleNotice(IdleMilliseconds - (uint)idle);
    }

    private void ShowIdleNotice(uint remainingMilliseconds)
    {
        if (currentUser is null)
            return;

        notice ??= new IdleNotice(() =>
        {
            if (!locked && CheckReader() && !locked)
                Lock("botón");
        });
        notice.UpdateCountdown(currentUser, remainingMilliseconds);
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
                    bool valid = !discardFrame;
                    ResetFrame();
                    if (valid && badge.Length > 0 && locked)
                        audit.Record(badges.Contains(badge) ? "scan_allowed" : "scan_denied", badge,
                            badges.Contains(badge) ? null : "gafete no autorizado");
                    if (valid && HandleBadge(badge))
                        return;
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

    private void HandleHidBadge(string badge)
    {
        bool authorized = badges.Contains(badge);
        AgentLog.Write(authorized ? "Gafete HID autorizado recibido." : "Gafete HID no autorizado.");
        audit.Record(authorized ? "scan_allowed" : "scan_denied", badge,
            authorized ? null : "gafete no autorizado");
        if (locked && badges.Contains(badge))
            pendingHidBadge = badge;
    }

    private bool HandleBadge(string badge)
    {
        if (waitingForReader)
            return false;

        if (locked && badges.Contains(badge))
        {
            Unlock(badge);
            return true;
        }

        if (!locked && string.Equals(badge, currentBadge, StringComparison.Ordinal))
        {
            Lock("gafete de la sesión");
            return true;
        }

        return false;
    }

    private void Unlock(string? badge = null)
    {
        AgentLog.Write(badge is null ? "Desbloqueo por lectora no disponible." : "Desbloqueo por gafete.");
        currentBadge = badge;
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
        if (configuration.HidInstance is { Length: > 0 } instance && hid is null)
            hid = new HidReader(instance, configuration.BadgeLength, HandleHidBadge);
        unlockedAt = Environment.TickCount64;
        locked = false;
        ShowIdleNotice(IdleMilliseconds);
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
            audit.Dispose();
            Native.SetThreadExecutionState(0x80000000);
            notice?.Dispose();
            hid?.Dispose();
            hid = null;
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
