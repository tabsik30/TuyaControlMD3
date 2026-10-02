using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Variables;
using Serilog;
using TuyaControl.Actions;
using TuyaControl.ConfigFlow;
using TuyaControl.Models;
using TuyaControl.Services;

namespace TuyaControl;

/// <summary>
/// Rewritten against real MacroDeck.Sdk.dll 3.0.0-preview.7. No IVariableProvider -
/// state synchronization (a per-device on/off variable kept fresh by a polling
/// BackgroundService) was removed on request. Back to the MD2 plugin's original
/// shape: actions only, plus the config flow.
/// </summary>
internal sealed class PluginIntegration(
    TuyaDeviceStore store,
    TuyaCloudClient cloudClient,
    TuyaLocalClient localClient,
    IIntegrationContext context,
    ILogger logger)
    : IPluginIntegration, IConfigFlowProvider, IVariableProvider
{
    public IReadOnlyList<IActionDefinition> Actions { get; } =
    [
        new ToggleSwitchAction(store, localClient),
        new SetSwitchStateAction(store, localClient),
        new SendCustomCommandAction(store, localClient),
        new DetectDeviceAction(store, context, logger),
    ];

    public IReadOnlyList<VariableDefinition> Variables { get; } =
    [
        VariableDefinition.Eager("tuya_state", VariableType.Boolean, refreshInterval: TimeSpan.FromSeconds(5)),
    ];

    public async Task InitializeAsync(IIntegrationContext integrationContext)
    {
        await store.LoadFromHostAsync(integrationContext);

        // Re-establish a cloud session immediately if an account was already
        // configured in a previous run, so cloud-fallback devices ("cloud" protocol
        // version) work right away without requiring the user to re-open the config
        // flow's account step.
        if (store.HasAccount && store.AccountRegion.HasValue)
        {
            cloudClient.Configure(store.AccountRegion.Value, store.AccountClientId!, store.AccountClientSecret!);
            cloudClient.CountryCode = store.AccountCountryCode!;
            cloudClient.Username = store.AccountUsername;
            cloudClient.Password = store.AccountPassword;
            cloudClient.Schema = store.AccountSchema;
        }
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    public async ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(localId, "tuya-state", StringComparison.Ordinal) &&
            !string.Equals(localId, "tuya_state", StringComparison.Ordinal))
        {
            return VariableReading.Unavailable;
        }

        var device = store.Devices.FirstOrDefault(device =>
            !string.Equals(device.ProtocolVersion.Trim(), "cloud", StringComparison.OrdinalIgnoreCase));
        if (device is null || string.Equals(device.ProtocolVersion.Trim(), "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return VariableReading.Unavailable;
        }

        try
        {
            var status = await localClient.GetStatusAsync(device);
            var value = status[TuyaLocalClient.ToLocalDpCode("switch_1")]?.ToObject<bool?>();
            return value.HasValue ? VariableReading.Of(value.Value) : VariableReading.Unavailable;
        }
        catch (TuyaApiException)
        {
            return VariableReading.Unavailable;
        }
    }

    // --- IConfigFlowProvider ---

    public IConfigFlow CreateConfigFlow() => new TuyaConfigFlow(cloudClient, store);
}
