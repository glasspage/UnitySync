using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Glasspage.UnitySync
{
    internal sealed class UnitySyncCrypto : IDisposable
    {
        private const byte EnvelopeVersion = 1;
        private const int IvSize = 16;
        private const int MacSize = 32;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _authenticationKey;

        internal UnitySyncCrypto(byte[] masterSecret)
        {
            if (masterSecret == null || masterSecret.Length != 32)
            {
                throw new ArgumentException("UnitySync requires a 256-bit master secret.", nameof(masterSecret));
            }

            _encryptionKey = DeriveKey(masterSecret, "UnitySync encryption key v1");
            _authenticationKey = DeriveKey(masterSecret, "UnitySync authentication key v1");
        }

        internal byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            byte[] iv = new byte[IvSize];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(iv);
            }

            byte[] ciphertext;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                {
                    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }

            byte[] envelope = new byte[1 + IvSize + ciphertext.Length + MacSize];
            envelope[0] = EnvelopeVersion;
            Buffer.BlockCopy(iv, 0, envelope, 1, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, envelope, 1 + IvSize, ciphertext.Length);

            using (HMACSHA256 hmac = new HMACSHA256(_authenticationKey))
            {
                byte[] mac = hmac.ComputeHash(envelope, 0, envelope.Length - MacSize);
                Buffer.BlockCopy(mac, 0, envelope, envelope.Length - MacSize, MacSize);
            }

            return envelope;
        }

        internal byte[] Decrypt(byte[] envelope)
        {
            if (envelope == null || envelope.Length < 1 + IvSize + 16 + MacSize || envelope[0] != EnvelopeVersion)
            {
                throw new CryptographicException("Invalid UnitySync encrypted message.");
            }

            int authenticatedLength = envelope.Length - MacSize;
            byte[] expectedMac;
            using (HMACSHA256 hmac = new HMACSHA256(_authenticationKey))
            {
                expectedMac = hmac.ComputeHash(envelope, 0, authenticatedLength);
            }

            if (!FixedTimeEquals(envelope, authenticatedLength, expectedMac))
            {
                throw new CryptographicException("UnitySync message authentication failed.");
            }

            byte[] iv = new byte[IvSize];
            Buffer.BlockCopy(envelope, 1, iv, 0, iv.Length);

            int ciphertextOffset = 1 + IvSize;
            int ciphertextLength = authenticatedLength - ciphertextOffset;
            if (ciphertextLength <= 0 || ciphertextLength % 16 != 0)
            {
                throw new CryptographicException("Invalid UnitySync ciphertext length.");
            }

            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(envelope, ciphertextOffset, ciphertextLength);
                }
            }
        }

        public void Dispose()
        {
            Array.Clear(_encryptionKey, 0, _encryptionKey.Length);
            Array.Clear(_authenticationKey, 0, _authenticationKey.Length);
        }

        private static byte[] DeriveKey(byte[] secret, string label)
        {
            using (HMACSHA256 hmac = new HMACSHA256(secret))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(label));
            }
        }

        private static bool FixedTimeEquals(byte[] envelope, int offset, byte[] expected)
        {
            int difference = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                difference |= envelope[offset + i] ^ expected[i];
            }

            return difference == 0;
        }
    }
}
