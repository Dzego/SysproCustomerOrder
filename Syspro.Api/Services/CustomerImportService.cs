using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public class CustomerImportService : ICustomerImportService
{
    private readonly LegacyCustomerParser _parser;
    private readonly ICustomerRepository _customerRepository;
    private readonly IImportRepository _importRepository;

    public CustomerImportService(
        LegacyCustomerParser parser,
        ICustomerRepository customerRepository,
        IImportRepository importRepository)
    {
        _parser = parser;
        _customerRepository = customerRepository;
        _importRepository = importRepository;
    }

    public async Task<ImportResult> ImportAsync(
        ImportLog importLog,
        string filePath,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);

        // Ignores case to match the unique index under SQL Server's default collation.
        var firstLineByLegacyId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[index];
            var lineNumber = index + 1;

            importLog.ProcessedCount++;

            try
            {
                var record = _parser.Parse(line);

                // Added only after parsing, so the first valid row is the one kept.
                if (!firstLineByLegacyId.TryAdd(record.LegacyCustomerId, lineNumber))
                {
                    RecordFailure(
                        importLog,
                        lineNumber,
                        line,
                        $"Duplicate LegacyCustomerId {record.LegacyCustomerId}. " +
                        $"First seen on line {firstLineByLegacyId[record.LegacyCustomerId]}.");

                    continue;
                }

                var existingCustomer =
                    await _customerRepository.GetByLegacyCustomerIdAsync(
                        record.LegacyCustomerId,
                        cancellationToken);

                if (existingCustomer is null)
                {
                    var customer = new Customer
                    {
                        LegacyCustomerId = record.LegacyCustomerId,
                        Name = record.Name,
                        Email = record.Email,
                        SignupDate = record.SignupDate,
                        Tier = record.Tier
                    };

                    await _customerRepository.AddAsync(customer, cancellationToken);

                    importLog.CreatedCount++;
                }
                else
                {
                    existingCustomer.Name = record.Name;
                    existingCustomer.Email = record.Email;
                    existingCustomer.SignupDate = record.SignupDate;
                    existingCustomer.Tier = record.Tier;

                    importLog.UpdatedCount++;
                }
            }
            catch (LegacyCustomerParseException exception)
            {
                RecordFailure(importLog, lineNumber, line, exception.Message);
            }
        }

        importLog.Status = ImportJobStatus.Completed;
        importLog.CompletedAt = DateTime.UtcNow;

        await _customerRepository.SaveChangesAsync(cancellationToken);
        await _importRepository.SaveChangesAsync(cancellationToken);
        
        return new ImportResult
        {
            Processed = importLog.ProcessedCount,
            Created = importLog.CreatedCount,
            Updated = importLog.UpdatedCount,
            Failed = importLog.FailedCount
        };
    }

    private static void RecordFailure(
        ImportLog importLog,
        int lineNumber,
        string line,
        string reason)
    {
        const int maxLength = ImportError.RawDataMaxLength;

        var isTooLong = line.Length > maxLength;

        importLog.FailedCount++;

        importLog.Errors.Add(new ImportError
        {
            LineNumber = lineNumber,
            RawData = isTooLong ? line[..maxLength] : line,
            Reason = isTooLong
                ? $"{reason} RawData was truncated from {line.Length} to {maxLength} characters."
                : reason
        });
    }
}