
using SecureLab.Api.Scaffolding;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data;
using SecureLab.Api.Presentation.Endpoints;
using Microsoft.AspNetCore.Authorization;
 
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = "Client"
});
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
 
var connectionString = builder.Configuration.GetConnectionString("SecureLab")
    ?? throw new InvalidOperationException(
        "Connection string 'SecureLab' не налаштовано.");
 
builder.Services.AddDbContext<SecureLabDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IncidentQueries>();
builder.Services.AddSingleton<IAuthorizationHandler, IncidentAuthorizationHandler>();
 
builder.AddLab03Identity();
 
// [LAB05-CORS-1] Навчальна CORS-політика: лише Development, один точний origin, без AllowAny*.
// ФІНАЛЬНИЙ стан: CORS з .AllowCredentials() + CSP.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Lab05BrowserProbe", policy =>
        {
            policy.WithOrigins("http://localhost:4173")   // без "/" наприкінці
                  .WithMethods("GET")
                  .WithHeaders("X-Lab-Cors-Probe")
                  .AllowCredentials();                  // [LAB05-CREDS] рисунок 21; для рисунка 22 тимчасово закоментуйте і поставте ";" після .WithHeaders(...)
        });
    });
}
 
var app = builder.Build();
 
var resetRequested = args.Contains("--reset-database", StringComparer.Ordinal);
await DatabaseBootstrap.InitializeAsync(app.Services, app.Configuration, app.Environment, resetRequested);
 
if (resetRequested)
{
    return;
}
 
app.UseExceptionHandler();
app.UseStatusCodePages();
 
// [LAB05-CSP-1] CSP увімкнена (enforcement).
// Для report-only змініть ім'я на "Content-Security-Policy-Report-Only".
const string cspHeaderName = "Content-Security-Policy";
var csp = "default-src 'self'; frame-ancestors 'none'";
 
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
 
    var isDevelopmentApiReference =
        app.Environment.IsDevelopment()
        && context.Request.Path.StartsWithSegments("/scalar");
 
    if (!isDevelopmentApiReference)
    {
        // [LAB05-CSP-2]
        context.Response.Headers[cspHeaderName] = csp;
    }
 
    await next(context);
});
 
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
 
app.UseRouting();
 
// [LAB05-CORS-2] до UseAuthentication/UseAuthorization.
if (app.Environment.IsDevelopment())
{
    app.UseCors("Lab05BrowserProbe");
}
 
app.UseAuthentication();
app.UseAuthorization();
 
app.UseDefaultFiles();
app.UseStaticFiles();
 
app.MapGet("/health", async (SecureLabDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(
            title: "База даних недоступна",
            statusCode: StatusCodes.Status503ServiceUnavailable))
    .WithName("GetHealth")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
 
app.MapIncidentEndpoints();
app.MapLab03Identity();
app.MapLab02Endpoints();
app.MapFallbackToFile("index.html");
 
app.Run();
 
public partial class Program;
 