using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Syspro.Api.BackgroundJobs;
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
builder.Services.AddScoped<ICustomerImportJobService, CustomerImportJobService>();
builder.Services.AddScoped<ICustomerImportJobRunner, CustomerImportJobRunner>();
builder.Services.AddOptions<CustomerImportOptions>()
    .Bind(builder.Configuration.GetSection(CustomerImportOptions.SectionName))
    .Validate(
        options => options.HasValidMaxInputBytes,
        "CustomerImport:MaxInputBytes must be above zero and at most 1 GiB.")
    .Validate(
        options => options.HasValidBatchSize,
        "CustomerImport:BatchSize must be above zero and at most 10,000.")
    .Validate(
        options => options.HasValidLeaseDuration,
        "CustomerImport:LeaseDuration must be above zero and at most one hour.")
    .ValidateOnStart();
builder.Services.AddOptions<CustomerImportWorkerOptions>()
    .Bind(builder.Configuration.GetSection(CustomerImportWorkerOptions.SectionName))
    .Validate(
        options => options.HasValidPollInterval,
        "CustomerImportWorker:PollInterval must be above zero and at most five minutes.")
    .Validate(
        options => options.MaxAttempts >= 1,
        "CustomerImportWorker:MaxAttempts must be at least 1.")
    .ValidateOnStart();
builder.Services.AddHostedService<CustomerImportWorker>();
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