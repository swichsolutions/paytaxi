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
    public void Already_encrypted_is_not_double_encrypted()
    {
        var once = FieldEncryptor.Encrypt("x");
        Assert.Equal(once, FieldEncryptor.Encrypt(once));
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

    [Fact]
    public void Hash_is_stable_lowercase_hex()
    {
        var h = FieldEncryptor.Hash("GE62TB0011223344556677");
        Assert.Equal(64, h.Length);
        Assert.Equal(h, FieldEncryptor.Hash("GE62TB0011223344556677"));
        Assert.Equal(h, h.ToLowerInvariant());
    }
}
