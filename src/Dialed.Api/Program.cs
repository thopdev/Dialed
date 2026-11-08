using Dialed.Api.Auth;
using Dialed.Api.Configuration;
using Dialed.Api.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The PWA is served from another origin (localhost port, dev tunnel, Static Web Apps).
var allowedOrigins = AllowedOrigins.From(builder.Configuration);
var originPatterns = AllowedOrigins.Patterns(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => AllowedOrigins.IsAllowed(origin, allowedOrigins, originPatterns))
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddDialedAuth(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentSeeding(builder.Configuration);

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Dialed API");
app.MapDialedAuth(app.Environment);

app.Run();
