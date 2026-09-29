// FlashToUnity - minimal PNG encoder (RGBA8, filter 0, zlib via DeflateStream).
// Pure C# so it compiles both inside Unity and in the standalone test harness.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FlashToUnity.Core
{
    public static class PngWriter
    {
        public static byte[] EncodeRgba(int width, int height, byte[] rgba)
        {
            if (rgba.Length < width * height * 4) throw new ArgumentException("pixel buffer too small");

            byte[] filtered = new byte[height * (width * 4 + 1)];
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 4;
                int dst = y * (width * 4 + 1);
                filtered[dst] = 0; // filter type None
                Array.Copy(rgba, src, filtered, dst + 1, width * 4);
            }

            byte[] idat = ZlibCompress(filtered);

            using (MemoryStream ms = new MemoryStream())
            {
                byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                ms.Write(signature, 0, 8);
                byte[] ihdr = new byte[13];
                WriteBE32(ihdr, 0, (uint)width);
                WriteBE32(ihdr, 4, (uint)height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 6;  // color type RGBA
                ihdr[10] = 0; // deflate
                ihdr[11] = 0; // filter
                ihdr[12] = 0; // interlace
                WriteChunk(ms, 0x49484452u, ihdr, 13);                     // IHDR
                WriteChunk(ms, 0x49444154u, idat, idat.Length);            // IDAT
                WriteChunk(ms, 0x49454E44u, null, 0);                      // IEND
                return ms.ToArray();
            }
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using (MemoryStream outMs = new MemoryStream())
            {
                outMs.WriteByte(0x78); // zlib header (deflate, 32K window)
                outMs.WriteByte(0x9C);
                using (DeflateStream ds = new DeflateStream(outMs, CompressionLevel.Optimal, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data, 0, data.Length);
                outMs.WriteByte((byte)(adler >> 24));
                outMs.WriteByte((byte)(adler >> 16));
                outMs.WriteByte((byte)(adler >> 8));
                outMs.WriteByte((byte)adler);
                return outMs.ToArray();
            }
        }

        private static void WriteChunk(Stream s, uint type, byte[] data, int len)
        {
            byte[] lenBuf = new byte[4];
            WriteBE32(lenBuf, 0, (uint)len);
            s.Write(lenBuf, 0, 4);
            byte[] typeBuf = new byte[4];
            typeBuf[0] = (byte)(type >> 24); typeBuf[1] = (byte)(type >> 16);
            typeBuf[2] = (byte)(type >> 8); typeBuf[3] = (byte)type;
            s.Write(typeBuf, 0, 4);
            if (data != null && len > 0) s.Write(data, 0, len);
            uint crc = Crc32(typeBuf, data, len);
            byte[] crcBuf = new byte[4];
            WriteBE32(crcBuf, 0, crc);
            s.Write(crcBuf, 0, 4);
        }

        private static void WriteBE32(byte[] buf, int o, uint v)
        {
            buf[o] = (byte)(v >> 24); buf[o + 1] = (byte)(v >> 16);
            buf[o + 2] = (byte)(v >> 8); buf[o + 3] = (byte)v;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] typeBytes, byte[] data, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < 4; i++) c = CrcTable[(c ^ typeBytes[i]) & 0xFF] ^ (c >> 8);
            if (data != null)
                for (int i = 0; i < len; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data, int offset, int count)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < count; i++)
            {
                a = (a + data[offset + i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        // ---------------- minimal PNG decoder (8-bit RGB/RGBA/grayscale, non-interlaced) ----------------

        public static byte[] DecodeRgba(byte[] png, out int width, out int height)
        {
            width = 0; height = 0;
            if (png == null || png.Length < 8 || png[0] != 0x89) throw new InvalidDataException("不是 PNG 文件");
            int pos = 8;
            int bitDepth = -1, colorType = -1;
            byte[] idat = null;
            while (pos + 8 <= png.Length)
            {
                int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                pos += 8;
                if (type == "IHDR")
                {
                    width = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                    height = (png[pos + 4] << 24) | (png[pos + 5] << 16) | (png[pos + 6] << 8) | png[pos + 7];
                    bitDepth = png[pos + 8];
                    colorType = png[pos + 9];
                }
                else if (type == "IDAT")
                {
                    if (idat == null) idat = new byte[len];
                    else { byte[] grown = new byte[idat.Length + len]; Array.Copy(idat, grown, idat.Length); idat = grown; }
                    Array.Copy(png, pos, idat, idat.Length - len, len);
                }
                else if (type == "IEND") break;
                pos += len + 4;
            }
            if (bitDepth != 8) throw new InvalidDataException("仅支持 8-bit PNG（得到 " + bitDepth + "）");
            if (colorType != 6 && colorType != 2 && colorType != 0) throw new InvalidDataException("不支持的 PNG 颜色类型 " + colorType);
            int channels = colorType == 6 ? 4 : (colorType == 2 ? 3 : 1);
            int stride = width * channels;

            byte[] raw = InflateZlib(idat);
            byte[] lines = new byte[height * stride];
            byte[] prev = new byte[stride];
            int rp = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[rp]; rp++;
                for (int i = 0; i < stride; i++)
                {
                    int x = lines[y * stride + i] = raw[rp + i];
                    int left = i >= channels ? lines[y * stride + i - channels] : 0;
                    int up = y > 0 ? prev[i] : 0;
                    int ul = (y > 0 && i >= channels) ? prev[i - channels] : 0;
                    switch (filter)
                    {
                        case 0: break;
                        case 1: lines[y * stride + i] = (byte)(x + left); break;
                        case 2: lines[y * stride + i] = (byte)(x + up); break;
                        case 3: lines[y * stride + i] = (byte)(x + ((left + up) >> 1)); break;
                        case 4:
                            {
                                int p = left + up - ul, pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - ul);
                                int pred = (pa <= pb && pa <= pc) ? left : (pb <= pc ? up : ul);
                                lines[y * stride + i] = (byte)(x + pred);
                                break;
                            }
                        default: throw new InvalidDataException("未知 PNG filter " + filter);
                    }
                }
                Array.Copy(lines, y * stride, prev, 0, stride);
                rp += stride;
            }

            byte[] outBuf = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int q = i * 4;
                if (colorType == 6) { outBuf[q] = lines[i * 4]; outBuf[q + 1] = lines[i * 4 + 1]; outBuf[q + 2] = lines[i * 4 + 2]; outBuf[q + 3] = lines[i * 4 + 3]; }
                else if (colorType == 2) { outBuf[q] = lines[i * 3]; outBuf[q + 1] = lines[i * 3 + 1]; outBuf[q + 2] = lines[i * 3 + 2]; outBuf[q + 3] = 255; }
                else { outBuf[q] = outBuf[q + 1] = outBuf[q + 2] = lines[i]; outBuf[q + 3] = 255; }
            }
            return outBuf;
        }

        private static byte[] InflateZlib(byte[] data)
        {
            using (MemoryStream input = new MemoryStream(data, 2, data.Length - 2))
            using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                ds.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
