using Syspro.Api.Models;
using Syspro.Api.Repositories;

namespace Syspro.Api.Services;

public class CustomerImportService : ICustomerImportService
{
    private readonly LegacyCustomerParser _parser;
    private readonly ICustomerRepository _customerRepository;

    public CustomerImportService(
        LegacyCustomerParser parser,
        ICustomerRepository customerRepository)
    {
        _parser = parser;
        _customerRepository = customerRepository;
    }

    public async Task ImportAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath);

        foreach (var line in lines)
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
            }
            else
            {
                existingCustomer.Name = record.Name;
                existingCustomer.Email = record.Email;
                existingCustomer.SignupDate = record.SignupDate;
                existingCustomer.Tier = record.Tier;
            }
        }

        await _customerRepository.SaveChangesAsync();
    }
}