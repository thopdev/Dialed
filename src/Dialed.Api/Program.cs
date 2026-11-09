using Dialed.Api.Auth;
using Dialed.Api.Configuration;
using Dialed.Api.Households;
using Dialed.Api.Passkeys;
using Dialed.Api.Protection;
using Dialed.Api.Seeding;
using Dialed.Api.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The PWA is served from another origin (localhost port, dev tunnel, Static Web Apps).
var allowedOrigins = AllowedOrigins.From(builder.Configuration);
var originPatterns = AllowedOrigins.Patterns(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => AllowedOrigins.IsAllowed(origin, allowedOrigins, originPatterns))
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddDialedProtection(builder.Configuration);
builder.Services.AddDialedAuth(builder.Configuration, builder.Environment);
builder.Services.AddDialedPasskeys(builder.Configuration);
builder.Services.AddDialedSync();
builder.Services.AddDialedHouseholds(builder.Environment);
builder.Services.AddDevelopmentSeeding(builder.Configuration);

var app = builder.Build();

app.UseCors();
app.UseRequestSizeLimit();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication: limits are per signed-in user
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Dialed API");
app.MapDialedAuth(app.Environment);
app.MapDialedPasskeys();
app.MapDialedSync();
app.MapDialedHouseholds();

app.Run();
