using System;

namespace nfaz3166iotcentral
{
    /// <summary>SHA-256, HMAC-SHA256, the Azure IoT Hub and Device Provisioning Service SAS tokens, in managed code (no crypto library needed).</summary>
    internal static class Sas
    {
        private static readonly uint[] K = new uint[]
        {
            0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5,
            0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
            0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3,
            0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
            0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC,
            0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
            0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7,
            0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
            0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13,
            0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
            0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3,
            0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
            0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5,
            0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
            0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208,
            0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
        };

        private static uint RotateRight(uint x, int n)
        {
            return (x >> n) | (x << (32 - n));
        }

        public static byte[] Sha256(byte[] data)
        {
            uint[] h = new uint[]
            {
            0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
            0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
            };

            // padding: 0x80, zeros, 64 bit length in bits (big endian), total a multiple of 64 bytes
            int paddedLength = ((data.Length + 8) / 64 + 1) * 64;
            byte[] padded = new byte[paddedLength];
            Array.Copy(data, padded, data.Length);
            padded[data.Length] = 0x80;

            long bitLength = (long)data.Length * 8;

            for (int i = 0; i < 8; i++)
            {
                padded[paddedLength - 1 - i] = (byte)(bitLength >> (8 * i));
            }

            uint[] w = new uint[64];

            for (int chunk = 0; chunk < paddedLength; chunk += 64)
            {
                for (int i = 0; i < 16; i++)
                {
                    int p = chunk + i * 4;
                    w[i] = ((uint)padded[p] << 24) | ((uint)padded[p + 1] << 16) | ((uint)padded[p + 2] << 8) | padded[p + 3];
                }

                for (int i = 16; i < 64; i++)
                {
                    uint s0 = RotateRight(w[i - 15], 7) ^ RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                    uint s1 = RotateRight(w[i - 2], 17) ^ RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                    w[i] = w[i - 16] + s0 + w[i - 7] + s1;
                }

                uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];

                for (int i = 0; i < 64; i++)
                {
                    uint s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
                    uint ch = (e & f) ^ (~e & g);
                    uint temp1 = hh + s1 + ch + K[i] + w[i];
                    uint s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
                    uint maj = (a & b) ^ (a & c) ^ (b & c);
                    uint temp2 = s0 + maj;

                    hh = g;
                    g = f;
                    f = e;
                    e = d + temp1;
                    d = c;
                    c = b;
                    b = a;
                    a = temp1 + temp2;
                }

                h[0] += a;
                h[1] += b;
                h[2] += c;
                h[3] += d;
                h[4] += e;
                h[5] += f;
                h[6] += g;
                h[7] += hh;
            }

            byte[] hash = new byte[32];

            for (int i = 0; i < 8; i++)
            {
                hash[i * 4] = (byte)(h[i] >> 24);
                hash[i * 4 + 1] = (byte)(h[i] >> 16);
                hash[i * 4 + 2] = (byte)(h[i] >> 8);
                hash[i * 4 + 3] = (byte)h[i];
            }

            return hash;
        }

        public static byte[] HmacSha256(byte[] key, byte[] message)
        {
            if (key.Length > 64)
            {
                key = Sha256(key);
            }

            byte[] inner = new byte[64 + message.Length];
            byte[] outer = new byte[64 + 32];

            for (int i = 0; i < 64; i++)
            {
                byte k = i < key.Length ? key[i] : (byte)0;
                inner[i] = (byte)(k ^ 0x36);
                outer[i] = (byte)(k ^ 0x5C);
            }

            Array.Copy(message, 0, inner, 64, message.Length);
            Array.Copy(Sha256(inner), 0, outer, 64, 32);

            return Sha256(outer);
        }

        /// <summary>Percent-encodes everything except unreserved characters (RFC 3986).</summary>
        public static string UrlEncode(string text)
        {
            const string hex = "0123456789ABCDEF";
            var result = new System.Text.StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('%').Append(hex[(c >> 4) & 0xF]).Append(hex[c & 0xF]);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Creates a device SAS token: SharedAccessSignature sr={host}%2Fdevices%2F{id}&amp;sig={signature}&amp;se={expiry}
        /// </summary>
        /// <param name="hostName">IoT Hub host name, e.g. myhub.azure-devices.net.</param>
        /// <param name="deviceId">Device id.</param>
        /// <param name="base64Key">The device's primary or secondary key (base64).</param>
        /// <param name="expiryUnixSeconds">Expiry time, seconds since 1970 (UTC).</param>
        public static string CreateToken(string hostName, string deviceId, string base64Key, long expiryUnixSeconds)
        {
            string resource = UrlEncode(hostName + "/devices/" + deviceId);
            string toSign = resource + "\n" + expiryUnixSeconds.ToString();

            byte[] signature = HmacSha256(Convert.FromBase64String(base64Key), System.Text.Encoding.UTF8.GetBytes(toSign));

            return "SharedAccessSignature sr=" + resource + "&sig=" + UrlEncode(Convert.ToBase64String(signature)) + "&se=" + expiryUnixSeconds.ToString();
        }

        /// <summary>
        /// Derives a device key from an IoT Central / DPS enrollment group key: base64(HMAC-SHA256(groupKey, registrationId)).
        /// </summary>
        public static string DeriveDeviceKey(string base64GroupKey, string registrationId)
        {
            byte[] key = HmacSha256(Convert.FromBase64String(base64GroupKey), System.Text.Encoding.UTF8.GetBytes(registrationId));

            return Convert.ToBase64String(key);
        }

        /// <summary>
        /// Creates the SAS token for the Device Provisioning Service:
        /// SharedAccessSignature sr={idScope}%2Fregistrations%2F{registrationId}&amp;sig={signature}&amp;se={expiry}&amp;skn=registration
        /// </summary>
        public static string CreateDpsToken(string idScope, string registrationId, string base64Key, long expiryUnixSeconds)
        {
            string resource = UrlEncode(idScope + "/registrations/" + registrationId);
            string toSign = resource + "\n" + expiryUnixSeconds.ToString();

            byte[] signature = HmacSha256(Convert.FromBase64String(base64Key), System.Text.Encoding.UTF8.GetBytes(toSign));

            return "SharedAccessSignature sr=" + resource + "&sig=" + UrlEncode(Convert.ToBase64String(signature)) + "&se=" + expiryUnixSeconds.ToString() + "&skn=registration";
        }
    }
}
