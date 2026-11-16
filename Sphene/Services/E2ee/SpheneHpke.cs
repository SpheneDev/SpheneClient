using System.Security.Cryptography;

namespace Sphene.Services.E2ee;

/// <summary>
/// RFC 9180 HPKE, single-shot Auth mode:
/// DHKEM(P-256, HKDF-SHA256) + HKDF-SHA256 + AES-256-GCM.
/// Implemented on <see cref="System.Security.Cryptography"/> primitives only -
/// no third-party/native crypto. Auth mode binds the sender's static DH key,
/// giving sender authentication without a signature scheme. Verified against
/// the official RFC 9180 test vectors (see Sphene.Tests HpkeVectorTests).
/// </summary>
internal static class SpheneHpke
{
    public const int PublicKeyLength = 65;   // uncompressed P-256 point
    public const int PrivateKeyLength = 32;
    public const int TagLength = 16;
    public const int EncLength = PublicKeyLength;

    private const byte ModeAuth = 0x02;
    private static readonly byte[] KemSuiteId = [.. "KEM"u8, 0x00, 0x10];
    private static readonly byte[] HpkeSuiteId = [.. "HPKE"u8, 0x00, 0x10, 0x00, 0x01, 0x00, 0x02];
    private static readonly byte[] VersionLabel = "HPKE-v1"u8.ToArray();

    /// <summary>Generates a fresh P-256 device keypair. Returns the private
    /// scalar; the caller persists it - this type is stateless.</summary>
    public static byte[] GeneratePrivateKey(out byte[] publicKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        publicKey = SerializePublicKey(ecdh);
        return ecdh.ExportParameters(includePrivateParameters: true).D!;
    }

