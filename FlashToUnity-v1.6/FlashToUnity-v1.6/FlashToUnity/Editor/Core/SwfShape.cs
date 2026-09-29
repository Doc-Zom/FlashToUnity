// FlashToUnity - SWF shape (vector) data model.

using System.Collections.Generic;

namespace FlashToUnity.Core
{
    public enum SwfFillType
    {
        Solid = 0,
        LinearGradient = 0x10,
        RadialGradient = 0x12,
        FocalGradient = 0x13,
        TiledBitmapSmooth = 0x40,
        ClippedBitmapSmooth = 0x41,
        TiledBitmap = 0x42,
        ClippedBitmap = 0x43,
    }

    public struct SwfGradientRecord
    {
        public byte Ratio; // 0..255
        public SwfRGBA Color;
    }

    public struct SwfFillStyle
    {
        public SwfFillType Type;
        public SwfRGBA Color;                       // solid
        public SwfMatrix Matrix;                    // gradient/bitmap matrix, pixel space
        public List<SwfGradientRecord> Gradient;    // gradients
        public float FocalPoint;                    // focal gradient (-1..1)
        public int BitmapId;                        // bitmap fills
    }

    public struct SwfLineStyle
    {
        public float Width;    // pixels
        public SwfRGBA Color;
    }

    /// <summary>A point on a contour; quadratic control point when IsQuad.</summary>
    public struct SwfShapeSegment
    {
        public float X0, Y0;      // start (implicit from previous segment)
        public float CX, CY;      // control point (quad only)
        public float X1, Y1;      // end
        public bool IsQuad;
    }

    /// <summary>
    /// One contour (sub-path) of a shape, with the style state that was active
    /// when it was drawn. Fill styles 0/1 are 1-based style indices (0 = none).
    /// </summary>
    public sealed class SwfShapeContour
    {
        public int FillStyle0;
        public int FillStyle1;
        public int LineStyle;
        public List<SwfShapeSegment> Segments = new List<SwfShapeSegment>();
        public float StartX, StartY;
        public float LastX, LastY;
        public bool IsClosed;

        public void Close()
        {
            if (Segments.Count == 0) return;
            SwfShapeSegment last = Segments[Segments.Count - 1];
            if (last.X1 != StartX || last.Y1 != StartY)
            {
                SwfShapeSegment s = new SwfShapeSegment();
                s.X0 = last.X1; s.Y0 = last.Y1; s.X1 = StartX; s.Y1 = StartY;
                s.IsQuad = false;
                Segments.Add(s);
            }
            IsClosed = true;
        }
    }

    public sealed class SwfShape
    {
        public int Id;
        public SwfRect Bounds;      // pixels (twips converted)
        public List<SwfFillStyle> FillStyles = new List<SwfFillStyle>();
        public List<SwfLineStyle> LineStyles = new List<SwfLineStyle>();
        public List<SwfShapeContour> Contours = new List<SwfShapeContour>();
    }
}
