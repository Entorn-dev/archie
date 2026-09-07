using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Archie.Contracts;

namespace Archie.Cli;

internal sealed class ScannerTrustVerifier
{
    private const string BootstrapKeyId = "entorn-scanner-signing-2026-01";
    private const string BootstrapPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERWbTLkNYDWSEv87o3LNoVa67uGbVoSUDTUMPt2quGen57ZqC9AIgTsn+K+h8vCaVlMgahQlNFYV2w0liE9cLYg==";
    private readonly IReadOnlyDictionary<string, byte[]> trustedKeys;

    public ScannerTrustVerifier() : this(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [BootstrapKeyId] = Convert.FromBase64String(BootstrapPublicKey)
    })
    { }

    internal ScannerTrustVerifier(IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        this.trustedKeys = trustedKeys;
    }

    public byte[] VerifyCatalog(SignedScannerCatalog envelope)
    {
        if (envelope.SchemaVersion != "signed-scanner-catalog/v1")
            throw new InvalidDataException("Scanner catalog uses an unsupported signed-envelope version.");
        var payload = Decode(envelope.Payload, "catalog payload");
        var signature = Decode(envelope.Signature, "catalog signature");
        Verify(envelope.KeyId, payload, signature, "scanner catalog");
        return payload;
    }

    public void VerifyRelease(ScannerCatalogRelease release, ReadOnlySpan<byte> archiveSha256)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(release.Sha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Catalog release SHA-256 is invalid.", exception);
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, archiveSha256))
            throw new InvalidDataException("Downloaded scanner package SHA-256 does not match the signed catalog.");
        Verify(release.SigningKeyId, ReleaseSigningBytes(release),
            Decode(release.PackageSignature, "package signature"), "scanner package");
    }

    internal static byte[] ReleaseSigningBytes(ScannerCatalogRelease release)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new[] { release.Id, release.Version, release.Platform, release.Architecture, release.Sha256 })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private void Verify(string keyId, ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature, string subject)
    {
        if (!trustedKeys.TryGetValue(keyId, out var publicKey))
            throw new InvalidDataException($"The {subject} uses unknown signing key '{keyId}'.");
        if (signature.Length != 64)
            throw new InvalidDataException($"The {subject} signature has an invalid length.");
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256 ||
                !ecdsa.VerifyData(content, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException($"The {subject} signature is invalid.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"The trusted key for the {subject} is invalid.", exception);
        }
    }

    private static byte[] Decode(string value, string field)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The {field} is not valid base64.", exception);
        }
    }
}
