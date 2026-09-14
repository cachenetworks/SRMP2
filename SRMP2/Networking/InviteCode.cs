using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SRMP2.Networking;

internal static class InviteCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int RawCodeLength = 10;

    internal static async Task<string> CreateForHostAsync(int port)
    {
        var address = await TryGetPublicIpv4Async().ConfigureAwait(false)
            ?? TryGetLanIpv4()
            ?? IPAddress.Loopback;

        return Encode(address, port);
    }

    internal static string Encode(IPAddress address, int port)
    {
        if (address == null)
            throw new ArgumentNullException(nameof(address));
        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var bytes = address.MapToIPv4().GetAddressBytes();
        ulong value = 0;
        for (var i = 0; i < 4; i++)
            value = (value << 8) | bytes[i];

        value = (value << 16) | (ushort)port;

        var chars = new char[RawCodeLength];
        for (var i = RawCodeLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31UL)];
            value >>= 5;
        }

        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    internal static bool TryDecode(string code, out string host, out int port)
    {
        host = null;
        port = 0;

        if (string.IsNullOrWhiteSpace(code))
            return false;

        Span<char> cleaned = stackalloc char[RawCodeLength];
        var count = 0;

        foreach (var raw in code.Trim().ToUpperInvariant())
        {
            if (raw == '-' || char.IsWhiteSpace(raw))
                continue;

            if (count >= RawCodeLength)
                return false;

            var c = raw switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                _ => raw
            };

            if (Alphabet.IndexOf(c) < 0)
                return false;

            cleaned[count++] = c;
        }

        if (count != RawCodeLength)
            return false;

        ulong value = 0;
        for (var i = 0; i < RawCodeLength; i++)
        {
            value = (value << 5) | (uint)Alphabet.IndexOf(cleaned[i]);
        }

        // Ten Crockford Base32 characters hold 50 bits. Our payload uses 48,
        // so reject codes whose two high padding bits are not zero.
        if ((value >> 48) != 0)
            return false;

        port = (int)(value & 0xFFFFUL);
        if (port <= 0)
            return false;

        value >>= 16;
        var bytes = new byte[4];
        for (var i = 3; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFFUL);
            value >>= 8;
        }

        host = new IPAddress(bytes).ToString();
        return true;
    }

    private static async Task<IPAddress> TryGetPublicIpv4Async()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SRMP2/0.1");
            var text = (await client.GetStringAsync("https://api.ipify.org").ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork
                ? address
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IPAddress TryGetLanIpv4()
    {
        try
        {
            foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address;
            }
        }
        catch
        {
        }

        return null;
    }
}
