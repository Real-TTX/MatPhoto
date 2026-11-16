using System.Net.Security;

namespace MatPhoto.Services.Storages;

/// <summary>The services of the storages (one call in Program.cs): the connections with their pools and the check of their reachability.</summary>
public static class StorageSetup
{
    public static IServiceCollection AddStorages(this IServiceCollection services)
    {
        services.AddSingleton<StorageConnections>();
        services.AddSingleton<StorageMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<StorageMonitor>());

        // WebDAV: redirects by hand (the authorization stays on the same host), no timeout for the bytes (each call has its own for the answer)
        services.AddHttpClient(StorageConnections.WebDavClient, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddHttpClient(StorageConnections.WebDavClientAnyCertificate, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // chosen by the administrator for a NAS with a certificate of its own (the switch of the storage)
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            });
        return services;
    }
}
