using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Dapper;
using Npgsql;

public class Program
{
    public static void Main()
    {
        var basePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "sbb-bot");
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.Development.json");

        var config = builder.Build();
        var uriString = config.GetSection("Database:ConnectionString").Value;
        
        var uri = new Uri(uriString);
        var db = uri.AbsolutePath.Trim('/');
        var user = uri.UserInfo.Split(':')[0];
        var passwd = uri.UserInfo.Split(':')[1];
        var port = uri.Port > 0 ? uri.Port : 5432;
        var connectionString = $"Host={uri.Host};Port={port};Database={db};Username={user};Password={passwd};SslMode=Require;TrustServerCertificate=True;";
        
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        var tables = new[] {
            "bus_lines", "routes", "bus_stops", "fares", "announcements", 
            "ukome_decisions", "news", "documents", "open_data_sets", "system_state"
        };

        foreach (var table in tables) 
        {
            Console.WriteLine($"Clearing table: {table}");
            try 
            {
                conn.Execute($"TRUNCATE TABLE {table} RESTART IDENTITY CASCADE;");
            } 
            catch (Exception ex) 
            {
                Console.WriteLine($"Error on {table}: {ex.Message}");
            }
        }

        Console.WriteLine("All tables have been cleared.");
    }
}
