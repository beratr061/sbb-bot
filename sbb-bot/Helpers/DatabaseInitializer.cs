using Dapper;
using Serilog;
using System.Threading.Tasks;

namespace SbbBot.Helpers;

public class DatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DatabaseInitializer(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync()
    {
        using var connection = _connectionFactory.CreateConnection();

        var tablesToCreate = new (string Name, string Sql)[]
        {
            ("bus_lines", @"
CREATE TABLE IF NOT EXISTS bus_lines (
    id SERIAL PRIMARY KEY,
    line_number TEXT NOT NULL,
    line_name TEXT,
    bus_type TEXT,
    raw_json TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(line_number)
);"),

            ("routes", @"
CREATE TABLE IF NOT EXISTS routes (
    id SERIAL PRIMARY KEY,
    line_number TEXT NOT NULL,
    direction TEXT,
    content_hash TEXT,
    raw_json TEXT,
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(line_number, direction)
);"),

            ("bus_stops", @"
CREATE TABLE IF NOT EXISTS bus_stops (
    id SERIAL PRIMARY KEY,
    line_number TEXT NOT NULL,
    direction TEXT,
    stop_order INT,
    stop_name TEXT,
    updated_at TIMESTAMPTZ DEFAULT NOW()
);"),

            ("fares", @"
CREATE TABLE IF NOT EXISTS fares (
    id SERIAL PRIMARY KEY,
    line_number TEXT NOT NULL,
    full_fare DECIMAL(10,2),
    student_fare DECIMAL(10,2),
    raw_json TEXT,
    discounted_fare DECIMAL(10,2),
    content_hash TEXT,
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(line_number)
);"),

            ("announcements", @"
CREATE TABLE IF NOT EXISTS announcements (
    id SERIAL PRIMARY KEY,
    announcement_id TEXT NOT NULL,
    title TEXT,
    content TEXT,
    start_date TIMESTAMPTZ,
    end_date TIMESTAMPTZ,
    content_hash TEXT,
    raw_json TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(announcement_id)
);"),

            ("ukome_decisions", @"
CREATE TABLE IF NOT EXISTS ukome_decisions (
    id SERIAL PRIMARY KEY,
    title TEXT NOT NULL,
    url TEXT,
    decision_date TIMESTAMPTZ,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(content_hash)
);"),

            ("news", @"
CREATE TABLE IF NOT EXISTS news (
    id SERIAL PRIMARY KEY,
    title TEXT NOT NULL,
    url TEXT,
    published_date TIMESTAMPTZ,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(content_hash)
);"),

            ("documents", @"
CREATE TABLE IF NOT EXISTS documents (
    id SERIAL PRIMARY KEY,
    title TEXT NOT NULL,
    url TEXT,
    published_date TIMESTAMPTZ,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(content_hash)
);"),

            ("meetings", @"
CREATE TABLE IF NOT EXISTS meetings (
    id SERIAL PRIMARY KEY,
    title TEXT NOT NULL,
    url TEXT,
    published_date TIMESTAMPTZ,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(content_hash)
);"),

            ("open_data_sets", @"
CREATE TABLE IF NOT EXISTS open_data_sets (
    id SERIAL PRIMARY KEY,
    dataset_id TEXT NOT NULL,
    title TEXT,
    activity_type TEXT,
    timestamp TIMESTAMPTZ,
    UNIQUE(dataset_id)
);"),

            ("system_state", @"
CREATE TABLE IF NOT EXISTS system_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TIMESTAMPTZ DEFAULT NOW()
);")
        };

        foreach (var table in tablesToCreate)
        {
            await connection.ExecuteAsync(table.Sql);
            Log.Information("[DB] {TableName} tablosu hazır.", table.Name);
        }

        // Schema migrations: add columns that may be missing from earlier versions
        var migrations = new (string Description, string Sql)[]
        {
            ("announcements.raw_json", "ALTER TABLE announcements ADD COLUMN IF NOT EXISTS raw_json TEXT;"),
            ("bus_lines.api_id", "ALTER TABLE bus_lines ADD COLUMN IF NOT EXISTS api_id INTEGER;"),
            ("fares.raw_json", "ALTER TABLE fares ADD COLUMN IF NOT EXISTS raw_json TEXT;"),
            ("fares.discounted_fare", "ALTER TABLE fares ADD COLUMN IF NOT EXISTS discounted_fare NUMERIC(10,2) DEFAULT 0;"),
            ("bus_stops.unique_constraint", @"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'uq_bus_stops_line_dir_order'
                    ) THEN
                        ALTER TABLE bus_stops ADD CONSTRAINT uq_bus_stops_line_dir_order 
                            UNIQUE (line_number, direction, stop_order);
                    END IF;
                END $$;"),
        };

        foreach (var migration in migrations)
        {
            try
            {
                await connection.ExecuteAsync(migration.Sql);
                Log.Information("[DB] Migration uygulandı: {Description}", migration.Description);
            }
            catch (Exception ex)
            {
                Log.Warning("[DB] Migration atlandı ({Description}): {Message}", migration.Description, ex.Message);
            }
        }
    }
}
