using PayTaxi.Core.Banking;
using Xunit;

namespace PayTaxi.Tests.Domain;

public class GeorgianIbanTests
{
    [Fact]
    public void Accepts_national_bank_example_iban()
    {
        Assert.True(GeorgianIban.TryParse("GE29 NB00 0000 0101 9049 17", out var iban, out var code, out _));
        Assert.Equal("GE29NB0000000101904917", iban);
        Assert.Equal("NB", code);
    }

    [Fact]
    public void Rejects_bad_checksum_and_bad_shape()
    {
        Assert.False(GeorgianIban.TryParse("GE00TB0011223344556677", out _, out _, out var err));
        Assert.Equal("invalid_iban_checksum", err);
        Assert.False(GeorgianIban.TryParse("GE62TB00112233445566", out _, out _, out err));
        Assert.Equal("invalid_iban_format", err);
        Assert.False(GeorgianIban.TryParse("", out _, out _, out err));
        Assert.Equal("iban_required", err);
    }

    [Fact]
    public void Build_produces_valid_ibans_with_bank_code()
    {
        var iban = GeorgianIban.Build("TB", "7000000001234567");
        Assert.True(GeorgianIban.TryParse(iban, out _, out var code, out _));
        Assert.Equal("TB", code);
        Assert.Equal("TBC", GeorgianIban.BankLabel(code));
        Assert.Equal("**** 4567", GeorgianIban.Mask(iban));
    }
}
