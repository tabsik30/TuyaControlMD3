using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using TuyaControl.Models;
using TuyaControl.Services;

namespace TuyaControl.Actions;

/// <summary>Ported from SetSwitchStateAction - sends a fixed boolean DP value.</summary>
internal sealed class SetSwitchStateAction(TuyaDeviceStore store, TuyaLocalClient localClient)
    : IActionDefinition, IDynamicOptionsActionDefinition
{
    public string Id => "set-switch-state";
    public LocalizedText Name => "Set switch state";
    public LocalizedText Description => "Sends a fixed ON or OFF value for a DP code, regardless of the current state.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice("device_id", label: "Device", required: true),
        ActionParameter.Text("code", label: "DP code", defaultValue: "switch_1", required: true),
        ActionParameter.Toggle("state", label: "State (on)", defaultValue: true),
    ];

    public Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
    {
        if (context.ParameterName != "device_id")
        {
            return Task.FromResult(new DynamicOptionsResult { Options = [] });
        }

        var options = store.Devices.Select(d => new ActionParameterOption { Value = d.Id, Label = d.Name }).ToList();
        return Task.FromResult(new DynamicOptionsResult { Options = options, CacheSeconds = 5 });
    }

    public IActionExecutor CreateExecutor() => new Executor(store, localClient);

    private sealed class Executor(TuyaDeviceStore store, TuyaLocalClient localClient) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var deviceId = context.Parameters.GetValueOrDefault("device_id")?.ToString();
            var code = context.Parameters.GetValueOrDefault("code")?.ToString();
            var state = context.Parameters.GetValueOrDefault("state") is bool b && b;

            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(code))
            {
                return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Pick a device and a DP code.");
            }

            var device = store.GetById(deviceId);
            if (device is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotFound, "That device is no longer configured.");
            }

            try
            {
                await localClient.SendCommandAsync(device, code, state);
                return ActionResult.Success();
            }
            catch (TuyaApiException ex)
            {
                return ActionResult.Failed(ActionErrorCodes.ProviderError, ex.Message);
            }
        }
    }
}
