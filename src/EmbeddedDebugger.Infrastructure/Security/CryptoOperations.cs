using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Security;

namespace EmbeddedDebugger.Infrastructure.Security;

/// <summary>Explicit algorithm allowlists for local interoperability tools.</summary>
public static class CryptoOperations
{
    public static IReadOnlyList<string> DigestAlgorithms { get; } = Array.AsReadOnly(new[]
        { "SM3", "MD5", "SHA-1", "SHA-224", "SHA-256", "SHA-384", "SHA-512", "SHA3-224", "SHA3-256", "SHA3-384", "SHA3-512" });
    public static IReadOnlyList<string> CipherAlgorithms { get; } = Array.AsReadOnly(new[]
        { "SM4-CBC", "SM4-ECB", "SM4-CTR", "SM4-GCM", "AES-GCM", "AES-CCM", "AES-CBC", "AES-CTR", "AES-ECB", "ChaCha20-Poly1305" });
    public static IReadOnlyList<string> SignatureAlgorithms { get; } = Array.AsReadOnly(new[]
        { "自动（按密钥）", "RSA-PSS-SHA224", "RSA-PSS-SHA256", "RSA-PSS-SHA384", "RSA-PSS-SHA512",
          "RSA-PKCS1-MD5", "RSA-PKCS1-SHA1", "RSA-PKCS1-SHA224", "RSA-PKCS1-SHA256", "RSA-PKCS1-SHA384", "RSA-PKCS1-SHA512",
          "RSA-PKCS1-SHA3-256", "RSA-PKCS1-SHA3-384", "RSA-PKCS1-SHA3-512",
          "ECDSA-SHA1", "ECDSA-SHA224", "ECDSA-SHA256", "ECDSA-SHA384", "ECDSA-SHA512",
          "ECDSA-SHA3-256", "ECDSA-SHA3-384", "ECDSA-SHA3-512", "Ed25519", "Ed448", "SM2-SM3" });
    public static IReadOnlyList<string> AsymmetricEncryptionAlgorithms { get; } = Array.AsReadOnly(new[]
        { "RSA-OAEP-SHA1", "RSA-OAEP-SHA224", "RSA-OAEP-SHA256", "RSA-OAEP-SHA384", "RSA-OAEP-SHA512", "SM2-C1C3C2", "SM2-C1C2C3" });

    public static byte[] Digest(string algorithm, byte[] input, byte[]? hmacKey = null)
    {
        CertificateCryptoService.CheckSize(input.Length);
        if (!DigestAlgorithms.Contains(algorithm)) throw new ArgumentException("不支持的摘要算法。");
        var digest = DigestUtilities.GetDigest(algorithm);
        if (hmacKey is not null)
        {
            CertificateCryptoService.CheckSize(hmacKey.Length);
            var mac = new HMac(digest); mac.Init(new KeyParameter(hmacKey));
            mac.BlockUpdate(input, 0, input.Length);
            var result = new byte[mac.GetMacSize()]; mac.DoFinal(result, 0); return result;
        }
        digest.BlockUpdate(input, 0, input.Length);
        var output = new byte[digest.GetDigestSize()]; digest.DoFinal(output, 0); return output;
    }

