namespace TuyaControl.Models;

/// <summary>A device as reported by the Tuya cloud (list only, no local key here).</summary>
public sealed class TuyaCloudDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Online { get; init; }
    public string? Category { get; init; }
    public string? ProductName { get; init; }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// A device as we control it: local network details plus whatever we know from the
/// cloud. Persisted as one MD3 config-flow entry per device (see
/// ConfigFlow/TuyaConfigFlow.cs and Services/TuyaDeviceStore.cs) - this replaces the
/// MD2 version's single local_devices.json file.
/// </summary>
public sealed class TuyaDeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? LocalKey { get; init; }
    public string IpAddress { get; init; } = "";

    // "3.3" / "3.4" / "3.5" / "cloud" (control via the cloud API instead of LAN)
    public string ProtocolVersion { get; init; } = "3.3";

    public override string ToString() => $"{Name} ({Id})";
}

public enum TuyaRegion
{
    China,
    WesternAmerica,
    EasternAmerica,
    CentralEurope,
    WesternEurope,
    India,
}

public static class TuyaRegionExtensions
{
    public static string BaseUrl(this TuyaRegion region) => region switch
    {
        TuyaRegion.China => "https://openapi.tuyacn.com",
        TuyaRegion.WesternAmerica => "https://openapi.tuyaus.com",
        TuyaRegion.EasternAmerica => "https://openapi-ueaz.tuyaus.com",
        TuyaRegion.CentralEurope => "https://openapi.tuyaeu.com",
        TuyaRegion.WesternEurope => "https://openapi-weaz.tuyaeu.com",
        TuyaRegion.India => "https://openapi.tuyain.com",
        _ => "https://openapi.tuyaeu.com",
    };
}

public enum TuyaValueType
{
    Bool,
    Integer,
    String,
}

public sealed class TuyaApiException(string message) : Exception(message);
