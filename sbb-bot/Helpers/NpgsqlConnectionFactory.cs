using Microsoft.Extensions.Options;
using Npgsql;
using SbbBot.Models;

namespace SbbBot.Helpers;

public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    public NpgsqlConnectionFactory(IOptions<DatabaseOptions> options)
        => _connectionString = ParseConnectionString(options.Value.ConnectionString);

    public NpgsqlConnection CreateConnection()
        => new NpgsqlConnection(_connectionString);

    private static string ParseConnectionString(string connectionUrl)
    {
        if (string.IsNullOrWhiteSpace(connectionUrl))
            return connectionUrl;

        if (!connectionUrl.StartsWith("postgres://", System.StringComparison.OrdinalIgnoreCase) && 
            !connectionUrl.StartsWith("postgresql://", System.StringComparison.OrdinalIgnoreCase))
        {
            return connectionUrl;
        }

        var uri = new System.Uri(connectionUrl);
        var userInfo = uri.UserInfo.Split(':');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = userInfo[0],
            Password = userInfo.Length > 1 ? userInfo[1] : "",
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
