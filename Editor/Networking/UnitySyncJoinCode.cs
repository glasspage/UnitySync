using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Glasspage.UnitySync
{
    internal readonly struct UnitySyncJoinCodeData
    {
        internal readonly IPAddress Address;
        internal readonly int Port;
        internal readonly byte[] Secret;

        internal UnitySyncJoinCodeData(IPAddress address, int port, byte[] secret)
        {
            Address = address;
            Port = port;
            Secret = secret;
        }
    }

    internal static class UnitySyncJoinCode
    {
        private const byte FormatVersion = 1;
        private const string Prefix = "US1-";
        private const int SecretSize = 32;
        private const int PayloadSize = 1 + 4 + 2 + SecretSize;
        private const int ChecksumSize = 4;

        internal static bool TryCreate(string addressText, int port, out string code, out byte[] secret, out string error)
        {
            code = string.Empty;
            secret = null;
            error = string.Empty;

            if (!IPAddress.TryParse(addressText, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "Host address must be a valid IPv4 address, such as the host's Hamachi IPv4 address.";
                return false;
            }

            if (port <= IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            {
                error = "Port must be between 1 and 65535.";
                return false;
            }

            secret = new byte[SecretSize];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(secret);
            }

            byte[] payload = new byte[PayloadSize + ChecksumSize];
            payload[0] = FormatVersion;

            byte[] addressBytes = address.GetAddressBytes();
            Buffer.BlockCopy(addressBytes, 0, payload, 1, addressBytes.Length);
            payload[5] = (byte)(port >> 8);
            payload[6] = (byte)port;
            Buffer.BlockCopy(secret, 0, payload, 7, secret.Length);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] checksum = sha.ComputeHash(payload, 0, PayloadSize);
                Buffer.BlockCopy(checksum, 0, payload, PayloadSize, ChecksumSize);
            }

            code = Prefix + ToBase64Url(payload);
            return true;
        }

        internal static bool TryParse(string code, out UnitySyncJoinCodeData data, out string error)
        {
            data = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(code))
            {
                error = "Enter a UnitySync join code.";
                return false;
            }

            string compactCode = RemoveWhitespace(code);
            if (!compactCode.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "This is not a UnitySync v1 join code.";
                return false;
            }

            byte[] payload;
            try
            {
                payload = FromBase64Url(compactCode.Substring(Prefix.Length));
            }
            catch (FormatException)
            {
                error = "The join code contains invalid characters.";
                return false;
            }

            if (payload.Length != PayloadSize + ChecksumSize || payload[0] != FormatVersion)
            {
                error = "The join code has an unsupported format.";
                return false;
            }

            byte[] expectedChecksum;
            using (SHA256 sha = SHA256.Create())
            {
                expectedChecksum = sha.ComputeHash(payload, 0, PayloadSize);
            }

            for (int i = 0; i < ChecksumSize; i++)
            {
                if (payload[PayloadSize + i] != expectedChecksum[i])
                {
                    error = "The join code is incomplete or mistyped.";
                    return false;
                }
            }

            byte[] addressBytes = new byte[4];
            Buffer.BlockCopy(payload, 1, addressBytes, 0, addressBytes.Length);
            int port = (payload[5] << 8) | payload[6];
            if (port <= IPEndPoint.MinPort)
            {
                error = "The join code contains an invalid port.";
                return false;
            }

            byte[] secret = new byte[SecretSize];
            Buffer.BlockCopy(payload, 7, secret, 0, secret.Length);

            data = new UnitySyncJoinCodeData(new IPAddress(addressBytes), port, secret);
            return true;
        }

        internal static string FindSuggestedAddress()
        {
            List<IPAddress> candidates = new List<IPAddress>();

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                        networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                        {
                            candidates.Add(address);
                        }
                    }
                }
            }
            catch
            {
                // Fall through to localhost if network enumeration is unavailable.
            }

            // Hamachi assigns clients IPv4 addresses in 25.0.0.0/8. Prefer that
            // adapter when available so the generated join code works out of the box
            // for the recommended connection method.
            foreach (IPAddress address in candidates)
            {
                if (address.GetAddressBytes()[0] == 25)
                {
                    return address.ToString();
                }
            }

            // Keep Radmin's 26.x.x.x range as a secondary fallback for existing users.
            foreach (IPAddress address in candidates)
            {
                if (address.GetAddressBytes()[0] == 26)
                {
                    return address.ToString();
                }
            }

            foreach (IPAddress address in candidates)
            {
                byte[] bytes = address.GetAddressBytes();
                bool isPrivate = bytes[0] == 10 ||
                                 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                                 (bytes[0] == 192 && bytes[1] == 168);
                if (isPrivate)
                {
                    return address.ToString();
                }
            }

            return candidates.Count > 0 ? candidates[0].ToString() : IPAddress.Loopback.ToString();
        }

        private static string RemoveWhitespace(string value)
        {
            char[] result = new char[value.Length];
            int count = 0;
            foreach (char character in value)
            {
                if (!char.IsWhiteSpace(character))
                {
                    result[count++] = character;
                }
            }

            return new string(result, 0, count);
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] FromBase64Url(string value)
        {
            string base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                case 1:
                    throw new FormatException();
            }

            return Convert.FromBase64String(base64);
        }
    }
}
