using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ITSeti.Server;

public sealed record OkdeskSyncResult(bool Success, int Companies, int Sites, DateTimeOffset? CompletedAt, string? Error);

public sealed class OkdeskSyncService(NpgsqlDataSource dataSource, IOkdeskClient okdesk, ILogger<OkdeskSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromDays(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (okdesk.Configured)
            {
                try
                {
                    var lastRefresh = await GetLastCatalogRefreshAsync(stoppingToken);
                    var wait = lastRefresh?.Add(CatalogRefreshInterval) - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait.Value, stoppingToken);
                        continue;
                    }
                    var result = await SyncOnceAsync(stoppingToken);
                    if (result.Success)
                    {
                        logger.LogInformation("Okdesk catalog updated: {Companies} companies, {Sites} sites.", result.Companies, result.Sites);
                        continue;
                    }
                    logger.LogWarning("Okdesk catalog sync failed: {Error}", result.Error);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (NpgsqlException)
                {
                    logger.LogWarning("Could not read last Okdesk catalog sync time.");
                }
            }
            try { await Task.Delay(RetryInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<DateTimeOffset?> GetLastCatalogRefreshAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT (SELECT max(updated_at) FROM companies), (SELECT max(updated_at) FROM service_objects)", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;
        var companiesAt = reader.GetDateTime(0);
        var sitesAt = reader.GetDateTime(1);
        return new DateTimeOffset(companiesAt < sitesAt ? companiesAt : sitesAt);
    }

    public async Task<OkdeskSyncResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!okdesk.Configured) return new(false, 0, 0, null, "Подключение Okdesk не настроено.");
        try
        {
            var companies = await okdesk.GetAllAsync("/api/v1/companies/list", cancellationToken);
            var sites = await okdesk.GetAllAsync("/api/v1/maintenance_entities/list", cancellationToken);
            if (companies.Count == 0) throw new JsonException("Okdesk returned an empty company catalog.");
            // Objects can reference companies omitted from the accessible company list.
            // Retain those relationships without offering these owners for new enrollment.
            var completeCompanies = companies.ToList();
            var ownerIds = companies.Select(x => ReadLong(x, "id") ?? throw new JsonException("Missing company ID.")).ToHashSet();
            foreach (var site in sites)
            {
                var owner = ReadLong(site, "company_id", "companyId") ?? throw new JsonException("Missing object owner.");
                if (ownerIds.Add(owner))
                    completeCompanies.Add(JsonSerializer.SerializeToElement(new
                    {
                        id = owner, name = JsonRead.String(site, "company_name") ?? "Недоступная компания", active = false
                    }));
            }
            await SaveCatalogAsync(completeCompanies, sites, cancellationToken);
            return new(true, companies.Count, sites.Count, DateTimeOffset.UtcNow, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OkdeskApiException ex)
        {
            logger.LogWarning("Okdesk API catalog request failed with HTTP {StatusCode}.", ex.StatusCode);
            return new(false, 0, 0, null, $"Okdesk вернул HTTP {ex.StatusCode}. Проверьте настройки входа и права на каталоги.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NpgsqlException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning("Okdesk API catalog sync failed: {ErrorType}.", ex.GetType().Name);
            return new(false, 0, 0, null, "Okdesk не ответил или вернул неожиданные данные. Существующий каталог сохранён.");
        }
    }

    private async Task SaveCatalogAsync(IReadOnlyList<JsonElement> companies, IReadOnlyList<JsonElement> sites, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var companyCommand = new NpgsqlCommand("""
            INSERT INTO companies(id,name,active,updated_at) VALUES(@id,@name,@active,now())
            ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name,active=EXCLUDED.active,updated_at=now()
            """, connection, transaction);
        var companyId = companyCommand.Parameters.Add("id", NpgsqlDbType.Bigint);
        var companyName = companyCommand.Parameters.Add("name", NpgsqlDbType.Text);
        var companyActive = companyCommand.Parameters.Add("active", NpgsqlDbType.Boolean);
        foreach (var item in companies)
        {
            companyId.Value = ReadLong(item, "id") ?? throw new JsonException("Okdesk company ID is missing.");
            companyName.Value = JsonRead.String(item, "name") ?? "Без названия";
            companyActive.Value = ReadBool(item, "active") ?? true;
            await companyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var siteCommand = new NpgsqlCommand("""
            INSERT INTO service_objects(id,company_id,name,address,active,updated_at)
            VALUES(@id,@company,@name,@address,@active,now())
            ON CONFLICT(id) DO UPDATE SET company_id=EXCLUDED.company_id,name=EXCLUDED.name,
                address=EXCLUDED.address,active=EXCLUDED.active,updated_at=now()
            """, connection, transaction);
        var siteId = siteCommand.Parameters.Add("id", NpgsqlDbType.Bigint);
        var siteCompany = siteCommand.Parameters.Add("company", NpgsqlDbType.Bigint);
        var siteName = siteCommand.Parameters.Add("name", NpgsqlDbType.Text);
        var siteAddress = siteCommand.Parameters.Add("address", NpgsqlDbType.Text);
        var siteActive = siteCommand.Parameters.Add("active", NpgsqlDbType.Boolean);
        foreach (var item in sites)
        {
            siteId.Value = ReadLong(item, "id") ?? throw new JsonException("Okdesk site ID is missing.");
            siteCompany.Value = ReadLong(item, "company_id", "companyId") ?? throw new JsonException("Okdesk site company ID is missing.");
            siteName.Value = JsonRead.String(item, "name") ?? "Без названия";
            siteAddress.Value = (object?)JsonRead.String(item, "address") ?? DBNull.Value;
            siteActive.Value = ReadBool(item, "active") ?? true;
            await siteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static long? ReadLong(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = JsonRead.Property(item, name);
            if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out var number)) return number;
            if (value is { ValueKind: JsonValueKind.String } && long.TryParse(value.Value.GetString(), out number)) return number;
        }
        return null;
    }

    private static bool? ReadBool(JsonElement item, string name)
    {
        var value = JsonRead.Property(item, name);
        return value is { ValueKind: JsonValueKind.True or JsonValueKind.False } ? value.Value.GetBoolean() : null;
    }
}
