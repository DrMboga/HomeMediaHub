using Api.Data;
using Api.Endpoints;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    var app = builder.Build();

    app.UseExceptionHandler(e => e.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        await ctx.Response.WriteAsJsonAsync(new { error = feature?.Error.Message ?? "Unexpected error" });
    }));

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
            ? Results.Ok(new { status = "ok", phase = 4 })
            : Results.Json(new { status = "unhealthy", phase = 4 }, statusCode: 503);
    });
    app.MapToolEndpoints();
    app.MapMediaEndpoints();

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
