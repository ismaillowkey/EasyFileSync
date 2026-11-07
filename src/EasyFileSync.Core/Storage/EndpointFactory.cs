using EasyFileSync.Core.Models;

namespace EasyFileSync.Core.Storage;

public static class EndpointFactory
{
    public static IStorageEndpoint Create(EndpointConfig config) => config.Kind switch
    {
        EndpointKind.Ftp => new FtpStorageEndpoint(config),
        EndpointKind.Smb => new LocalStorageEndpoint(config),
        _ => new LocalStorageEndpoint(config),
    };
}
