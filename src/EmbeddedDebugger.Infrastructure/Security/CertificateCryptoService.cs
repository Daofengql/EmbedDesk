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
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace EmbeddedDebugger.Infrastructure.Security;

public enum CertificateKeyAlgorithm
{
    Rsa2048,
    EcdsaP256,
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

/// <summary>Creates and inspects X.509 material without invoking OpenSSL or another external process.</summary>
public static class CertificateCryptoService
{
    public static CertificateArtifact CreateSelfSigned(CertificateKeyAlgorithm algorithm, string subject,
        IReadOnlyCollection<string> subjectAlternativeNames, int validDays, bool certificateAuthority,
        bool serverAuthentication, bool clientAuthentication, string? pfxPassword = null)
    {
        ValidateSubjectAndDays(subject, validDays);
        var names = NormalizeNames(subjectAlternativeNames);
        return algorithm == CertificateKeyAlgorithm.Sm2
            ? CreateSm2Certificate(subject, names, validDays, certificateAuthority, serverAuthentication, clientAuthentication)
            : CreateDotNetCertificate(algorithm, subject, names, validDays, certificateAuthority, serverAuthentication, clientAuthentication, pfxPassword);
    }

    public static CertificateRequestArtifact CreateSigningRequest(CertificateKeyAlgorithm algorithm, string subject,
        IReadOnlyCollection<string> subjectAlternativeNames)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("主题不能为空。", nameof(subject));
        var names = NormalizeNames(subjectAlternativeNames);
        return algorithm == CertificateKeyAlgorithm.Sm2
            ? CreateSm2Request(subject, names)
            : CreateDotNetRequest(algorithm, subject, names);
    }

