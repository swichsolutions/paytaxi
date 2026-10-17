using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using Xunit;

namespace PayTaxi.Tests.Tbc;

/// <summary>A tenant-written certificatePath opens nothing outside the operator's certificate directory, and a failed load says only that.</summary>
public class TbcCertificatePathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("paytaxi-certs-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static TbcCredentials With(string? path) => new() { Username = "u", Password = "p", CertificatePath = path };

    [Fact]
    public void Without_a_configured_directory_no_path_is_honoured()
    {
        Assert.Null(TbcCredentials.CertificatePathProblem(null, null));
        Assert.Null(TbcCredentials.CertificatePathProblem("", null));
        Assert.Equal("certificate_path_not_allowed", TbcCredentials.CertificatePathProblem("/etc/passwd", null));
        Assert.Equal("certificate_path_not_allowed", TbcCredentials.CertificatePathProblem(Path.Combine(_dir, "park.pfx"), null));

        var ex = Assert.Throws<TbcConfigurationException>(() => With("/etc/passwd").LoadCertificate(null));
        Assert.StartsWith("TBC_CERT_PATH_NOT_ALLOWED", ex.Message);
    }

    [Fact]
    public void Only_paths_inside_the_configured_directory_are_honoured()
    {
        Assert.Null(TbcCredentials.CertificatePathProblem(Path.Combine(_dir, "park.pfx"), _dir));
        Assert.Equal("certificate_path_not_allowed", TbcCredentials.CertificatePathProblem(Path.Combine(_dir, "..", "other.pfx"), _dir));
        Assert.Equal("certificate_path_not_allowed", TbcCredentials.CertificatePathProblem(_dir + "-sibling" + Path.DirectorySeparatorChar + "x.pfx", _dir));
        Assert.Throws<TbcConfigurationException>(() => With(Path.Combine(_dir, "..", "x.pfx")).LoadCertificate(_dir));
    }

    [Fact]
    public void A_failed_load_names_no_file_and_quotes_no_loader_text()
    {
        var missing = Path.Combine(_dir, "missing.pfx");
        var junk = Path.Combine(_dir, "junk.pfx");
        File.WriteAllText(junk, "not a certificate");
        var huge = Path.Combine(_dir, "huge.pfx");
        File.WriteAllBytes(huge, new byte[TbcCredentials.MaxCertificateBytes + 1]);

        // Missing, unparsable and oversized all answer the same — nothing to tell files apart by.
        foreach (var path in new[] { missing, junk, huge })
        {
            var ex = Assert.Throws<TbcConfigurationException>(() => With(path).LoadCertificate(_dir));
            Assert.StartsWith("TBC_CERT_UNLOADABLE", ex.Message);
            Assert.DoesNotContain(_dir, ex.Message);
            Assert.DoesNotContain("Could not find", ex.Message);
        }
    }
}
