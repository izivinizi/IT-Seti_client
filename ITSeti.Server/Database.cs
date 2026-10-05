using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace ITSeti.Server;

public static class Database
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, IConfiguration configuration, PasswordHasher<PortalAdmin> hasher)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS portal_admins (
                username text PRIMARY KEY,
                password_hash text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS companies (
                id bigint PRIMARY KEY,
                name text NOT NULL,
                active boolean NOT NULL DEFAULT true,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS service_objects (
                id bigint PRIMARY KEY,
                company_id bigint NOT NULL,
                name text NOT NULL,
                address text,
                active boolean NOT NULL DEFAULT true,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_service_objects_company ON service_objects(company_id,name);
            CREATE TABLE IF NOT EXISTS devices (
                id uuid PRIMARY KEY,
                computer_name text NOT NULL,
                serial_number text,
                hardware_fingerprint text,
                inventory_number text,
                company_id bigint NOT NULL,
                service_object_id bigint,
                api_key_hash text NOT NULL UNIQUE,
                support_key_hash text,
                os_name text,
                os_version text,
                created_at timestamptz NOT NULL,
                last_seen_at timestamptz
            );
            CREATE INDEX IF NOT EXISTS ix_devices_site_seen ON devices(company_id,service_object_id,last_seen_at DESC);
            ALTER TABLE devices ALTER COLUMN service_object_id DROP NOT NULL;
            ALTER TABLE devices ADD COLUMN IF NOT EXISTS support_key_hash text;
            ALTER TABLE devices ADD COLUMN IF NOT EXISTS hardware_fingerprint text;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_support_key ON devices(support_key_hash) WHERE support_key_hash IS NOT NULL;
            CREATE TABLE IF NOT EXISTS ticket_requests (
                request_id uuid PRIMARY KEY,
                device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
                company_id bigint NOT NULL,
                service_object_id bigint,
                title text NOT NULL,
                description text NOT NULL,
                service_code text NOT NULL,
                payload_hash text NOT NULL,
                state text NOT NULL CHECK (state IN ('queued','sending','created','rejected','unknown')),
                okdesk_issue_id bigint,
                last_error text,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_ticket_requests_device_created ON ticket_requests(device_id,created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_ticket_requests_queue ON ticket_requests(created_at) WHERE state='queued';
            CREATE TABLE IF NOT EXISTS ticket_messages (
                id uuid PRIMARY KEY,
                request_id uuid NOT NULL REFERENCES ticket_requests(request_id) ON DELETE CASCADE,
                direction text NOT NULL CHECK(direction IN ('user','engineer')),
                content text NOT NULL,
                payload_hash text,
                state text NOT NULL CHECK(state IN ('queued','sending','created','rejected','unknown')),
                okdesk_comment_id bigint,
                last_error text,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_ticket_messages_remote ON ticket_messages(request_id,okdesk_comment_id) WHERE okdesk_comment_id IS NOT NULL;
            ALTER TABLE ticket_messages ADD COLUMN IF NOT EXISTS payload_hash text;
            ALTER TABLE ticket_messages ADD COLUMN IF NOT EXISTS is_public boolean NOT NULL DEFAULT true;
            CREATE INDEX IF NOT EXISTS ix_ticket_messages_public_created ON ticket_messages(request_id,created_at,id)
                WHERE is_public AND direction='engineer';
            ALTER TABLE ticket_requests ADD COLUMN IF NOT EXISTS workflow_state text NOT NULL DEFAULT 'opened';
            CREATE TABLE IF NOT EXISTS ticket_actions (
                id uuid PRIMARY KEY, request_id uuid NOT NULL REFERENCES ticket_requests(request_id) ON DELETE CASCADE,
                target text NOT NULL CHECK(target IN ('completed','cancelled')), state text NOT NULL DEFAULT 'queued',
                last_error text, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_ticket_messages_queue ON ticket_messages(created_at) WHERE state='queued';
            CREATE TABLE IF NOT EXISTS ticket_attachments (
                id uuid PRIMARY KEY,
                request_id uuid NOT NULL REFERENCES ticket_requests(request_id) ON DELETE CASCADE,
                message_id uuid REFERENCES ticket_messages(id) ON DELETE CASCADE,
                file_name text NOT NULL,
                content_type text NOT NULL,
                bytes bytea NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_ticket_attachments_request ON ticket_attachments(request_id,message_id);
            CREATE TABLE IF NOT EXISTS check_runs (
                id uuid PRIMARY KEY,
                device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
                started_at timestamptz NOT NULL,
                kind text NOT NULL,
                summary jsonb NOT NULL,
                details jsonb NOT NULL,
                received_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_check_runs_device_started ON check_runs(device_id,started_at DESC);
            CREATE TABLE IF NOT EXISTS monitor_alerts (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
                run_id uuid NOT NULL REFERENCES check_runs(id) ON DELETE CASCADE,
                detected_at timestamptz NOT NULL DEFAULT now(),
                category text NOT NULL,
                identity text NOT NULL,
                title text NOT NULL,
                detail text NOT NULL,
                severity smallint NOT NULL,
                UNIQUE(run_id,category,identity)
            );
            CREATE INDEX IF NOT EXISTS ix_monitor_alerts_recent ON monitor_alerts(detected_at DESC,id DESC);
            ALTER TABLE monitor_alerts ADD COLUMN IF NOT EXISTS visible boolean NOT NULL DEFAULT true;
            ALTER TABLE check_runs ADD COLUMN IF NOT EXISTS is_test boolean NOT NULL DEFAULT false;
            CREATE TABLE IF NOT EXISTS packages (
                id uuid PRIMARY KEY,
                package_key text NOT NULL,
                version text NOT NULL,
                platform text NOT NULL,
                sha256 text NOT NULL,
                size_bytes bigint NOT NULL,
                download_name text NOT NULL,
                storage_name text NOT NULL,
                is_latest boolean NOT NULL DEFAULT false,
                created_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE(package_key,version,platform)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_packages_latest ON packages(package_key,platform) WHERE is_latest;
            """;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand(schema, connection)) await command.ExecuteNonQueryAsync();
        await MigrateDeviceIdentitiesAsync(connection);

        var username = configuration["BootstrapAdminUser"]?.Trim();
        var password = configuration["BootstrapAdminPassword"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || password.Length < 16)
            throw new InvalidOperationException("Set BootstrapAdminUser and a BootstrapAdminPassword of at least 16 characters.");
        var admin = new PortalAdmin(username);
        var hash = hasher.HashPassword(admin, password);
        await using var seed = new NpgsqlCommand("INSERT INTO portal_admins(username,password_hash) VALUES(@username,@hash) ON CONFLICT(username) DO NOTHING", connection);
        seed.Parameters.AddWithValue("username", username);
        seed.Parameters.AddWithValue("hash", hash);
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task MigrateDeviceIdentitiesAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var serialFingerprints = new List<object>();
        await using (var dropIndex = new NpgsqlCommand("DROP INDEX IF EXISTS ix_devices_hardware_fingerprint", connection, transaction))
            await dropIndex.ExecuteNonQueryAsync();
        await using (var read = new NpgsqlCommand(
            "SELECT id,serial_number FROM devices WHERE serial_number IS NOT NULL", connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var fingerprint = DeviceIdentity.CreateFingerprint(reader.GetString(1), null);
                if (fingerprint is not null) serialFingerprints.Add(new { id = reader.GetGuid(0), fingerprint });
            }
        }
        if (serialFingerprints.Count > 0)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE devices AS d SET hardware_fingerprint=x.fingerprint
                FROM jsonb_to_recordset(@rows::jsonb) AS x(id uuid,fingerprint text)
                WHERE d.id=x.id
                """, connection, transaction);
            update.Parameters.AddWithValue("rows", NpgsqlTypes.NpgsqlDbType.Jsonb,
                System.Text.Json.JsonSerializer.Serialize(serialFingerprints));
            await update.ExecuteNonQueryAsync();
        }

        await using (var merge = new NpgsqlCommand("""
            CREATE TEMP TABLE device_identity_merge ON COMMIT DROP AS
            SELECT id AS duplicate_id, canonical_id FROM (
                SELECT id, first_value(id) OVER (
                    PARTITION BY hardware_fingerprint
                    ORDER BY last_seen_at DESC NULLS LAST, created_at DESC, id) AS canonical_id
                FROM devices WHERE hardware_fingerprint IS NOT NULL
            ) ranked WHERE id<>canonical_id;
            UPDATE ticket_requests child SET device_id=map.canonical_id
            FROM device_identity_merge map WHERE child.device_id=map.duplicate_id;
            UPDATE check_runs child SET device_id=map.canonical_id
            FROM device_identity_merge map WHERE child.device_id=map.duplicate_id;
            UPDATE monitor_alerts child SET device_id=map.canonical_id
            FROM device_identity_merge map WHERE child.device_id=map.duplicate_id;
            DELETE FROM devices device USING device_identity_merge map WHERE device.id=map.duplicate_id;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_hardware_fingerprint
                ON devices(hardware_fingerprint) WHERE hardware_fingerprint IS NOT NULL;
            """, connection, transaction))
            await merge.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public static async Task<IReadOnlyList<object>> ReadCatalogAsync(NpgsqlConnection connection, string sql, string kind)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object>();
        while (await reader.ReadAsync())
        {
            if (kind == "company") rows.Add(new { id = reader.GetInt64(0), name = reader.GetString(1), active = reader.GetBoolean(2) });
            else rows.Add(new { id = reader.GetInt64(0), name = reader.GetString(1), address = reader.IsDBNull(2) ? null : reader.GetString(2), companyId = reader.GetInt64(3) });
        }
        return rows;
    }
}
