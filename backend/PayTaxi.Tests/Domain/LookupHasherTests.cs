using System.Security.Cryptography;
using System.Text;
using PayTaxi.Infrastructure.Security;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>
/// The lookup hashes beside encrypted columns (phone, IBAN) and the stored form of one-time sign-in codes. A plain
/// SHA-256 of a Georgian mobile number or of a 6-digit code is reversed by trying every candidate; keyed (HMAC-SHA256)
/// the same search needs the server-held hash key. Instances only — the process-wide <see cref="LookupHash"/> is the
/// API host's and is never reconfigured here.
/// </summary>
public class LookupHasherTests
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private const string Phone = "+995599123456";
    private const string Iban = "GE29TB7000000001234567";

    private static string Sha256Hex(string v) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();

    [Fact]
    public void A_keyed_phone_hash_is_not_the_plain_sha256_that_trying_every_number_would_find()
    {
        var keyed = new LookupHasher(KeyA).Phone(Phone);
        Assert.NotEqual(Sha256Hex(Phone), keyed);
        Assert.DoesNotContain(Sha256Hex(Phone), keyed);
        Assert.NotEqual(Sha256Hex(Iban), new LookupHasher(KeyA).Iban(Iban));
    }

    [Fact]
    public void Same_key_finds_the_same_row_another_key_does_not()
    {
        var a = new LookupHasher(KeyA);
        Assert.Equal(a.Phone(Phone), new LookupHasher(KeyA).Phone(Phone));   // deterministic: it is a lookup column
        Assert.NotEqual(a.Phone(Phone), new LookupHasher(KeyB).Phone(Phone));
        Assert.NotEqual(a.Iban(Iban), new LookupHasher(KeyB).Iban(Iban));
        Assert.NotEqual(a.Phone("+995599123457"), a.Phone(Phone));
    }

    [Fact]
    public void The_stored_form_names_the_key_that_made_it()
    {
        var a = new LookupHasher(KeyA);
        var b = new LookupHasher(KeyB);
        var h = a.Phone(Phone);

        Assert.StartsWith(LookupHasher.Scheme, h);
        Assert.Equal(a.Prefix + h[a.Prefix.Length..], h);
        Assert.Equal(64, h.Length - a.Prefix.Length);
        Assert.Matches("^[0-9a-f]+$", h[a.Prefix.Length..]);
        Assert.NotEqual(a.KeyId, b.KeyId);

        // "Current" is what the startup migrator leaves alone: made by this key — not by another key, not unkeyed.
        Assert.True(a.IsCurrent(h));
        Assert.False(b.IsCurrent(h));
        Assert.False(a.IsCurrent(Sha256Hex(Phone)));
        Assert.False(a.IsCurrent(""));
        Assert.True(LookupHasher.Unkeyed.IsCurrent(Sha256Hex(Phone)));
        Assert.False(LookupHasher.Unkeyed.IsCurrent(h));
    }

    [Fact]
    public void Phone_and_iban_hashes_of_the_same_text_differ()
    {
        var a = new LookupHasher(KeyA);
        Assert.NotEqual(a.Phone("GE29TB7000000001234567"), a.Iban("GE29TB7000000001234567"));
    }

    [Fact]
    public void A_sign_in_code_is_stored_keyed_and_bound_to_its_row()
    {
        var a = new LookupHasher(KeyA);
        var row1 = Guid.NewGuid();
        var row2 = Guid.NewGuid();
        var stored = a.SignInCode(row1, "123456");

        Assert.NotEqual(Sha256Hex("123456"), stored);
        Assert.DoesNotContain(Sha256Hex("123456"), stored);
        Assert.StartsWith(a.Prefix, stored);
        // The same code issued to two sign-ins does not show up as two equal hashes.
        Assert.NotEqual(stored, a.SignInCode(row2, "123456"));

        Assert.True(a.SignInCodeMatches(row1, "123456", stored));
        Assert.False(a.SignInCodeMatches(row1, "123457", stored));
        Assert.False(a.SignInCodeMatches(row2, "123456", stored));
        Assert.False(new LookupHasher(KeyB).SignInCodeMatches(row1, "123456", stored));
        Assert.False(a.SignInCodeMatches(row1, "123456", Sha256Hex("123456")));
    }

    [Fact]
    public void Without_a_key_it_is_the_old_plain_sha256_so_a_dev_database_keeps_working()
    {
        // Development only — Production refuses to start without Encryption:HashKey.
        var u = LookupHasher.Unkeyed;
        Assert.False(u.IsKeyed);
        Assert.Null(u.KeyId);
        Assert.Equal("", u.Prefix);
        Assert.Equal(Sha256Hex(Phone), u.Phone(Phone));
        Assert.Equal(Sha256Hex(Iban), u.Iban(Iban));
        Assert.True(u.SignInCodeMatches(Guid.NewGuid(), "123456", Sha256Hex("123456")));
        Assert.Same(LookupHasher.Unkeyed, LookupHasher.FromBase64(null));
        Assert.Same(LookupHasher.Unkeyed, LookupHasher.FromBase64("  "));
    }

    [Fact]
    public void A_key_must_be_32_bytes()
    {
        Assert.Throws<InvalidOperationException>(() => LookupHasher.FromBase64(Convert.ToBase64String(new byte[16])));
        Assert.Throws<InvalidOperationException>(() => new LookupHasher(new byte[31]));
        Assert.True(LookupHasher.FromBase64(Convert.ToBase64String(KeyA)).IsKeyed);
        Assert.Equal(new LookupHasher(KeyA).Phone(Phone), LookupHasher.FromBase64(Convert.ToBase64String(KeyA)).Phone(Phone));
    }
}
