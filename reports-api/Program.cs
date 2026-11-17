using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ReportsApi.ClaimsTransformations;
using ReportsApi.Repositories;
using ReportsApi.Utils;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

var keycloakUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_URL");
var keycloakInternalUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_INTERNAL_URL");
var keycloakRealm = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_REALM");
var clHost = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_HOST");
var clPort = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_PORT");
var clUser = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_USER");
var clPassword = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_PASSWORD");
var clDb = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_DB");

var builder = WebApplication.CreateBuilder(args);

builder.Services                                                  //
       .AddAuthentication(JwtBearerDefaults.AuthenticationScheme) //
       .AddJwtBearer(options => {
           options.MetadataAddress = UriUtils.CombineUriPath(keycloakInternalUrl, $"realms/{keycloakRealm}/.well-known/openid-configuration");
           options.RequireHttpsMetadata = false;

           options.MapInboundClaims = false;

           options.TokenValidationParameters = new TokenValidationParameters {
               ValidateAudience = true,
               ValidAudience = "reports-api",

               ValidateIssuer = true,
               ValidIssuer = UriUtils.CombineUriPath(keycloakUrl, $"realms/{keycloakRealm}")
           };

#if DEBUG_AUTH
           options.IncludeErrorDetails = true;

           options.Events = new JwtBearerEvents {
               OnAuthenticationFailed = context => {
                   var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                   logger.LogError(context.Exception, $"OnAuthenticationFailed: JWT auth error: {context.Exception.Message}");
                   return Task.CompletedTask;
               },
               OnChallenge = context => {
                   var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                   logger.LogWarning($"OnChallenge: Error={context.Error}, Description={context.ErrorDescription}");
                   return Task.CompletedTask;
               }
           };
#endif
       });

builder.Services.AddAuthorization();

builder.Services.AddTransient<IClaimsTransformation, KeycloakClaimsTransformation>();
builder.Services.AddSingleton(new ClickhouseConStringBuilder(clHost, clPort, clUser, clPassword, clDb));
builder.Services.AddTransient<ReportRepository, ReportRepository>();

#if DEBUG_AUTH
Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
#endif

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/reports", async ([FromQuery] string? date, ClaimsPrincipal user, ReportRepository repo) => {
    var ownerId = user.FindFirst(v => v.Type == "sub");
    if (ownerId == null || string.IsNullOrWhiteSpace(ownerId.Value)) {
        return Results.Forbid();
    }
    if (!DateTimeOffset.TryParse(date, out var reportDate)) {
        return Results.Problem("Дата отчёта не указана", statusCode: StatusCodes.Status400BadRequest);
    }
    var report = await repo.GetActualReportAsync(ownerId.Value, reportDate.UtcDateTime.Date);
    if (report.Prostheses.Count == 0) {
        return Results.NotFound();
    }
    return Results.Ok(report);
}).RequireAuthorization(v => v.RequireRole("prothetic_user"));

app.Run();