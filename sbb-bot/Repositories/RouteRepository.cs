using Dapper;
using Microsoft.Extensions.Logging;
using SbbBot.Helpers;

namespace SbbBot.Repositories;

public class RouteRepository
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<RouteRepository> _logger;

    public RouteRepository(IDbConnectionFactory dbFactory, ILogger<RouteRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<string?> GetHashAsync(string lineNumber, string direction)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT content_hash 
                FROM routes 
                WHERE line_number = @LineNumber AND direction = @Direction";
            return await conn.QuerySingleOrDefaultAsync<string?>(sql, new { LineNumber = lineNumber, Direction = direction });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHashAsync error for {LineNumber} - {Direction}", lineNumber, direction);
            throw;
        }
    }

    public async Task<string?> GetRawJsonAsync(string lineNumber, string direction)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT raw_json 
                FROM routes 
                WHERE line_number = @LineNumber AND direction = @Direction";
            return await conn.QuerySingleOrDefaultAsync<string?>(sql, new { LineNumber = lineNumber, Direction = direction });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRawJsonAsync error for {LineNumber} - {Direction}", lineNumber, direction);
            throw;
        }
    }

    public async Task UpsertAsync(string lineNumber, string direction, string hash, string rawJson)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO routes (line_number, direction, content_hash, raw_json, updated_at)
                VALUES (@LineNumber, @Direction, @Hash, @RawJson, NOW())
                ON CONFLICT (line_number, direction) DO UPDATE SET 
                    content_hash = @Hash, 
                    raw_json = @RawJson, 
                    updated_at = NOW()";
            await conn.ExecuteAsync(sql, new { LineNumber = lineNumber, Direction = direction, Hash = hash, RawJson = rawJson });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertAsync error for {LineNumber} - {Direction}", lineNumber, direction);
            throw;
        }
    }

    public async Task<bool> IsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM system_state WHERE key = 'routes_seeded'";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql);
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsSeededAsync error in RouteRepository");
            throw;
        }
    }

    public async Task MarkAsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO system_state (key, value, updated_at) 
                VALUES ('routes_seeded', 'true', NOW())
                ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = NOW()";
            await conn.ExecuteAsync(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkAsSeededAsync error in RouteRepository");
            throw;
        }
    }
}
