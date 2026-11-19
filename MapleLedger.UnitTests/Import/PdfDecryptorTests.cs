using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MapleLedger.Core.Import;
using UglyToad.PdfPig;
using static MapleLedger.UnitTests.Import.PdfStatementReaderTests;

namespace MapleLedger.UnitTests.Import;

public class PdfDecryptorTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static readonly byte[] Padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    [Fact]
    public void Md5MatchesDotNet()
    {
        var random = new Random(42);
        foreach (var length in new[] { 0, 1, 55, 56, 63, 64, 65, 119, 120, 1000 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(MD5.HashData(data), Md5.Hash(data));
        }
    }

    [Fact]
    public void BankStatementEncryptedWithoutAnOpenPasswordIsRead()
    {
        var plain = Statement();
        var encrypted = new StatementFile("visa.pdf", EncryptRc4(plain.Content));

        // The fixture is a real encrypted PDF: PdfPig (with .NET's MD5) reads the same words from it.
        Assert.Equal(Words(plain.Content), Words(encrypted.Content));
        Assert.Equal(PdfEncryption.Decrypted, PdfDecryptor.Decrypt(encrypted.Content, out var decrypted));
        Assert.Equal(Words(plain.Content), Words(decrypted));

        var expected = PdfStatementReader.Read([plain], Today);
        var actual = PdfStatementReader.Read([encrypted], Today);
        Assert.Equal(PdfTextLayout.Table, actual.Layout);
        Assert.Equal(expected.Text, actual.Text);
    }

    [Fact]
    public void AesMatchesDotNet()
    {
        var random = new Random(7);
        foreach (var keyLength in new[] { 16, 24, 32 })
        {
            foreach (var length in new[] { 0, 1, 15, 16, 17, 100 })
            {
                var (key, iv, plain) = (new byte[keyLength], new byte[16], new byte[length]);
                random.NextBytes(key);
                random.NextBytes(iv);
                random.NextBytes(plain);
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                byte[] data = [.. iv, .. aes.EncryptCbc(plain, iv)];

                Assert.Equal(plain, AesCbc.Decrypt(key, data));
            }
        }
    }

    [Fact]
    public void BankStatementEncryptedWithAesIsRead()
    {
        var plain = Statement();
        var encrypted = new StatementFile("bmo.pdf", EncryptAes(plain.Content));

        // The fixture is a real AES-encrypted PDF: PdfPig (with .NET's MD5 and AES) reads the same words from it.
        Assert.Equal(Words(plain.Content), Words(encrypted.Content));
        Assert.Equal(PdfEncryption.Decrypted, PdfDecryptor.Decrypt(encrypted.Content, out var decrypted));
        Assert.Equal(Words(plain.Content), Words(decrypted));

        var expected = PdfStatementReader.Read([plain], Today);
        var actual = PdfStatementReader.Read([encrypted], Today);
        Assert.Equal(PdfTextLayout.Table, actual.Layout);
        Assert.Equal(expected.Text, actual.Text);
    }

    [Fact]
    public void AesStatementThatNeedsAPasswordSaysSo()
    {
        var encrypted = EncryptAes(Statement().Content, userPasswordWorks: false);

        Assert.Equal(PdfEncryption.NeedsPassword, PdfDecryptor.Decrypt(encrypted, out _));
    }

    [Fact]
    public void StatementThatNeedsAPasswordSaysSo()
    {
        var encrypted = new StatementFile("locked.pdf", EncryptRc4(Statement().Content, userPasswordWorks: false));

        Assert.Equal(PdfEncryption.NeedsPassword, PdfDecryptor.Decrypt(encrypted.Content, out _));
        var ex = Assert.Throws<PdfStatementException>(() => PdfStatementReader.Read([encrypted], Today));
        Assert.Contains("password-protected", ex.Message);
    }

    [Fact]
    public void UnencryptedPdfIsLeftAlone()
    {
        var plain = Statement().Content;

        Assert.Equal(PdfEncryption.None, PdfDecryptor.Decrypt(plain, out var output));
        Assert.Same(plain, output);
    }

    private static StatementFile Statement()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "TD Cash Back Visa Infinite statement");
        pdf.Text(40, 690, "Date");
        pdf.Text(100, 690, "Activity description");
        pdf.Right(540, 690, "Amount ($)");
        pdf.Text(40, 675, "MAR 3");
        pdf.Text(100, 675, "LOBLAWS #1234 KINGSTON");
        pdf.Right(540, 675, "45.10");
        pdf.Text(40, 660, "MAR 5");
        pdf.Text(100, 660, "PAYMENT - THANK YOU");
        pdf.Right(540, 660, "-500.00");
        return pdf.Build("statement.pdf");
    }

    private static List<string> Words(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().SelectMany(p => p.GetWords().Select(w => $"{w.Text}@{w.BoundingBox.Left:F2},{w.BoundingBox.Bottom:F2}")).ToList();
    }

    /// <summary>
    /// Encrypts like TD's statements: standard security handler revision 3, 128-bit RC4, an empty user
    /// password (so it opens without asking) and an owner password. With <paramref name="userPasswordWorks"/>
    /// false, /U doesn't match the empty password, as for a file that asks for one.
    /// </summary>
    private static byte[] EncryptRc4(byte[] pdf, bool userPasswordWorks = true)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailer > 0, "the test PDF has a classic trailer");

        // The key depends on the file's /ID: reuse the builder's, or add one.
        var existingId = Regex.Match(text[trailer..], @"/ID\s*\[\s*<([0-9A-Fa-f]+)>");
        var id = existingId.Success ? Convert.FromHexString(existingId.Groups[1].Value) : RandomNumberGenerator.GetBytes(16);
        var owner = RandomNumberGenerator.GetBytes(32); // any /O works when only the user password is used
        const int permissions = -1036;

        var key = MD5.HashData([.. Padding, .. owner, .. BitConverter.GetBytes(permissions), .. id]);
        for (var i = 0; i < 50; i++) key = MD5.HashData(key);

        var user = MD5.HashData([.. Padding, .. id]);
        for (var i = 0; i < 20; i++) Rc4.Apply(key.Select(b => (byte)(b ^ i)).ToArray(), user);
        if (!userPasswordWorks) user[0] ^= 0xFF;

        var output = (byte[])pdf.Clone();
        foreach (Match m in Regex.Matches(text, @"(\d+) (\d+) obj\b(?:(?!endobj).)*?/Length (\d+)(?:(?!endobj).)*?stream\r?\n", RegexOptions.Singleline))
        {
            var number = int.Parse(m.Groups[1].Value);
            var generation = int.Parse(m.Groups[2].Value);
            var objectKey = MD5.HashData([.. key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8)]);
            Rc4.Apply(objectKey, output.AsSpan(m.Index + m.Length, int.Parse(m.Groups[3].Value)));
        }

        // The trailer follows the cross-reference table, so adding to it moves no object.
        var open = text.IndexOf("<<", trailer, StringComparison.Ordinal) + 2;
        var entries = $" /Encrypt << /Filter /Standard /V 2 /R 3 /Length 128 /P {permissions} /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}{new string('0', 32)}> >>"
            + (existingId.Success ? "" : $" /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]");
        return [.. output[..open], .. Encoding.Latin1.GetBytes(entries), .. output[open..]];
    }

    /// <summary>
    /// Encrypts like BMO's statements: revision 4 with the AESV2 crypt filter, 128-bit key, an empty
    /// user password and unencrypted metadata. AES makes each stream longer (an IV in front, padding
    /// at the end), so this writes the file out again with a new cross-reference table.
    /// </summary>
    private static byte[] EncryptAes(byte[] pdf, bool userPasswordWorks = true)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailer > 0, "the test PDF has a classic trailer");
        var trailerDictionary = text[(text.IndexOf("<<", trailer, StringComparison.Ordinal) + 2)..text.IndexOf("startxref", trailer, StringComparison.Ordinal)];
        trailerDictionary = trailerDictionary[..trailerDictionary.LastIndexOf(">>", StringComparison.Ordinal)];

        var existingId = Regex.Match(trailerDictionary, @"/ID\s*\[\s*<([0-9A-Fa-f]+)>");
        var id = existingId.Success ? Convert.FromHexString(existingId.Groups[1].Value) : RandomNumberGenerator.GetBytes(16);
        var owner = RandomNumberGenerator.GetBytes(32);
        const int permissions = -1852;

        // Algorithm 2, with 0xFFFFFFFF hashed in because the metadata isn't encrypted.
        var key = MD5.HashData([.. Padding, .. owner, .. BitConverter.GetBytes(permissions), .. id, 0xFF, 0xFF, 0xFF, 0xFF]);
        for (var i = 0; i < 50; i++) key = MD5.HashData(key);

        var user = MD5.HashData([.. Padding, .. id]);
        for (var i = 0; i < 20; i++) Rc4.Apply(key.Select(b => (byte)(b ^ i)).ToArray(), user);
        if (!userPasswordWorks) user[0] ^= 0xFF;

        using var aes = System.Security.Cryptography.Aes.Create();
        var output = new List<byte>(Encoding.Latin1.GetBytes(text[..text.IndexOf("\n", StringComparison.Ordinal)] + "\n"));
        var offsets = new SortedDictionary<int, int>();
        foreach (Match m in Regex.Matches(text, @"(?<num>\d+) (?<gen>\d+) obj\b(?<body>(?:(?!endobj).)*?)endobj", RegexOptions.Singleline))
        {
            var number = int.Parse(m.Groups["num"].Value);
            var generation = int.Parse(m.Groups["gen"].Value);
            offsets[number] = output.Count;
            var body = m.Groups["body"].Value;
            aes.Key = MD5.HashData([.. key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8), .. "sAlT"u8]);
            var stream = Regex.Match(body, @"/Length (?<length>\d+)(?:(?!stream).)*?stream\r?\n", RegexOptions.Singleline);
            if (!stream.Success)
            {
                // Strings are encrypted too (the document information's /Producer, say).
                var withStrings = Regex.Replace(body, @"\((?<s>[^\\()]*)\)", s =>
                {
                    var stringIv = RandomNumberGenerator.GetBytes(16);
                    return $"<{Convert.ToHexString([.. stringIv, .. aes.EncryptCbc(Encoding.Latin1.GetBytes(s.Groups["s"].Value), stringIv)])}>";
                });
                output.AddRange(Encoding.Latin1.GetBytes($"{number} {generation} obj{withStrings}endobj\n"));
                continue;
            }

            var data = Encoding.Latin1.GetBytes(body.Substring(stream.Index + stream.Length, int.Parse(stream.Groups["length"].Value)));
            var iv = RandomNumberGenerator.GetBytes(16);
            byte[] encrypted = [.. iv, .. aes.EncryptCbc(data, iv)];

            var dictionary = body[..stream.Index] + $"/Length {encrypted.Length}" + body[(stream.Groups["length"].Index + stream.Groups["length"].Length)..(stream.Index + stream.Length)];
            output.AddRange(Encoding.Latin1.GetBytes($"{number} {generation} obj{dictionary}"));
            output.AddRange(encrypted);
            output.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        }

        var size = offsets.Keys.Max() + 1;
        var xref = output.Count;
        var table = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++) table.Append(offsets.TryGetValue(n, out var at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");

        var encrypt = $"/Encrypt << /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /Length 16 /CFM /AESV2 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /EncryptMetadata false"
            + $" /P {permissions} /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}{new string('0', 32)}> >>";
        var ids = existingId.Success ? "" : $" /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]";
        var trailerEntries = Regex.Replace(trailerDictionary, @"/Size \d+", $"/Size {size}");
        table.Append($"trailer\n<<{trailerEntries} {encrypt}{ids} >>\nstartxref\n{xref}\n%%EOF\n");
        output.AddRange(Encoding.Latin1.GetBytes(table.ToString()));
        return [.. output];
    }
}
