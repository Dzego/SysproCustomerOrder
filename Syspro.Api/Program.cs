using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.ExceptionHandling;
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
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ProblemDetailsCustomization.Apply);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var app = builder.Build();

// First in the pipeline so that it catches failures from everything after it.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

public partial class Program { }