using Npgsql;

namespace SbbBot.Helpers;

public interface IDbConnectionFactory
{
    NpgsqlConnection CreateConnection();
}
