// FlashToUnity - SWF -> Unity animation converter
// Core types: bit reader, rect, matrix, color transform. No Unity dependencies.
// All matrices/colors in this file work in PIXEL space (SWF twips are converted at parse time).

using System;

namespace FlashToUnity.Core
{
    public struct SwfRect
    {
        public float MinX, MinY, MaxX, MaxY; // pixels

        public float Width { get { return MaxX - MinX; } }
        public float Height { get { return MaxY - MinY; } }

        public static readonly SwfRect Empty = new SwfRect { MinX = 0, MinY = 0, MaxX = 0, MaxY = 0 };

        public bool IsValid { get { return MaxX >= MinX && MaxY >= MinY; } }

        public void Union(SwfRect o)
        {
            if (o.MaxX < o.MinX || o.MaxY < o.MinY) return;
            if (MaxX < MinX || MaxY < MinY) { this = o; return; }
            if (o.MinX < MinX) MinX = o.MinX;
            if (o.MinY < MinY) MinY = o.MinY;
            if (o.MaxX > MaxX) MaxX = o.MaxX;
            if (o.MaxY > MaxY) MaxY = o.MaxY;
        }
    }

    public struct SwfRGBA
    {
        public byte R, G, B, A;

        public SwfRGBA(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }

        public static readonly SwfRGBA White = new SwfRGBA(255, 255, 255, 255);
    }

    /// <summary>Affine matrix: x' = A*x + C*y + Tx, y' = B*x + D*y + Ty (pixel space).</summary>
    public struct SwfMatrix
    {
        public float A, B, C, D, Tx, Ty;

        public static SwfMatrix Identity
        {
            get { SwfMatrix m = default(SwfMatrix); m.A = 1; m.D = 1; return m; }
        }

        public bool IsIdentity
        {
            get { return A == 1 && B == 0 && C == 0 && D == 1 && Tx == 0 && Ty == 0; }
        }

        public bool HasInverse
        {
            get { float det = A * D - B * C; return det * det > 1e-12f; }
        }

        /// <summary>result = this * m, i.e. apply m first, then this.</summary>
        public static SwfMatrix Multiply(SwfMatrix m1, SwfMatrix m2)
        {
            SwfMatrix r;
            r.A = m1.A * m2.A + m1.C * m2.B;
            r.B = m1.B * m2.A + m1.D * m2.B;
            r.C = m1.A * m2.C + m1.C * m2.D;
            r.D = m1.B * m2.C + m1.D * m2.D;
            r.Tx = m1.A * m2.Tx + m1.C * m2.Ty + m1.Tx;
            r.Ty = m1.B * m2.Tx + m1.D * m2.Ty + m1.Ty;
            return r;
        }

        public void Apply(float x, float y, out float ox, out float oy)
        {
            ox = A * x + C * y + Tx;
            oy = B * x + D * y + Ty;
        }

        public SwfMatrix Inverse()
        {
            float det = A * D - B * C;
            if (Math.Abs(det) < 1e-12f) det = det >= 0 ? 1e-12f : -1e-12f;
            SwfMatrix r;
            float ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
            r.A = ia; r.B = ib; r.C = ic; r.D = id;
            r.Tx = -(ia * Tx + ic * Ty);
            r.Ty = -(ib * Tx + id * Ty);
            return r;
        }

        /// <summary>
        /// Decompose into position/rotation(uniform-flip aware)/scale.
        /// Mirrored matrices come out with a negative scale component,
        /// which composes correctly down a hierarchy.
        /// </summary>
        public void Decompose(out float posX, out float posY, out float rotRad, out float scaleX, out float scaleY)
        {
            posX = Tx; posY = Ty;
            rotRad = (float)Math.Atan2(B, A);
            scaleX = (float)Math.Sqrt(A * A + B * B);
            if (scaleX < 1e-6f) { rotRad = 0; scaleY = 0; return; }
            scaleY = (A * D - B * C) / scaleX;
        }

