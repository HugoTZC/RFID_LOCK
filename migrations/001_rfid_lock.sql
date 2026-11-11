BEGIN;

CREATE SCHEMA rfid_lock;

CREATE TYPE rfid_lock.badge_role AS ENUM ('usuario', 'admin', 'superUsuario');
CREATE TYPE rfid_lock.reader_mode AS ENUM ('hid', 'serial');
CREATE TYPE rfid_lock.event_type AS ENUM (
    'scan_allowed', 'scan_denied', 'session_started', 'session_ended',
    'screen_locked', 'reader_connected', 'reader_disconnected', 'reader_error',
    'admin_ui_opened', 'config_validated', 'config_failed',
    'protection_enabled', 'protection_paused', 'recovery_used',
    'service_started', 'service_stopping'
);
CREATE TYPE rfid_lock.session_end_reason AS ENUM (
    'idle', 'sign_out', 'admin_pause', 'shutdown', 'recovery', 'interrupted'
);

CREATE TABLE rfid_lock.machines (
    id uuid PRIMARY KEY,
    name text NOT NULL CHECK (btrim(name) <> ''),
    time_zone text NOT NULL CHECK (btrim(time_zone) <> ''),
    upload_after time NOT NULL,
    idle_seconds integer NOT NULL DEFAULT 300 CHECK (idle_seconds > 0),
    configuration_verified_at timestamptz,
    protection_enabled boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (NOT protection_enabled OR configuration_verified_at IS NOT NULL)
);

CREATE UNIQUE INDEX machines_name_unique ON rfid_lock.machines (lower(name));

CREATE TABLE rfid_lock.readers (
    id uuid PRIMARY KEY,
    machine_id uuid NOT NULL REFERENCES rfid_lock.machines (id),
    mode rfid_lock.reader_mode NOT NULL,
    hid_instance text,
    serial_port text,
    baud_rate integer,
    badge_length integer CHECK (badge_length BETWEEN 1 AND 128),
    enabled boolean NOT NULL DEFAULT true,
    registered_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (machine_id, id),
    CHECK (
        (mode = 'hid' AND nullif(btrim(hid_instance), '') IS NOT NULL
            AND badge_length IS NOT NULL AND serial_port IS NULL AND baud_rate IS NULL)
        OR (mode = 'serial' AND hid_instance IS NULL
            AND nullif(btrim(serial_port), '') IS NOT NULL
            AND baud_rate IS NOT NULL AND baud_rate > 0)
    )
);

CREATE UNIQUE INDEX readers_active_hid_unique
    ON rfid_lock.readers (machine_id, lower(hid_instance))
    WHERE enabled AND mode = 'hid';
CREATE UNIQUE INDEX readers_active_serial_unique
    ON rfid_lock.readers (machine_id, lower(serial_port))
    WHERE enabled AND mode = 'serial';

CREATE TABLE rfid_lock.badges (
    id uuid PRIMARY KEY,
    code text NOT NULL UNIQUE CHECK (length(code) BETWEEN 1 AND 128 AND btrim(code) <> ''),
    user_name text NOT NULL CHECK (btrim(user_name) <> ''),
    employee_number text NOT NULL CHECK (btrim(employee_number) <> ''),
    role rfid_lock.badge_role NOT NULL DEFAULT 'usuario',
    enabled boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE rfid_lock.machine_badge_access (
    machine_id uuid NOT NULL REFERENCES rfid_lock.machines (id),
    badge_id uuid NOT NULL REFERENCES rfid_lock.badges (id),
    granted_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz CHECK (revoked_at >= granted_at),
    PRIMARY KEY (machine_id, badge_id)
);

CREATE INDEX machine_badge_access_badge_idx
    ON rfid_lock.machine_badge_access (badge_id);

CREATE TABLE rfid_lock.sessions (
    id uuid PRIMARY KEY,
    machine_id uuid NOT NULL REFERENCES rfid_lock.machines (id),
    reader_id uuid NOT NULL,
    badge_id uuid NOT NULL REFERENCES rfid_lock.badges (id),
    started_at timestamptz NOT NULL,
    ended_at timestamptz,
    end_reason rfid_lock.session_end_reason,
    UNIQUE (machine_id, id),
    FOREIGN KEY (machine_id, reader_id) REFERENCES rfid_lock.readers (machine_id, id),
    CHECK (ended_at >= started_at),
    CHECK ((ended_at IS NULL) = (end_reason IS NULL))
);

CREATE UNIQUE INDEX sessions_one_open_per_machine
    ON rfid_lock.sessions (machine_id) WHERE ended_at IS NULL;
CREATE INDEX sessions_machine_started_idx ON rfid_lock.sessions (machine_id, started_at DESC);
CREATE INDEX sessions_badge_started_idx ON rfid_lock.sessions (badge_id, started_at DESC);

CREATE VIEW rfid_lock.session_durations AS
SELECT s.*, EXTRACT(epoch FROM (ended_at - started_at)) AS duration_seconds
FROM rfid_lock.sessions AS s;

CREATE TABLE rfid_lock.events (
    id uuid PRIMARY KEY,
    machine_id uuid NOT NULL REFERENCES rfid_lock.machines (id),
    reader_id uuid,
    badge_id uuid REFERENCES rfid_lock.badges (id),
    session_id uuid,
    event_type rfid_lock.event_type NOT NULL,
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    scanned_code text CHECK (length(scanned_code) BETWEEN 1 AND 128),
    reason text,
    details jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(details) = 'object'),
    FOREIGN KEY (machine_id, reader_id) REFERENCES rfid_lock.readers (machine_id, id),
    FOREIGN KEY (machine_id, session_id) REFERENCES rfid_lock.sessions (machine_id, id),
    CHECK (event_type NOT IN ('scan_allowed', 'scan_denied')
        OR (reader_id IS NOT NULL AND scanned_code IS NOT NULL)),
    CHECK (event_type <> 'scan_allowed' OR badge_id IS NOT NULL),
    CHECK (event_type <> 'scan_denied' OR nullif(btrim(reason), '') IS NOT NULL),
    CHECK (event_type NOT IN ('session_started', 'session_ended') OR session_id IS NOT NULL)
);

CREATE INDEX events_machine_time_idx ON rfid_lock.events (machine_id, occurred_at DESC);
CREATE INDEX events_badge_time_idx ON rfid_lock.events (badge_id, occurred_at DESC)
    WHERE badge_id IS NOT NULL;

COMMIT;
