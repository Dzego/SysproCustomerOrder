using Syspro.Api.DTOs;

namespace Syspro.Api.Services;

public interface ICustomerImportService
{
    Task<ImportResult> ImportAsync(string filePath);
}