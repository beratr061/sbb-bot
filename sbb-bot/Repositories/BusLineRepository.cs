using Dapper;
using Microsoft.Extensions.Logging;
using SbbBot.Helpers;
using SbbBot.Models;

namespace SbbBot.Repositories;

public class BusLineRepository
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<BusLineRepository> _logger;

    public BusLineRepository(IDbConnectionFactory dbFactory, ILogger<BusLineRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<BusLine>> GetAllAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT api_id as ApiId, line_number as LineNumber, line_name as LineName, 
                       bus_type as BusType, raw_json as RawJson, updated_at as UpdatedAt 
                FROM bus_lines";
            var result = await conn.QueryAsync<BusLine>(sql);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllAsync error in BusLineRepository");
            throw;
        }
    }

    public async Task<BusLine?> GetByLineNumberAsync(string lineNumber)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT api_id as ApiId, line_number as LineNumber, line_name as LineName, 
                       bus_type as BusType, raw_json as RawJson, updated_at as UpdatedAt 
                FROM bus_lines 
                WHERE line_number = @LineNumber";
            return await conn.QuerySingleOrDefaultAsync<BusLine?>(sql, new { LineNumber = lineNumber });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetByLineNumberAsync error for {LineNumber}", lineNumber);
            throw;
        }
    }

    public async Task UpsertAsync(BusLine line)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO bus_lines (line_number, line_name, bus_type, raw_json, api_id, updated_at)
                VALUES (@LineNumber, @LineName, @BusType, @RawJson, @ApiId, NOW())
                ON CONFLICT (line_number) DO UPDATE SET 
                    line_name = @LineName,
                    bus_type = @BusType,
                    raw_json = @RawJson,
                    api_id = @ApiId,
                    updated_at = NOW()";
            await conn.ExecuteAsync(sql, line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertAsync error for {LineNumber}", line?.LineNumber);
            throw;
        }
    }

    public async Task DeleteAsync(string lineNumber)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "DELETE FROM bus_lines WHERE line_number = @LineNumber";
            await conn.ExecuteAsync(sql, new { LineNumber = lineNumber });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteAsync error for {LineNumber}", lineNumber);
            throw;
        }
    }

    public async Task<bool> IsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM system_state WHERE key = 'bus_lines_seeded'";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql);
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsSeededAsync error in BusLineRepository");
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
                VALUES ('bus_lines_seeded', 'true', NOW())
                ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = NOW()";
            await conn.ExecuteAsync(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkAsSeededAsync error in BusLineRepository");
            throw;
        }
    }
}
