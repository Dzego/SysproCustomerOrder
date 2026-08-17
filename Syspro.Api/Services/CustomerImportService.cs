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

    public async Task<ImportResult> ImportAsync(string filePath)
    {
        var importLog = new ImportLog
        {
            StartedAt = DateTime.UtcNow
        };

        await _importRepository.AddLogAsync(importLog);

        var lines = await File.ReadAllLinesAsync(filePath);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;

            importLog.ProcessedCount++;

            try
            {
                var record = _parser.Parse(line);

                var existingCustomer =
                    await _customerRepository.GetByLegacyCustomerIdAsync(
                        record.LegacyCustomerId);

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

                    await _customerRepository.AddAsync(customer);

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
                importLog.FailedCount++;

                importLog.Errors.Add(new ImportError
                {
                    LineNumber = lineNumber,
                    RawData = line,
                    Reason = exception.Message
                });
            }
            
        }

        importLog.CompletedAt = DateTime.UtcNow;

        await _customerRepository.SaveChangesAsync();
        await _importRepository.SaveChangesAsync();
        
        return new ImportResult
        {
            Processed = importLog.ProcessedCount,
            Created = importLog.CreatedCount,
            Updated = importLog.UpdatedCount,
            Failed = importLog.FailedCount
        };
    }
}