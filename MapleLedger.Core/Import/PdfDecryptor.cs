using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MapleLedger.Core.Import;

internal enum PdfEncryption
{
    /// <summary>No encryption: read the file as it is.</summary>
    None,

    /// <summary>Encrypted with an empty open password and decrypted here.</summary>
    Decrypted,

    /// <summary>Opening it needs a password the user would have to type.</summary>
    NeedsPassword,

    /// <summary>Encrypted some other way (e.g. AES-256); left for PdfPig to try.</summary>
    Unsupported,
}

/// <summary>
/// Removes the standard encryption that banks put on statements which open without a password, only
/// to restrict editing or copying: RC4 (security handler revisions 2 and 3, 40 to 128-bit keys, as on
/// TD's statements) and AES-128 (revision 4, as on BMO's). PdfPig can read these on desktop .NET, but
/// it derives the key with MD5 and decrypts with .NET's AES, neither of which .NET provides in the
/// browser, so this decrypts with its own MD5, RC4 and AES first. Streams are decrypted in place so
/// every byte offset in the file stays valid: RC4 keeps their length, and AES's shorter plaintext is
/// followed by spaces up to the original length, which the stream's decoder stops before (Flate and
/// ASCII85 data mark their own end) or reads as whitespace (content streams). The trailer's /Encrypt
/// entry is blanked out. Strings outside streams are left encrypted: the text on a page lives in its
/// content streams.
/// </summary>
internal static partial class PdfDecryptor
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    public static PdfEncryption Decrypt(byte[] pdf, out byte[] decrypted)
    {
        decrypted = pdf;
        // Latin-1 maps each byte to one char, so string indexes are byte offsets.
        var text = Encoding.Latin1.GetString(pdf);

        var encrypt = EncryptEntry().Match(text);
        if (!encrypt.Success) return PdfEncryption.None;

        var dictionary = encrypt.Groups["ref"].Success
            ? ObjectDictionary(text, int.Parse(encrypt.Groups["num"].Value), int.Parse(encrypt.Groups["gen"].Value))
            : Balanced(text, encrypt.Groups["dict"].Index);
        if (dictionary is null) return PdfEncryption.Unsupported;

        var filter = Regex.Match(dictionary, @"/Filter\s*/(\w+)").Groups[1].Value;
        var v = (int)(Integer(dictionary, "V") ?? 0);
        var r = (int)(Integer(dictionary, "R") ?? 0);
        if (filter != "Standard" || !((v is 1 or 2 && r is 2 or 3) || (v == 4 && r == 4))) return PdfEncryption.Unsupported;

        var cipher = v == 4 ? StreamCipher(dictionary) : Cipher.Rc4;
        if (cipher is null) return PdfEncryption.Unsupported;
        var encryptMetadata = !Regex.IsMatch(dictionary, @"/EncryptMetadata\s*false\b");

        var o = StringValue(dictionary, "O");
        var u = StringValue(dictionary, "U");
        var p = Integer(dictionary, "P");
        if (o is not { Length: >= 32 } || u is not { Length: >= 16 } || p is null) return PdfEncryption.Unsupported;

        var idMatch = Regex.Match(text, @"/ID\s*\[\s*");
        var id = idMatch.Success ? ReadString(text, idMatch.Index + idMatch.Length, out _) ?? [] : [];

        var keyLength = v == 1 ? 5 : (int)Math.Clamp((Integer(dictionary, "Length") ?? (v == 4 ? 128 : 40)) / 8, 5, 16);
        var key = FileKey(o, (int)p.Value, id, r, keyLength, encryptMetadata);
        if (!IsUserKey(key, u, id, r)) return PdfEncryption.NeedsPassword;

        var output = (byte[])pdf.Clone();
        var encryptObject = encrypt.Groups["ref"].Success ? int.Parse(encrypt.Groups["num"].Value) : -1;
        DecryptStreams(text, output, key, cipher.Value, encryptObject, encryptMetadata);

        // Without /Encrypt, PdfPig reads the decrypted streams as plain ones.
        foreach (Match m in EncryptEntry().Matches(text))
        {
            var end = m.Groups["ref"].Success ? m.Index + m.Length : m.Groups["dict"].Index + (Balanced(text, m.Groups["dict"].Index)?.Length ?? 0);
            output.AsSpan(m.Index, end - m.Index).Fill((byte)' ');
        }

        decrypted = output;
        return PdfEncryption.Decrypted;
    }

    // ---------------------------------------------------------------- keys (PDF 32000-1, 7.6.3.3)

    /// <summary>Algorithm 2, for the empty user password.</summary>
    private static byte[] FileKey(byte[] o, int p, byte[] id, int revision, int length, bool encryptMetadata)
    {
        var unencryptedMetadata = revision >= 4 && !encryptMetadata;
        var input = new byte[32 + 32 + 4 + id.Length + (unencryptedMetadata ? 4 : 0)];
        Padding.CopyTo(input, 0);
        o.AsSpan(0, 32).CopyTo(input.AsSpan(32));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(64), p);
        id.CopyTo(input, 68);
        if (unencryptedMetadata) input.AsSpan(68 + id.Length).Fill(0xFF);

        var hash = Md5.Hash(input);
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++) hash = Md5.Hash(hash.AsSpan(0, length));
        }

        return hash[..length];
    }

    /// <summary>Algorithms 4 and 5: the key opens the file without a password if it reproduces /U.</summary>
    private static bool IsUserKey(byte[] key, byte[] u, byte[] id, int revision)
    {
        if (revision == 2)
        {
            var check = (byte[])Padding.Clone();
            Rc4.Apply(key, check);
            return check.AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
        }

        var hash = Md5.Hash([.. Padding, .. id]);
        var round = new byte[key.Length];
        for (var i = 0; i < 20; i++)
        {
            for (var k = 0; k < key.Length; k++) round[k] = (byte)(key[k] ^ i);
            Rc4.Apply(round, hash);
        }

        return hash.AsSpan().SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>Algorithm 1: each object has its own key, from the file key and the object's number.</summary>
    private static byte[] ObjectKey(byte[] key, int number, int generation, Cipher cipher)
    {
        // AES keys also hash in "sAlT".
        var input = new byte[key.Length + 5 + (cipher == Cipher.Aes ? 4 : 0)];
        key.CopyTo(input, 0);
        input[key.Length] = (byte)number;
        input[key.Length + 1] = (byte)(number >> 8);
        input[key.Length + 2] = (byte)(number >> 16);
        input[key.Length + 3] = (byte)generation;
        input[key.Length + 4] = (byte)(generation >> 8);
        if (cipher == Cipher.Aes) "sAlT"u8.CopyTo(input.AsSpan(key.Length + 5));
        return Md5.Hash(input)[..Math.Min(key.Length + 5, 16)];
    }

    private enum Cipher
    {
        /// <summary>Streams aren't encrypted (the Identity crypt filter); only strings may be.</summary>
        None,
        Rc4,
        Aes,
    }

    /// <summary>Revision 4 names the streams' crypt filter (/StmF) and defines it in /CF.</summary>
    private static Cipher? StreamCipher(string dictionary)
    {
        var name = Regex.Match(dictionary, @"/StmF\s*/(\w+)").Groups[1].Value;
        if (name is "" or "Identity") return Cipher.None;

        var definition = Regex.Match(dictionary, $@"/{name}\s*<<(?<body>[^<>]*)>>");
        if (!definition.Success) return null;
        return Regex.Match(definition.Groups["body"].Value, @"/CFM\s*/(\w+)").Groups[1].Value switch
        {
            "V2" => Cipher.Rc4,
            "AESV2" => Cipher.Aes,
            "None" => Cipher.None,
            _ => null,
        };
    }

    // ---------------------------------------------------------------- streams

    private static void DecryptStreams(string text, byte[] output, byte[] key, Cipher cipher, int encryptObject, bool encryptMetadata)
    {
        if (cipher == Cipher.None) return;

        var position = 0;
        while (ObjectHeader().Match(text, position) is { Success: true } header)
        {
            var number = int.Parse(header.Groups[1].Value);
            var generation = int.Parse(header.Groups[2].Value);
            var bodyStart = header.Index + header.Length;
            var endObject = text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            if (endObject < 0) endObject = text.Length;
            var stream = StreamKeyword().Match(text, bodyStart, endObject - bodyStart);
            if (!stream.Success)
            {
                position = Math.Min(text.Length, endObject + 6);
                continue;
            }

            var dataStart = stream.Index + stream.Length;
            var dictionary = text[bodyStart..stream.Index];
            var length = StreamLength(text, dictionary, dataStart);
            if (length is null)
            {
                position = dataStart;
                continue;
            }

            // Cross-reference streams are never encrypted, nor are streams with their own (Identity)
            // crypt filter, such as the metadata when /EncryptMetadata is false.
            var plain = Regex.IsMatch(dictionary, @"/Type\s*/XRef\b|/Crypt\b")
                || (!encryptMetadata && Regex.IsMatch(dictionary, @"/Type\s*/Metadata\b"));
            if (!plain && number != encryptObject)
            {
                var data = output.AsSpan(dataStart, length.Value);
                var objectKey = ObjectKey(key, number, generation, cipher);
                if (cipher == Cipher.Rc4)
                {
                    Rc4.Apply(objectKey, data);
                }
                else if (AesCbc.Decrypt(objectKey, data) is { } decrypted)
                {
                    decrypted.CopyTo(data);
                    data[decrypted.Length..].Fill((byte)' ');
                }
            }

            position = dataStart + length.Value;
        }
    }

    private static int? StreamLength(string text, string dictionary, int dataStart)
    {
        int? length = null;
        var indirect = Regex.Match(dictionary, @"/Length\s+(\d+)\s+(\d+)\s+R");
        if (indirect.Success)
        {
            var target = new Regex($@"(?<![0-9]){indirect.Groups[1].Value}\s+{indirect.Groups[2].Value}\s+obj\s*(\d+)").Match(text);
            if (target.Success) length = int.Parse(target.Groups[1].Value);
        }
        else if (Integer(dictionary, "Length") is { } direct)
        {
            length = (int)direct;
        }

        if (length is { } l && l >= 0 && dataStart + l <= text.Length) return l;

        // No usable /Length: the data runs to the end-of-line before "endstream".
        var end = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
        if (end < 0) return null;
        if (end > dataStart && text[end - 1] == '\n') end--;
        if (end > dataStart && text[end - 1] == '\r') end--;
        return end - dataStart;
    }

    // ---------------------------------------------------------------- reading the syntax

    [GeneratedRegex(@"/Encrypt\s*(?:(?<ref>(?<num>\d+)\s+(?<gen>\d+)\s+R)|(?<dict><<))")]
    private static partial Regex EncryptEntry();

    [GeneratedRegex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b")]
    private static partial Regex ObjectHeader();

    [GeneratedRegex(@"(?<!end)stream\r?\n")]
    private static partial Regex StreamKeyword();

    private static string? ObjectDictionary(string text, int number, int generation)
    {
        var header = new Regex($@"(?<![0-9]){number}\s+{generation}\s+obj\s*").Match(text);
        return header.Success && text.AsSpan(header.Index + header.Length).StartsWith("<<") ? Balanced(text, header.Index + header.Length) : null;
    }

    /// <summary>The dictionary starting at <paramref name="start"/> ("&lt;&lt;"), through its closing "&gt;&gt;".</summary>
    private static string? Balanced(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            switch (text[i])
            {
                case '(':
                    if (ReadString(text, i, out var after) is null) return null;
                    i = after - 1;
                    break;
                case '<' when text[i + 1] == '<':
                    depth++;
                    i++;
                    break;
                case '<':
                    var close = text.IndexOf('>', i);
                    if (close < 0) return null;
                    i = close;
                    break;
                case '>' when text[i + 1] == '>':
                    depth--;
                    i++;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    private static long? Integer(string dictionary, string key)
    {
        var m = Regex.Match(dictionary, $@"/{key}\s+(-?\d+)\b(?!\s+\d+\s+R)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    private static byte[]? StringValue(string dictionary, string key)
    {
        var m = Regex.Match(dictionary, $@"/{key}\s*(?=[<(])");
        return m.Success ? ReadString(dictionary, m.Index + m.Length, out _) : null;
    }

    /// <summary>Reads a hex (&lt;...&gt;) or literal ((...)) string.</summary>
    private static byte[]? ReadString(string text, int start, out int end)
    {
        end = start;
        if (start >= text.Length) return null;
        var bytes = new List<byte>();
        if (text[start] == '<')
        {
            var close = text.IndexOf('>', start);
            if (close < 0) return null;
            var hex = new string(text.AsSpan(start + 1, close - start - 1).ToArray().Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1) hex += "0";
            end = close + 1;
            return Convert.FromHexString(hex);
        }

        if (text[start] != '(') return null;
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var e = text[++i];
                switch (e)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        break;
                    case '\n':
                        break;
                    case >= '0' and <= '7':
                        var value = e - '0';
                        for (var k = 0; k < 2 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7'; k++) value = value * 8 + (text[++i] - '0');
                        bytes.Add((byte)value);
                        break;
                    default: bytes.Add((byte)e); break;
                }

                continue;
            }

            if (c == '(' && depth++ == 0) continue;
            if (c == ')' && --depth == 0)
            {
                end = i + 1;
                return [.. bytes];
            }

            bytes.Add((byte)c);
        }

        return null;
    }
}

/// <summary>
/// AES decryption (FIPS 197), which .NET doesn't provide in the browser. PDF's AESV2 encrypts each
/// stream in CBC mode with a 16-byte initialization vector in front and PKCS#5 padding at the end.
/// </summary>
internal static class AesCbc
{
    private static readonly byte[] SBox = new byte[256];
    private static readonly byte[] InverseSBox = new byte[256];

    static AesCbc()
    {
        // Walks the multiplicative group with generator 3, so q is always p's inverse in GF(2^8).
        int p = 1, q = 1;
        do
        {
            p = (p ^ (p << 1) ^ ((p & 0x80) != 0 ? 0x1B : 0)) & 0xFF;
            q ^= q << 1;
            q ^= q << 2;
            q ^= q << 4;
            q &= 0xFF;
            if ((q & 0x80) != 0) q ^= 0x09;
            var x = q ^ Rotate(q, 1) ^ Rotate(q, 2) ^ Rotate(q, 3) ^ Rotate(q, 4) ^ 0x63;
            SBox[p] = (byte)x;
            InverseSBox[x] = (byte)p;
        }
        while (p != 1);

        SBox[0] = 0x63;
        InverseSBox[0x63] = 0;
    }

    private static int Rotate(int b, int shift) => ((b << shift) | (b >> (8 - shift))) & 0xFF;

    /// <summary>
    /// Decrypts <paramref name="data"/> (an IV, then whole blocks) and removes the padding. Null if
    /// it isn't whole blocks. A padding byte that doesn't make sense is kept as data.
    /// </summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (data.Length < 32 || data.Length % 16 != 0) return null;

        var (roundKeys, rounds) = ExpandKey(key);
        var plain = new byte[data.Length - 16];
        Span<byte> block = stackalloc byte[16];
        for (var offset = 16; offset < data.Length; offset += 16)
        {
            data.Slice(offset, 16).CopyTo(block);
            DecryptBlock(roundKeys, rounds, block);
            for (var i = 0; i < 16; i++) plain[offset - 16 + i] = (byte)(block[i] ^ data[offset - 16 + i]);
        }

        var pad = plain[^1];
        var padded = pad is >= 1 and <= 16 && plain.AsSpan(plain.Length - pad).IndexOfAnyExcept(pad) < 0;
        return padded ? plain[..^pad] : plain;
    }

    private static (byte[] RoundKeys, int Rounds) ExpandKey(ReadOnlySpan<byte> key)
    {
        var words = key.Length / 4;
        var rounds = words + 6;
        var w = new byte[16 * (rounds + 1)];
        key.CopyTo(w);
        var rcon = 1;
        Span<byte> t = stackalloc byte[4];
        for (var i = words; i < 4 * (rounds + 1); i++)
        {
            w.AsSpan(4 * (i - 1), 4).CopyTo(t);
            if (i % words == 0)
            {
                (t[0], t[1], t[2], t[3]) = ((byte)(SBox[t[1]] ^ rcon), SBox[t[2]], SBox[t[3]], SBox[t[0]]);
                rcon = Times2(rcon);
            }
            else if (words > 6 && i % words == 4)
            {
                for (var k = 0; k < 4; k++) t[k] = SBox[t[k]];
            }

            for (var k = 0; k < 4; k++) w[4 * i + k] = (byte)(w[4 * (i - words) + k] ^ t[k]);
        }

        return (w, rounds);
    }

    /// <summary>The inverse cipher. The state is column by column, as the bytes come.</summary>
    private static void DecryptBlock(byte[] roundKeys, int rounds, Span<byte> s)
    {
        Span<byte> t = stackalloc byte[16];
        AddRoundKey(s, roundKeys, rounds);
        for (var round = rounds - 1; round >= 0; round--)
        {
            // Inverse ShiftRows (row r moves right by r) and inverse SubBytes.
            s.CopyTo(t);
            for (var c = 0; c < 4; c++)
            {
                for (var r = 0; r < 4; r++) s[4 * ((c + r) % 4) + r] = InverseSBox[t[4 * c + r]];
            }

            AddRoundKey(s, roundKeys, round);
            if (round == 0) break;

            for (var c = 0; c < 4; c++)
            {
                int a0 = s[4 * c], a1 = s[4 * c + 1], a2 = s[4 * c + 2], a3 = s[4 * c + 3];
                s[4 * c] = (byte)(Multiply(a0, 14) ^ Multiply(a1, 11) ^ Multiply(a2, 13) ^ Multiply(a3, 9));
                s[4 * c + 1] = (byte)(Multiply(a0, 9) ^ Multiply(a1, 14) ^ Multiply(a2, 11) ^ Multiply(a3, 13));
                s[4 * c + 2] = (byte)(Multiply(a0, 13) ^ Multiply(a1, 9) ^ Multiply(a2, 14) ^ Multiply(a3, 11));
                s[4 * c + 3] = (byte)(Multiply(a0, 11) ^ Multiply(a1, 13) ^ Multiply(a2, 9) ^ Multiply(a3, 14));
            }
        }
    }

    private static void AddRoundKey(Span<byte> s, byte[] roundKeys, int round)
    {
        for (var i = 0; i < 16; i++) s[i] ^= roundKeys[16 * round + i];
    }

    private static int Times2(int a) => ((a << 1) ^ ((a & 0x80) != 0 ? 0x1B : 0)) & 0xFF;

    private static int Multiply(int a, int b)
    {
        var result = 0;
        for (; b != 0; b >>= 1, a = Times2(a))
        {
            if ((b & 1) != 0) result ^= a;
        }

        return result;
    }
}

/// <summary>RC4, which PDF encryption uses and .NET doesn't provide.</summary>
internal static class Rc4
{
    public static void Apply(ReadOnlySpan<byte> key, Span<byte> data)
    {
        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            data[n] ^= s[(s[i] + s[j]) & 0xFF];
        }
    }
}

/// <summary>MD5 (RFC 1321). .NET has it everywhere except the browser, where PDF keys need it.</summary>
internal static class Md5
{
    private static readonly int[] Shifts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static readonly uint[] Constants =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        var padded = new byte[(message.Length + 8) / 64 * 64 + 64];
        message.CopyTo(padded);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), (ulong)message.Length * 8);

        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        Span<uint> m = stackalloc uint[16];
        for (var chunk = 0; chunk < padded.Length; chunk += 64)
        {
            for (var w = 0; w < 16; w++) m[w] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(chunk + w * 4));

            uint a = a0, b = b0, c = c0, d = d0;
            for (var i = 0; i < 64; i++)
            {
                uint f;
                int g;
                switch (i >> 4)
                {
                    case 0: f = (b & c) | (~b & d); g = i; break;
                    case 1: f = (d & b) | (~d & c); g = (5 * i + 1) & 15; break;
                    case 2: f = b ^ c ^ d; g = (3 * i + 5) & 15; break;
                    default: f = c ^ (b | ~d); g = (7 * i) & 15; break;
                }

                f += a + Constants[i] + m[g];
                a = d;
                d = c;
                c = b;
                b += uint.RotateLeft(f, Shifts[i]);
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        var hash = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(hash, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(12), d0);
        return hash;
    }
}
