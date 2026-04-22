var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", phase = 1 }));

// GET /reverse?lat=&lon=
// Returns reverse geocoding result for the given coordinates.
// Checks local cache first; falls back to Nominatim on a miss.
app.MapGet("/reverse", (double lat, double lon) =>
{
    return Results.Ok(new { lat, lon, display_name = (string?)null, status = "stub" });
});

app.Run();
