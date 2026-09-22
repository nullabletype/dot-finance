using System.Security.Cryptography;
namespace AuthenticatedSnapshots.Tests;

public sealed class CryptographicKnownAnswerTests
{
    [Fact]
    public void Dotnet_pbkdf2_matches_the_rfc_7914_sha256_vector()
    {
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            "Password"u8,
            "NaCl"u8,
            80_000,
            HashAlgorithmName.SHA256,
            64);

        Assert.Equal(
            "4DDCD8F60B98BE21830CEE5EF22701F9641A4418D04C0414AEFF08876B34AB56A1D425A1225833549ADB841B51C9B3176A272BDEBBA1D078478F62B397F33C8D",
            Convert.ToHexString(actual));
    }

    [Fact]
    public void Dotnet_aes_gcm_matches_the_nist_256_bit_vector()
    {
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        byte[] plaintext = new byte[16];
        byte[] ciphertext = new byte[16];
        byte[] tag = new byte[16];

        using (AesGcm aes = new(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        Assert.Equal("CEA7403D4D606B6E074EC5D3BAF39D18", Convert.ToHexString(ciphertext));
        Assert.Equal("D0D1C8A799996BF0265B98B5D48AB919", Convert.ToHexString(tag));
    }
}