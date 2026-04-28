using GeoService.Data;
using GeoService.Endpoints;
using GeoService.Services;
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

    var connStr = builder.Configuration.GetConnectionString("GeoDb")
        ?? throw new InvalidOperationException("ConnectionStrings:GeoDb is required.");

    builder.Services.AddDbContext<GeoDbContext>(opts => opts
        .UseNpgsql(connStr, npgsql => npgsql
            .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)
            .CommandTimeout(60))
        .UseSnakeCaseNamingConvention());

    builder.Services.AddHttpClient<NominatimClient>(client =>
    {
        client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "HomeMediaHub/1.0 (private home geocoding cache; contact: admin@homemediahub.local)");
        client.Timeout = TimeSpan.FromSeconds(10);
    });

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
        var db = scope.ServiceProvider.GetRequiredService<GeoDbContext>();
        await db.Database.MigrateAsync();
    }

    app.MapGet("/health", async (GeoDbContext db, CancellationToken ct) =>
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return ok
            ? Results.Ok(new { status = "ok", phase = 2 })
            : Results.Json(new { status = "unhealthy", phase = 2 }, statusCode: 503);
    });
    app.MapGeoEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "GeoService startup failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}
