namespace Syspro.Api.Services;

public interface ICustomerImportService
{
    Task ImportAsync(string filePath);
}