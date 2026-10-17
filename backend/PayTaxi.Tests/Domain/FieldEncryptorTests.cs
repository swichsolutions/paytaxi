using System.Security.Cryptography;
using PayTaxi.Infrastructure.Security;
using Xunit;

namespace PayTaxi.Tests.Domain;

[Collection("encryptor")] // static key state — run serially
public class FieldEncryptorTests : IDisposable
{
    public FieldEncryptorTests() => FieldEncryptor.Configure(FieldEncryptor.NewKeyBase64());
    public void Dispose() => FieldEncryptor.Reset();

    [Fact]
    public void Roundtrip_and_prefix()
    {
        var enc = FieldEncryptor.Encrypt("+995599123456");
        Assert.StartsWith("enc:v1:", enc);
        Assert.NotEqual("+995599123456", enc);
        Assert.Equal("+995599123456", FieldEncryptor.Decrypt(enc));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        Assert.NotEqual(FieldEncryptor.Encrypt("GE62TB0011223344556677"), FieldEncryptor.Encrypt("GE62TB0011223344556677"));
    }

    [Fact]
    public void Legacy_plaintext_and_empty_pass_through()
    {
        Assert.Equal("plain", FieldEncryptor.Decrypt("plain"));
        Assert.Equal("", FieldEncryptor.Encrypt(""));
        Assert.Equal("", FieldEncryptor.Decrypt(""));
        Assert.False(FieldEncryptor.IsEncrypted("plain"));
    }

    [Fact]
    public void A_value_that_only_looks_encrypted_is_encrypted_like_any_other()
    {
        // The converter hands Encrypt what a caller typed. Passing a look-alike through unencrypted let a park manager
        // store "enc:v1:x" verbatim — undecryptable, so every later read of the row (the whole park list) threw.
        foreach (var lookAlike in new[] { "enc:v1:x", FieldEncryptor.Encrypt("x") })
        {
            var stored = FieldEncryptor.Encrypt(lookAlike);
            Assert.NotEqual(lookAlike, stored);
            Assert.Equal(lookAlike, FieldEncryptor.Decrypt(stored));
        }
    }

    [Fact]
    public void Tampering_or_wrong_key_is_detected()
    {
        var enc = FieldEncryptor.Encrypt("secret");
        var tampered = enc[..^2] + (enc[^2] == 'A' ? "BB" : "AA");
        Assert.ThrowsAny<CryptographicException>(() => FieldEncryptor.Decrypt(tampered));

        FieldEncryptor.Configure(FieldEncryptor.NewKeyBase64());
        Assert.ThrowsAny<CryptographicException>(() => FieldEncryptor.Decrypt(enc));
    }

    [Fact]
    public void Rejects_wrong_key_length()
    {
        Assert.Throws<InvalidOperationException>(() => FieldEncryptor.Configure(Convert.ToBase64String(new byte[16])));
    }

    // The lookup hashes beside the encrypted columns are keyed now: LookupHasherTests.
}
