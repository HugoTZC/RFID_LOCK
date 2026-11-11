using System.Text.Json;

internal sealed class InitialSetup : Form
{
    private readonly TextBox machine = new() { Text = Environment.MachineName };
    private readonly TextBox instance = new();
    private readonly NumericUpDown length = new() { Minimum = 1, Maximum = 128, Value = 10 };
    private readonly TextBox user = new();
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
        var fields = new TableLayoutPanel { AutoSize = true, Padding = new Padding(16) };
        fields.ColumnCount = 1;
        AddField("Nombre de la máquina", machine);
        AddField("Ruta de instancia HID (Administrador de dispositivos)", instance);
        AddField("Caracteres antes de Enter", length);
        AddField("Nombre del usuario del gafete", user);
        fields.Controls.Add(test);
        fields.Controls.Add(status);
        fields.Controls.Add(save);
        Controls.Add(fields);

        instance.TextChanged += (_, _) => ClearTests();
        length.ValueChanged += (_, _) => ClearTests();
        timeout.Tick += (_, _) => FinishTest("No se recibió un código válido durante el bloqueo de prueba. Revise la longitud, el Enter y la ruta HID.");
        test.Click += (_, _) => StartTest();
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(machine.Text) || badges.Count == 0)
            {
                status.Text = "Indique el nombre de la máquina y pruebe al menos un gafete.";
                return;
            }

            var configuration = new Configuration(null, 0, badges.Keys.ToArray(),
                instance.Text.Trim(), (int)length.Value, badges, true,
                DateTimeOffset.UtcNow, machine.Text.Trim());
            string temporaryPath = configPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration));
                File.Move(temporaryPath, configPath, overwrite: true);
                SavedConfiguration = configuration;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "No se pudo guardar la configuración. " + ex.Message;
            }
        };

        void AddField(string label, Control field)
        {
            fields.Controls.Add(new Label { Text = label, AutoSize = true });
            field.Width = 520;
            fields.Controls.Add(field);
        }
    }

    private void ClearTests()
    {
        badges.Clear();
        save.Enabled = false;
    }

    private void StartTest()
    {
        string name = user.Text.Trim();
        if (name.Length == 0 || instance.Text.Trim().Split('\\') is not ["HID", { Length: > 0 }, { Length: > 0 }])
        {
            status.Text = "Ingrese la ruta HID completa y el nombre del usuario.";
            return;
        }

        try
        {
            reader = new HidReader(instance.Text.Trim(), (int)length.Value, code =>
            {
                // Finish after Raw Input returns so its window can complete message cleanup.
                BeginInvoke(() =>
                {
                    if (reader is null)
                        return;
                    badges[code] = name;
                    FinishTest($"Lectura válida para {name}. Gafetes verificados: {badges.Count}.");
                });
            });
            if (!reader.IsConnected())
            {
                FinishTest("Windows no detecta esa instancia HID como teclado.");
                return;
            }

            test.Enabled = false;
            save.Enabled = false;
            instance.Enabled = false;
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
        save.Enabled = badges.Count != 0;
        instance.Enabled = true;
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
