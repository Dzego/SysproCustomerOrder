using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Repositories;
using Syspro.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();

builder.Services.AddScoped<ICustomerImportService, CustomerImportService>();
builder.Services.AddScoped<LegacyCustomerParser>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

public partial class Program { }