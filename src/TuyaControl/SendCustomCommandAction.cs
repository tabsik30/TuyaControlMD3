using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using TuyaControl.Models;
using TuyaControl.Services;

namespace TuyaControl.Actions;

/// <summary>
/// Ported from SendCustomCommandAction. ValueType+RawValue from TuyaActionConfig
/// become explicit ActionParameters (Choice + Text) instead of a WinForms
/// radio-button/textbox pair.
/// </summary>
internal sealed class SendCustomCommandAction(TuyaDeviceStore store, TuyaLocalClient localClient)
    : IActionDefinition, IDynamicOptionsActionDefinition
{
    public string Id => "send-custom-command";
    public LocalizedText Name => "Send custom command";
    public LocalizedText Description => "Sends any DP code/value pair - for dimmers, color, modes, or anything not covered by the other actions.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice("device_id", label: "Device", required: true),
        ActionParameter.Text("code", label: "DP code", required: true),
        ActionParameter.Choice("value_type",
            [
                new ActionParameterOption { Value = "Bool", Label = "Boolean (true/false)" },
                new ActionParameterOption { Value = "Integer", Label = "Integer" },
                new ActionParameterOption { Value = "String", Label = "Text" },
            ],
            label: "Value type", defaultValue: "Bool", required: true),
        ActionParameter.Text("raw_value", label: "Value", required: true),
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
            var valueType = context.Parameters.GetValueOrDefault("value_type")?.ToString() ?? "Bool";
            var rawValue = context.Parameters.GetValueOrDefault("raw_value")?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(code))
            {
                return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Pick a device and a DP code.");
            }

            var device = store.GetById(deviceId);
            if (device is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotFound, "That device is no longer configured.");
            }

            object value;
            switch (Enum.TryParse<TuyaValueType>(valueType, out var parsed) ? parsed : TuyaValueType.Bool)
            {
                case TuyaValueType.Bool:
                    if (!bool.TryParse(rawValue, out var boolValue))
                    {
                        return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Value must be 'true' or 'false'.");
                    }

                    value = boolValue;
                    break;
                case TuyaValueType.Integer:
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    {
                        return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Value must be a whole number.");
                    }

                    value = intValue;
                    break;
                default:
                    value = rawValue;
                    break;
            }

            try
            {
                await localClient.SendCommandAsync(device, code, value);
                return ActionResult.Success();
            }
            catch (TuyaApiException ex)
            {
                return ActionResult.Failed(ActionErrorCodes.ProviderError, ex.Message);
            }
        }
    }
}
