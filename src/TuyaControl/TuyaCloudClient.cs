using System.Security.Cryptography;
using System.Text;
using Serilog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TuyaControl.Models;

namespace TuyaControl.Services;

/// <summary>
/// Talks to the Tuya cloud (openapi.tuyaXX.com): login, device list, local key
/// lookup, and cloud-relayed commands (used when a device's ProtocolVersion is
/// "cloud" instead of a LAN version). Ported from TuyaApiClient - the signing logic
/// (HMAC-SHA256 over method+content-hash+headers) is unchanged.
///
/// Registered as a DI singleton so both the config flow (login + device sync) and
/// TuyaLocalClient (cloud-fallback control) share one logged-in session for the
/// lifetime of the plugin process.
/// </summary>
public sealed class TuyaCloudClient(ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<TuyaCloudClient>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc = DateTime.MinValue;
    private string? _uid;

    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }
    public TuyaRegion Region { get; private set; } = TuyaRegion.CentralEurope;
    public string CountryCode { get; set; } = "48";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string Schema { get; set; } = "tuyaSmart";

    public bool IsLoggedIn => !string.IsNullOrEmpty(_uid) && !string.IsNullOrEmpty(_accessToken);
    public string? Uid => _uid;

    // Populated by GetDevicesAsync - used by the "pick from cloud" step of the device
    // config flow, and by SendCustomCommandAction/etc. via TuyaDeviceStore.
    public List<TuyaCloudDevice> CachedDevices { get; private set; } = [];

    // Client ID/Secret now come from the account config flow step (each person's own
    // Tuya IoT Platform "Cloud" project), not a value baked into the plugin.
    public void Configure(TuyaRegion region, string clientId, string clientSecret)
    {
        Region = region;
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public void ResetSession()
    {
        _accessToken = null;
        _accessTokenExpiresUtc = DateTime.MinValue;
        _uid = null;
    }

    private static string Sha256Hex(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? ""));
        return Convert.ToHexStringLower(hash);
    }

    private static string Md5Hex(string content)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(content ?? ""));
        return Convert.ToHexStringLower(hash);
    }

    private static string HmacSha256Upper(string content, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? ""), Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash); // already uppercase
    }

    private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<JObject> SignedRequestAsync(HttpMethod method, string pathAndQuery, string? jsonBody, bool includeAccessToken)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new TuyaApiException("No Access ID/Secret is set - enter them in the account config step.");
        }

        var t = NowMillis().ToString();
        var bodyForSign = jsonBody ?? "";
        var contentSha256 = Sha256Hex(bodyForSign);
        var stringToSign = $"{method.Method.ToUpperInvariant()}\n{contentSha256}\n\n{pathAndQuery}";
        var strToHash = includeAccessToken ? ClientId + _accessToken + t + stringToSign : ClientId + t + stringToSign;
        var sign = HmacSha256Upper(strToHash, ClientSecret);

        var request = new HttpRequestMessage(method, Region.BaseUrl() + pathAndQuery);
        request.Headers.TryAddWithoutValidation("client_id", ClientId);
        request.Headers.TryAddWithoutValidation("sign", sign);
        request.Headers.TryAddWithoutValidation("t", t);
        request.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        if (includeAccessToken && !string.IsNullOrEmpty(_accessToken))
        {
            request.Headers.TryAddWithoutValidation("access_token", _accessToken);
        }

        if (!string.IsNullOrEmpty(jsonBody))
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new TuyaApiException("Tuya connection error: " + ex.Message);
        }

        var responseText = await response.Content.ReadAsStringAsync();
        JObject result;
        try
        {
            result = JObject.Parse(responseText);
        }
        catch
        {
            throw new TuyaApiException($"Invalid Tuya server response ({(int)response.StatusCode}): {responseText}");
        }

        if (result["success"]?.ToObject<bool?>() != true)
        {
            var code = result["code"]?.ToString() ?? "?";
            var msg = result["msg"]?.ToString() ?? "Unknown error";
            throw new TuyaApiException($"Tuya API error {code}: {msg}");
        }

        return result;
    }

    public async Task GetClientTokenAsync()
    {
        var r = (await SignedRequestAsync(HttpMethod.Get, "/v1.0/token?grant_type=1", null, includeAccessToken: false))["result"];
        _accessToken = r?["access_token"]?.ToString();
        var expireSeconds = r?["expire_time"]?.ToObject<int?>() ?? 7200;
        _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expireSeconds - 60));
        _logger.Information("Tuya: obtained access token.");
    }

    private async Task EnsureClientTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _accessTokenExpiresUtc)
        {
            await GetClientTokenAsync();
        }
    }

    public async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new TuyaApiException("Enter your Tuya account username and password.");
        }

        await EnsureClientTokenAsync();
        if (!int.TryParse(CountryCode, out var countryCodeInt))
        {
            countryCodeInt = 48;
        }

        var bodyObj = new JObject
        {
            ["username"] = Username,
            ["password"] = Md5Hex(Password),
            ["country_code"] = countryCodeInt,
            ["schema"] = Schema,
        };

        _uid = (await SignedRequestAsync(HttpMethod.Post, "/v1.0/iot-01/associated-users/actions/authorized-login",
            bodyObj.ToString(Formatting.None), includeAccessToken: true))["result"]?["uid"]?.ToString();

        if (string.IsNullOrEmpty(_uid))
        {
            throw new TuyaApiException("Logged in, but no user id (uid) was returned.");
        }

        _logger.Information("Tuya: logged in successfully (uid: {Uid}).", _uid);
    }

    private async Task EnsureLoggedInAsync()
    {
        await EnsureClientTokenAsync();
        if (string.IsNullOrEmpty(_uid))
        {
            await LoginAsync();
        }
    }

    public async Task<List<TuyaCloudDevice>> GetDevicesAsync()
    {
        await EnsureLoggedInAsync();
        var result = await SignedRequestAsync(HttpMethod.Get, $"/v1.0/users/{_uid}/devices", null, includeAccessToken: true);
        var devices = new List<TuyaCloudDevice>();
        if (result["result"] is JArray arr)
        {
            foreach (var item in arr)
            {
                var id = item["id"]?.ToString();
                var name = item["name"]?.ToString();
                if (id is null || name is null)
                {
                    continue;
                }

                devices.Add(new TuyaCloudDevice
                {
                    Id = id,
                    Name = name,
                    Online = item["online"]?.ToObject<bool?>() == true,
                    Category = item["category"]?.ToString(),
                    ProductName = item["product_name"]?.ToString(),
                });
            }
        }

        CachedDevices = devices;
        return devices;
    }

    public async Task<JArray?> GetDeviceStatusAsync(string deviceId)
    {
        await EnsureLoggedInAsync();
        return (await SignedRequestAsync(HttpMethod.Get, $"/v1.0/devices/{deviceId}/status", null, includeAccessToken: true))["result"] as JArray;
    }

    public async Task<string?> GetLocalKeyAsync(string deviceId)
    {
        await EnsureLoggedInAsync();
        return (await SignedRequestAsync(HttpMethod.Get, $"/v1.0/devices/{deviceId}", null, includeAccessToken: true))["result"]?["local_key"]?.ToString();
    }

    public async Task SendCommandAsync(string deviceId, string code, object value)
    {
        await EnsureLoggedInAsync();
        var bodyObj = new JObject
        {
            ["commands"] = new JArray { new JObject { ["code"] = code, ["value"] = JToken.FromObject(value) } },
        };

        await SignedRequestAsync(HttpMethod.Post, $"/v1.0/devices/{deviceId}/commands", bodyObj.ToString(Formatting.None), includeAccessToken: true);
        _logger.Information("Tuya: sent command {Code}={Value} to device {DeviceId} via cloud.", code, value, deviceId);
    }
}
