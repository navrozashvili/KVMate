using KVMate.Core.Devices;
using KVMate.Core.Handoff;
using KVMate.Core.Power;
using KVMate.Core.Settings;
using KVMate.Core.Speakers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KVMate.Core;

/// <summary>Registers Core's services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Add the real watcher, speaker link, engine, settings store and power events, all as
    /// singletons. <c>TryAdd</c> throughout, so a caller can register a replacement first.
    /// </summary>
    public static IServiceCollection AddKvMateCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new SettingsStore(StoragePaths.SettingsFile));
        services.TryAddSingleton<IDeviceWatcher, UsbDeviceWatcher>();
        services.TryAddSingleton<ISpeakerLink, KsSpeakerLink>();
        services.TryAddSingleton<HandoffEngine>();
        services.TryAddSingleton<PowerEvents>();

        return services;
    }
}
