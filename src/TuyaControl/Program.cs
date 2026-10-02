using MacroDeck.Plugin.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TuyaControl.Services;

namespace TuyaControl;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = MacroDeckPlugin.CreatePlugin(args)
            .RegisterIntegration<PluginIntegration>();

        // Shared singletons: one cloud session and one device store for the whole
        // plugin process, used by the config flow, actions, and TuyaLocalClient's
        // cloud-fallback path alike. No local bootstrap cache needed anymore - with
        // no IVariableProvider, there's no declare-before-load timing problem to
        // work around (see TuyaDeviceStore's class remarks).
        builder.Services.AddSingleton<TuyaDeviceStore>();
        builder.Services.AddSingleton<TuyaCloudClient>();
        builder.Services.AddSingleton<TuyaLocalClient>();

        var plugin = builder.Build();
        await plugin.RunAsync();
    }
}
