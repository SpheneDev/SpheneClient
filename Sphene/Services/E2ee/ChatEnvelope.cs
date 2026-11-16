using Sphene.API.Data.Enum;
using System.Security.Cryptography;
using System.Text;

namespace Sphene.Services.E2ee;

/// <summary>
/// Versioned wire envelope for one device-addressed chat copy:
///   byte 0      : version (0x01)
///   byte 1..65  : DHKEM encapsulated key (uncompressed P-256 point)
///   byte 66..   : AES-256-GCM ciphertext || 16-byte tag
/// Routing/provenance metadata is bound via AEAD associated data - swapping a
/// copy onto another row, conversation or device breaks authentication.
/// </summary>
internal static class ChatEnvelope
{
    public const byte Version = 0x01;
    private const int HeaderLength = 1 + SpheneHpke.EncLength;
    private static readonly byte[] Info = "SpheneChatE2EE/v1"u8.ToArray();

    public static int MinimumLength => HeaderLength + SpheneHpke.TagLength;

    /// <summary>Truncates to microsecond precision - the resolution durable
    /// storage (PostgreSQL timestamptz) actually preserves. Sealing with a
    /// normalized timestamp makes the persisted value bit-identical to the
    /// sealed one.</summary>
    public static DateTime NormalizeTimestamp(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);

    /// <summary>Seals <paramref name="text"/> into a single device copy blob.</summary>
    public static byte[] Seal(ECDiffieHellman sealerKey, byte[] recipientPublicKey,
        EnvelopeContext context, string text)
    {
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ctTag = SpheneHpke.SealAuth(sealerKey, recipientPublicKey, Info,
            context.ToAad(), plaintext, out var enc);
        CryptographicOperations.ZeroMemory(plaintext);
        var blob = new byte[HeaderLength + ctTag.Length];
        blob[0] = Version;
        Array.Copy(enc, 0, blob, 1, enc.Length);
        Array.Copy(ctTag, 0, blob, HeaderLength, ctTag.Length);
        return blob;
    }

    /// <summary>Opens a blob addressed to <paramref name="forDeviceKey"/>.
    /// Throws <see cref="CryptographicException"/> on any authentication
    /// failure - callers map it to the undecryptable display state.
    /// <paramref name="legacyTickAad"/> retries with the raw-tick timestamp
    /// encoding used by the pre-precision-fix build so rows sealed by that
    /// build stay readable.</summary>
    public static string Open(byte[] blob, ECDiffieHellman forDeviceKey,
        byte[] sealerPublicKey, EnvelopeContext context, bool legacyTickAad = false)
    {
        if (blob.Length < MinimumLength || blob[0] != Version)
        {
            throw new CryptographicException("Unsupported chat envelope.");
        }
        var enc = blob[1..HeaderLength];
        var ctTag = blob[HeaderLength..];
        var plaintext = SpheneHpke.OpenAuth(forDeviceKey, sealerPublicKey, enc,
            ctTag, Info, legacyTickAad ? context.ToAadRawTicks() : context.ToAad());
        var text = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return text;
    }

    /// <summary>Candidate timestamps for rows sealed by the pre-precision-fix
    /// build: its AAD bound raw 100ns ticks, but the DB kept microseconds, so
    /// the sealed value differs from the stored one by &lt; 1µs either way
    /// (truncation or rounding). Bounded window - covers every sub-µs delta.</summary>
    public static IEnumerable<DateTime> LegacySentAtCandidates(DateTime stored)
    {
        for (var d = -9; d <= 9; d++)
        {
            yield return new DateTime(stored.Ticks + d, DateTimeKind.Utc);
        }
    }

    /// <summary>Opens a blob, falling back to the legacy raw-tick AAD across
    /// the ±9-tick window when the canonical µs AAD fails. Recovers rows
    /// sealed by the pre-precision-fix build without guessing keys.</summary>
    public static bool TryOpenRecovered(byte[] blob, ECDiffieHellman forDeviceKey,
        byte[] sealerPublicKey, EnvelopeContext context, out string text)
    {
        try
        {
            text = Open(blob, forDeviceKey, sealerPublicKey, context);
            return true;
        }
        catch (CryptographicException)
        {
            // fall through to the legacy-candidate recovery below
        }
        foreach (var candidate in LegacySentAtCandidates(context.SentAtUtc))
        {
            var legacy = context with { SentAtUtc = candidate };
            try
            {
                text = Open(blob, forDeviceKey, sealerPublicKey, legacy,
                    legacyTickAad: true);
                return true;
            }
            catch (CryptographicException)
            {
                // next candidate
            }
        }
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Identity context bound into every seal/open. All fields must match
    /// between sealer and opener or the AEAD tag fails.
    /// </summary>
    public sealed record EnvelopeContext(
        Guid MessageId,
        ChatMessageKind Kind,
        string SenderUid,
        string RecipientUid,
        string ForDeviceId,
        string SealerUid,
        string SealerDeviceId,
        DateTime SentAtUtc)
    {
        /// <summary>Canonical binary AAD: version | kind | msgId | len-prefixed
        /// utf8 fields | sentAt microseconds (big-endian). The timestamp is
        /// quantized to microseconds: PostgreSQL timestamptz stores µs while
        /// DateTime ticks are 100ns, so binding raw ticks makes every
        /// DB-round-tripped envelope undecryptable.</summary>
        public byte[] ToAad() => BuildAad(microseconds: true);

        /// <summary>AAD with raw 100ns ticks - the encoding used by the build
        /// before the timestamp-precision fix. Only used to keep rows sealed
        /// by that build openable; never sealed anew.</summary>
        public byte[] ToAadRawTicks() => BuildAad(microseconds: false);

        private byte[] BuildAad(bool microseconds)
        {
            using var ms = new MemoryStream(256);
            ms.WriteByte(Version);
            ms.WriteByte((byte)Kind);
            ms.Write(MessageId.ToByteArray());
            WriteField(ms, SenderUid);
            WriteField(ms, RecipientUid);
            WriteField(ms, ForDeviceId);
            WriteField(ms, SealerUid);
            WriteField(ms, SealerDeviceId);
            var ticks = microseconds
                ? SentAtUtc.Ticks / TimeSpan.TicksPerMicrosecond
                : SentAtUtc.Ticks;
            Span<byte> tickBytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(tickBytes, ticks);
            ms.Write(tickBytes);
            return ms.ToArray();
        }

        private static void WriteField(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > byte.MaxValue)
            {
                throw new CryptographicException("Envelope field too long.");
            }
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
    }
}
