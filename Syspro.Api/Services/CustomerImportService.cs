using System.Text;
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
        Stream input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Decodes exactly as File.ReadAllLines did: UTF-8 unless a byte order mark says
        // otherwise, and lines ending in LF, CR or CRLF. It also handles characters and
        // line endings that are split across two reads from the stream.
        using var reader = new StreamReader(
            input,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        // Ignores case to match the unique index under SQL Server's default collation.
        var firstLineByLegacyId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lineNumber++;

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

        _importRepository.RemoveJobInput(importLog.Id);

        // The only save in the import. Both repositories share one scoped context, so
        // this commits the customers, row errors, job status and input removal in one
        // transaction.
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