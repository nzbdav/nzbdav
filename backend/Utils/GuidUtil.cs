using System.Security.Cryptography;

namespace NzbWebDAV.Utils;

public static class GuidUtil
{
    // Generates a guid using a cryptographically secure RNG
    public static Guid GenerateSecureGuid()
    {
        byte[] bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);

        // Set version to 4 (random)
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        // Set variant to RFC 4122
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