    public static byte[] IssueCertificate(byte[] requestDer, byte[] issuerCertificateDer, string issuerPrivateKeyPem,
        int validDays, string? subjectAlternativeNames = null)
    {
        if (requestDer.Length == 0 || issuerCertificateDer.Length == 0) throw new ArgumentException("CSR 和 CA 证书不能为空。");
        if (validDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(validDays), "有效期应为 1–3650 天。");
        using var issuerCertificate = new X509Certificate2(issuerCertificateDer);
        using var issuerKey = ImportDotNetPrivateKey(issuerPrivateKeyPem, issuerCertificate);
        var signatureGenerator = issuerKey switch
        {
            RSA rsa => X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => X509SignatureGenerator.CreateForECDsa(ecdsa),
            _ => throw new NotSupportedException("不支持的 CA 私钥类型。")
        };
        var request = CertificateRequest.LoadSigningRequest(requestDer, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        if (!string.IsNullOrWhiteSpace(subjectAlternativeNames))
        {
            var builder = new SubjectAlternativeNameBuilder();
            foreach (var name in NormalizeNames(subjectAlternativeNames.Split(',', StringSplitOptions.RemoveEmptyEntries))) AddSubjectAlternativeName(builder, name);
            request.CertificateExtensions.Add(builder.Build());
        }
        using var certificate = request.Create(issuerCertificate.SubjectName, signatureGenerator,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(validDays), RandomNumberGenerator.GetBytes(16));
        return certificate.Export(X509ContentType.Cert);
    }

    public static CertificateDetails InspectCertificate(byte[] der)
    {
        if (der.Length == 0) throw new ArgumentException("证书内容为空。", nameof(der));
        using var certificate = new X509Certificate2(der);
        var sans = certificate.Extensions.Cast<System.Security.Cryptography.X509Certificates.X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.17")?.Format(true) ?? "无";
        var usages = certificate.Extensions.Cast<System.Security.Cryptography.X509Certificates.X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.15")?.Format(true) ?? "未声明";
        var extended = certificate.Extensions.Cast<System.Security.Cryptography.X509Certificates.X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.37")?.Format(true) ?? "未声明";
        var keyAlgorithm = certificate.PublicKey.Oid?.FriendlyName ?? certificate.PublicKey.Oid?.Value ?? "未知";
        var keySize = GetPublicKeySize(certificate);
        var pem = certificate.ExportCertificatePem();
        return new(certificate.Subject, certificate.Issuer, certificate.SerialNumber,
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
            certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? "未知",
            keyAlgorithm, keySize, sans, usages, extended,
            Convert.ToHexString(SHA256.HashData(der)), pem,
            certificate.Extensions.Cast<System.Security.Cryptography.X509Certificates.X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.19") is X509BasicConstraintsExtension basic && basic.CertificateAuthority);
    }

    public static byte[] ReadCertificate(string textOrPath) => ReadPem(textOrPath, "CERTIFICATE");

    /// <summary>Reads a PEM block, Base64 text, or a DER file. Private keys are never logged or persisted by this method.</summary>
    public static byte[] ReadPem(string textOrPath, string label)
    {
        if (string.IsNullOrWhiteSpace(textOrPath)) throw new ArgumentException($"请输入 {label} PEM 或文件路径。");
        if (File.Exists(textOrPath)) return File.ReadAllBytes(textOrPath);
        var text = textOrPath.Trim();
        var beginMarker = $"-----BEGIN {label}-----";
        var endMarker = $"-----END {label}-----";
        if (text.Contains(beginMarker, StringComparison.Ordinal))
        {
            var begin = text.IndexOf(beginMarker, StringComparison.Ordinal);
            var start = begin + beginMarker.Length;
            var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (start < 0 || end < start) throw new FormatException("证书 PEM 边界无效。");
            return Convert.FromBase64String(new string(text[start..end].Where(c => !char.IsWhiteSpace(c)).ToArray()));
        }
        try { return Convert.FromBase64String(new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray())); }
        catch (FormatException) { throw new FormatException("请输入证书 PEM、Base64 或文件路径。"); }
    }

    public static string ComputeSm3Hex(ReadOnlySpan<byte> data)
    {
        var digest = new Org.BouncyCastle.Crypto.Digests.SM3Digest();
        var bytes = data.ToArray(); digest.BlockUpdate(bytes, 0, bytes.Length);
        var output = new byte[digest.GetDigestSize()]; digest.DoFinal(output, 0);
        return Convert.ToHexString(output);
    }

    public static byte[] Sm4Cbc(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, bool encrypt)
    {
        if (key.Length != 16 || iv.Length != 16) throw new ArgumentException("SM4 密钥和 IV 必须都是 16 字节。");
        var cipher = new Org.BouncyCastle.Crypto.Paddings.PaddedBufferedBlockCipher(
            new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.SM4Engine()));
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key.ToArray()), iv.ToArray()));
        var buffer = new byte[cipher.GetOutputSize(input.Length)];
        var count = cipher.ProcessBytes(input.ToArray(), 0, input.Length, buffer, 0);
        count += cipher.DoFinal(buffer, count);
        return buffer[..count];
    }

    public static string ToPem(string label, ReadOnlySpan<byte> der)
    {
        var base64 = Convert.ToBase64String(der.ToArray());
        var builder = new StringBuilder(base64.Length + 64);
        builder.Append("-----BEGIN ").Append(label).AppendLine("-----");
        for (var offset = 0; offset < base64.Length; offset += 64)
            builder.AppendLine(base64.Substring(offset, Math.Min(64, base64.Length - offset)));
        builder.Append("-----END ").Append(label).AppendLine("-----");
        return builder.ToString();
    }

    private static CertificateArtifact CreateDotNetCertificate(CertificateKeyAlgorithm algorithm, string subject, string[] names,
        int validDays, bool ca, bool server, bool client, string? pfxPassword)
    {
        using var key = CreateDotNetKey(algorithm);
        var request = CreateDotNetRequestBuilder(algorithm, subject, names, key);
        AddCommonExtensions(request, names, ca, server, client);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(validDays));
        var der = certificate.Export(X509ContentType.Cert);
        var material = new CertificateKeyMaterial(algorithm, ExportPrivateKeyPem(key), ExportPublicKeyPem(key));
        var pfx = pfxPassword is null ? null : certificate.Export(X509ContentType.Pfx, pfxPassword);
        return new(material, certificate.ExportCertificatePem(), der, pfx, subject,
            algorithm == CertificateKeyAlgorithm.Rsa2048 ? "RSA-2048 / SHA-256" : "ECDSA P-256 / SHA-256",
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
    }

    private static CertificateRequestArtifact CreateDotNetRequest(CertificateKeyAlgorithm algorithm, string subject, string[] names)
    {
        using var key = CreateDotNetKey(algorithm);
        var request = CreateDotNetRequestBuilder(algorithm, subject, names, key);
        AddCommonExtensions(request, names, false, true, true);
        var der = request.CreateSigningRequest();
        var material = new CertificateKeyMaterial(algorithm, ExportPrivateKeyPem(key), ExportPublicKeyPem(key));
        return new(material, ToPem("CERTIFICATE REQUEST", der), der, subject,
            algorithm == CertificateKeyAlgorithm.Rsa2048 ? "RSA-2048 / SHA-256" : "ECDSA P-256 / SHA-256");
    }

    private static CertificateRequest CreateDotNetRequestBuilder(CertificateKeyAlgorithm algorithm, string subject,
        string[] names, AsymmetricAlgorithm key)
    {
        var request = algorithm == CertificateKeyAlgorithm.Rsa2048
            ? new CertificateRequest(subject, (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)key, HashAlgorithmName.SHA256);
        if (names.Length > 0)
        {
            var builder = new SubjectAlternativeNameBuilder();
            foreach (var name in names) AddSubjectAlternativeName(builder, name);
            request.CertificateExtensions.Add(builder.Build());
        }
        return request;
    }

    private static void AddCommonExtensions(CertificateRequest request, string[] names, bool ca, bool server, bool client)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, ca, 0, true));
        var usage = ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment;
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        if (!ca && (server || client))
        {
            var eku = new OidCollection();
            if (server) eku.Add(new("1.3.6.1.5.5.7.3.1", "Server Authentication"));
            if (client) eku.Add(new("1.3.6.1.5.5.7.3.2", "Client Authentication"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        }
    }

    private sealed record Sm2KeyPair(CertificateKeyMaterial Material, AsymmetricKeyParameter PrivateKey, AsymmetricKeyParameter PublicKey);

    private static Sm2KeyPair CreateSm2Key()
    {
        var curve = GMNamedCurves.GetByName("sm2p256v1") ?? throw new CryptographicException("SM2 曲线不可用。");
        var parameters = new Org.BouncyCastle.Crypto.Parameters.ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H, curve.GetSeed());
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(parameters, new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = pair.Private;
        var publicKey = pair.Public;
        var privateDer = Org.BouncyCastle.Pkcs.PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey).GetEncoded();
        var publicDer = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(publicKey).GetEncoded();
        return new(new CertificateKeyMaterial(CertificateKeyAlgorithm.Sm2, ToPem("PRIVATE KEY", privateDer), ToPem("PUBLIC KEY", publicDer)), privateKey, publicKey);
    }

    private static CertificateRequestArtifact CreateSm2Request(string subject, string[] names)
    {
        var key = CreateSm2Key();
        Asn1Set? attributes = null;
        if (names.Length > 0)
        {
            var extensions = new Dictionary<DerObjectIdentifier, Org.BouncyCastle.Asn1.X509.X509Extension>
            {
                [X509Extensions.SubjectAlternativeName] = new Org.BouncyCastle.Asn1.X509.X509Extension(false,
                    new DerOctetString(new GeneralNames(names.Select(ToGeneralName).ToArray()).GetEncoded()))
            };
            var requested = new X509Extensions(extensions);
            attributes = new DerSet(new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(requested)));
        }
        var csr = new Pkcs10CertificationRequest("SM3WITHSM2", new X509Name(subject), key.PublicKey, attributes, key.PrivateKey);
        var der = csr.GetEncoded();
        return new(key.Material, ToPem("CERTIFICATE REQUEST", der), der, subject, "SM2 / SM3");
    }

    private static CertificateArtifact CreateSm2Certificate(string subject, string[] names, int validDays, bool ca, bool server, bool client)
    {
        var key = CreateSm2Key();
        var generator = new X509V3CertificateGenerator();
        var now = DateTime.UtcNow;
        generator.SetSerialNumber(new BigInteger(1, RandomNumberGenerator.GetBytes(16)));
        generator.SetIssuerDN(new X509Name(subject)); generator.SetSubjectDN(new X509Name(subject));
        generator.SetNotBefore(now.AddMinutes(-5)); generator.SetNotAfter(now.AddDays(validDays));
        generator.SetPublicKey(key.PublicKey);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(ca));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(ca ? KeyUsage.KeyCertSign | KeyUsage.CrlSign : KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
        if (!ca && (server || client))
        {
            var usages = new List<KeyPurposeID>();
            if (server) usages.Add(KeyPurposeID.id_kp_serverAuth);
            if (client) usages.Add(KeyPurposeID.id_kp_clientAuth);
            generator.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(usages));
        }
        if (names.Length > 0)
            generator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(names.Select(ToGeneralName).ToArray()));
        var certificate = generator.Generate(new Asn1SignatureFactory("SM3WITHSM2", key.PrivateKey));
        var der = certificate.GetEncoded();
        return new(key.Material, ToPem("CERTIFICATE", der), der, null, subject, "SM2 / SM3",
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
    }

    private static string ExportPrivateKeyPem(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => rsa.ExportPkcs8PrivateKeyPem(),
        ECDsa ecdsa => ecdsa.ExportPkcs8PrivateKeyPem(),
        _ => throw new InvalidOperationException("未知密钥类型")
    };

    private static string ExportPublicKeyPem(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => rsa.ExportSubjectPublicKeyInfoPem(),
        ECDsa ecdsa => ecdsa.ExportSubjectPublicKeyInfoPem(),
        _ => throw new InvalidOperationException("未知密钥类型")
    };

    private static IDisposable ImportDotNetPrivateKey(string pem, X509Certificate2 certificate)
    {
        if (certificate.GetRSAPublicKey() is not null)
        {
            var key = RSA.Create(); key.ImportFromPem(pem); return key;
        }
        if (certificate.GetECDsaPublicKey() is not null)
        {
            var key = ECDsa.Create(); key.ImportFromPem(pem); return key;
        }
        throw new NotSupportedException("当前 CA 证书不是 RSA 或 ECDSA，暂不支持使用它签发标准证书。");
    }

    private static AsymmetricAlgorithm CreateDotNetKey(CertificateKeyAlgorithm algorithm) => algorithm switch
    {
        CertificateKeyAlgorithm.Rsa2048 => RSA.Create(2048),
        CertificateKeyAlgorithm.EcdsaP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        _ => throw new ArgumentException("该算法属于 SM2，请使用国密实现。", nameof(algorithm))
    };

    private static string[] NormalizeNames(IEnumerable<string> names) => names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static GeneralName ToGeneralName(string value) => IPAddress.TryParse(value, out _) ? new GeneralName(GeneralName.IPAddress, value) : new GeneralName(GeneralName.DnsName, value);

    private static void AddSubjectAlternativeName(SubjectAlternativeNameBuilder builder, string value)
    {
        if (IPAddress.TryParse(value, out var address)) builder.AddIpAddress(address);
        else builder.AddDnsName(value);
    }

    private static string GetPublicKeySize(X509Certificate2 certificate)
    {
        try { if (certificate.GetRSAPublicKey() is { } rsa) return $"RSA {rsa.KeySize} bit"; }
        catch (CryptographicException) { }
        try { if (certificate.GetECDsaPublicKey() is { } ecdsa) return $"EC {ecdsa.KeySize} bit"; }
        catch (CryptographicException) { }
        return "SM2 / EC 256 bit";
    }

    private static void ValidateSubjectAndDays(string subject, int validDays)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("主题不能为空。", nameof(subject));
        if (validDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(validDays), "有效期应为 1–3650 天。");
    }
}