        /// <summary>
        /// Full affine decomposition: this matrix == Translate(pos) * Rot(rot1) * Scale(sx,sy) * Rot(rot2).
        /// Exact for ANY invertible 2D matrix including shears (Unity's Transform cannot express
        /// shear directly, so the importer maps this onto a three-node hierarchy:
        /// node(pos+rot1) -> node(scale) -> node(rot2)).
        /// Returns angles in radians. Degenerate (zero-scale) matrices return zeroed rotation/scale.
        /// </summary>
        public void DecomposeAffine(out float posX, out float posY,
            out float rot1Rad, out float sx, out float sy, out float rot2Rad)
        {
            posX = Tx; posY = Ty;
            double a = A, b = B, c = C, d = D;
            double t = a * a + b * b - (c * c + d * d);
            double s = a * c + b * d;
            double thetaV = 0.5 * Math.Atan2(2.0 * s, t);
            double cosV = Math.Cos(thetaV), sinV = Math.Sin(thetaV);
            double e1 = cosV * cosV * (a * a + b * b) + 2.0 * cosV * sinV * s + sinV * sinV * (c * c + d * d);
            double e2 = sinV * sinV * (a * a + b * b) - 2.0 * cosV * sinV * s + cosV * cosV * (c * c + d * d);
            double s1 = Math.Sqrt(Math.Max(e1, 0.0));
            double det = (double)A * D - (double)B * C;
            double s2 = det >= 0 ? Math.Sqrt(Math.Max(e2, 0.0)) : -Math.Sqrt(Math.Max(e2, 0.0));

            if (s1 < 1e-12)
            {
                rot1Rad = 0; sx = (float)s1; sy = 0; rot2Rad = 0;
                return;
            }
            // U = M * V * Sigma^-1 must be a pure rotation; recover its angle
            double u1x = (a * cosV + c * sinV) / s1;
            double u1y = (b * cosV + d * sinV) / s1;
            rot1Rad = (float)Math.Atan2(u1y, u1x);
            rot2Rad = (float)(-thetaV);
            sx = (float)s1; sy = (float)s2;
        }

        /// <summary>Recompose check helper (diagnostics): Translate(pos)*Rot(r1)*Scale(sx,sy)*Rot(r2).</summary>
        public static SwfMatrix ComposeAffine(float posX, float posY, float rot1Rad, float sx, float sy, float rot2Rad)
        {
            double c1 = Math.Cos(rot1Rad), s1 = Math.Sin(rot1Rad);
            double c2 = Math.Cos(rot2Rad), s2 = Math.Sin(rot2Rad);
            // R1 = [c1 -s1; s1 c1], S = diag(sx,sy), R2 = [c2 -s2; s2 c2]; M = R1*S*R2 (column vectors)
            double A = c1 * sx * c2 - s1 * sy * s2;
            double B = s1 * sx * c2 + c1 * sy * s2;
            double C = -c1 * sx * s2 - s1 * sy * c2;
            double D = -s1 * sx * s2 + c1 * sy * c2;
            SwfMatrix m;
            m.A = (float)A; m.B = (float)B; m.C = (float)C; m.D = (float)D;
            m.Tx = posX; m.Ty = posY;
            return m;
        }
    }

    /// <summary>Color transform: out = clamp(c * mult + add). Mult 0..1, add 0..255.</summary>
    public struct SwfColorTransform
    {
        public float RMult, GMult, BMult, AMult;
        public float RAdd, GAdd, BAdd, AAdd;

        public static SwfColorTransform Identity
        {
            get
            {
                SwfColorTransform t = default(SwfColorTransform);
                t.RMult = t.GMult = t.BMult = t.AMult = 1;
                return t;
            }
        }

        public bool IsIdentity
        {
            get
            {
                return RMult == 1 && GMult == 1 && BMult == 1 && AMult == 1
                    && RAdd == 0 && GAdd == 0 && BAdd == 0 && AAdd == 0;
            }
        }

        /// <summary>parent applied after child: total(c) = parent(child(c)).</summary>
        public static SwfColorTransform Combine(SwfColorTransform parent, SwfColorTransform child)
        {
            SwfColorTransform r;
            r.RMult = parent.RMult * child.RMult;
            r.GMult = parent.GMult * child.GMult;
            r.BMult = parent.BMult * child.BMult;
            r.AMult = parent.AMult * child.AMult;
            r.RAdd = child.RAdd * parent.RMult + parent.RAdd;
            r.GAdd = child.GAdd * parent.GMult + parent.GAdd;
            r.BAdd = child.BAdd * parent.BMult + parent.BAdd;
            r.AAdd = child.AAdd * parent.AMult + parent.AAdd;
            return r;
        }

        public SwfRGBA Apply(SwfRGBA c)
        {
            return new SwfRGBA(
                ClampByte(c.R * RMult + RAdd),
                ClampByte(c.G * GMult + GAdd),
                ClampByte(c.B * BMult + BAdd),
                ClampByte(c.A * AMult + AAdd));
        }

        private static byte ClampByte(float v)
        {
            if (v <= 0) return 0;
            if (v >= 255) return 255;
            return (byte)(v + 0.5f);
        }
    }

    /// <summary>LSB-first bit reader over a byte buffer, as required by SWF compressed bit fields.</summary>
    public sealed class SwfBitReader
    {
        private readonly byte[] _data;
        private int _bitPos;

        public SwfBitReader(byte[] data, int byteOffset)
        {
            _data = data;
            _bitPos = byteOffset * 8;
        }

