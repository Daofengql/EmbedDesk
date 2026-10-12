# 证书与密码工具

工具位于「网络工具 → 地址与证书 → 证书与密码工具」。密码学计算在本机完成，不调用 OpenSSL 或外部 EXE；目前支持项如下。算法库使用项目已依赖并嵌入发布包的 BouncyCastle.Cryptography（MIT）。

## 证书与密钥

- 密钥：RSA 2048/3072/4096、ECDSA NIST P-256/P-384/P-521、Ed25519、Ed448、SM2（sm2p256v1）。
- 证书：CSR、自签名终端证书或 CA 证书；可填 DNS/IP SAN，选择服务器或客户端 EKU。密钥以未加密 PKCS#8 PEM 显示，只有用户主动复制或保存时才离开窗口。
- CA 签发：检查 CSR 自身签名、CA Basic Constraints 与 Key Usage、CA 私钥与证书公钥是否匹配，以及签发时间范围；签发为终端证书，拒绝申请 CA 权限的 CSR，并拒绝工具未处理的关键扩展。CSR 的 SAN 会沿用，也可在界面中覆盖。
- 证书检查：解析 PEM/DER，显示主题、签发者、期限、SAN、用途、公钥和签名算法及 SHA-256 指纹。纯解析不等于验证信任链。
- PKCS#12：使用 AES-256-CBC 和 HMAC-SHA-256 密码保护；导入要求一个私钥条目并验证它与证书匹配。SM2 / EdDSA PFX 的互通取决于目标软件是否支持对应密钥和算法。

CA 签发不建立或验证完整外部证书链，不查询吊销状态，也不替代 CA 对申请者身份及域名所有权的审查。只有 SAN、CA 约束、Key Usage、Extended Key Usage 这几类 CSR 扩展会被识别；实际签发时工具只沿用 SAN，并自行设置终端证书用途。

## 摘要、签名和加密

| 类别 | 支持算法 |
| --- | --- |
| 摘要 / HMAC | MD5、SHA-1、SHA-224/256/384/512、SHA3-224/256/384/512、SM3 |
| 对称加密 | AES-GCM/CCM/CBC/CTR/ECB、SM4-GCM/CBC/CTR/ECB、ChaCha20-Poly1305 |
| RSA 签名 | PSS-SHA224/256/384/512；PKCS#1 v1.5-MD5/SHA-1/SHA-224/256/384/512/SHA3-256/384/512 |
| ECDSA 签名 | SHA-1/SHA-224/256/384/512、SHA3-256/384/512 |
| 其他签名 | Ed25519、Ed448、SM2/SM3；“自动”按所选密钥类型使用对应默认方案 |
| 非对称加密 | RSA-OAEP-SHA1/224/256/384/512、SM2 C1C3C2 或 C1C2C3 |

RSA-OAEP 是单块加密，输入上限由 RSA 密钥长度和 OAEP 摘要共同决定；较长数据应使用混合加密方案。签名和密文以 Base64 输出；ECDSA / SM2 签名使用 ASN.1 DER 格式。对称输入输出使用 HEX。AEAD 密文格式为“密文 || 16 字节认证标签”；AES-GCM、AES-CCM、SM4-GCM、ChaCha20-Poly1305 使用 12 字节 Nonce，AES 密钥支持 16/24/32 字节，SM4 密钥为 16 字节，ChaCha20 密钥为 32 字节。CBC/ECB 使用 PKCS#7，CTR 不填充。

MD5、SHA-1、RSA PKCS#1 MD5/SHA-1 和 ECB 仅用于旧系统互通；不应用于新的安全设计。CBC、ECB、CTR 本身不验证数据完整性，不应单独保护需要认证的数据。相同密钥下不可重复使用 GCM、CCM、ChaCha20-Poly1305 或 CTR Nonce / IV。工具不会自动保存或管理密钥，也不提供硬件密钥存储。

国密支持限于 SM2 密钥、SM2/SM3 签名、SM2 公钥加密、SM3 摘要及 SM4 对称加密。TLS 诊断使用 Windows `SslStream`；当前未实现 TLS/TLCP 国密握手、国密套件协商或国密 HTTPS 客户端。
