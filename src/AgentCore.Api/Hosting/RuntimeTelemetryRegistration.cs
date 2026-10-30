using AgentCore.Application.Observability;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentCore.Api;

internal static class RuntimeTelemetryRegistration
{
    internal static void AddRuntimeExport(this IServiceCollection services, ObservabilityOptions options)
    {
        if (!options.OtlpEnabled) return;
        if (!options.HasValidOtlpEndpoint()) throw new InvalidOperationException("Observability:OtlpEndpoint must be an HTTP(S) collector origin without credentials, path or query.");
        void Configure(OtlpExporterOptions exporter, string signal)
        {
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Endpoint = new Uri(new Uri(options.OtlpEndpoint!), "v1/" + signal);
            exporter.TimeoutMilliseconds = 1000;
            exporter.ExportProcessorType = ExportProcessorType.Batch;
            exporter.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions {
                MaxQueueSize = 512, MaxExportBatchSize = 128, ScheduledDelayMilliseconds = 1000, ExporterTimeoutMilliseconds = 1000
            };
        }
        // Deliberately subscribe only to the incumbent content-free runtime sources.
        // No ASP.NET/HttpClient/SQL instrumentation, request headers, bodies or logs are exported.
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddService("AgentCore", autoGenerateServiceInstanceId: false))
            .WithTracing(traces => traces.AddSource(RuntimeTelemetry.Name)
                .AddOtlpExporter(exporter => Configure(exporter, "traces")))
            .WithMetrics(metrics => metrics.AddMeter(RuntimeTelemetry.Name)
                .AddOtlpExporter((exporter, reader) => {
                    Configure(exporter, "metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000;
                    reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = 1000;
                }));
    }
}
