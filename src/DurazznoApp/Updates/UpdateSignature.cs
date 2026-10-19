using System.Security.Cryptography;

namespace Durazzno.Updates;

/// <summary>
/// Signed update manifests. publish-main.ps1 signs manifest.json with a private RSA key that never leaves the publisher's
/// PC (publisher/crypto/update-signing-key.xml, git-ignored; see publisher/crypto/README.md) and uploads the signature
/// as manifest.json.sig next to it. The app only accepts a manifest whose signature verifies against the public key
/// below, so a tampered manifest (server, network, plain HTTP) cannot point Durazzno at another exe. The public key
/// can be public: it verifies signatures, it cannot make them.
/// </summary>
internal static class UpdateSignature
{
    private const string PublicKey =
        "<RSAKeyValue><Modulus>yX69DyGX/yX8Eu/gNgrVTOsQQYjn6ppNo2h4cOiRsBHcJ6jZ65p466r8Y8izMX3i4/7PtfNqjYq3TS+UlPAYkKvMNa8jODRfw55TP9GRyy2apMXJp1yjiB9wX9xfOUHrPhNAbpfOE5SAiW8lDP5aBujH3v18bkMwKUtDcP3htB/jlTLAx1pqIe1NRbpu/3TUFqytO/KuH1dRY2xvdG3QdNbjdEo3IlmgJqYqcJ1Yz0t82GAck9qwczUsxc1Mj1DDwdSgZ4IGbAitovzCsj9rpa9mh7tuIe6cNYtZ3clOTVj1LME2Rfa/rS86r8SLqgtnyjNTyAEa+pv9EqdLZTuOWZqUlsfdUVCpXhA9X7BXjeWLedT8Y67BXXknQV8HlX1no5ADXLs4LHklX1KlYK0QiXI8F5WjAOvkdsFBcrmkYtAOk1jC4RV1+lMXT37EqfxWlvMTqtlbP7iCwDzoxW7urgA1dMWqOP5lNKRr7hueEbICUQILhtD+5DBixajaks5R</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    /// <summary>True when <paramref name="signatureBase64"/> is a valid RSA-SHA256 signature of the manifest bytes.</summary>
    public static bool IsValid(byte[] manifest, string signatureBase64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(PublicKey);
            return rsa.VerifyData(manifest, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
