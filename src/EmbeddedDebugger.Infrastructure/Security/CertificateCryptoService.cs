using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.GM;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace EmbeddedDebugger.Infrastructure.Security;

public enum CertificateKeyAlgorithm
{
    Rsa2048,
    Rsa3072,
    Rsa4096,
    EcdsaP256,
    EcdsaP384,
    EcdsaP521,
    Ed25519,
    Ed448,
    Sm2
}

public sealed record CertificateKeyMaterial(
    CertificateKeyAlgorithm Algorithm,
    string PrivateKeyPem,
    string PublicKeyPem);

public sealed record CertificateArtifact(
    CertificateKeyMaterial Key,
    string CertificatePem,
    byte[] CertificateDer,
    byte[]? Pkcs12,
    string Subject,
    string Algorithm,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

public sealed record CertificateRequestArtifact(
    CertificateKeyMaterial Key,
    string RequestPem,
    byte[] RequestDer,
    string Subject,
    string Algorithm);

public sealed record CertificateDetails(
    string Subject,
    string Issuer,
    string SerialNumber,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string SignatureAlgorithm,
    string PublicKeyAlgorithm,
    string PublicKeySize,
    string SubjectAlternativeNames,
    string KeyUsage,
    string ExtendedKeyUsage,
    string Sha256,
    string Pem,
    bool IsCertificateAuthority);

/// <summary>Local X.509 operations. No system trust-store changes or implicit persistence.</summary>
public static class CertificateCryptoService
{
    public const int MaxInputBytes = 4 * 1024 * 1024;

    public static CertificateKeyMaterial GenerateKey(CertificateKeyAlgorithm algorithm)
    {
        if (algorithm is CertificateKeyAlgorithm.Rsa2048 or CertificateKeyAlgorithm.Rsa3072 or CertificateKeyAlgorithm.Rsa4096)
        {
            using var rsa = RSA.Create(algorithm == CertificateKeyAlgorithm.Rsa2048 ? 2048 : algorithm == CertificateKeyAlgorithm.Rsa3072 ? 3072 : 4096);
            return new(algorithm, rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
        }
        if (algorithm is CertificateKeyAlgorithm.EcdsaP256 or CertificateKeyAlgorithm.EcdsaP384 or CertificateKeyAlgorithm.EcdsaP521)
        {
            using var ec = ECDsa.Create(algorithm == CertificateKeyAlgorithm.EcdsaP256 ? ECCurve.NamedCurves.nistP256 :
                algorithm == CertificateKeyAlgorithm.EcdsaP384 ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP521);
            return new(algorithm, ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem());
        }
        var random = new SecureRandom();
        AsymmetricKeyParameter privateKey;
        AsymmetricKeyParameter publicKey;
        if (algorithm == CertificateKeyAlgorithm.Sm2)
        {
            var generator = new ECKeyPairGenerator();
            generator.Init(new ECKeyGenerationParameters(GMObjectIdentifiers.sm2p256v1, random));
            var pair = generator.GenerateKeyPair();
            privateKey = pair.Private; publicKey = pair.Public;
        }
        else if (algorithm == CertificateKeyAlgorithm.Ed25519)
        {
            var key = new Ed25519PrivateKeyParameters(random); privateKey = key; publicKey = key.GeneratePublicKey();
        }
        else if (algorithm == CertificateKeyAlgorithm.Ed448)
        {
            var key = new Ed448PrivateKeyParameters(random); privateKey = key; publicKey = key.GeneratePublicKey();
        }
        else throw new ArgumentOutOfRangeException(nameof(algorithm));
        return new(algorithm, ToPem("PRIVATE KEY", PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey).GetEncoded()),
            ToPem("PUBLIC KEY", SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(publicKey).GetEncoded()));
    }

    public static CertificateArtifact CreateSelfSigned(CertificateKeyAlgorithm algorithm, string subject,
        IReadOnlyCollection<string> subjectAlternativeNames, int validDays, bool certificateAuthority,
        bool serverAuthentication, bool clientAuthentication, string? pfxPassword = null)
    {
        ValidateSubjectAndDays(subject, validDays);
        var names = NormalizeNames(subjectAlternativeNames);
        var key = GenerateKey(algorithm);
        var publicKey = ReadKey(key.PublicKeyPem, false);
        var generator = NewCertificate(new X509Name(subject), new X509Name(subject), publicKey,
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddDays(validDays));
        AddExtensions(generator, publicKey, names, certificateAuthority, serverAuthentication, clientAuthentication);
        generator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, X509ExtensionUtilities.CreateAuthorityKeyIdentifier(publicKey));
        var certificate = generator.Generate(new Asn1SignatureFactory(SignatureName(publicKey), ReadKey(key.PrivateKeyPem, true)));
        var der = certificate.GetEncoded();
        return new(key, ToPem("CERTIFICATE", der), der,
            pfxPassword is null ? null : ExportPkcs12(der, key.PrivateKeyPem, pfxPassword),
            certificate.SubjectDN.ToString(), certificate.SigAlgName, certificate.NotBefore, certificate.NotAfter);
    }

    public static CertificateRequestArtifact CreateSigningRequest(CertificateKeyAlgorithm algorithm, string subject,
        IReadOnlyCollection<string> subjectAlternativeNames)
    {
        ValidateSubjectAndDays(subject, 1);
        var names = NormalizeNames(subjectAlternativeNames);
        var key = GenerateKey(algorithm);
        var extensions = new X509ExtensionsGenerator();
        if (names.Length > 0)
            extensions.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(names.Select(ToGeneralName).ToArray()));
        var attributes = new DerSet(new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extensions.Generate())));
        var publicKey = ReadKey(key.PublicKeyPem, false);
        var signature = SignatureName(publicKey);
        var csr = new Pkcs10CertificationRequest(signature, new X509Name(subject), publicKey, attributes, ReadKey(key.PrivateKeyPem, true));
        var der = csr.GetEncoded();
        return new(key, ToPem("CERTIFICATE REQUEST", der), der, subject, signature);
    }

    // Issue end-entity certificates only: CSR extensions must never silently confer CA authority.
    public static byte[] IssueCertificate(byte[] requestDer, byte[] issuerCertificateDer, string issuerPrivateKeyPem,
        int validDays, string? subjectAlternativeNames = null)
    {
        if (validDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(validDays), "有效期应为 1–3650 天。");
        CheckSize(requestDer.Length); CheckSize(issuerCertificateDer.Length);
        var issuer = new X509CertificateParser().ReadCertificate(issuerCertificateDer);
        var privateKey = ReadKey(issuerPrivateKeyPem, true);
        issuer.CheckValidity();
        var usage = issuer.GetKeyUsage();
        if (issuer.GetBasicConstraints() < 0 || (usage is not null && !usage[5]))
            throw new CryptographicException("签发者必须为允许签发证书的 CA。");
        EnsureKeyMatches(privateKey, issuer.GetPublicKey());
        var request = new Pkcs10CertificationRequest(requestDer);
        if (!request.Verify()) throw new CryptographicException("CSR 签名无效。");
        var publicKey = request.GetPublicKey();
        _ = SignatureName(publicKey); // Reject unsupported key families before issuing.
        var requested = request.GetRequestedExtensions();
        if (requested is not null)
        {
            var basic = requested.GetExtension(X509Extensions.BasicConstraints);
            if (basic is not null && BasicConstraints.GetInstance(basic.GetParsedValue()).IsCA())
                throw new CryptographicException("本工具仅签发终端证书，不接受申请 CA 权限的 CSR。");
            foreach (var oid in requested.ExtensionOids)
                if (requested.GetExtension(oid).IsCritical && oid != X509Extensions.SubjectAlternativeName)
                    throw new CryptographicException("CSR 含未支持的关键扩展。");
        }
        var now = DateTime.UtcNow;
        var end = now.AddDays(validDays);
        if (end > issuer.NotAfter) throw new ArgumentException("签发有效期不能超过 CA 到期时间。");
        var start = now.AddMinutes(-5) < issuer.NotBefore ? issuer.NotBefore : now.AddMinutes(-5);
        var generator = NewCertificate(issuer.SubjectDN, request.GetCertificationRequestInfo().Subject, publicKey, start, end);
        var names = string.IsNullOrWhiteSpace(subjectAlternativeNames) ? [] : NormalizeNames(subjectAlternativeNames.Split(','));
        AddExtensions(generator, publicKey, names, false, true, true);
        if (names.Length == 0 && requested?.GetExtension(X509Extensions.SubjectAlternativeName) is { } san)
            generator.AddExtension(X509Extensions.SubjectAlternativeName, san);
        generator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, X509ExtensionUtilities.CreateAuthorityKeyIdentifier(issuer));
        return generator.Generate(new Asn1SignatureFactory(SignatureName(issuer.GetPublicKey()), privateKey)).GetEncoded();
    }

    public static byte[] ExportPkcs12(byte[] certificateDer, string privateKeyPem, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("PFX 导出需要设置密码。");
        var certificate = new X509CertificateParser().ReadCertificate(certificateDer);
        var key = ReadKey(privateKeyPem, true);
        EnsureKeyMatches(key, certificate.GetPublicKey());
        var store = new Pkcs12StoreBuilder()
            .SetKeyAlgorithm(Org.BouncyCastle.Asn1.Nist.NistObjectIdentifiers.IdAes256Cbc, PkcsObjectIdentifiers.IdHmacWithSha256)
            .SetCertAlgorithm(Org.BouncyCastle.Asn1.Nist.NistObjectIdentifiers.IdAes256Cbc, PkcsObjectIdentifiers.IdHmacWithSha256)
            .Build();
        store.SetKeyEntry("certificate", new AsymmetricKeyEntry(key), [new X509CertificateEntry(certificate)]);
        using var output = new MemoryStream();
        var chars = password.ToCharArray();
        try { store.Save(output, chars, new SecureRandom()); return output.ToArray(); }
        finally { Array.Clear(chars); }
    }

    public static (string CertificatePem, string PrivateKeyPem) ImportPkcs12(byte[] bytes, string password)
    {
        CheckSize(bytes.Length);
        var store = new Pkcs12StoreBuilder().Build();
        using var input = new MemoryStream(bytes, false);
        var chars = password.ToCharArray();
        try { store.Load(input, chars); }
        finally { Array.Clear(chars); }
        var keys = store.Aliases.Where(store.IsKeyEntry).ToArray();
        if (keys.Length != 1) throw new ArgumentException("PFX 必须包含且仅包含一个私钥条目。");
        var key = store.GetKey(keys[0]).Key;
        var chain = store.GetCertificateChain(keys[0]);
        if (chain is null || chain.Length == 0) throw new ArgumentException("PFX 缺少证书。");
        EnsureKeyMatches(key, chain[0].Certificate.GetPublicKey());
        return (string.Concat(chain.Select(c => ToPem("CERTIFICATE", c.Certificate.GetEncoded()))),
            ToPem("PRIVATE KEY", PrivateKeyInfoFactory.CreatePrivateKeyInfo(key).GetEncoded()));
    }

    public static CertificateDetails InspectCertificate(byte[] der)
    {
        CheckSize(der.Length);
        var certificate = new X509CertificateParser().ReadCertificate(der);
        var key = certificate.GetPublicKey();
        var names = certificate.GetSubjectAlternativeNames();
        var usage = certificate.GetKeyUsage();
        string[] usageNames = ["DigitalSignature", "NonRepudiation", "KeyEncipherment", "DataEncipherment", "KeyAgreement", "KeyCertSign", "CrlSign", "EncipherOnly", "DecipherOnly"];
        return new(certificate.SubjectDN.ToString(), certificate.IssuerDN.ToString(), certificate.SerialNumber.ToString(16),
            certificate.NotBefore, certificate.NotAfter, certificate.SigAlgName, PublicKeyAlgorithmName(key),
            key switch { RsaKeyParameters rsa => $"{rsa.Modulus.BitLength} bit", ECKeyParameters ec => $"{ec.Parameters.N.BitLength} bit",
                Ed25519PublicKeyParameters => "256 bit", Ed448PublicKeyParameters => "448 bit", _ => "未知" },
            names is null ? "无" : string.Join(", ", names.Select(n => n[1])),
            usage is null ? "未声明" : string.Join(", ", usageNames.Where((_, i) => i < usage.Length && usage[i])),
            string.Join(", ", certificate.GetExtendedKeyUsage()?.Select(o => o.Id) ?? []),
            Convert.ToHexString(SHA256.HashData(certificate.GetEncoded())), ToPem("CERTIFICATE", certificate.GetEncoded()), certificate.GetBasicConstraints() >= 0);
    }

    public static byte[] ReadCertificate(string textOrPath) => ReadPem(textOrPath, "CERTIFICATE");
    public static byte[] ReadPem(string textOrPath, string label)
    {
        var text = textOrPath.Trim();
        if (File.Exists(text))
        {
            var bytes = ReadFile(text);
            if (bytes.Length > 0 && bytes[0] == 0x30) return bytes;
            text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Trim();
        }
        CheckSize(Encoding.UTF8.GetByteCount(text));
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            var fields = PemEncoding.Find(text);
            if (text[fields.Label] != label) throw new FormatException($"需要 {label} PEM。");
            return Convert.FromBase64String(text[fields.Base64Data]);
        }
        return Convert.FromBase64String(text);
    }

    public static byte[] ReadFile(string path)
    {
        using var file = File.OpenRead(path);
        CheckSize(checked((int)Math.Min(file.Length, int.MaxValue)));
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    public static string ReadTextOrPath(string text) => File.Exists(text.Trim()) ? Encoding.UTF8.GetString(ReadFile(text.Trim())) : text;
    public static string ToPem(string label, ReadOnlySpan<byte> der) => PemEncoding.WriteString(label, der);
    public static string ComputeSm3Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(CryptoOperations.Digest("SM3", data.ToArray()));
    public static byte[] Sm4Cbc(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, bool encrypt)
        => CryptoOperations.Transform("SM4-CBC", input.ToArray(), key.ToArray(), iv.ToArray(), [], encrypt);

    internal static AsymmetricKeyParameter ReadKey(string pem, bool privateKey)
    {
        var text = ReadTextOrPath(pem);
        CheckSize(Encoding.UTF8.GetByteCount(text));
        using var reader = new StringReader(text);
        var value = new PemReader(reader).ReadObject();
        var key = value switch { AsymmetricCipherKeyPair pair => privateKey ? pair.Private : pair.Public,
            BcX509Certificate certificate when !privateKey => certificate.GetPublicKey(),
            AsymmetricKeyParameter parameter => parameter, _ => throw new ArgumentException("需要未加密的 PEM 密钥或公钥证书。") };
        if (key.IsPrivate != privateKey) throw new ArgumentException(privateKey ? "需要私钥。" : "验签或加密需要公钥 / 证书。");
        return key;
    }

    internal static string SignatureName(AsymmetricKeyParameter key) => key switch
    {
        RsaKeyParameters => "SHA256WITHRSA",
        ECKeyParameters ec when ec.Parameters.Equals(Sm2Domain) => "SM3WITHSM2",
        ECKeyParameters ec when ec.Parameters.N.BitLength <= 256 => "SHA256WITHECDSA",
        ECKeyParameters ec when ec.Parameters.N.BitLength <= 384 => "SHA384WITHECDSA",
        ECKeyParameters => "SHA512WITHECDSA",
        Ed25519PrivateKeyParameters or Ed25519PublicKeyParameters => "Ed25519",
        Ed448PrivateKeyParameters or Ed448PublicKeyParameters => "Ed448",
        _ => throw new NotSupportedException("不支持该密钥算法。")
    };
    private static string PublicKeyAlgorithmName(AsymmetricKeyParameter key) => key switch
    {
        RsaKeyParameters => "RSA",
        ECKeyParameters ec when ec.Parameters.Equals(Sm2Domain) => "SM2",
        ECKeyParameters => "ECDSA",
        Ed25519PrivateKeyParameters or Ed25519PublicKeyParameters => "Ed25519",
        Ed448PrivateKeyParameters or Ed448PublicKeyParameters => "Ed448",
        _ => "未知"
    };
    private static readonly ECDomainParameters Sm2Domain = new(GMNamedCurves.GetByName("sm2p256v1"));
    internal static void EnsureKeyMatches(AsymmetricKeyParameter privateKey, AsymmetricKeyParameter publicKey)
    {
        if (SignatureName(privateKey) != SignatureName(publicKey)) throw new CryptographicException("私钥和证书不匹配。");
        var challenge = RandomNumberGenerator.GetBytes(32);
        var signer = SignerUtilities.GetSigner(SignatureName(publicKey));
        signer.Init(true, privateKey); signer.BlockUpdate(challenge, 0, challenge.Length);
        var signature = signer.GenerateSignature();
        signer.Init(false, publicKey); signer.BlockUpdate(challenge, 0, challenge.Length);
        if (!signer.VerifySignature(signature)) throw new CryptographicException("私钥和证书不匹配。");
    }
    internal static void CheckSize(int length)
    {
        if (length > MaxInputBytes) throw new ArgumentException("输入超过 4 MiB 上限。");
    }
    private static X509V3CertificateGenerator NewCertificate(X509Name issuer, X509Name subject, AsymmetricKeyParameter key, DateTime start, DateTime end)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new BigInteger(1, RandomNumberGenerator.GetBytes(16)).Add(BigInteger.One));
        generator.SetIssuerDN(issuer); generator.SetSubjectDN(subject); generator.SetPublicKey(key);
        generator.SetNotBefore(start); generator.SetNotAfter(end);
        return generator;
    }
    private static void AddExtensions(X509V3CertificateGenerator generator, AsymmetricKeyParameter key, string[] names, bool ca, bool server, bool client)
    {
        generator.AddExtension(X509Extensions.BasicConstraints, true, ca ? new BasicConstraints(0) : new BasicConstraints(false));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(ca ? KeyUsage.KeyCertSign | KeyUsage.CrlSign :
            KeyUsage.DigitalSignature | (key is RsaKeyParameters ? KeyUsage.KeyEncipherment : 0)));
        generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, X509ExtensionUtilities.CreateSubjectKeyIdentifier(key));
        if (!ca && (server || client))
        {
            var usage = new List<KeyPurposeID>();
            if (server) usage.Add(KeyPurposeID.id_kp_serverAuth);
            if (client) usage.Add(KeyPurposeID.id_kp_clientAuth);
            generator.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(usage));
        }
        if (names.Length > 0) generator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(names.Select(ToGeneralName).ToArray()));
    }
    private static string[] NormalizeNames(IEnumerable<string> names)
    {
        var values = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Use the platform validator so malformed DNS / IP input fails before key generation.
        var validator = new SubjectAlternativeNameBuilder();
        foreach (var name in values) { if (IPAddress.TryParse(name, out var ip)) validator.AddIpAddress(ip); else validator.AddDnsName(name); }
        return values;
    }
    private static GeneralName ToGeneralName(string value) => new(IPAddress.TryParse(value, out _) ? GeneralName.IPAddress : GeneralName.DnsName, value);
    private static void ValidateSubjectAndDays(string subject, int days)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("主题不能为空。");
        _ = new X509Name(subject);
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days), "有效期应为 1–3650 天。");
    }
}
