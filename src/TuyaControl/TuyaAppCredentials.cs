namespace TuyaControl.Models;

/// <summary>
/// Tuya IoT Platform "Cloud" project credentials, per region. Carried over verbatim
/// from the MD2 plugin's TuyaAppCredentials - these are real, already-issued
/// credentials for your Tuya developer account, not placeholders.
///
/// IMPORTANT: don't publish this file (or the built plugin, if it embeds it in a
/// public repo) - ClientSecret is a live credential.
/// </summary>
public static class TuyaAppCredentials
{
    private const string Placeholder = "WPISZ_TUTAJ";

    public static readonly Dictionary<TuyaRegion, (string ClientId, string ClientSecret)> Credentials = new()
    {
        [TuyaRegion.CentralEurope] = ("wuppta8m3pkgmdep3syg", "21771427458740d488afe589731d4532"),
    };

    public static bool IsConfigured(TuyaRegion region) =>
        Credentials.TryGetValue(region, out var value)
        && !string.IsNullOrWhiteSpace(value.ClientId)
        && !string.IsNullOrWhiteSpace(value.ClientSecret)
        && value.ClientId != Placeholder
        && value.ClientSecret != Placeholder;
}
