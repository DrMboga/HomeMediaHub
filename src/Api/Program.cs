using System.Text;
using Api.Data;
using Api.Endpoints;
using Api.Features.Auth;
using Api.Models;
using Api.Services;
using Contracts.Messages;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console(new CompactJsonFormatter()));

    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

    builder.Services.AddDbContext<AppDbContext>(opts => opts
        .UseNpgsql(connStr, npgsql => npgsql
            .UseVector()
            .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)
            .CommandTimeout(60))
        .UseSnakeCaseNamingConvention());

    builder.Services
        .AddIdentityCore<AppUser>(opts =>
        {
            opts.Password.RequireDigit           = false;
            opts.Password.RequireNonAlphanumeric = false;
            opts.Password.RequireUppercase       = false;
            opts.Password.RequiredLength         = 8;
        })
        .AddEntityFrameworkStores<AppDbContext>();

    // JWT authentication
    var jwtKey = builder.Configuration["Jwt:SecretKey"]
        ?? throw new InvalidOperationException("Jwt:SecretKey is required.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer              = builder.Configuration["Jwt:Issuer"],
                ValidAudience            = builder.Configuration["Jwt:Audience"],
                IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                ClockSkew                = TimeSpan.Zero,
            };

            // Allow JWT from SignalR query string (WebSocket connections)
            opts.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) &&
                        ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddHttpClient();

    // Named HttpClient for AI sidecar
    var aiSidecarUrl = builder.Configuration["AiSidecarUrl"] ?? "http://ai-sidecar:8000";
    builder.Services.AddHttpClient("AiSidecar", client =>
    {
        client.BaseAddress = new Uri(aiSidecarUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.AddSingleton<JwtTokenService>();

    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    // The RABBITMQ_* env vars compose passes in must win over the appsettings RabbitMq section,
    // which is only a local-dev default. Reading the config key first made the env vars dead:
    // appsettings always supplied "guest", and guest is refused off-loopback by the broker.
    var rabbitHost = RabbitSetting("RABBITMQ_HOST", "RabbitMq:Host", "rabbitmq");
    var rabbitUser = RabbitSetting("RABBITMQ_USER", "RabbitMq:User", "guest");
    var rabbitPass = RabbitSetting("RABBITMQ_PASS", "RabbitMq:Pass", "guest");

    string RabbitSetting(string envKey, string configKey, string fallback)
    {
        // Blank counts as absent: compose sets the variable to "" when it's missing from .env.
        var value = builder.Configuration[envKey];
        if (string.IsNullOrWhiteSpace(value)) value = builder.Configuration[configKey];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    // Publish-only bus: the Api has no consumers, only publishes ScanRequested (admin/scheduler)
    // and FileDiscovered(Reprocess: true) (retry). SetEntityName kept consistent with MediaWorker
    // in case a future publisher of AiAnalysisRequested is added here.
    builder.Services.AddMassTransit(x =>
    {
        x.SetKebabCaseEndpointNameFormatter();

        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rabbitHost, "/", h =>
            {
                h.Username(rabbitUser);
                h.Password(rabbitPass);
            });

            cfg.Message<AiAnalysisRequested>(m => m.SetEntityName("ai-analysis-requested"));
        });
    });

    builder.Services.AddHostedService<ScanScheduler>();

    // MassTransit v8 emits instrumentation as OTel Meters.
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddMeter("MassTransit")
            .AddPrometheusExporter());

    var app = builder.Build();

    app.UseExceptionHandler(e => e.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        await ctx.Response.WriteAsJsonAsync(new { error = feature?.Error.Message ?? "Unexpected error" });
    }));

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapPrometheusScrapingEndpoint("/metrics");

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await DbSeeder.SeedAsync(scope.ServiceProvider);
    }

    app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return ok
            ? Results.Ok(new { status = "ok", phase = 5 })
            : Results.Json(new { status = "unhealthy", phase = 5 }, statusCode: 503);
    });

    app.MapAuthEndpoints();
    app.MapMediaEndpoints();
    app.MapSearchEndpoints();
    app.MapAlbumsEndpoints();
    app.MapSeriesEndpoints();
    app.MapAdminEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Application startup failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}
