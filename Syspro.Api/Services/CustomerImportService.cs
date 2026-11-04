using System.Text;
using Microsoft.Extensions.Options;
using Syspro.Api.Models;
using Syspro.Api.Repositories;
using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public class CustomerImportService : ICustomerImportService
{
    private readonly LegacyCustomerParser _parser;
    private readonly ICustomerRepository _customerRepository;
    private readonly IImportRepository _importRepository;
    private readonly int _batchSize;

    public CustomerImportService(
        LegacyCustomerParser parser,
        ICustomerRepository customerRepository,
        IImportRepository importRepository,
        IOptions<CustomerImportOptions> options)
    {
        _parser = parser;
        _customerRepository = customerRepository;
        _importRepository = importRepository;
        _batchSize = options.Value.BatchSize;
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

        await SkipCommittedLinesAsync(reader, importLog.ProcessedCount, firstLineByLegacyId, cancellationToken);

        // Reads one line ahead, so that the last batch can complete the job in the same
        // transaction. Reading happens between transactions.
        var nextLine = await reader.ReadLineAsync(cancellationToken);

        while (true)
        {
            var lines = new List<string>();

            while (nextLine is not null && lines.Count < _batchSize)
            {
                lines.Add(nextLine);

                nextLine = await reader.ReadLineAsync(cancellationToken);
            }

            await CommitBatchAsync(
                importLog,
                lines,
                firstLineByLegacyId,
                isLastBatch: nextLine is null,
                cancellationToken);

            if (nextLine is null)
            {
                break;
            }
        }

        return new ImportResult
        {
            Processed = importLog.ProcessedCount,
            Created = importLog.CreatedCount,
            Updated = importLog.UpdatedCount,
            Failed = importLog.FailedCount
        };
    }

    // ProcessedCount is the durable checkpoint, and these lines are already committed.
    // They are only parsed, to rebuild the duplicate check as it was when they ran.
    private async Task SkipCommittedLinesAsync(
        StreamReader reader,
        int committedLines,
        Dictionary<string, int> firstLineByLegacyId,
        CancellationToken cancellationToken)
    {
        for (var lineNumber = 1; lineNumber <= committedLines; lineNumber++)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"The stored input has fewer lines than the {committedLines} already committed.");

            try
            {
                firstLineByLegacyId.TryAdd(_parser.Parse(line).LegacyCustomerId, lineNumber);
            }
            catch (LegacyCustomerParseException)
            {
                // An invalid row does not count as the first occurrence of an ID.
            }
        }
    }

    private async Task CommitBatchAsync(
        ImportLog importLog,
        List<string> lines,
        Dictionary<string, int> firstLineByLegacyId,
        bool isLastBatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var records = new List<LegacyCustomerRecord>(lines.Count);
        var errors = new List<ImportError>();

        // Parsing needs no database access, so it happens before the transaction opens.
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var lineNumber = importLog.ProcessedCount + index + 1;

            try
            {
                var record = _parser.Parse(line);

                // Added only after parsing, so the first valid row is the one kept.
                if (firstLineByLegacyId.TryAdd(record.LegacyCustomerId, lineNumber))
                {
                    records.Add(record);
                }
                else
                {
                    errors.Add(CreateError(
                        importLog.Id,
                        lineNumber,
                        line,
                        $"Duplicate LegacyCustomerId {record.LegacyCustomerId}. " +
                        $"First seen on line {firstLineByLegacyId[record.LegacyCustomerId]}."));
                }
            }
            catch (LegacyCustomerParseException exception)
            {
                errors.Add(CreateError(importLog.Id, lineNumber, line, exception.Message));
            }
        }

        var created = 0;
        var updated = 0;

        // Customers, row errors, counters and the checkpoint commit together, so a batch
        // is either fully applied or not applied at all.
        await _importRepository.ExecuteInTransactionAsync(
            async token =>
            {
                var existing = await _customerRepository.GetByLegacyCustomerIdsAsync(
                    records.Select(record => record.LegacyCustomerId).ToList(),
                    token);

                token.ThrowIfCancellationRequested();

                foreach (var record in records)
                {
                    if (existing.TryGetValue(record.LegacyCustomerId, out var customer))
                    {
                        customer.Name = record.Name;
                        customer.Email = record.Email;
                        customer.SignupDate = record.SignupDate;
                        customer.Tier = record.Tier;

                        updated++;
                    }
                    else
                    {
                        _customerRepository.Add(new Customer
                        {
                            LegacyCustomerId = record.LegacyCustomerId,
                            Name = record.Name,
                            Email = record.Email,
                            SignupDate = record.SignupDate,
                            Tier = record.Tier
                        });

                        created++;
                    }
                }

                _importRepository.AddErrors(errors);

                var applied = await _importRepository.ApplyBatchProgressAsync(
                    importLog.Id,
                    importLog.ProcessedCount,
                    new ImportBatchProgress(lines.Count, created, updated, errors.Count),
                    isLastBatch,
                    token);

                // Another execution has changed this job, so this batch must not apply.
                if (!applied)
                {
                    throw new ImportJobStateChangedException(importLog.Id);
                }

                if (isLastBatch)
                {
                    _importRepository.RemoveJobInput(importLog.Id);
                }

                await _importRepository.SaveChangesAsync(token);
            },
            cancellationToken);

        importLog.ProcessedCount += lines.Count;
        importLog.CreatedCount += created;
        importLog.UpdatedCount += updated;
        importLog.FailedCount += errors.Count;

        if (isLastBatch)
        {
            importLog.Status = ImportJobStatus.Completed;
        }
    }

    private static ImportError CreateError(int jobId, int lineNumber, string line, string reason)
    {
        const int maxLength = ImportError.RawDataMaxLength;

        var isTooLong = line.Length > maxLength;

        return new ImportError
        {
            ImportLogId = jobId,
            LineNumber = lineNumber,
            RawData = isTooLong ? line[..maxLength] : line,
            Reason = isTooLong
                ? $"{reason} RawData was truncated from {line.Length} to {maxLength} characters."
                : reason
        };
    }
}