        public int BytePos
        {
            get { return (_bitPos + 7) >> 3; }
        }

        public void ByteAlign()
        {
            _bitPos = (_bitPos + 7) & ~7;
        }

        public uint ReadUB(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = _bitPos >> 3;
                int bit = (_data[byteIdx] >> (7 - (_bitPos & 7))) & 1;
                v = (v << 1) | (uint)bit;
                _bitPos++;
            }
            return v;
        }

        public int ReadSB(int n)
        {
            if (n == 0) return 0;
            int v = (int)ReadUB(n);
            int sign = 1 << (n - 1);
            if ((v & sign) != 0) v -= (1 << n);
            return v;
        }

        /// <summary>Fixed-point 16.16 bit field.</summary>
        public float ReadFB(int n)
        {
            return ReadSB(n) / 65536f;
        }

        public SwfRect ReadRect()
        {
            ByteAlign();
            int nBits = (int)ReadUB(5);
            int minX = ReadSB(nBits);
            int maxX = ReadSB(nBits);
            int minY = ReadSB(nBits);
            int maxY = ReadSB(nBits);
            ByteAlign();
            SwfRect r;
            r.MinX = minX / 20f; r.MaxX = maxX / 20f;
            r.MinY = minY / 20f; r.MaxY = maxY / 20f;
            return r;
        }

        /// <summary>Parse a matrix, converting translation from twips to pixels.</summary>
        public SwfMatrix ReadMatrix()
        {
            ByteAlign();
            SwfMatrix m = default(SwfMatrix);
            if (ReadUB(1) != 0)
            {
                int nBits = (int)ReadUB(5);
                m.A = ReadFB(nBits);
                m.D = ReadFB(nBits);
            }
            else { m.A = 1; m.D = 1; }
            if (ReadUB(1) != 0)
            {
                int nBits = (int)ReadUB(5);
                m.B = ReadFB(nBits); // RotateSkew0: x contribution to y
                m.C = ReadFB(nBits); // RotateSkew1: y contribution to x
            }
            int nTrans = (int)ReadUB(5);
            m.Tx = ReadSB(nTrans) / 20f;
            m.Ty = ReadSB(nTrans) / 20f;
            ByteAlign();
            return m;
        }

        /// <summary>CXFormWithAlpha. Returns mult in 0..1 and add in 0..255.</summary>
        /// Actual bit layout (verified against real files + reference implementations):
        ///   HasAddTerms UB[1], HasMultTerms UB[1], NBits UB[4]  (ONE shared bit count!),
        ///   then mult terms (if HasMultTerms), then add terms (if HasAddTerms).
        public SwfColorTransform ReadColorTransformWithAlpha()
        {
            return ReadColorTransformImpl(true);
        }

        /// <summary>Plain CXFORM (3 channels, old PlaceObject tag).</summary>
        public SwfColorTransform ReadColorTransform()
        {
            return ReadColorTransformImpl(false);
        }

        private SwfColorTransform ReadColorTransformImpl(bool withAlpha)
        {
            ByteAlign();
            SwfColorTransform t = default(SwfColorTransform);
            bool hasAdd = ReadUB(1) != 0;
            bool hasMult = ReadUB(1) != 0;
            int nBits = (int)ReadUB(4);
            t.RMult = 1; t.GMult = 1; t.BMult = 1; t.AMult = 1;
            int channels = withAlpha ? 4 : 3;
            if (hasMult)
            {
                t.RMult = ReadSB(nBits) / 256f;
                t.GMult = ReadSB(nBits) / 256f;
                t.BMult = ReadSB(nBits) / 256f;
                if (channels == 4) t.AMult = ReadSB(nBits) / 256f;
            }
            if (hasAdd)
            {
                // Add terms are plain integers in -255..255 (NOT 8.8 fixed point), applied
                // after the multiply: out = color*mult + add.
                t.RAdd = ReadSB(nBits);
                t.GAdd = ReadSB(nBits);
                t.BAdd = ReadSB(nBits);
                if (channels == 4) t.AAdd = ReadSB(nBits);
            }
            ByteAlign();
            return t;
        }
    }

    public struct SwfFrameLabel
    {
        public int Frame;      // 0-based
        public string Name;
    }

    public enum SwfCharacterKind
    {
        Shape,
        Bitmap,
        Sprite,
    }

    public struct SwfPlaceCommand
    {
        public bool IsRemove;
        public int Depth;
        public int CharacterId;      // valid when placing
        public bool HasCharacter;
        public bool Move;            // modify existing placement
        public bool HasMatrix;
        public SwfMatrix Matrix;
        public bool HasColor;
        public SwfColorTransform Color;
        public string Name;
        public bool HasClipDepth;
        public int ClipDepth;
    }
}
