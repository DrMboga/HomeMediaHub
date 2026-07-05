using System.Net.Mime;
using Contracts.Messages;
using MassTransit;
using MediaWorker.Consumers;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, cfg) => cfg
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Console(new CompactJsonFormatter()));

    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

    builder.Services.AddDbContext<WorkerDbContext>(opts => opts
        .UseNpgsql(connStr, npgsql => npgsql
            .UseVector()
            .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)
            .CommandTimeout(60)
            // The worker records its migrations in a separate history table so it never collides
            // with the Api's migrations against the same database.
            .MigrationsHistoryTable("__ef_migrations_history_worker", "public"))
        .UseSnakeCaseNamingConvention());

    // Processing services (ported from the old /api/tools/* handlers).
    builder.Services.AddSingleton<FileHashService>();
    builder.Services.AddSingleton<FileConvertService>();
    builder.Services.AddSingleton<MetadataService>();
    builder.Services.AddSingleton<ThumbnailService>();
    builder.Services.AddScoped<DedupService>();
    builder.Services.AddScoped<ScanService>();
    builder.Services.AddScoped<IJobStore, JobStore>();

    var rabbitHost = builder.Configuration["RabbitMq:Host"]
        ?? builder.Configuration["RABBITMQ_HOST"] ?? "rabbitmq";
    var rabbitUser = builder.Configuration["RabbitMq:User"]
        ?? builder.Configuration["RABBITMQ_USER"] ?? "guest";
    var rabbitPass = builder.Configuration["RabbitMq:Pass"]
        ?? builder.Configuration["RABBITMQ_PASS"] ?? "guest";

    builder.Services.AddMassTransit(x =>
    {
        x.SetKebabCaseEndpointNameFormatter();

        x.AddConsumer<ScanConsumer>();
        x.AddConsumer<IngestConsumer>();
        x.AddConsumer<AiResultBridgeConsumer>();
        x.AddConsumer<ApplyEnrichmentConsumer>();
        x.AddConsumer<JobFailureConsumer>();

        x.AddSagaStateMachine<EnrichmentStateMachine, EnrichmentState>()
            .EntityFrameworkRepository(r =>
            {
                r.ConcurrencyMode = ConcurrencyMode.Optimistic;      // xmin token
                r.ExistingDbContext<WorkerDbContext>();
            });

        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rabbitHost, "/", h =>
            {
                h.Username(rabbitUser);
                h.Password(rabbitPass);
            });

            // Python-facing name pinned explicitly (fanout exchange the sidecar binds).
            cfg.Message<AiAnalysisRequested>(m => m.SetEntityName("ai-analysis-requested"));

            cfg.ReceiveEndpoint("media-scan", e =>
            {
                e.PrefetchCount = 1;
                e.ConcurrentMessageLimit = 1;
                e.ConfigureConsumer<ScanConsumer>(context);
            });

            // Deliberately low: hashing 750 GB off one SATA spindle — more concurrency = seek thrash.
            cfg.ReceiveEndpoint("media-ingest", e =>
            {
                e.PrefetchCount = 2;
                e.ConcurrentMessageLimit = 2;
                e.ConfigureConsumer<IngestConsumer>(context);
            });

            cfg.ReceiveEndpoint("media-apply-enrichment", e =>
            {
                e.PrefetchCount = 4;
                e.ConcurrentMessageLimit = 4;
                e.ConfigureConsumer<ApplyEnrichmentConsumer>(context);
            });

            cfg.ReceiveEndpoint("media-job-status", e =>
            {
                e.PrefetchCount = 4;
                e.ConfigureConsumer<JobFailureConsumer>(context);
            });

            // Raw-JSON endpoint for the Python sidecar's plain-JSON output (architecture §5).
            cfg.ReceiveEndpoint("ai-analysis-completed", e =>
            {
                e.PrefetchCount = 4;
                e.ConfigureConsumeTopology = false;          // no urn-based bindings
                e.DefaultContentType = new ContentType("application/json");
                e.UseRawJsonDeserializer(isDefault: true);
                e.Bind("ai-analysis-completed", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.ConfigureConsumer<AiResultBridgeConsumer>(context);
            });

            cfg.ReceiveEndpoint("media-enrichment-saga", e =>
            {
                e.PrefetchCount = 8;
                e.ConcurrentMessageLimit = 8;
                // Absorbs optimistic-concurrency faults when AI and geo results land together.
                e.UseMessageRetry(r => r.Intervals(
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10)));
                e.UseInMemoryOutbox(context);
                e.ConfigureSaga<EnrichmentState>(context);
            });
        });
    });

    // MassTransit v8 emits instrumentation as OTel Meters; MediaWorker has no HTTP API, so the
    // Prometheus HttpListener exporter serves /metrics on :9090.
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddMeter("MassTransit")
            .AddPrometheusHttpListener(o =>
            {
                o.Host = "+";       // all interfaces — scraped by Prometheus on the docker network
                o.Port = 9090;
            }));

    var host = builder.Build();

    // Create the enrichment_sagas table before consumers start (media tables are owned by the Api).
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
        await db.Database.MigrateAsync();
    }

    await host.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "MediaWorker startup failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}