    /// <summary>AEAD output is ciphertext || 16-byte tag; CBC/ECB use PKCS#7, CTR uses no padding.</summary>
    public static byte[] Transform(string algorithm, byte[] input, byte[] key, byte[] iv, byte[] aad, bool encrypt)
    {
        CertificateCryptoService.CheckSize(input.Length); CertificateCryptoService.CheckSize(aad.Length);
        if (!CipherAlgorithms.Contains(algorithm)) throw new ArgumentException("不支持的加密算法。");
        var sm4 = algorithm.StartsWith("SM4", StringComparison.Ordinal);
        var chacha = algorithm == "ChaCha20-Poly1305";
        if ((sm4 && key.Length != 16) || (chacha && key.Length != 32) || (!sm4 && !chacha && key.Length is not (16 or 24 or 32)))
            throw new ArgumentException("SM4 密钥为 16 字节，AES 为 16/24/32 字节，ChaCha20 为 32 字节。");
        var aead = algorithm.EndsWith("GCM", StringComparison.Ordinal) || algorithm.EndsWith("CCM", StringComparison.Ordinal) || chacha;
        var ecb = algorithm.EndsWith("ECB", StringComparison.Ordinal);
        if (iv.Length != (ecb ? 0 : aead ? 12 : 16)) throw new ArgumentException(ecb ? "ECB 不使用 IV，请留空。" : aead ? "Nonce 必须为 12 字节。" : "IV 必须为 16 字节。");
        if (!aead && aad.Length != 0) throw new ArgumentException("只有 GCM / ChaCha20-Poly1305 接受 AAD。");
        if (aead && !encrypt && input.Length < 16) throw new ArgumentException("密文必须包含末尾 16 字节认证标签。");
        IBlockCipher engine = sm4 ? new SM4Engine() : new AesEngine();
        if (aead)
        {
            IAeadCipher cipher = chacha ? new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305()
                : algorithm.EndsWith("CCM", StringComparison.Ordinal) ? new CcmBlockCipher(engine) : new GcmBlockCipher(engine);
            cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv, aad));
            var output = new byte[cipher.GetOutputSize(input.Length)];
            try
            {
                var count = cipher.ProcessBytes(input, 0, input.Length, output, 0);
                count += cipher.DoFinal(output, count);
                return output[..count];
            }
            catch (InvalidCipherTextException)
            {
                CryptographicOperations.ZeroMemory(output);
                throw new CryptographicException("认证失败：密钥、Nonce、AAD 或密文不匹配。");
            }
        }
        IBufferedCipher buffered = algorithm.EndsWith("CTR", StringComparison.Ordinal)
            ? new BufferedBlockCipher(new SicBlockCipher(engine))
            : new PaddedBufferedBlockCipher(ecb ? engine : new CbcBlockCipher(engine), new Pkcs7Padding());
        buffered.Init(encrypt, ecb ? new KeyParameter(key) : new ParametersWithIV(new KeyParameter(key), iv));
        try { return buffered.DoFinal(input); }
        catch (InvalidCipherTextException) { throw new CryptographicException("解密失败：密钥、IV、填充或密文不匹配。"); }
    }

    public static byte[] Sign(byte[] input, string privateKeyPem, string algorithm = "自动（按密钥）", string sm2Id = "1234567812345678")
    {
        CertificateCryptoService.CheckSize(input.Length);
        var key = CertificateCryptoService.ReadKey(privateKeyPem, true);
        var signer = CreateSigner(key, algorithm, sm2Id, true);
        signer.BlockUpdate(input, 0, input.Length); return signer.GenerateSignature();
    }

    public static bool Verify(byte[] input, byte[] signature, string publicKeyPem, string algorithm = "自动（按密钥）", string sm2Id = "1234567812345678")
    {
        CertificateCryptoService.CheckSize(input.Length); CertificateCryptoService.CheckSize(signature.Length);
        var key = CertificateCryptoService.ReadKey(publicKeyPem, false);
        var signer = CreateSigner(key, algorithm, sm2Id, false);
        signer.BlockUpdate(input, 0, input.Length);
        try { return signer.VerifySignature(signature); }
        catch (Exception ex) when (ex is ArgumentException or Org.BouncyCastle.Crypto.CryptoException) { return false; }
    }

    private static ISigner CreateSigner(AsymmetricKeyParameter key, string algorithm, string sm2Id, bool signing)
    {
        if (!SignatureAlgorithms.Contains(algorithm)) throw new ArgumentException("不支持的签名算法。");
        var name = CertificateCryptoService.SignatureName(key);
        if (algorithm != "自动（按密钥）")
        {
            if (algorithm.StartsWith("RSA-", StringComparison.Ordinal))
            {
                if (key is not RsaKeyParameters) throw new ArgumentException("所选签名方案需要 RSA 密钥。");
                name = algorithm switch
                {
                    "RSA-PSS-SHA224" => "SHA224WITHRSAANDMGF1",
                    "RSA-PSS-SHA256" => "SHA256WITHRSAANDMGF1",
                    "RSA-PSS-SHA384" => "SHA384WITHRSAANDMGF1",
                    "RSA-PSS-SHA512" => "SHA512WITHRSAANDMGF1",
                    "RSA-PKCS1-MD5" => "MD5WITHRSA",
                    "RSA-PKCS1-SHA1" => "SHA1WITHRSA",
                    "RSA-PKCS1-SHA224" => "SHA224WITHRSA",
                    "RSA-PKCS1-SHA256" => "SHA256WITHRSA",
                    "RSA-PKCS1-SHA384" => "SHA384WITHRSA",
                    "RSA-PKCS1-SHA512" => "SHA512WITHRSA",
                    "RSA-PKCS1-SHA3-256" => "SHA3-256WITHRSA",
                    "RSA-PKCS1-SHA3-384" => "SHA3-384WITHRSA",
                    "RSA-PKCS1-SHA3-512" => "SHA3-512WITHRSA",
                    _ => throw new ArgumentException("不支持的 RSA 签名方案。")
                };
            }
            else if (algorithm.StartsWith("ECDSA-", StringComparison.Ordinal))
            {
                if (key is not ECKeyParameters || CertificateCryptoService.SignatureName(key) == "SM3WITHSM2")
                    throw new ArgumentException("所选签名方案需要 NIST ECDSA 密钥。");
                name = algorithm[6..] + "WITHECDSA";
            }
            else if (algorithm is "Ed25519" or "Ed448")
            {
                if (CertificateCryptoService.SignatureName(key) != algorithm) throw new ArgumentException($"所选签名方案需要 {algorithm} 密钥。");
                name = algorithm;
            }
            else if (algorithm == "SM2-SM3")
            {
                if (CertificateCryptoService.SignatureName(key) != "SM3WITHSM2") throw new ArgumentException("所选签名方案需要 SM2 密钥。");
                name = "SM3WITHSM2";
            }
        }
        var signer = SignerUtilities.GetSigner(name);
        ICipherParameters parameters = key;
        if (name == "SM3WITHSM2")
        {
            var id = Encoding.UTF8.GetBytes(sm2Id);
            if (id.Length > 8191) throw new ArgumentException("SM2 ID 最多 8191 字节。");
            parameters = new ParametersWithID(key, id);
        }
        signer.Init(signing, parameters); return signer;
    }

    public static byte[] AsymmetricTransform(string algorithm, byte[] input, string pem, bool encrypt)
    {
        CertificateCryptoService.CheckSize(input.Length);
        var key = CertificateCryptoService.ReadKey(pem, !encrypt);
        if (algorithm.StartsWith("RSA-OAEP-", StringComparison.Ordinal))
        {
            if (key is not RsaKeyParameters) throw new ArgumentException("需要 RSA 密钥。");
            var digestName = algorithm[9..];
            var digest = digestName switch
            {
                "SHA1" => "SHA-1", "SHA224" => "SHA-224", "SHA256" => "SHA-256",
                "SHA384" => "SHA-384", "SHA512" => "SHA-512",
                _ => throw new ArgumentException("不支持的 RSA-OAEP 摘要算法。")
            };
            var cipher = new OaepEncoding(new RsaEngine(), DigestUtilities.GetDigest(digest));
            cipher.Init(encrypt, encrypt ? new ParametersWithRandom(key, new SecureRandom()) : key);
            if (input.Length > cipher.GetInputBlockSize()) throw new ArgumentException("输入超过 RSA-OAEP 单块容量。");
            return cipher.ProcessBlock(input, 0, input.Length);
        }
        if (!AsymmetricEncryptionAlgorithms.Contains(algorithm) || algorithm is not ("SM2-C1C3C2" or "SM2-C1C2C3")) throw new ArgumentException("不支持的非对称加密算法。");
        if (CertificateCryptoService.SignatureName(key) != "SM3WITHSM2") throw new ArgumentException("需要 SM2 密钥。");
        if (encrypt && input.Length == 0) throw new ArgumentException("SM2 明文不能为空。");
        var sm2 = new SM2Engine(algorithm == "SM2-C1C3C2" ? SM2Engine.Mode.C1C3C2 : SM2Engine.Mode.C1C2C3);
        sm2.Init(encrypt, encrypt ? new ParametersWithRandom(key, new SecureRandom()) : key);
        return sm2.ProcessBlock(input, 0, input.Length);
    }
}
