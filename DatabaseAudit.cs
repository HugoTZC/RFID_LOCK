using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

internal sealed class DatabaseAudit : IDisposable
{
    private readonly Configuration configuration;
    private readonly string directory;
    private readonly System.Threading.Timer upload;
    private int uploading;

    public DatabaseAudit(Configuration configuration)
    {
        this.configuration = configuration;
        directory = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "RfidLock", "audit");
        upload = new System.Threading.Timer(_ => _ = UploadAsync(), null,
            TimeSpan.Zero, TimeSpan.FromHours(5));
    }

    public void Record(string type, string? code = null, string? reason = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            string Encode(string? value) => value is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            File.WriteAllText(path + ".tmp", string.Join(",", DateTime.UtcNow.ToString("O"),
                type, Encode(code), Encode(reason)));
            File.Move(path + ".tmp", path + ".csv");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentLog.Write("No se pudo guardar un evento de auditoría local.");
        }
    }

    private async Task UploadAsync()
    {
        if (configuration.ProtectedDatabaseConnection is not { Length: > 0 } secret
            || Interlocked.Exchange(ref uploading, 1) != 0)
            return;

        try
        {
            string[] files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.csv") : [];
            string credentials = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser));
            await using var connection = new NpgsqlConnection(credentials);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            NpgsqlCommand Command(string sql, params object[] values)
            {
                var command = new NpgsqlCommand(sql, connection, transaction);
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] is DBNull)
                        command.Parameters.AddWithValue("p" + (i + 1), NpgsqlTypes.NpgsqlDbType.Unknown, values[i]);
                    else
                        command.Parameters.AddWithValue("p" + (i + 1), values[i]);
                }
                return command;
            }

            await using var machine = Command("""
                INSERT INTO rfid_lock.machines (id, name, time_zone, upload_after, configuration_verified_at, protection_enabled)
                VALUES ($1, $2, $3, TIME '00:00', $4, true)
                ON CONFLICT (lower(name)) DO UPDATE SET name = EXCLUDED.name
                RETURNING id
                """, Guid.NewGuid(), configuration.MachineName ?? Environment.MachineName,
                TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? zone) ? zone : "UTC",
                configuration.ConfigurationVerifiedAt!.Value.ToUniversalTime());
            var machineId = (Guid)(await machine.ExecuteScalarAsync())!;

            await using var reader = configuration.HidInstance is { Length: > 0 } hid
                ? Command("""
                    INSERT INTO rfid_lock.readers (id, machine_id, mode, hid_instance, badge_length)
                    VALUES ($1, $2, 'hid', $3, $4)
                    ON CONFLICT (machine_id, lower(hid_instance)) WHERE enabled AND mode = 'hid'
                    DO UPDATE SET badge_length = EXCLUDED.badge_length RETURNING id
                    """, Guid.NewGuid(), machineId, hid, configuration.BadgeLength)
                : Command("""
                    INSERT INTO rfid_lock.readers (id, machine_id, mode, serial_port, baud_rate)
                    VALUES ($1, $2, 'serial', $3, $4)
                    ON CONFLICT (machine_id, lower(serial_port)) WHERE enabled AND mode = 'serial'
                    DO UPDATE SET baud_rate = EXCLUDED.baud_rate RETURNING id
                    """, Guid.NewGuid(), machineId, configuration.Port!, configuration.BaudRate);
            var readerId = (Guid)(await reader.ExecuteScalarAsync())!;
            var badgeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (string code in configuration.Badges.Distinct(StringComparer.Ordinal))
            {
                string name = configuration.BadgeNames?.GetValueOrDefault(code) ?? "Usuario autorizado";
                await using var badge = Command("""
                    INSERT INTO rfid_lock.badges (id, code, user_name)
                    VALUES ($1, $2, $3)
                    ON CONFLICT (code) DO UPDATE SET code = EXCLUDED.code RETURNING id
                    """, Guid.NewGuid(), code, name);
                var badgeId = (Guid)(await badge.ExecuteScalarAsync())!;
                badgeIds.Add(code, badgeId);
                await using var permission = Command("""
                    INSERT INTO rfid_lock.machine_badge_access (machine_id, badge_id)
                    VALUES ($1, $2) ON CONFLICT DO NOTHING
                    """, machineId, badgeId);
                await permission.ExecuteNonQueryAsync();
            }

            foreach (string file in files)
            {
                string[] row = (await File.ReadAllTextAsync(file)).Split(',');
                string? Decode(string value) => value.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(value));
                string? code = Decode(row[2]);
                string type = row[1];
                object badgeId = code is not null && badgeIds.TryGetValue(code, out Guid id) ? id : DBNull.Value;
                if (type == "scan_allowed" && badgeId is DBNull)
                {
                    await using var find = Command("SELECT id FROM rfid_lock.badges WHERE code = $1", code!);
                    badgeId = await find.ExecuteScalarAsync() ?? DBNull.Value;
                }

                await using var insert = Command("""
                    INSERT INTO rfid_lock.events (id, machine_id, reader_id, badge_id, event_type,
                        occurred_at, scanned_code, reason)
                    VALUES ($1, $2, $3, $4::uuid, $5::rfid_lock.event_type, $6, $7::text, $8::text)
                    ON CONFLICT (id) DO NOTHING
                    """, Guid.Parse(Path.GetFileNameWithoutExtension(file)), machineId, readerId,
                    badgeId, type, DateTime.Parse(row[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    (object?)code ?? DBNull.Value, (object?)Decode(row[3]) ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            foreach (string file in files)
                File.Delete(file);
            AgentLog.Write($"PostgreSQL confirmó {files.Length} eventos.");
        }
        catch (Exception ex)
        {
            string error = ex is PostgresException postgres ? postgres.SqlState : ex.GetType().Name;
            AgentLog.Write($"La sincronización con PostgreSQL falló ({error}). Se conserva la cola local.");
        }
        finally
        {
            Volatile.Write(ref uploading, 0);
        }
    }

    public void Dispose() => upload.Dispose();
}
