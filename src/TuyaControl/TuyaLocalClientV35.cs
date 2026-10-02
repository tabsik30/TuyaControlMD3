using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TuyaControl.Models;

namespace TuyaControl.Services;

/// <summary>
/// Protocol 3.5 (AES-GCM) LAN client, ported from TuyaLocalClientV35. Stays static
/// like the original - it's called by TuyaLocalClient, which passes its own ILogger
/// through (TuyaDebugLog.Write(...) calls became logger.Debug(...)).
/// </summary>
internal static class TuyaLocalClientV35
{
    private const int Port = 6668;
    private const uint Prefix = 26265u;
    private const uint Footer = 39270u;

    private static int _seq;

    public static async Task<JObject> GetStatusAsync(TuyaDeviceRecord device, int timeoutMs, ILogger logger)
    {
        using var client = new TcpClient();
        if (!await ConnectWithTimeoutAsync(client, device.IpAddress, Port, timeoutMs))
        {
            throw new TuyaApiException($"Could not connect to {device.IpAddress}:{Port} (timeout).");
        }

        using var stream = client.GetStream();
        var sessionKey = await NegotiateSessionKeyAsync(stream, device.LocalKey!, timeoutMs, logger);
        var payload = new JObject();
        var response = await SendCommandOnStreamAsync(stream, sessionKey, 16, payload, timeoutMs, logger);

        return (response?["dps"] as JObject) ?? response ?? [];
    }

    public static async Task SendCommandAsync(TuyaDeviceRecord device, string code, object value, int timeoutMs, ILogger logger)
    {
        code = TuyaLocalClient.ToLocalDpCode(code);

        using var client = new TcpClient();
        if (!await ConnectWithTimeoutAsync(client, device.IpAddress, Port, timeoutMs))
        {
            throw new TuyaApiException($"Could not connect to {device.IpAddress}:{Port} (timeout).");
        }

        using var stream = client.GetStream();
        var sessionKey = await NegotiateSessionKeyAsync(stream, device.LocalKey!, timeoutMs, logger);
        var payload = new JObject
        {
            ["protocol"] = 5,
            ["t"] = UnixTime(),
            ["data"] = new JObject { ["dps"] = new JObject { [code] = JToken.FromObject(value) } },
        };

        await SendCommandOnStreamAsync(stream, sessionKey, 13, payload, timeoutMs, logger);
    }

    private static long UnixTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static async Task<byte[]> NegotiateSessionKeyAsync(NetworkStream stream, string realLocalKeyStr, int timeoutMs, ILogger logger)
    {
        var realKey = Encoding.UTF8.GetBytes(realLocalKeyStr);
        var clientNonce = new byte[16];
        RandomNumberGenerator.Fill(clientNonce);

        var startIv = new byte[12];
        Array.Copy(clientNonce, startIv, 12);
        var startPacket = BuildFrame(NextSeq(), 3u, clientNonce, realKey, startIv);
        logger.Debug("[LocalClientV35] sending SESS_KEY_NEG_START.");
        await stream.WriteAsync(startPacket);

        var respRaw = await ReadWithTimeoutAsync(stream, timeoutMs);
        if (respRaw is null || respRaw.Length == 0)
        {
            throw new TuyaApiException("The device did not respond to the 3.5 session negotiation (SESS_KEY_NEG_RESP).");
        }

        var respPlain = DecryptFrame(respRaw, realKey, logger);
        if (respPlain is null || respPlain.Length < 48)
        {
            throw new TuyaApiException("Could not decrypt SESS_KEY_NEG_RESP (3.5) - check the local key.");
        }

        var deviceNonce = new byte[16];
        Array.Copy(respPlain, 0, deviceNonce, 0, 16);

        byte[] finishHmac;
        using (var h = new HMACSHA256(realKey))
        {
            finishHmac = h.ComputeHash(deviceNonce);
        }

        var finishIv = new byte[12];
        RandomNumberGenerator.Fill(finishIv);
        var finishPacket = BuildFrame(NextSeq(), 5u, finishHmac, realKey, finishIv);
        logger.Debug("[LocalClientV35] sending SESS_KEY_NEG_FINISH.");
        await stream.WriteAsync(finishPacket);

        var xorNonce = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            xorNonce[i] = (byte)(clientNonce[i] ^ deviceNonce[i]);
        }

