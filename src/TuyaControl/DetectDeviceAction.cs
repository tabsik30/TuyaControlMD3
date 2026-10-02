using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Notifications;
using Serilog;
using TuyaControl.Services;

namespace TuyaControl.Actions;

/// <summary>
/// Replaces RefreshDevicesAction (which just reloaded local_devices.json - no longer
/// meaningful now that devices are config entries and always current). Instead: a
/// diagnostic action covering what DeviceManagerForm's "detect version" button did -
/// listen for the device's UDP beacon and report its current IP/protocol version via
/// a host notification, since there's no grid to display it in anymore.
/// </summary>
internal sealed class DetectDeviceAction(TuyaDeviceStore store, IIntegrationContext context, ILogger logger)
    : IActionDefinition, IDynamicOptionsActionDefinition
{
    public string Id => "detect-device";
    public LocalizedText Name => "Detect device (UDP)";
    public LocalizedText Description => "Listens for 15s for this device's Tuya LAN broadcast to find its current IP and protocol version.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice("device_id", label: "Device", required: true),
    ];

    public Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context2, CancellationToken cancellationToken)
    {
        if (context2.ParameterName != "device_id")
        {
            return Task.FromResult(new DynamicOptionsResult { Options = [] });
        }

        var options = store.Devices.Select(d => new ActionParameterOption { Value = d.Id, Label = d.Name }).ToList();
        return Task.FromResult(new DynamicOptionsResult { Options = options, CacheSeconds = 5 });
    }

    public IActionExecutor CreateExecutor() => new Executor(store, context, logger.ForContext<DetectDeviceAction>());

    private sealed class Executor(TuyaDeviceStore store, IIntegrationContext context, ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext execContext)
        {
            var deviceId = execContext.Parameters.GetValueOrDefault("device_id")?.ToString();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Pick a device.");
            }

            var device = store.GetById(deviceId);
            if (device is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotFound, "That device is no longer configured.");
            }

            var found = await TuyaDiscovery.ListenAsync(15, logger);
            var match = found.FirstOrDefault(d => d.GwId == device.Id);

            if (match is null)
            {
                context.Notifications.Notify(new UserNotificationRequest
                {
                    Title = "Tuya: device not found",
                    Message = $"No UDP beacon from '{device.Name}' in 15s. Is it powered on and on the same network?",
                    Level = UserNotificationLevel.Warning,
                });
                return ActionResult.Failed(ActionErrorCodes.NotFound, "No beacon received within 15s.");
            }

            context.Notifications.Notify(new UserNotificationRequest
            {
                Title = "Tuya: device found",
                Message = $"'{device.Name}': IP {match.Ip}, version {match.Version}{(match.Encrypted ? " (encrypted beacon)" : "")}.",
                Level = UserNotificationLevel.Info,
            });

            return ActionResult.Accepted($"Found at {match.Ip}, version {match.Version}.");
        }
    }
}
