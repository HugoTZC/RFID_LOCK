using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class InitialSetup : Form
{
    private readonly TextBox machine = new() { Text = Environment.MachineName };
    private readonly TextBox instance = new() { ReadOnly = true };
    private readonly Button resetReader = new() { Text = "Cambiar lectora", AutoSize = true, Enabled = false };
    private readonly NumericUpDown length = new() { Minimum = 1, Maximum = 128, Value = 10 };
    private readonly TextBox user = new();
    private readonly TextBox host = new();
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox databaseUser = new();
    private readonly TextBox password = new() { UseSystemPasswordChar = true };
    private readonly ComboBox database = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button verifyDatabase = new() { Text = "Probar conexión", AutoSize = true };
    private readonly Button next = new() { Text = "Continuar", AutoSize = true, Enabled = false };
    private readonly Label databaseStatus = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly TableLayoutPanel databaseFields = new() { AutoSize = true, Padding = new Padding(16) };
    private readonly TableLayoutPanel readerFields = new() { AutoSize = true, Padding = new Padding(16), Visible = false };
    private string? verifiedConnection;
    private readonly TextBox scan = new() { ReadOnly = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button test = new() { Text = "Probar gafete", AutoSize = true };
    private readonly Button save = new() { Text = "Guardar y activar", AutoSize = true, Enabled = false };
    private readonly Dictionary<string, string> badges = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer timeout = new() { Interval = 8000 };
    private HidReader? reader;

    public Configuration? SavedConfiguration { get; private set; }

    public InitialSetup(string configPath)
    {
        Text = "RFID Lock · Configuración inicial";
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AddField(databaseFields, "Servidor PostgreSQL (IP o nombre)", host);
        AddField(databaseFields, "Puerto", port);
        AddField(databaseFields, "Usuario PostgreSQL", databaseUser);
        AddField(databaseFields, "Contraseña", password);
        databaseFields.Controls.Add(verifyDatabase);
        AddField(databaseFields, "Base de datos", database);
        databaseFields.Controls.Add(databaseStatus);
        databaseFields.Controls.Add(next);

        AddField(readerFields, "Nombre de la máquina", machine);
        AddField(readerFields, "Lectora HID detectada", instance);
        readerFields.Controls.Add(resetReader);
        AddField(readerFields, "Caracteres antes de Enter", length);
        AddField(readerFields, "Nombre del usuario del gafete", user);
        AddField(readerFields, "Código leído por la lectora", scan);
        readerFields.Controls.Add(test);
        readerFields.Controls.Add(status);
        readerFields.Controls.Add(save);
        Controls.Add(databaseFields);
        Controls.Add(readerFields);

        resetReader.Click += (_, _) =>
        {
            instance.Clear();
            resetReader.Enabled = false;
            ClearTests();
            status.Text = "Pulse Probar gafete y escanee con la nueva lectora.";
        };
        length.ValueChanged += (_, _) => ClearTests();
        timeout.Tick += (_, _) => FinishTest("No se recibió un código válido durante el bloqueo de prueba. Revise que la lectora envíe la longitud indicada y termine con Enter.");
        host.TextChanged += (_, _) => ResetDatabaseList();
        port.ValueChanged += (_, _) => ResetDatabaseList();
        databaseUser.TextChanged += (_, _) => ResetDatabaseList();
        password.TextChanged += (_, _) => ResetDatabaseList();
        database.SelectedIndexChanged += (_, _) => next.Enabled = database.SelectedIndex >= 0;
        next.Click += async (_, _) => await ConfirmDatabaseAsync();
        verifyDatabase.Click += async (_, _) => await CheckDatabaseAsync();
        test.Click += (_, _) => StartTest();
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(machine.Text) || badges.Count == 0)
            {
                status.Text = "Indique el nombre de la máquina y pruebe al menos un gafete.";
                return;
            }

            string temporaryPath = configPath + ".tmp";
            try
            {
                string protectedConnection = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(verifiedConnection!), null, DataProtectionScope.CurrentUser));
                var configuration = new Configuration(null, 0, badges.Keys.ToArray(),
                    instance.Text.Trim(), (int)length.Value, badges, true,
                    DateTimeOffset.UtcNow, machine.Text.Trim(), protectedConnection);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration));
                File.Move(temporaryPath, configPath, overwrite: true);
                SavedConfiguration = configuration;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                status.Text = "No se pudo guardar la configuración. " + ex.Message;
            }
        };

    }

    private static void AddField(TableLayoutPanel panel, string label, Control field)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true });
        field.Width = 520;
        panel.Controls.Add(field);
    }

    private void ResetDatabaseList()
    {
        database.Items.Clear();
        next.Enabled = false;
        databaseStatus.Text = "";
    }

    private NpgsqlConnectionStringBuilder ConnectionSettings(string name) => new()
    {
        Host = host.Text.Trim(),
        Port = (int)port.Value,
        Username = databaseUser.Text.Trim(),
        Password = password.Text,
        Database = name,
        Timeout = 5,
        CommandTimeout = 5,
        Pooling = false
    };

    private async Task CheckDatabaseAsync()
    {
        ResetDatabaseList();
        if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(databaseUser.Text))
        {
            databaseStatus.Text = "Ingrese el servidor y el usuario PostgreSQL.";
            return;
        }

        databaseFields.Enabled = false;
        try
        {
            foreach (string catalog in new[] { "postgres", "rfid_lock", "template1", databaseUser.Text.Trim() }.Distinct())
            {
                await using var connection = new NpgsqlConnection(ConnectionSettings(catalog).ConnectionString);
                try
                {
                    await connection.OpenAsync();
                }
                catch (PostgresException ex) when (ex.SqlState is "3D000" or "42501")
                {
                    continue;
                }

                await using var command = new NpgsqlCommand("""
                    SELECT datname FROM pg_database
                    WHERE datallowconn AND NOT datistemplate AND has_database_privilege(oid, 'CONNECT')
                    ORDER BY datname
                    """, connection);
                await using var rows = await command.ExecuteReaderAsync();
                while (await rows.ReadAsync())
                    database.Items.Add(rows.GetString(0));

                if (database.Items.Contains("rfid_lock"))
                    database.SelectedItem = "rfid_lock";
                else if (database.Items.Count == 1)
                    database.SelectedIndex = 0;

                databaseStatus.Text = database.Items.Count == 0
                    ? "El usuario no tiene bases de datos disponibles."
                    : "Seleccione la base de datos y pulse Continuar.";
                return;
            }

            databaseStatus.Text = "El usuario no puede abrir una base de datos para consultar el catálogo.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NpgsqlException or TimeoutException)
        {
            databaseStatus.Text = "No se pudo conectar. Revise servidor, puerto, credenciales y acceso de red.";
        }
        finally
        {
            databaseFields.Enabled = true;
        }
    }

    private async Task ConfirmDatabaseAsync()
    {
        if (database.SelectedItem is not string selected)
            return;

        databaseFields.Enabled = false;
        try
        {
            string connectionString = ConnectionSettings(selected).ConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            verifiedConnection = connectionString;
            databaseFields.Hide();
            readerFields.Show();
            machine.Focus();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NpgsqlException or TimeoutException)
        {
            databaseStatus.Text = "No se pudo abrir la base seleccionada. Revise el permiso de conexión.";
        }
        finally
        {
            databaseFields.Enabled = true;
        }
    }

    private void ClearTests()
    {
        badges.Clear();
        scan.Clear();
        save.Enabled = false;
    }

    private void StartTest()
    {
        string name = user.Text.Trim();
        if (name.Length == 0)
        {
            status.Text = "Ingrese el nombre del usuario.";
            return;
        }

        string? selectedReader = instance.Text.Length == 0 ? null : instance.Text;
        try
        {
            reader = new HidReader(selectedReader, (int)length.Value, code =>
            {
                // Finish after Raw Input returns so its window can complete message cleanup.
                BeginInvoke(() =>
                {
                    if (reader is null)
                        return;
                    instance.Text = selectedReader ?? reader.DetectedInstance!;
                    scan.Text = code;
                    badges[code] = name;
                    FinishTest($"Lectura válida para {name}. Gafetes verificados: {badges.Count}.");
                });
            });
            if (selectedReader is not null && !reader.IsConnected())
            {
                FinishTest("Windows no detecta esa instancia HID como teclado.");
                return;
            }

            scan.Clear();
            scan.Focus();
            test.Enabled = false;
            machine.Enabled = false;
            save.Enabled = false;
            resetReader.Enabled = false;
            length.Enabled = false;
            user.Enabled = false;
            status.Text = "Escanee el gafete ahora. Teclado y mouse se liberarán en un máximo de ocho segundos.";
            timeout.Start();
            reader.SetLocked(true);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            FinishTest("Windows no pudo iniciar la captura HID. " + ex.Message);
        }
    }

    private void FinishTest(string message)
    {
        timeout.Stop();
        reader?.Dispose();
        reader = null;
        test.Enabled = true;
        machine.Enabled = true;
        save.Enabled = badges.Count != 0;
        resetReader.Enabled = instance.Text.Length != 0;
        length.Enabled = true;
        user.Enabled = true;
        status.Text = message;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timeout.Dispose();
            reader?.Dispose();
        }
        base.Dispose(disposing);
    }
}