        using var aesGcm = new AesGcm(realKey, 16);
        var cipherText = new byte[16];
        var tag = new byte[16];
        aesGcm.Encrypt(startIv, xorNonce, cipherText, tag);
        logger.Debug("[LocalClientV35] session negotiated, session_key established.");
        return cipherText;
    }

    private static async Task<JObject?> SendCommandOnStreamAsync(NetworkStream stream, byte[] sessionKey, int command, JObject payload, int timeoutMs, ILogger logger)
    {
        var plain = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        if (command is not (10 or 16))
        {
            plain = PrependVersionHeader(plain, "3.5");
        }

        var iv = new byte[12];
        RandomNumberGenerator.Fill(iv);
        var packet = BuildFrame(NextSeq(), (uint)command, plain, sessionKey, iv);
        logger.Debug("[LocalClientV35] sending command={Command}, plaintext='{Payload}'", command, payload.ToString(Formatting.None));
        await stream.WriteAsync(packet);

        var response = await ReadWithTimeoutAsync(stream, timeoutMs);
        if (response is null)
        {
            if (command is 7 or 13)
            {
                return [];
            }

            throw new TuyaApiException("The device did not respond (timeout, 3.5).");
        }

        if (response.Length == 0)
        {
            logger.Debug("[LocalClientV35] connection closed with no data.");
            return null;
        }

        var plainResp = DecryptFrame(response, sessionKey, logger);
        if (plainResp is null)
        {
            return [];
        }

        string? text = null;
        foreach (var skip in new[] { 4, 0 })
        {
            if (plainResp.Length - skip <= 0)
            {
                continue;
            }

            var slice = new byte[plainResp.Length - skip];
            Array.Copy(plainResp, skip, slice, 0, slice.Length);
            var candidate = SafeUtf8(slice)?.Trim().TrimEnd('\0');
            if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith('{') && candidate.EndsWith('}'))
            {
                text = candidate;
                break;
            }
        }

        if (text is null)
        {
            return [];
        }

        try
        {
            return JObject.Parse(text);
        }
        catch
        {
            return [];
        }
    }

    private static byte[] BuildFrame(uint seq, uint command, byte[] plaintext, byte[] key, byte[] iv)
    {
        var cipherText = new byte[plaintext.Length];
        var tag = new byte[16];
        var length = (uint)(12 + plaintext.Length + 16);
        var header = new byte[14];
        using (var s = new MemoryStream(header))
        {
            WriteUInt16BE(s, 0);
            WriteUInt32BE(s, seq);
            WriteUInt32BE(s, command);
            WriteUInt32BE(s, length);
        }

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Encrypt(iv, plaintext, cipherText, tag, header);
        }

        using var ms = new MemoryStream();
        WriteUInt32BE(ms, Prefix);
        ms.Write(header, 0, header.Length);
        ms.Write(iv, 0, iv.Length);
        ms.Write(cipherText, 0, cipherText.Length);
        ms.Write(tag, 0, tag.Length);
        WriteUInt32BE(ms, Footer);
        return ms.ToArray();
    }

    private static byte[]? DecryptFrame(byte[] data, byte[] key, ILogger logger)
    {
        try
        {
            if (data.Length < 46)
            {
                return null;
            }

            var header = new byte[14];
            Array.Copy(data, 4, header, 0, 14);
            var declaredLength = ReadUInt32BE(data, 14);
            const int ivOffset = 18;
            var iv = new byte[12];
            Array.Copy(data, ivOffset, iv, 0, 12);

            var cipherLen = (int)(declaredLength - 12 - 16);
            var cipherOffset = ivOffset + 12;
            if (cipherLen < 0 || cipherOffset + cipherLen + 16 > data.Length)
            {
                cipherLen = Math.Max(0, data.Length - cipherOffset - 16 - 4);
            }

            if (cipherLen <= 0)
            {
                return null;
            }

            var cipherText = new byte[cipherLen];
            Array.Copy(data, cipherOffset, cipherText, 0, cipherLen);
            var tag = new byte[16];
            Array.Copy(data, cipherOffset + cipherLen, tag, 0, 16);

            var plain = new byte[cipherLen];
            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Decrypt(iv, cipherText, tag, plain, header);
            }

            return plain;
        }
        catch (Exception ex)
        {
            logger.Debug("[LocalClientV35] GCM decryption error: {Message}", ex.Message);
            return null;
        }
    }

    private static async Task<bool> ConnectWithTimeoutAsync(TcpClient client, string ip, int port, int timeoutMs)
    {
        var connectTask = client.ConnectAsync(ip, port);
        if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) != connectTask)
        {
            return false;
        }

        await connectTask;
        return true;
    }

    private static async Task<byte[]?> ReadWithTimeoutAsync(NetworkStream stream, int timeoutMs)
    {
        var buffer = new byte[4096];
        var readTask = stream.ReadAsync(buffer).AsTask();
        if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) != readTask)
        {
            return null;
        }

        var read = readTask.Result;
        if (read <= 0)
        {
            return [];
        }

        var response = new byte[read];
        Array.Copy(buffer, response, read);
        return response;
    }

    private static string? SafeUtf8(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] PrependVersionHeader(byte[] plain, string version)
    {
        var header = new byte[15];
        Encoding.ASCII.GetBytes(version).CopyTo(header, 0);
        var result = new byte[header.Length + plain.Length];
        header.CopyTo(result, 0);
        plain.CopyTo(result, header.Length);
        return result;
    }

    private static uint NextSeq() => (uint)Interlocked.Increment(ref _seq);

    private static void WriteUInt32BE(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static void WriteUInt16BE(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
