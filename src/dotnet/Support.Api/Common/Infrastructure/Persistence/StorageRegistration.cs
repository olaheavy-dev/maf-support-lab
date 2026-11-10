using Support.Api.Common.Application;

namespace Support.Api.Common.Infrastructure.Persistence;

/// <summary>
/// Registers the local durable store. <c>Storage:Path</c> chooses the SQLite file; by default it is
/// <c>.data/dotnet/support.db</c> under the content root (ignored by Git). The Python backend and each evaluation run
/// use their own file, so no two runs ever share mutable state.
/// </summary>
internal static class StorageRegistration
{
    public const string PathSetting = "Storage:Path";

    public static IServiceCollection AddSupportStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration[PathSetting] is { Length: > 0 } configured ? configured : Path.Combine(".data", "dotnet", "support.db");
        services.AddSingleton(_ => new SupportDatabase(Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path)));
        services.AddSingleton<IUnitOfWork>(provider => provider.GetRequiredService<SupportDatabase>());
        return services;
    }
}
