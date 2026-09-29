// FlashToUnity - SWF binary parser (FlashToUnity.Core).
// Supports the subset of SWF needed by Flash-authored cartoon animations:
//   DefineShape 1/2/3, DefineBitsLossless / DefineBitsLossless2,
//   DefineSprite, PlaceObject / PlaceObject2 / PlaceObject3 (filters ignored),
//   RemoveObject / RemoveObject2, FrameLabel, DefineSceneAndFrameLabelData,
//   SymbolClass / ExportAssets (for symbol names).
// Animation matrices are converted to pixel space at parse time.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FlashToUnity.Core
{
    public sealed class SwfBitmap
    {
        public int Id;
        public int Width;
        public int Height;
        public byte[] Rgba;    // 4 bytes per pixel, straight (non-premultiplied) alpha
        public bool HasAlpha;
    }

    public sealed class SwfSprite
    {
        public int Id;
        public SwfTimeline Timeline;
    }

    /// <summary>A timeline: per-frame lists of place/remove commands, in tag order.</summary>
    public sealed class SwfTimeline
    {
        public int FrameCount;
        public List<List<SwfPlaceCommand>> Frames = new List<List<SwfPlaceCommand>>();
    }

    public sealed class SwfCharacter
    {
        public int Id;
        public SwfCharacterKind Kind;
        public SwfShape Shape;
        public SwfBitmap Bitmap;
        public SwfSprite Sprite;
        public SwfRect Bounds;    // pixels
        public string Name;       // linkage/class name if the file carried one
    }

    public sealed class SwfMovie
    {
        public string SourceName;
        public float FrameRate;
        public int FrameCount;
        public SwfRect Stage;
        public string DocumentClassName;
        public Dictionary<int, SwfCharacter> Characters = new Dictionary<int, SwfCharacter>();
        public SwfTimeline MainTimeline = new SwfTimeline();
        public List<SwfFrameLabel> Labels = new List<SwfFrameLabel>();
        public List<string> Scenes = new List<string>();
        public List<string> Warnings = new List<string>();

        internal readonly Dictionary<int, SwfRect> SpriteBoundsCache = new Dictionary<int, SwfRect>();

        public SwfCharacter GetCharacter(int id)
        {
            SwfCharacter ch;
            return Characters.TryGetValue(id, out ch) ? ch : null;
        }
    }

    public static class SwfParser
    {
        public static SwfMovie Parse(byte[] raw, string sourceName)
        {
            if (raw == null || raw.Length < 8) throw new InvalidDataException("SWF 文件为空或已损坏");
            string sig = Encoding.ASCII.GetString(raw, 0, 3);
            byte[] body;
            if (sig == "CWS" || sig == "ZWS")
            {
                body = Inflate(raw, 8, raw.Length - 8);
            }
            else if (sig == "FWS")
            {
                body = new byte[raw.Length - 8];
                Array.Copy(raw, 8, body, 0, body.Length);
            }
            else
            {
                throw new InvalidDataException("不是有效的 SWF 文件（文件头 '" + sig + "'）");
            }

            SwfMovie movie = new SwfMovie();
            movie.SourceName = sourceName;
            SwfBitReader r = new SwfBitReader(body, 0);
            movie.Stage = r.ReadRect();
            int o = r.BytePos;
            movie.FrameRate = ReadU16(body, o) / 256f;
            o += 2;
            movie.FrameCount = ReadU16(body, o);
            o += 2;

            ParseTagStream(body, o, body.Length, movie, null, 0);
            if (movie.MainTimeline.FrameCount < movie.FrameCount) movie.MainTimeline.FrameCount = movie.FrameCount;
            if (movie.MainTimeline.FrameCount == 0) movie.MainTimeline.FrameCount = movie.MainTimeline.Frames.Count;
            return movie;
        }

        private static byte[] Inflate(byte[] src, int offset, int count)
        {
            // zlib stream = 2-byte header + raw deflate + 4-byte adler
            using (MemoryStream input = new MemoryStream(src, offset + 2, count - 2))
            using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                ds.CopyTo(output);
                return output.ToArray();
            }
        }

        // ---------- byte-aligned little-endian helpers ----------

        private static ushort ReadU16(byte[] d, int o) { return (ushort)(d[o] | (d[o + 1] << 8)); }
        private static uint ReadU32(byte[] d, int o) { return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)); }

        private static uint ReadEncodedU32(byte[] d, ref int o)
        {
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = d[o]; o++;
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        private static string ReadString(byte[] d, ref int o)
        {
            int end = o;
            while (end < d.Length && d[end] != 0) end++;
            string s = Encoding.UTF8.GetString(d, o, end - o);
            o = end + 1;
            return s;
        }

        // ---------- tag stream ----------

        private sealed class TimelineBuilder
        {
            public SwfTimeline Timeline = new SwfTimeline();
            public List<SwfPlaceCommand> Current = new List<SwfPlaceCommand>();
            public int CurrentFrame;

            public void EndFrame()
            {
                Timeline.Frames.Add(Current);
                Current = new List<SwfPlaceCommand>();
                CurrentFrame++;
            }

            public void Finish(bool isMain)
            {
                if (Current.Count > 0 || Timeline.Frames.Count == 0) Timeline.Frames.Add(Current);
                Timeline.FrameCount = Timeline.Frames.Count;
            }
        }

        private static void ParseTagStream(byte[] d, int pos, int end, SwfMovie movie, SwfCharacter ownerSprite, int depth)
        {
            TimelineBuilder builder = new TimelineBuilder();
            bool mainStream = ownerSprite == null;
            int o = pos;

            while (o < end)
            {
                ushort codeLen = ReadU16(d, o); o += 2;
                int code = codeLen >> 6;
                int len = codeLen & 0x3F;
                if (len == 0x3F) { len = (int)ReadU32(d, o); o += 4; }
                int bodyStart = o;
                int bodyEnd = o + len;
                if (bodyEnd > d.Length) bodyEnd = d.Length;
                o = bodyEnd;

                switch (code)
                {
                    case 0: // End
                        if (!mainStream) goto done;
                        break;

                    case 1: // ShowFrame
                        builder.EndFrame();
                        break;

                    case 4: ParsePlaceObject(d, bodyStart, bodyEnd, builder); break;
                    case 26: // PlaceObject2
                    case 70: // PlaceObject3
                        ParsePlaceObject2(d, bodyStart, bodyEnd, code, builder);
                        break;
                    case 5: // RemoveObject
                        if (bodyStart + 4 <= bodyEnd)
                        {
                            SwfPlaceCommand cmd = new SwfPlaceCommand();
                            cmd.IsRemove = true;
                            cmd.Depth = ReadU16(d, bodyStart + 2);
                            builder.Current.Add(cmd);
                        }
                        break;
                    case 28: // RemoveObject2
                        if (bodyStart + 2 <= bodyEnd)
                        {
                            SwfPlaceCommand cmd = new SwfPlaceCommand();
                            cmd.IsRemove = true;
                            cmd.Depth = ReadU16(d, bodyStart);
                            builder.Current.Add(cmd);
                        }
                        break;

                    case 2: ParseDefineShape(d, bodyStart, bodyEnd, 1, movie); break;
                    case 22: ParseDefineShape(d, bodyStart, bodyEnd, 2, movie); break;
                    case 32: ParseDefineShape(d, bodyStart, bodyEnd, 3, movie); break;

                    case 20: // DefineBitsLossless
                    case 36: // DefineBitsLossless2
                        ParseBitsLossless(d, bodyStart, bodyEnd, code == 36, movie);
                        break;
                    case 6: AddWarning(movie, "DefineBits (JPEG) 位图暂不支持，已跳过"); break;
                    case 21: AddWarning(movie, "DefineBitsJPEG2 位图暂不支持，已跳过"); break;
                    case 35: AddWarning(movie, "DefineBitsJPEG3 位图暂不支持，已跳过"); break;

                    case 39: // DefineSprite
                        ParseDefineSprite(d, bodyStart, bodyEnd, movie, depth);
                        break;

                    case 43: // FrameLabel
                        if (mainStream)
                        {
                            int p = bodyStart;
                            string name = ReadString(d, ref p);
                            movie.Labels.Add(new SwfFrameLabel { Frame = builder.CurrentFrame, Name = name });
                        }
                        break;

                    case 86: // DefineSceneAndFrameLabelData
                        if (mainStream) ParseSceneAndFrameLabelData(d, bodyStart, bodyEnd, builder, movie);
                        break;

                    case 76: // SymbolClass
                        {
                            int p = bodyStart;
                            ushort n = ReadU16(d, p); p += 2;
                            for (int i = 0; i < n && p < bodyEnd; i++)
                            {
                                ushort cid = ReadU16(d, p); p += 2;
                                string name = ReadString(d, ref p);
                                SwfCharacter ch = movie.GetCharacter(cid);
                                if (ch != null && string.IsNullOrEmpty(ch.Name)) ch.Name = name;
                                else if (cid == 0) movie.DocumentClassName = name;
                            }
                        }
                        break;

                    case 56: // ExportAssets
                        {
                            int p = bodyStart;
                            ushort n = ReadU16(d, p); p += 2;
                            for (int i = 0; i < n && p < bodyEnd; i++)
                            {
                                ushort cid = ReadU16(d, p); p += 2;
                                string name = ReadString(d, ref p);
                                SwfCharacter ch = movie.GetCharacter(cid);
                                if (ch != null && string.IsNullOrEmpty(ch.Name)) ch.Name = name;
                            }
                        }
                        break;

                    default:
                        break; // DoABC, sounds, fonts... irrelevant to animation
                }
            }
        done:
            builder.Finish(mainStream);
            if (mainStream) movie.MainTimeline = builder.Timeline;
            else ownerSprite.Sprite.Timeline = builder.Timeline;
        }

        private static void AddWarning(SwfMovie movie, string msg)
        {
            if (movie.Warnings.Count < 200) movie.Warnings.Add(msg);
        }

        private static void ParseSceneAndFrameLabelData(byte[] d, int start, int end, TimelineBuilder builder, SwfMovie movie)
        {
            int o = start;
            uint sceneCount = ReadEncodedU32(d, ref o);
            for (uint i = 0; i < sceneCount && o < end; i++)
            {
                ReadEncodedU32(d, ref o); // scene offset
                string name = ReadString(d, ref o);
                movie.Scenes.Add(name);
            }
            uint labelCount = ReadEncodedU32(d, ref o);
            for (uint i = 0; i < labelCount && o < end; i++)
            {
                uint frame = ReadEncodedU32(d, ref o);
                string name = ReadString(d, ref o);
                movie.Labels.Add(new SwfFrameLabel { Frame = (int)frame, Name = name });
            }
        }

        private static void ParseDefineSprite(byte[] d, int start, int end, SwfMovie movie, int depth)
        {
            if (depth > 4) { AddWarning(movie, "DefineSprite 嵌套过深，已跳过"); return; }
            int o = start;
            int id = ReadU16(d, o); o += 2;
            o += 2; // frame count field; we count ShowFrames instead

            SwfCharacter ch = new SwfCharacter();
            ch.Id = id;
            ch.Kind = SwfCharacterKind.Sprite;
            ch.Sprite = new SwfSprite();
            ch.Sprite.Id = id;
            movie.Characters[id] = ch;

            ParseTagStream(d, o, end, movie, ch, depth + 1);
        }

        // ---------- place object ----------

        private static void ParsePlaceObject(byte[] d, int start, int end, TimelineBuilder builder)
        {
            SwfPlaceCommand cmd = new SwfPlaceCommand();
            int o = start;
            cmd.CharacterId = ReadU16(d, o); o += 2;
            cmd.Depth = ReadU16(d, o); o += 2;
            cmd.HasCharacter = true;
            if (o < end) // matrix present if any data remains
            {
                SwfBitReader r = new SwfBitReader(d, o);
                cmd.Matrix = r.ReadMatrix();
                cmd.HasMatrix = true;
                o = r.BytePos;
                if (o < end) // then an optional plain (3-channel) color transform
                {
                    cmd.Color = r.ReadColorTransform();
                    cmd.HasColor = true;
                }
            }
            builder.Current.Add(cmd);
        }

        private static void ParsePlaceObject2(byte[] d, int start, int end, int tagCode, TimelineBuilder builder)
        {
            SwfPlaceCommand cmd = new SwfPlaceCommand();
            int o = start;
            byte flags = d[o]; o++;
            if (tagCode == 70) o++; // PlaceObject3 second flags byte (filters/blend info), ignored
            cmd.HasClipDepth = (flags & 0x40) != 0;
            bool hasName = (flags & 0x20) != 0;
            bool hasRatio = (flags & 0x10) != 0;
            cmd.HasColor = (flags & 0x08) != 0;
            cmd.HasMatrix = (flags & 0x04) != 0;
            cmd.HasCharacter = (flags & 0x02) != 0;
            cmd.Move = (flags & 0x01) != 0;

            cmd.Depth = ReadU16(d, o); o += 2;
            if (cmd.HasCharacter) { cmd.CharacterId = ReadU16(d, o); o += 2; }
            if (cmd.HasMatrix) { SwfBitReader r = new SwfBitReader(d, o); cmd.Matrix = r.ReadMatrix(); o = r.BytePos; }
            if (cmd.HasColor) { SwfBitReader r = new SwfBitReader(d, o); cmd.Color = r.ReadColorTransformWithAlpha(); o = r.BytePos; }
            if (hasRatio) o += 2;
            if (hasName) cmd.Name = ReadString(d, ref o);
            if (cmd.HasClipDepth) { cmd.ClipDepth = ReadU16(d, o); o += 2; }
            builder.Current.Add(cmd);
        }

        // ---------- shapes ----------

        private static void ParseDefineShape(byte[] d, int start, int end, int version, SwfMovie movie)
        {
            int o = start;
            int id = ReadU16(d, o); o += 2;
            SwfBitReader r = new SwfBitReader(d, o);
            SwfShape shape = new SwfShape();
            shape.Id = id;
            shape.Bounds = r.ReadRect();
            if (version >= 2) r.ReadRect(); // edge bounds
            if (version >= 3) r.ByteAlign();
            o = r.BytePos;
            if (version >= 3) o++; // shape flags byte (scaling stroke bits)

            ParseFillAndLineStyles(d, ref o, shape, version >= 3);

            SwfBitReader er = new SwfBitReader(d, o);
            int numFillBits = (int)er.ReadUB(4);
            int numLineBits = (int)er.ReadUB(4);

            SwfShapeContour contour = null;
            int fill0 = 0, fill1 = 0, line = 0;
            int curX = 0, curY = 0; // twips

            while (true)
            {
                uint typeFlag = er.ReadUB(1);
                if (typeFlag == 0)
                {
                    // NOTE: the spec says DefineShape1 has no StateNewStyles flag bit, but
                    // Flash CC exports v1 tags with the full 5 flag bits (verified against
                    // real PvZ files), so we always read 5 here.
                    uint newStyles = er.ReadUB(1);
                    uint lineStyleFlag = er.ReadUB(1);
                    uint fill1Flag = er.ReadUB(1);
                    uint fill0Flag = er.ReadUB(1);
                    uint moveToFlag = er.ReadUB(1);
                    if (newStyles == 0 && lineStyleFlag == 0 && fill1Flag == 0 && fill0Flag == 0 && moveToFlag == 0)
                        break; // end of shape records

                    if (moveToFlag != 0)
                    {
                        if (contour != null) shape.Contours.Add(contour);
                        contour = new SwfShapeContour();
                        int moveBits = (int)er.ReadUB(5);
                        curX += er.ReadSB(moveBits);
                        curY += er.ReadSB(moveBits);
                        contour.StartX = curX / 20f;
                        contour.StartY = curY / 20f;
                        contour.LastX = contour.StartX;
                        contour.LastY = contour.StartY;
                    }
                    if (fill0Flag != 0) fill0 = (int)er.ReadUB(numFillBits);
                    if (fill1Flag != 0) fill1 = (int)er.ReadUB(numFillBits);
                    if (lineStyleFlag != 0) line = (int)er.ReadUB(numLineBits);
                    if (newStyles != 0)
                    {
                        if (contour != null) { shape.Contours.Add(contour); contour = null; }
                        er.ByteAlign();
                        int p = er.BytePos;
                        ParseFillAndLineStyles(d, ref p, shape, version >= 3);
                        SwfBitReader r2 = new SwfBitReader(d, p);
                        numFillBits = (int)r2.ReadUB(4);
                        numLineBits = (int)r2.ReadUB(4);
                        er = r2;
                        fill0 = fill1 = line = 0;
                        curX = 0; curY = 0;
                        continue;
                    }
                    if (contour != null)
                    {
                        contour.FillStyle0 = fill0;
                        contour.FillStyle1 = fill1;
                        contour.LineStyle = line;
                    }
                }
                else
                {
                    uint straightFlag = er.ReadUB(1);
                    int numBits = (int)er.ReadUB(4) + 2;
                    if (contour == null)
                    {
                        // edge record without a preceding moveTo: implicit contour from origin
                        contour = new SwfShapeContour();
                        contour.StartX = 0; contour.StartY = 0;
                        contour.LastX = 0; contour.LastY = 0;
                        contour.FillStyle0 = fill0;
                        contour.FillStyle1 = fill1;
                        contour.LineStyle = line;
                    }
                    SwfShapeSegment s = new SwfShapeSegment();
                    s.X0 = contour.LastX; s.Y0 = contour.LastY;
                    if (straightFlag != 0)
                    {
                        int dx, dy;
                        uint general = er.ReadUB(1);
                        if (general != 0)
                        {
                            dx = er.ReadSB(numBits);
                            dy = er.ReadSB(numBits);
                        }
                        else
                        {
                            uint vert = er.ReadUB(1);
                            if (vert != 0) { dx = 0; dy = er.ReadSB(numBits); }
                            else { dx = er.ReadSB(numBits); dy = 0; }
                        }
                        curX += dx; curY += dy;
                        s.X1 = curX / 20f; s.Y1 = curY / 20f;
                        s.IsQuad = false;
                    }
                    else
                    {
                        int cdx = er.ReadSB(numBits);
                        int cdy = er.ReadSB(numBits);
                        int adx = er.ReadSB(numBits);
                        int ady = er.ReadSB(numBits);
                        s.CX = (curX + cdx) / 20f; s.CY = (curY + cdy) / 20f;
                        curX += cdx + adx; curY += cdy + ady;
                        s.X1 = curX / 20f; s.Y1 = curY / 20f;
                        s.IsQuad = true;
                    }
                    contour.Segments.Add(s);
                    contour.LastX = curX / 20f;
                    contour.LastY = curY / 20f;
                }
            }
            if (contour != null) shape.Contours.Add(contour);

            SwfCharacter ch = new SwfCharacter();
            ch.Id = id;
            ch.Kind = SwfCharacterKind.Shape;
            ch.Shape = shape;
            ch.Bounds = shape.Bounds;
            movie.Characters[id] = ch;
        }

        private static void ParseFillAndLineStyles(byte[] d, ref int o, SwfShape shape, bool withAlpha)
        {
            int fillCount = d[o]; o++;
            if (fillCount == 0xFF) { fillCount = ReadU16(d, o); o += 2; }
            for (int i = 0; i < fillCount; i++)
            {
                SwfFillStyle fs = new SwfFillStyle();
                byte type = d[o]; o++;
                fs.Type = (SwfFillType)type;
                switch (type)
                {
                    case 0x00:
                        fs.Color = withAlpha ? ReadRGBA(d, ref o) : ReadRGB(d, ref o);
                        break;
                    case 0x10: // linear gradient
                    case 0x12: // radial
                    case 0x13: // focal radial
                        {
                            SwfBitReader r = new SwfBitReader(d, o);
                            fs.Matrix = r.ReadMatrix();
                            o = r.BytePos;
                            int count = d[o]; o++;
                            fs.Gradient = new List<SwfGradientRecord>(count);
                            for (int g = 0; g < count; g++)
                            {
                                SwfGradientRecord gr;
                                gr.Ratio = d[o]; o++;
                                gr.Color = withAlpha ? ReadRGBA(d, ref o) : ReadRGB(d, ref o);
                                fs.Gradient.Add(gr);
                            }
                            if (type == 0x13)
                            {
                                fs.FocalPoint = (short)((d[o] << 8) | d[o + 1]) / 256f; // FIXED(8.8)
                                o += 2;
                            }
                        }
                        break;
                    case 0x40: case 0x41: case 0x42: case 0x43:
                        fs.BitmapId = ReadU16(d, o); o += 2;
                        {
                            SwfBitReader r = new SwfBitReader(d, o);
                            fs.Matrix = r.ReadMatrix();
                            o = r.BytePos;
                            // The bitmap fill matrix maps BITMAP PIXELS -> shape twips, so unlike
                            // placement/gradient matrices its scale part is in twips-per-pixel and
                            // must also be divided by 20 to work in pixel space.
                            fs.Matrix.A /= 20f; fs.Matrix.B /= 20f;
                            fs.Matrix.C /= 20f; fs.Matrix.D /= 20f;
                        }
                        break;
                    default:
                        throw new InvalidDataException("不支持的填充样式类型 0x" + type.ToString("X2"));
                }
                shape.FillStyles.Add(fs);
            }

            int lineCount = d[o]; o++;
            if (lineCount == 0xFF) { lineCount = ReadU16(d, o); o += 2; }
            for (int i = 0; i < lineCount; i++)
            {
                SwfLineStyle ls = new SwfLineStyle();
                ls.Width = ReadU16(d, o) / 20f; o += 2;
                ls.Color = withAlpha ? ReadRGBA(d, ref o) : ReadRGB(d, ref o);
                shape.LineStyles.Add(ls);
            }
        }

        private static SwfRGBA ReadRGB(byte[] d, ref int o)
        {
            SwfRGBA c = new SwfRGBA(d[o], d[o + 1], d[o + 2], 255);
            o += 3;
            return c;
        }

        private static SwfRGBA ReadRGBA(byte[] d, ref int o)
        {
            SwfRGBA c = new SwfRGBA(d[o], d[o + 1], d[o + 2], d[o + 3]);
            o += 4;
            return c;
        }

        // ---------- lossless bitmaps ----------

        private static void ParseBitsLossless(byte[] d, int start, int end, bool hasAlpha, SwfMovie movie)
        {
            int o = start;
            int id = ReadU16(d, o); o += 2;
            int format = d[o]; o++;
            int width = ReadU16(d, o); o += 2;
            int height = ReadU16(d, o); o += 2;
            byte[] compressed;
            try { compressed = Inflate(d, o, end - o); }
            catch (Exception ex)
            {
                AddWarning(movie, "位图 #" + id + "：zlib 解压失败（" + ex.Message + "）");
                return;
            }

            SwfBitmap bmp = new SwfBitmap();
            bmp.Id = id;
            bmp.Width = width;
            bmp.Height = height;
            bmp.HasAlpha = hasAlpha;
            bmp.Rgba = DecodeLossless(compressed, width, height, format, hasAlpha);
            if (bmp.Rgba == null)
            {
                AddWarning(movie, "位图 #" + id + "：不支持的无损格式 " + format);
                return;
            }

            SwfCharacter ch = new SwfCharacter();
            ch.Id = id;
            ch.Kind = SwfCharacterKind.Bitmap;
            ch.Bitmap = bmp;
            ch.Bounds = new SwfRect { MinX = 0, MinY = 0, MaxX = width, MaxY = height };
            movie.Characters[id] = ch;
        }

        internal static byte[] DecodeLossless(byte[] data, int width, int height, int format, bool hasAlpha)
        {
            if (width <= 0 || height <= 0) return null;
            byte[] outBuf = new byte[width * height * 4];

            if (format == 3) // colormapped
            {
                int entrySize = hasAlpha ? 4 : 3;
                int strideNoPad = width;
                int stridePadded = (width + 3) & ~3;
                int stride = strideNoPad;
                long neededNoPad = (long)strideNoPad * height;
                if (data.Length < neededNoPad)
                {
                    if (data.Length >= (long)stridePadded * height) stride = stridePadded;
                    else return null;
                }
                else if ((data.Length - neededNoPad) % entrySize != 0 && data.Length >= (long)stridePadded * height
                         && (data.Length - (long)stridePadded * height) % entrySize == 0)
                {
                    stride = stridePadded;
                }
                long mapBytes = data.Length - (long)stride * height;
                int mapSize = (int)(mapBytes / entrySize);
                if (mapSize <= 0 || mapSize > 256) return null;

                byte[] mapR = new byte[mapSize], mapG = new byte[mapSize], mapB = new byte[mapSize], mapA = new byte[mapSize];
                int mo = (int)(stride * height);
                for (int i = 0; i < mapSize; i++)
                {
                    if (hasAlpha)
                    {
                        mapA[i] = data[mo]; mapR[i] = data[mo + 1]; mapG[i] = data[mo + 2]; mapB[i] = data[mo + 3];
                        mo += 4;
                    }
                    else
                    {
                        mapA[i] = 255; mapR[i] = data[mo]; mapG[i] = data[mo + 1]; mapB[i] = data[mo + 2];
                        mo += 3;
                    }
                }

                int p = 0;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = data[row + x];
                        if (idx >= mapSize) idx = mapSize - 1;
                        int q = p * 4;
                        outBuf[q] = mapR[idx];
                        outBuf[q + 1] = mapG[idx];
                        outBuf[q + 2] = mapB[idx];
                        outBuf[q + 3] = mapA[idx];
                        p++;
                    }
                }
                return outBuf;
            }

            if (format == 4) // 15-bit packed
            {
                int stride = width * 2;
                if (data.Length < (long)stride * height) return null;
                int p = 0;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int v = (data[row + x * 2] << 8) | data[row + x * 2 + 1];
                        int rc = (v >> 10) & 31, gc = (v >> 5) & 31, bc = v & 31;
                        int q = p * 4;
                        outBuf[q] = (byte)((rc << 3) | (rc >> 2));
                        outBuf[q + 1] = (byte)((gc << 3) | (gc >> 2));
                        outBuf[q + 2] = (byte)((bc << 3) | (bc >> 2));
                        outBuf[q + 3] = 255;
                        p++;
                    }
                }
                return outBuf;
            }

            if (format == 5) // 32-bit
            {
                int stride = width * 4;
                if (data.Length < (long)stride * height) return null;
                for (int i = 0; i < width * height; i++)
                {
                    int s = i * 4, q = s;
                    if (hasAlpha)
                    {
                        // DefineBitsLossless2 format 5 stores PREMULTIPLIED ARGB
                        // (SWF19 ALPHABITMAPDATA). The renderer and PNG encoder use
                        // straight RGBA; leaving RGB premultiplied applies alpha twice
                        // and creates dark seams around every translucent part.
                        int alpha = data[s];
                        for (int channel = 0; channel < 3; channel++)
                            outBuf[q + channel] = alpha == 0 ? (byte)0
                                : (byte)Math.Min(255, (data[s + channel + 1] * 255 + alpha / 2) / alpha);
                        outBuf[q + 3] = (byte)alpha;
                    }
                    else { outBuf[q] = data[s + 1]; outBuf[q + 1] = data[s + 2]; outBuf[q + 2] = data[s + 3]; outBuf[q + 3] = 255; }
                }
                return outBuf;
            }

            return null;
        }

        // ---------- character bounds (recursive, memoized) ----------

        internal static SwfRect GetCharacterBounds(SwfMovie movie, SwfCharacter ch, int nest)
        {
            if (ch == null) return SwfRect.Empty;
            if (ch.Kind != SwfCharacterKind.Sprite) return ch.Bounds;
            SwfRect cached;
            if (movie.SpriteBoundsCache.TryGetValue(ch.Id, out cached)) return cached;
            if (nest > 4) return SwfRect.Empty;

            SwfRect bounds = SwfRect.Empty;
            Dictionary<int, SwfMatrix> matrices = new Dictionary<int, SwfMatrix>();
            Dictionary<int, int> chars = new Dictionary<int, int>();
            foreach (List<SwfPlaceCommand> frame in ch.Sprite.Timeline.Frames)
            {
                ApplyCommandsToState(frame, matrices, chars);
                foreach (KeyValuePair<int, int> kv in chars)
                {
                    SwfMatrix m;
                    if (!matrices.TryGetValue(kv.Key, out m)) continue;
                    SwfRect cb = GetCharacterBounds(movie, movie.GetCharacter(kv.Value), nest + 1);
                    if (cb.MaxX < cb.MinX) continue;
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    for (int i = 0; i < 4; i++)
                    {
                        float x = (i & 1) == 0 ? cb.MinX : cb.MaxX;
                        float y = (i & 2) == 0 ? cb.MinY : cb.MaxY;
                        float ox, oy;
                        m.Apply(x, y, out ox, out oy);
                        if (ox < minX) minX = ox;
                        if (oy < minY) minY = oy;
                        if (ox > maxX) maxX = ox;
                        if (oy > maxY) maxY = oy;
                    }
                    SwfRect t;
                    t.MinX = minX; t.MinY = minY; t.MaxX = maxX; t.MaxY = maxY;
                    bounds.Union(t);
                }
            }
            movie.SpriteBoundsCache[ch.Id] = bounds;
            return bounds;
        }

        private static void ApplyCommandsToState(List<SwfPlaceCommand> commands,
            Dictionary<int, SwfMatrix> matrices, Dictionary<int, int> chars)
        {
            foreach (SwfPlaceCommand c in commands)
            {
                if (c.IsRemove)
                {
                    matrices.Remove(c.Depth);
                    chars.Remove(c.Depth);
                }
                else if (c.HasCharacter)
                {
                    chars[c.Depth] = c.CharacterId;
                    matrices[c.Depth] = c.HasMatrix ? c.Matrix : SwfMatrix.Identity;
                }
                else if (chars.ContainsKey(c.Depth))
                {
                    if (c.HasMatrix) matrices[c.Depth] = c.Matrix;
                }
            }
        }
    }
}
