using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Infrastructure.Security;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Service = EmbedDesk.Infrastructure.Security.CertificateCryptoService;

namespace EmbedDesk.App.SmokeTests;

internal static class CryptoChecks
{
    public static async Task RunAsync()
    {
        await Task.Run(CheckAlgorithms);
        await CheckUiAsync();
    }

    private static void CheckAlgorithms()
    {
        var message = Encoding.UTF8.GetBytes("local interoperability check");
        var parser = new X509CertificateParser();
        foreach (var algorithm in Enum.GetValues<CertificateKeyAlgorithm>())
        {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var ca = Service.CreateSelfSigned(algorithm, "CN=local-test-ca", [], 5, true, false, false, password);
            var certificate = parser.ReadCertificate(ca.CertificateDer);
            certificate.Verify(certificate.GetPublicKey());
            Require(Service.InspectCertificate(ca.CertificateDer).IsCertificateAuthority, $"{algorithm} self signature / CA");
            var request = Service.CreateSigningRequest(algorithm, "CN=localhost", ["localhost", "127.0.0.1"]);
            Require(new Pkcs10CertificationRequest(request.RequestDer).Verify(), $"{algorithm} CSR signature");
            var issued = Service.IssueCertificate(request.RequestDer, ca.CertificateDer, ca.Key.PrivateKeyPem, 2);
            parser.ReadCertificate(issued).Verify(certificate.GetPublicKey());
            var details = Service.InspectCertificate(issued);
            Require(!details.IsCertificateAuthority && details.SubjectAlternativeNames.Contains("localhost", StringComparison.Ordinal), $"{algorithm} CA issuance preserves SAN and leaf constraints");
            var signature = CryptoOperations.Sign(message, request.Key.PrivateKeyPem);
            Require(CryptoOperations.Verify(message, signature, request.Key.PublicKeyPem), $"{algorithm} signature");
            Require(!CryptoOperations.Verify([0], signature, request.Key.PublicKeyPem), $"{algorithm} modified message rejected");
            var container = Service.ImportPkcs12(ca.Pkcs12!, password);
            Require(Service.ReadCertificate(container.CertificatePem).SequenceEqual(ca.CertificateDer), $"{algorithm} encrypted PFX round trip");
            Require(CryptoOperations.Verify(message, CryptoOperations.Sign(message, container.PrivateKeyPem), ca.Key.PublicKeyPem), $"{algorithm} PFX key matches");
            Reject(() => Service.ImportPkcs12(ca.Pkcs12!, password + "!"), $"{algorithm} wrong PFX password");
            Reject(() => Service.IssueCertificate(request.RequestDer, ca.CertificateDer, request.Key.PrivateKeyPem, 2), $"{algorithm} wrong CA key");
            Reject(() => Service.IssueCertificate(request.RequestDer, ca.CertificateDer, ca.Key.PrivateKeyPem, 10), $"{algorithm} excessive validity");
            var tampered = (byte[])request.RequestDer.Clone(); tampered[^1] ^= 1;
            Reject(() => Service.IssueCertificate(tampered, ca.CertificateDer, ca.Key.PrivateKeyPem, 2), $"{algorithm} tampered CSR");
            Reject(() => Service.IssueCertificate(request.RequestDer, issued, request.Key.PrivateKeyPem, 1), $"{algorithm} non-CA issuer");
            if (algorithm == CertificateKeyAlgorithm.Sm2)
            {
                Require(!CryptoOperations.Verify(message, signature, request.Key.PublicKeyPem, sm2Id: "different-id"), "SM2 ID changes signature verification");
                foreach (var mode in new[] { "SM2-C1C3C2", "SM2-C1C2C3" })
                {
                    var cipher = CryptoOperations.AsymmetricTransform(mode, message, request.Key.PublicKeyPem, true);
                    Require(CryptoOperations.AsymmetricTransform(mode, cipher, request.Key.PrivateKeyPem, false).SequenceEqual(message), mode);
                    cipher[^1] ^= 1;
                    Reject(() => CryptoOperations.AsymmetricTransform(mode, cipher, request.Key.PrivateKeyPem, false), mode + " tampered");
                }
            }
            if (algorithm == CertificateKeyAlgorithm.Rsa2048)
            {
                using var dotnet = new X509Certificate2(ca.Pkcs12!, password, X509KeyStorageFlags.EphemeralKeySet);
                Require(dotnet.HasPrivateKey, ".NET imports AES-protected PFX");
                using var rsa = RSA.Create(); rsa.ImportFromPem(request.Key.PublicKeyPem);
                foreach (var scheme in new[] { "RSA-PSS-SHA256", "RSA-PSS-SHA384", "RSA-PSS-SHA512", "RSA-PKCS1-SHA256", "RSA-PKCS1-SHA384", "RSA-PKCS1-SHA512" })
                {
                    var signed = CryptoOperations.Sign(message, request.Key.PrivateKeyPem, scheme);
                    var hash = new HashAlgorithmName(scheme.Split('-')[2]);
                    Require(rsa.VerifyData(message, signed, hash, scheme.Contains("PSS", StringComparison.Ordinal) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1), scheme + " .NET interoperability");
                }
                var encrypted = CryptoOperations.AsymmetricTransform("RSA-OAEP-SHA256", message, request.Key.PublicKeyPem, true);
                using var privateRsa = RSA.Create(); privateRsa.ImportFromPem(request.Key.PrivateKeyPem);
                Require(privateRsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256).SequenceEqual(message), "OAEP .NET interoperability");
                Reject(() => CryptoOperations.AsymmetricTransform("RSA-OAEP-SHA256", new byte[191], request.Key.PublicKeyPem, true), "OAEP oversized input");
                var extensions = new X509ExtensionsGenerator();
                extensions.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
                var caRequest = new Pkcs10CertificationRequest("SHA256WITHRSA", new X509Name("CN=escalation-test"),
                    PublicKeyFactory.CreateKey(Service.ReadPem(request.Key.PublicKeyPem, "PUBLIC KEY")),
                    new DerSet(new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extensions.Generate()))),
                    PrivateKeyFactory.CreateKey(Service.ReadPem(request.Key.PrivateKeyPem, "PRIVATE KEY")));
                Reject(() => Service.IssueCertificate(caRequest.GetEncoded(), ca.CertificateDer, ca.Key.PrivateKeyPem, 2), "CSR cannot request CA escalation");
            }
            if (algorithm == CertificateKeyAlgorithm.EcdsaP384)
            {
                using var ec = ECDsa.Create(); ec.ImportFromPem(request.Key.PublicKeyPem);
                Require(ec.VerifyData(message, signature, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence), "ECDSA DER signature .NET interoperability");
            }
        }
        var abc = Encoding.ASCII.GetBytes("abc");
        Require(Convert.ToHexString(CryptoOperations.Digest("SM3", abc)) == "66C7F0F462EEEDD9D1F2D46BDC10E4E24167C4875CF2F7A2297DA02B8F4BA8E0", "SM3 standard vector");
        Require(Convert.ToHexString(CryptoOperations.Digest("SHA3-256", abc)) == "3A985DA74FE225B2045C172D6BD390BD855F086E3E9D525B46BFE24511431532", "SHA3-256 FIPS vector");
        Require(Convert.ToHexString(CryptoOperations.Digest("SHA-256", Encoding.ASCII.GetBytes("Hi There"), Enumerable.Repeat((byte)0x0b, 20).ToArray())) ==
            "B0344C61D8DB38535CA8AFCEAF0BF12B881DC200C9833DA726E9376C2E32CFF7", "RFC4231 HMAC vector");
        foreach (var digest in CryptoOperations.DigestAlgorithms.Where(value => value is not ("MD5" or "SHA-1")))
            Require(CryptoOperations.Digest(digest, []).Length >= 28 && CryptoOperations.Digest(digest, abc, []).Length >= 28, digest + " empty digest / HMAC");
        // RFC8032 public verification vector; no fixture private key is stored.
        var edPublic = PublicKeyFactory.CreateKey(Convert.FromHexString("302A300506032B6570032100D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"));
        Require(CryptoOperations.Verify([], Convert.FromHexString("E5564300C360AC729086E2CC806E828A84877F1EB8E5D974D873E0652249015555FB8821590A33BACC61E39701CF9B46BD25BF5F0595BBE24655141438E7A100B"),
            Service.ToPem("PUBLIC KEY", SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(edPublic).GetEncoded())), "Ed25519 RFC8032 vector");
        Require(Convert.ToHexString(CryptoOperations.Transform("AES-GCM", new byte[16], new byte[16], new byte[12], [], true)) ==
            "0388DACE60B6A392F328C2B971B2FE78AB6E47D42CEC13BDF53A67B21257BDDF", "AES-GCM NIST vector");
        var sm4 = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
        Require(Convert.ToHexString(CryptoOperations.Transform("SM4-ECB", sm4, sm4, [], [], true)).StartsWith("681EDF34D206965E86B3E94F536E4246", StringComparison.Ordinal), "SM4 GB/T vector");
        foreach (var algorithm in CryptoOperations.CipherAlgorithms)
        {
            var aead = algorithm.EndsWith("GCM", StringComparison.Ordinal) || algorithm.EndsWith("CCM", StringComparison.Ordinal) || algorithm == "ChaCha20-Poly1305";
            var key = RandomNumberGenerator.GetBytes(algorithm.StartsWith("SM4", StringComparison.Ordinal) ? 16 : 32);
            var iv = RandomNumberGenerator.GetBytes(algorithm.EndsWith("ECB", StringComparison.Ordinal) ? 0 : aead ? 12 : 16);
            var aad = aead ? abc : [];
            foreach (var plain in new[] { Array.Empty<byte>(), message })
            {
                var encrypted = CryptoOperations.Transform(algorithm, plain, key, iv, aad, true);
                Require(CryptoOperations.Transform(algorithm, encrypted, key, iv, aad, false).SequenceEqual(plain), algorithm + " round trip");
                if (aead)
                {
                    encrypted[^1] ^= 1;
                    Reject(() => CryptoOperations.Transform(algorithm, encrypted, key, iv, aad, false), algorithm + " authentication failure");
                }
            }
            if (algorithm == "ChaCha20-Poly1305")
            {
                var cipher = new byte[message.Length]; var tag = new byte[16];
                using var dotnet = new System.Security.Cryptography.ChaCha20Poly1305(key);
                dotnet.Encrypt(iv, message, cipher, tag, aad);
                Require(CryptoOperations.Transform(algorithm, message, key, iv, aad, true).SequenceEqual(cipher.Concat(tag)), "ChaCha20-Poly1305 .NET interoperability");
            }
        }
        var path = Path.Combine(Path.GetTempPath(), "crypto-certificate-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            var cert = Service.CreateSelfSigned(CertificateKeyAlgorithm.EcdsaP256, "CN=localhost", [], 1, false, true, true);
            File.WriteAllText(path, cert.CertificatePem);
            Require(Service.ReadCertificate(path).SequenceEqual(cert.CertificateDer), "PEM file is decoded, not passed as DER");
        }
        finally { File.Delete(path); }
    }

    private static async Task CheckUiAsync()
    {
        var window = new TlsCertificateWindow(); window.Show();
        try
        {
            var tabs = (TabControl)window.FindName("ToolTabs");
            tabs.SelectedIndex = 1; window.UpdateLayout();
            ((ComboBox)window.FindName("AlgorithmBox")).SelectedValue = CertificateKeyAlgorithm.Sm2;
            Click(window, "GenerateCertificateButton"); await Idle(window);
            Require(((TextBox)window.FindName("CertificateOutputBox")).Text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal), "SM2 certificate UI");
            tabs.SelectedIndex = 4; window.UpdateLayout();
            ((TextBox)window.FindName("DigestInputBox")).Text = "abc";
            Click(window, "DigestButton"); await Idle(window);
            Require(((TextBox)window.FindName("DigestOutputBox")).Text.StartsWith("66C7", StringComparison.Ordinal), "SM3 UI");
            var algorithms = (TabControl)window.FindName("AlgorithmTabs");
            algorithms.SelectedIndex = 1; window.UpdateLayout();
            ((ComboBox)window.FindName("CipherAlgorithmBox")).SelectedItem = "AES-GCM";
            ((TextBox)window.FindName("CipherKeyBox")).Text = new string('0', 32);
            ((TextBox)window.FindName("CipherIvBox")).Text = new string('0', 24);
            ((TextBox)window.FindName("CipherInputBox")).Text = new string('0', 32);
            Click(window, "EncryptButton"); await Idle(window);
            Require(((TextBox)window.FindName("CipherOutputBox")).Text.StartsWith("0388DACE", StringComparison.Ordinal), "AES-GCM UI");
            ((TextBox)window.FindName("CipherInputBox")).Text = "00";
            Click(window, "DecryptButton"); await Idle(window);
            Require(((TextBox)window.FindName("CipherOutputBox")).Text.Length == 0 && ((TextBlock)window.FindName("StatusText")).Text.StartsWith("操作失败", StringComparison.Ordinal), "Failed decrypt clears old output");
            // Never include generated private keys or password material in screenshots.
            foreach (var name in new[] { "PrivateKeyOutputBox", "CipherKeyBox", "CipherIvBox", "CipherInputBox", "CertificateOutputBox", "PublicKeyOutputBox" })
                ((TextBox)window.FindName(name)).Clear();
            foreach (var small in new[] { false, true })
            {
                window.Width = small ? window.MinWidth : 1120; window.Height = small ? window.MinHeight : 820;
                for (var tab = 1; tab < tabs.Items.Count; tab++)
                {
                    tabs.SelectedIndex = tab; window.UpdateLayout();
                    for (var sub = 0; sub < (tab == 4 ? 3 : 1); sub++)
                    {
                        if (tab == 4) algorithms.SelectedIndex = sub;
                        await FileWorkspaceChecks.CaptureAsync(window, $"crypto-{tab}-{sub}-{(small ? "min" : "default")}.png");
                    }
                }
            }
        }
        finally { window.Close(); }
        Console.WriteLine("PASS crypto algorithms and UI");
    }
    private static async Task Idle(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!((Button)window.FindName("GenerateCertificateButton")).IsEnabled)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Crypto UI did not finish");
            await Task.Delay(30);
        }
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine("PASS rejects " + label); return; }
        throw new InvalidOperationException("Expected rejection: " + label);
    }
}
