using System.Security.Cryptography;
using System.Text;

namespace LiteMonitor.Updates;

internal static class UpdatePackage
{
    private const string PublicKeyResource = "LiteMonitor.UpdatePublicKey";
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    internal const int MaxSignatureBytes = 1024;
    internal static bool HasPublicKey =>
        typeof(UpdatePackage).Assembly.GetManifestResourceInfo(PublicKeyResource) != null;

    internal static void Verify(Stream package, Version version, byte[] signature)
    {
        if (package.Length is <= 0 or > MaxPackageBytes || signature.Length is <= 0 or > MaxSignatureBytes)
            throw new InvalidDataException("Invalid update package size.");

        using var keyStream = typeof(UpdatePackage).Assembly.GetManifestResourceStream(PublicKeyResource)
            ?? throw new InvalidOperationException("This build has no update signing key.");
        using var reader = new StreamReader(keyStream);
        using var key = RSA.Create();
        key.ImportFromPem(reader.ReadToEnd());

        package.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(package));
        package.Position = 0;
        var payload = Encoding.UTF8.GetBytes($"LiteMonitor\n{version}\n{hash}\n");
        if (key.KeySize < 2048 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("The update package signature is invalid.");
    }
}
