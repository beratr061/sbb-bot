using Dapper;
using Microsoft.Extensions.Logging;
using SbbBot.Helpers;
using SbbBot.Models;

namespace SbbBot.Repositories;

public class FareRepository
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<FareRepository> _logger;

    public FareRepository(IDbConnectionFactory dbFactory, ILogger<FareRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Fare?> GetByLineNumberAsync(string lineNumber)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT line_number as LineNumber, full_fare as FullFare, 
                       student_fare as StudentFare, discounted_fare as DiscountedFare,
                       raw_json as RawJson, updated_at as UpdatedAt 
                FROM fares 
                WHERE line_number = @LineNumber";
            return await conn.QuerySingleOrDefaultAsync<Fare?>(sql, new { LineNumber = lineNumber });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetByLineNumberAsync error for {LineNumber}", lineNumber);
            throw;
        }
    }

    public async Task UpsertAsync(Fare fare)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO fares (line_number, full_fare, student_fare, discounted_fare, raw_json, updated_at)
                VALUES (@LineNumber, @FullFare, @StudentFare, @DiscountedFare, @RawJson, NOW())
                ON CONFLICT (line_number) DO UPDATE SET 
                    full_fare = @FullFare, 
                    student_fare = @StudentFare,
                    discounted_fare = @DiscountedFare, 
                    raw_json = @RawJson,
                    updated_at = NOW()";
            await conn.ExecuteAsync(sql, fare);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertAsync error for {LineNumber}", fare?.LineNumber);
            throw;
        }
    }

    public async Task<bool> IsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM system_state WHERE key = 'fares_seeded'";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql);
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsSeededAsync error in FareRepository");
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
                VALUES ('fares_seeded', 'true', NOW())
                ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = NOW()";
            await conn.ExecuteAsync(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkAsSeededAsync error in FareRepository");
            throw;
        }
    }
}
