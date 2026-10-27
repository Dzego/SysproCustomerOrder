using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Repositories;
using Syspro.Api.Services;
using Syspro.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));
        
builder.Services.AddControllers();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();

builder.Services.AddScoped<ICustomerImportService, CustomerImportService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IImportRepository, ImportRepository>();
builder.Services.AddScoped<LegacyCustomerParser>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>();


var app = builder.Build();

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

public partial class Program { }