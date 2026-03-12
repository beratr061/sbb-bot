using System;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

class Program
{
    static async Task Main()
    {
        string connStr = "postgresql://neondb_owner:npg_8xNO2AdUGpMD@ep-little-breeze-ags9nrue.c-2.eu-central-1.aws.neon.tech/neondb?sslmode=require";
        
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "ep-little-breeze-ags9nrue.c-2.eu-central-1.aws.neon.tech",
            Port = 5432,
            Database = "neondb",
            Username = "neondb_owner",
            Password = "npg_8xNO2AdUGpMD",
            SslMode = SslMode.Require
        };
        
        using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        
        var tables = await conn.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'");
        Console.WriteLine("Tables:");
        foreach (var t in tables)
        {
            Console.WriteLine(t);
        }
    }
}