    /// <summary>Reconstructs an ECDH handle from a stored scalar + point.</summary>
    public static ECDiffieHellman CreateKeyPair(byte[] privateKey, byte[] publicKey)
        => ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = ParsePoint(publicKey),
        });

    /// <summary>0x04 || X || Y uncompressed point - the on-wire key form.</summary>
    public static byte[] SerializePublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var result = new byte[PublicKeyLength];
        result[0] = 0x04;
        Array.Copy(q.X!, 0, result, 1, 32);
        Array.Copy(q.Y!, 0, result, 33, 32);
        return result;
    }

    /// <summary>SHA-256 fingerprint of the uncompressed public key.</summary>
    public static byte[] Fingerprint(byte[] publicKey)
        => SHA256.HashData(publicKey);

    /// <summary>
    /// HPKE-Auth seal: encrypts <paramref name="plaintext"/> to the recipient's
    /// public key, authenticated by the sender's static key. Returns the
    /// DHKEM encapsulated key via <paramref name="enc"/> and the AEAD
    /// ciphertext||tag as the return value.
    /// </summary>
    public static byte[] SealAuth(ECDiffieHellman senderKey, byte[] recipientPublicKey,
        byte[] info, byte[] aad, byte[] plaintext, out byte[] enc,
        ECParameters? ephemeralOverride = null)
    {
        using var recipientKey = CreateRecipientKey(recipientPublicKey);
        using var ephemeral = ephemeralOverride.HasValue
            ? ECDiffieHellman.Create(ephemeralOverride.Value)
            : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        enc = SerializePublicKey(ephemeral);
        var sharedSecret = KemAuth(senderKey, ephemeral, recipientKey, enc);
        var (key, nonce) = KeySchedule(sharedSecret, info);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        CryptographicOperations.ZeroMemory(key);
        var combined = new byte[ciphertext.Length + TagLength];
        Array.Copy(ciphertext, combined, ciphertext.Length);
        Array.Copy(tag, 0, combined, ciphertext.Length, TagLength);
        return combined;
    }

    /// <summary>
    /// HPKE-Auth open. Throws <see cref="CryptographicException"/> when the
    /// envelope, sender key or associated data does not authenticate.
    /// </summary>
    public static byte[] OpenAuth(ECDiffieHellman recipientKey, byte[] senderPublicKey,
        byte[] enc, byte[] ciphertextAndTag, byte[] info, byte[] aad)
    {
        if (enc.Length != EncLength || enc[0] != 0x04
            || ciphertextAndTag.Length < TagLength)
        {
            throw new CryptographicException("Malformed HPKE envelope.");
        }
        using var ephemeralKey = CreateRecipientKey(enc);
        using var senderPub = CreateRecipientKey(senderPublicKey);
        var sharedSecret = KemAuthOpen(recipientKey, ephemeralKey, senderPub, enc);
        var (key, nonce) = KeySchedule(sharedSecret, info);
        var ciphertext = ciphertextAndTag[..^TagLength];
        var tag = ciphertextAndTag[^TagLength..];
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        CryptographicOperations.ZeroMemory(key);
        return plaintext;
    }

    /// <summary>DHKEM AuthEncap shared secret: DH(eph,pkR) || DH(sS,pkR) with
    /// kem_context = enc || pkRm || pkSm.</summary>
    private static byte[] KemAuth(ECDiffieHellman sender, ECDiffieHellman ephemeral,
        ECDiffieHellman recipient, byte[] enc)
    {
        var dh = Concat(
            ephemeral.DeriveRawSecretAgreement(recipient.PublicKey),
            sender.DeriveRawSecretAgreement(recipient.PublicKey));
        var context = Concat(enc, SerializePublicKey(recipient), SerializePublicKey(sender));
        return KemExtractAndExpand(dh, context);
    }

    /// <summary>DHKEM AuthDecap shared secret: DH(skR,pkE) || DH(skR,pkS).</summary>
    private static byte[] KemAuthOpen(ECDiffieHellman recipient,
        ECDiffieHellman ephemeral, ECDiffieHellman sender, byte[] enc)
    {
        var dh = Concat(
            recipient.DeriveRawSecretAgreement(ephemeral.PublicKey),
            recipient.DeriveRawSecretAgreement(sender.PublicKey));
        var context = Concat(enc, SerializePublicKey(recipient), SerializePublicKey(sender));
        return KemExtractAndExpand(dh, context);
    }

    private static byte[] KemExtractAndExpand(byte[] dh, byte[] kemContext)
    {
        var eaePrk = LabeledExtract(KemSuiteId, salt: [], "eae_prk", dh);
        var shared = LabeledExpand(KemSuiteId, eaePrk, "shared_secret", kemContext, 32);
        CryptographicOperations.ZeroMemory(eaePrk);
        return shared;
    }

    /// <summary>KeySchedule for mode=auth, empty PSK. Single-shot: the seq=0
    /// nonce equals base_nonce, so only (key, base_nonce) is returned.</summary>
    private static (byte[] Key, byte[] Nonce) KeySchedule(byte[] sharedSecret, byte[] info)
    {
        var pskIdHash = LabeledExtract(HpkeSuiteId, salt: [], "psk_id_hash", []);
        var infoHash = LabeledExtract(HpkeSuiteId, salt: [], "info_hash", info);
        var context = new byte[1 + pskIdHash.Length + infoHash.Length];
        context[0] = ModeAuth;
        Array.Copy(pskIdHash, 0, context, 1, pskIdHash.Length);
        Array.Copy(infoHash, 0, context, 1 + pskIdHash.Length, infoHash.Length);
        var secret = LabeledExtract(HpkeSuiteId, salt: sharedSecret, "secret", []);
        var key = LabeledExpand(HpkeSuiteId, secret, "key", context, 32);
        var nonce = LabeledExpand(HpkeSuiteId, secret, "base_nonce", context, 12);
        CryptographicOperations.ZeroMemory(secret);
        return (key, nonce);
    }

    /// <summary>RFC 9180 §4: LabeledExtract(salt, label, ikm) =
    /// HKDF-Extract(salt, "HPKE-v1" || suite_id || label || ikm).</summary>
    private static byte[] LabeledExtract(byte[] suiteId, byte[] salt, string label, byte[] ikm)
    {
        var labeled = new byte[VersionLabel.Length + suiteId.Length + label.Length + ikm.Length];
        var offset = 0;
        Buffer.BlockCopy(VersionLabel, 0, labeled, offset, VersionLabel.Length);
        offset += VersionLabel.Length;
        Buffer.BlockCopy(suiteId, 0, labeled, offset, suiteId.Length);
        offset += suiteId.Length;
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes(label), 0, labeled, offset, label.Length);
        offset += label.Length;
        Buffer.BlockCopy(ikm, 0, labeled, offset, ikm.Length);
        return HKDF.Extract(HashAlgorithmName.SHA256, labeled, salt);
    }

    /// <summary>LabeledExpand(prk, label, info, L) =
    /// HKDF-Expand(prk, I2OSP(L,2) || "HPKE-v1" || suite_id || label || info, L).</summary>
    private static byte[] LabeledExpand(byte[] suiteId, byte[] prk, string label,
        byte[] info, int length)
    {
        var labeled = new byte[2 + VersionLabel.Length + suiteId.Length + label.Length + info.Length];
        labeled[0] = (byte)(length >> 8);
        labeled[1] = (byte)length;
        var offset = 2;
        Buffer.BlockCopy(VersionLabel, 0, labeled, offset, VersionLabel.Length);
        offset += VersionLabel.Length;
        Buffer.BlockCopy(suiteId, 0, labeled, offset, suiteId.Length);
        offset += suiteId.Length;
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes(label), 0, labeled, offset, label.Length);
        offset += label.Length;
        Buffer.BlockCopy(info, 0, labeled, offset, info.Length);
        return HKDF.Expand(HashAlgorithmName.SHA256, prk, length, labeled);
    }

    private static ECDiffieHellman CreateRecipientKey(byte[] publicKey)
    {
        if (publicKey.Length != PublicKeyLength || publicKey[0] != 0x04)
        {
            throw new CryptographicException("Invalid P-256 public key encoding.");
        }
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = ParsePoint(publicKey),
        });
    }

    private static ECPoint ParsePoint(byte[] uncompressed)
    {
        if (uncompressed.Length != PublicKeyLength || uncompressed[0] != 0x04)
        {
            throw new CryptographicException("Invalid P-256 point encoding.");
        }
        var x = new byte[32];
        var y = new byte[32];
        Array.Copy(uncompressed, 1, x, 0, 32);
        Array.Copy(uncompressed, 33, y, 0, 32);
        return new ECPoint { X = x, Y = y };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
