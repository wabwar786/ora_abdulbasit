using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Orapmshms.Services
{
    /// <summary>
    /// The QR code the invoice carries, drawn here instead of by a package.
    ///
    /// The old page used Zen.Barcode (CodeQrBarcodeDraw) and System.Drawing. Neither is in this
    /// application and the guide does not allow a .csproj or NuGet change, so the symbol and the
    /// PNG around it are both produced by this file. Nothing outside the framework is referenced.
    ///
    /// Scope, on purpose: byte mode, error correction level M, versions 1 to 10. That covers
    /// 154 characters, and the two things this page encodes - a verification address and an FBR
    /// invoice number - are far shorter. Anything longer is refused rather than drawn wrong.
    /// </summary>
    public static class BookingConfirmationInvoiceQrEncoder
    {
        /// <summary>
        /// The finished symbol as a data URI, ready for the src of an img, or an empty string
        /// when the text cannot be encoded. This never throws: an invoice must still print when
        /// the QR cannot be drawn.
        /// </summary>
        public static string ToPngDataUri(string text, int moduleSize = 8, int quietZone = 4)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                if (moduleSize < 1) moduleSize = 1;
                if (moduleSize > 20) moduleSize = 20;
                if (quietZone < 0) quietZone = 0;

                var matrix = Encode(text);
                if (matrix == null) return string.Empty;

                var png = ToPng(matrix, moduleSize, quietZone);
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            catch
            {
                // a missing QR is a cosmetic loss; a failed invoice is not
                return string.Empty;
            }
        }

        // ============================================================ the symbol

        /// <summary>Data capacity in bytes for level M, versions 1..10.</summary>
        private static readonly int[] ByteCapacityM = { 0, 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };

        /// <summary>Total data codewords for level M, versions 1..10.</summary>
        private static readonly int[] DataCodewordsM = { 0, 16, 28, 44, 64, 86, 108, 124, 154, 182, 216 };

        /// <summary>Error correction codewords PER BLOCK for level M, versions 1..10.</summary>
        private static readonly int[] EccPerBlockM = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };

        /// <summary>Number of blocks in group 1 / group 2 for level M, versions 1..10.</summary>
        private static readonly int[] Group1BlocksM = { 0, 1, 1, 1, 2, 2, 4, 4, 2, 3, 4 };
        private static readonly int[] Group2BlocksM = { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 1 };

        /// <summary>Alignment pattern centres by version (empty for version 1).</summary>
        private static readonly int[][] AlignmentCentres =
        {
            new int[0],                       // 0 - unused
            new int[0],                       // 1
            new[] { 6, 18 },                  // 2
            new[] { 6, 22 },                  // 3
            new[] { 6, 26 },                  // 4
            new[] { 6, 30 },                  // 5
            new[] { 6, 34 },                  // 6
            new[] { 6, 22, 38 },              // 7
            new[] { 6, 24, 42 },              // 8
            new[] { 6, 26, 46 },              // 9
            new[] { 6, 28, 50 }               // 10
        };

        /// <summary>
        /// The 15-bit format strings for level M, masks 0..7, already XOR-ed with 0x5412 as the
        /// specification requires. Held as a table because they are fixed and a table cannot drift.
        /// </summary>
        private static readonly int[] FormatBitsM =
        {
            0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0
        };

        /// <summary>true = dark module.</summary>
        private static bool[,]? Encode(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);

            var version = 0;
            for (var v = 1; v <= 10; v++)
            {
                if (data.Length <= ByteCapacityM[v]) { version = v; break; }
            }
            if (version == 0) return null;              // too long for this encoder

            var size = 17 + version * 4;
            var totalDataCodewords = DataCodewordsM[version];

            // ---------------- the bit stream ----------------
            var bits = new BitSink(totalDataCodewords * 8);
            bits.Write(0b0100, 4);                                       // byte mode
            bits.Write(data.Length, version <= 9 ? 8 : 16);              // character count
            foreach (var b in data) bits.Write(b, 8);

            // terminator, then pad to a whole byte, then the two alternating pad bytes
            var remaining = totalDataCodewords * 8 - bits.Length;
            bits.Write(0, Math.Min(4, remaining));
            while (bits.Length % 8 != 0) bits.Write(0, 1);
            var pad = true;
            while (bits.Length < totalDataCodewords * 8)
            {
                bits.Write(pad ? 0xEC : 0x11, 8);
                pad = !pad;
            }

            // ---------------- blocks and error correction ----------------
            var group1 = Group1BlocksM[version];
            var group2 = Group2BlocksM[version];
            var blockCount = group1 + group2;
            var shortLength = totalDataCodewords / blockCount;

            var dataBlocks = new List<byte[]>(blockCount);
            var eccBlocks = new List<byte[]>(blockCount);

            var all = bits.ToBytes();
            var offset = 0;
            for (var i = 0; i < blockCount; i++)
            {
                var length = i < group1 ? shortLength : shortLength + 1;
                var block = new byte[length];
                Array.Copy(all, offset, block, 0, length);
                offset += length;

                dataBlocks.Add(block);
                eccBlocks.Add(ReedSolomon(block, EccPerBlockM[version]));
            }

            // interleave: every block's first codeword, then every block's second, and so on
            var final = new List<byte>(totalDataCodewords + EccPerBlockM[version] * blockCount);
            var longest = shortLength + (group2 > 0 ? 1 : 0);
            for (var i = 0; i < longest; i++)
            {
                foreach (var block in dataBlocks)
                {
                    if (i < block.Length) final.Add(block[i]);
                }
            }
            for (var i = 0; i < EccPerBlockM[version]; i++)
            {
                foreach (var block in eccBlocks) final.Add(block[i]);
            }

            // ---------------- the grid ----------------
            var modules = new bool[size, size];
            var reserved = new bool[size, size];

            PlaceFinder(modules, reserved, 0, 0, size);
            PlaceFinder(modules, reserved, size - 7, 0, size);
            PlaceFinder(modules, reserved, 0, size - 7, size);
            PlaceTiming(modules, reserved, size);
            PlaceAlignment(modules, reserved, version, size);
            ReserveFormat(reserved, size);
            if (version >= 7) ReserveVersion(reserved, size);

            // the one module that is always dark
            modules[8, size - 8] = true;
            reserved[8, size - 8] = true;

            PlaceData(modules, reserved, final, size);

            // ---------------- mask, format, version ----------------
            var bestMask = 0;
            var bestPenalty = int.MaxValue;
            bool[,]? best = null;

            for (var mask = 0; mask < 8; mask++)
            {
                var candidate = (bool[,])modules.Clone();
                ApplyMask(candidate, reserved, mask, size);
                WriteFormat(candidate, mask, size);
                if (version >= 7) WriteVersion(candidate, version, size);

                var penalty = Penalty(candidate, size);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                    best = candidate;
                }
            }

            _ = bestMask;
            return best;
        }

        private static void PlaceFinder(bool[,] modules, bool[,] reserved, int x, int y, int size)
        {
            for (var dy = -1; dy <= 7; dy++)
            {
                for (var dx = -1; dx <= 7; dx++)
                {
                    var px = x + dx;
                    var py = y + dy;
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;

                    var inRing = (dx >= 0 && dx <= 6 && (dy == 0 || dy == 6))
                              || (dy >= 0 && dy <= 6 && (dx == 0 || dx == 6));
                    var inCore = dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4;

                    modules[px, py] = inRing || inCore;
                    reserved[px, py] = true;
                }
            }
        }

        private static void PlaceTiming(bool[,] modules, bool[,] reserved, int size)
        {
            for (var i = 8; i < size - 8; i++)
            {
                var dark = i % 2 == 0;
                modules[i, 6] = dark; reserved[i, 6] = true;
                modules[6, i] = dark; reserved[6, i] = true;
            }
        }

        private static void PlaceAlignment(bool[,] modules, bool[,] reserved, int version, int size)
        {
            var centres = AlignmentCentres[version];
            foreach (var cy in centres)
            {
                foreach (var cx in centres)
                {
                    // the three finder corners have no alignment pattern
                    if (cx == 6 && cy == 6) continue;
                    if (cx == 6 && cy == size - 7) continue;
                    if (cx == size - 7 && cy == 6) continue;

                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var px = cx + dx;
                            var py = cy + dy;
                            modules[px, py] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                            reserved[px, py] = true;
                        }
                    }
                }
            }
        }

        private static void ReserveFormat(bool[,] reserved, int size)
        {
            for (var i = 0; i <= 8; i++)
            {
                if (i != 6) { reserved[i, 8] = true; reserved[8, i] = true; }
            }
            for (var i = 0; i < 8; i++)
            {
                reserved[size - 1 - i, 8] = true;
                reserved[8, size - 1 - i] = true;
            }
        }

        private static void ReserveVersion(bool[,] reserved, int size)
        {
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    reserved[i, size - 11 + j] = true;
                    reserved[size - 11 + j, i] = true;
                }
            }
        }

        private static void PlaceData(bool[,] modules, bool[,] reserved, List<byte> codewords, int size)
        {
            var bitIndex = 0;
            var total = codewords.Count * 8;
            var upward = true;

            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;                       // the vertical timing column is skipped

                for (var step = 0; step < size; step++)
                {
                    var y = upward ? size - 1 - step : step;

                    for (var c = 0; c < 2; c++)
                    {
                        var x = right - c;
                        if (reserved[x, y]) continue;

                        var dark = false;
                        if (bitIndex < total)
                        {
                            var b = codewords[bitIndex >> 3];
                            dark = ((b >> (7 - (bitIndex & 7))) & 1) == 1;
                            bitIndex++;
                        }
                        modules[x, y] = dark;
                    }
                }
                upward = !upward;
            }
        }

        private static void ApplyMask(bool[,] modules, bool[,] reserved, int mask, int size)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (reserved[x, y]) continue;
                    if (MaskAt(mask, x, y)) modules[x, y] = !modules[x, y];
                }
            }
        }

        private static bool MaskAt(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (y + x) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (y + x) % 3 == 0;
                case 4: return (y / 2 + x / 3) % 2 == 0;
                case 5: return (y * x) % 2 + (y * x) % 3 == 0;
                case 6: return ((y * x) % 2 + (y * x) % 3) % 2 == 0;
                default: return ((y + x) % 2 + (y * x) % 3) % 2 == 0;
            }
        }

        /// <summary>
        /// The fifteen format modules, twice. Two things here are easy to get wrong and are
        /// checked against a reference encoder in the test notes: the string is written most
        /// significant bit FIRST (hence 14 - i), and the second copy is seven modules up the
        /// bottom-left column and then eight along the top-right row - not eight and seven.
        /// </summary>
        private static void WriteFormat(bool[,] modules, int mask, int size)
        {
            var format = FormatBitsM[mask];

            // first copy: around the top-left finder
            for (var i = 0; i <= 5; i++) modules[i, 8] = Bit(format, 14 - i);
            modules[7, 8] = Bit(format, 14 - 6);
            modules[8, 8] = Bit(format, 14 - 7);
            modules[8, 7] = Bit(format, 14 - 8);
            for (var i = 9; i <= 14; i++) modules[8, 14 - i] = Bit(format, 14 - i);

            // second copy: up the bottom-left column, then along the top-right row
            for (var i = 0; i <= 6; i++) modules[8, size - 1 - i] = Bit(format, 14 - i);
            for (var i = 7; i <= 14; i++) modules[size - 15 + i, 8] = Bit(format, 14 - i);

            modules[8, size - 8] = true;                    // always dark
        }

        private static void WriteVersion(bool[,] modules, int version, int size)
        {
            // the 18-bit version string: six version bits and a twelve-bit BCH remainder
            var remainder = version;
            for (var i = 0; i < 12; i++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            }
            var bits = ((version << 12) | (remainder & 0xFFF)) & 0x3FFFF;

            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) == 1;
                modules[i / 3, size - 11 + i % 3] = dark;
                modules[size - 11 + i % 3, i / 3] = dark;
            }
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) == 1;

        /// <summary>The four penalty rules, used only to choose the mask.</summary>
        private static int Penalty(bool[,] m, int size)
        {
            var score = 0;

            // rule 1 - runs of five or more
            for (var y = 0; y < size; y++)
            {
                var runColour = m[0, y]; var run = 1;
                for (var x = 1; x < size; x++)
                {
                    if (m[x, y] == runColour) run++;
                    else { if (run >= 5) score += 3 + (run - 5); runColour = m[x, y]; run = 1; }
                }
                if (run >= 5) score += 3 + (run - 5);
            }
            for (var x = 0; x < size; x++)
            {
                var runColour = m[x, 0]; var run = 1;
                for (var y = 1; y < size; y++)
                {
                    if (m[x, y] == runColour) run++;
                    else { if (run >= 5) score += 3 + (run - 5); runColour = m[x, y]; run = 1; }
                }
                if (run >= 5) score += 3 + (run - 5);
            }

            // rule 2 - 2x2 blocks of one colour
            for (var y = 0; y < size - 1; y++)
            {
                for (var x = 0; x < size - 1; x++)
                {
                    var c = m[x, y];
                    if (m[x + 1, y] == c && m[x, y + 1] == c && m[x + 1, y + 1] == c) score += 3;
                }
            }

            // rule 3 - the finder-like sequence
            var pattern1 = new[] { true, false, true, true, true, false, true, false, false, false, false };
            var pattern2 = new[] { false, false, false, false, true, false, true, true, true, false, true };
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x <= size - 11; x++)
                {
                    if (Matches(m, x, y, pattern1, true) || Matches(m, x, y, pattern2, true)) score += 40;
                }
            }
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y <= size - 11; y++)
                {
                    if (Matches(m, x, y, pattern1, false) || Matches(m, x, y, pattern2, false)) score += 40;
                }
            }

            // rule 4 - how far the dark share is from half
            var dark = 0;
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (m[x, y]) dark++;
            var percent = dark * 100 / (size * size);
            score += Math.Abs(percent - 50) / 5 * 10;

            return score;
        }

        private static bool Matches(bool[,] m, int x, int y, bool[] pattern, bool horizontal)
        {
            for (var i = 0; i < pattern.Length; i++)
            {
                var value = horizontal ? m[x + i, y] : m[x, y + i];
                if (value != pattern[i]) return false;
            }
            return true;
        }

        // ============================================================ Reed-Solomon over GF(256)

        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static BookingConfirmationInvoiceQrEncoder()
        {
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;           // the QR primitive polynomial
            }
            for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        private static byte Multiply(byte a, byte b) =>
            a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

        private static byte[] Generator(int degree)
        {
            var poly = new byte[] { 1 };
            for (var i = 0; i < degree; i++)
            {
                var next = new byte[poly.Length + 1];
                for (var j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= Multiply(poly[j], Exp[i]);
                }
                poly = next;
            }
            return poly;
        }

        private static byte[] ReedSolomon(byte[] data, int eccLength)
        {
            var generator = Generator(eccLength);
            var result = new byte[data.Length + eccLength];
            Array.Copy(data, result, data.Length);

            for (var i = 0; i < data.Length; i++)
            {
                var factor = result[i];
                if (factor == 0) continue;
                for (var j = 0; j < generator.Length; j++)
                {
                    result[i + j] ^= Multiply(generator[j], factor);
                }
            }

            var ecc = new byte[eccLength];
            Array.Copy(result, data.Length, ecc, 0, eccLength);
            return ecc;
        }

        // ============================================================ the PNG

        /// <summary>
        /// A greyscale 8-bit PNG, written by hand. Deflate comes from the framework
        /// (System.IO.Compression); the zlib wrapper and the CRC are three lines each.
        /// </summary>
        private static byte[] ToPng(bool[,] modules, int moduleSize, int quietZone)
        {
            var count = modules.GetLength(0);
            var side = (count + quietZone * 2) * moduleSize;

            // raw scan lines: one filter byte (0 = none) then one byte per pixel
            var raw = new byte[side * (side + 1)];
            var p = 0;
            for (var y = 0; y < side; y++)
            {
                raw[p++] = 0;
                var my = y / moduleSize - quietZone;
                for (var x = 0; x < side; x++)
                {
                    var mx = x / moduleSize - quietZone;
                    var dark = mx >= 0 && my >= 0 && mx < count && my < count && modules[mx, my];
                    raw[p++] = dark ? (byte)0x00 : (byte)0xFF;
                }
            }

            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, side);
            WriteBigEndian(ihdr, 4, side);
            ihdr[8] = 8;        // bit depth
            ihdr[9] = 0;        // colour type: greyscale
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(output, "IHDR", ihdr);

            WriteChunk(output, "IDAT", ZlibCompress(raw));
            WriteChunk(output, "IEND", Array.Empty<byte>());

            return output.ToArray();
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x78);                     // zlib header: deflate, 32K window
            stream.WriteByte(0x01);                     // no preset dictionary, fastest
            using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true))
            {
                deflate.Write(data, 0, data.Length);
            }
            var adler = Adler32(data);
            stream.WriteByte((byte)(adler >> 24));
            stream.WriteByte((byte)(adler >> 16));
            stream.WriteByte((byte)(adler >> 8));
            stream.WriteByte((byte)adler);
            return stream.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[4];
            WriteBigEndian(header, 0, data.Length);
            stream.Write(header, 0, 4);

            var name = Encoding.ASCII.GetBytes(type);
            stream.Write(name, 0, 4);
            stream.Write(data, 0, data.Length);

            var crcInput = new byte[4 + data.Length];
            Array.Copy(name, crcInput, 4);
            Array.Copy(data, 0, crcInput, 4, data.Length);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, unchecked((int)Crc32(crcInput)));
            stream.Write(crc, 0, 4);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var value in data) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        // ============================================================ a small bit writer

        private sealed class BitSink
        {
            private readonly List<byte> _bytes;
            private int _bitLength;

            public BitSink(int expectedBits)
            {
                _bytes = new List<byte>(expectedBits / 8 + 2);
            }

            public int Length => _bitLength;

            public void Write(int value, int bitCount)
            {
                for (var i = bitCount - 1; i >= 0; i--)
                {
                    if (_bitLength % 8 == 0) _bytes.Add(0);
                    if (((value >> i) & 1) == 1)
                        _bytes[_bitLength / 8] |= (byte)(1 << (7 - _bitLength % 8));
                    _bitLength++;
                }
            }

            public byte[] ToBytes() => _bytes.ToArray();
        }
    }
}
