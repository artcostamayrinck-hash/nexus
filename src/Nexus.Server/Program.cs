using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Security;
using Nexus.Modules.BuiltIn;
using Nexus.Security.Tls;
using Nexus.Server.Authentication;
using Nexus.Server.Endpoints;
using Nexus.Server.Health;
using Nexus.Server.Hubs;
using Nexus.Server.Middleware;
using Nexus.Server.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var storageRoot = config["Nexus:Storage:RootDirectory"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "server");
    var dbFileName = config["Nexus:Storage:DatabaseFileName"] ?? "nexus-server.db";
    var disablePooling = config.GetValue<bool>("Nexus:Storage:DisablePooling");

    return new Nexus.Infrastructure.Persistence.NexusStorageOptions
    {
        RootDirectory = storageRoot,
        DatabaseFileName = dbFileName,
        EnablePooling = !disablePooling
    };
});

builder.Services.AddNexusInfrastructure(
    configureLogging: log =>
    {
        log.ApplicationName = "NEXUS.Server";
    },
    configureLocalDevice: dev =>
    {
        dev.DeviceType = DeviceType.Server;
        dev.DeviceName = $"{Environment.MachineName} (Server)";
    },
    registerLoggingProvider: false);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "NEXUS.Server")
        .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{Application}] {Message:lj}{NewLine}{Exception}");
});

// Configure Kestrel TLS endpoint lazily using final IConfiguration and DI ITlsCertificateProvider
builder.WebHost.ConfigureKestrel((context, options) =>
{
    if (!context.Configuration.GetValue<bool>("Nexus:Tls:BindKestrelHttps"))
    {
        return;
    }

    var httpPort = context.Configuration.GetValue("Nexus:Tls:HttpPort", 5280);
    if (!context.Configuration.GetValue<bool>("Nexus:Tls:RequireHttps"))
    {
        options.Listen(IPAddress.Loopback, httpPort);
    }

    var httpsPort = context.Configuration.GetValue("Nexus:Tls:HttpsPort", 5443);
    var tlsProvider = options.ApplicationServices.GetRequiredService<ITlsCertificateProvider>();
    var certResult = tlsProvider.GetOrCreateServerCertificateAsync(Environment.MachineName).GetAwaiter().GetResult();
    if (certResult.IsSuccess && certResult.Value is not null)
    {
        options.Listen(IPAddress.Any, httpsPort, listenOptions =>
        {
            listenOptions.UseHttps(certResult.Value, https =>
            {
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            });
        });
    }
});

builder.Services.AddNexusModules();
builder.Services.AddSingleton<INexusPresenceTracker, InMemoryPresenceTracker>();
builder.Services.AddSingleton<ServerRealtimeCoordinator>();
builder.Services.AddSingleton<ServerBasicSyncCoordinator>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var rootDir = config["Nexus:Storage:RootDirectory"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "server");
    return new LocalHostTokenBridgeOptions
    {
        Enabled = OperatingSystem.IsWindows() && config.GetValue("Nexus:Security:EnableLocalHostBridge", true),
        DirectoryPath = Path.Combine(Path.GetDirectoryName(rootDir) ?? rootDir, "host-bridge")
    };
});
builder.Services.AddHostedService<NexusServerStartupService>();
builder.Services.AddHostedService<NexusMdnsAdvertisementService>();

builder.Services
    .AddAuthentication(NexusAuthDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, NexusDeviceAuthenticationHandler>(
        NexusAuthDefaults.AuthenticationScheme, _ => { });

builder.Services.AddSingleton<IAuthorizationHandler, NexusPermissionAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(NexusAuthDefaults.PolicyViewStatus, policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new NexusPermissionRequirement(NexusPermission.ViewStatus)));
    options.AddPolicy(NexusAuthDefaults.PolicyManageDevices, policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new NexusPermissionRequirement(NexusPermission.ManageDevices)));
});

builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<NexusExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddCheck<SqliteStorageHealthCheck>("sqlite_storage")
    .AddCheck<NexusRuntimeHealthCheck>("nexus_runtime");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    var coreOptions = new SystemTextJsonSerializer().Options;
    foreach (var converter in coreOptions.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<TlsEnforcementMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.MapNexusEndpoints();
app.MapPairingAndSecurityEndpoints();
app.MapConnectivityEndpoints();
app.MapRealtimeEndpoints();
app.MapBasicSyncEndpoints();
app.MapHub<NexusHub>("/hubs/nexus");

app.Run();

public partial class Program;
