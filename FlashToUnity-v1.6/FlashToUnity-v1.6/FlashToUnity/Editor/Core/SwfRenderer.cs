// FlashToUnity - software rasterizer for SWF vector shapes + display-list compositor.
// Renders shapes (solid / gradient / bitmap fills, round-join strokes) and composites
// whole timeline frames (sprites, color transforms, alpha masks) into RGBA buffers.
// Pure C#, no Unity dependencies.

using System;
using System.Collections.Generic;

namespace FlashToUnity.Core
{
    public class RgbaImage
    {
        public int Width;
        public int Height;
        public byte[] Pixels; // RGBA, straight alpha

        public RgbaImage(int w, int h)
        {
            Width = w; Height = h;
            Pixels = new byte[w * h * 4];
        }
    }

    public sealed class RenderOptions
    {
        public int Supersample = 2;
        public bool SpriteLoop;           // loop sprite timelines instead of holding the last frame
        public bool IgnoreMasks;
        public int MaxTextureSize = 4096; // clamp for standalone shape textures
    }

    /// <summary>A rendered standalone texture plus the shape-space coordinate of its top-left corner.</summary>
    public sealed class RenderedTexture : RgbaImage
    {
        public float OriginX; // shape-space x of the texture's left edge
        public float OriginY; // shape-space y of the texture's top edge
        public int Supersample;

        public RenderedTexture(int w, int h) : base(w, h) { }
    }

    // ------------------------------------------------------------------
    // Device buffer: premultiplied RGBA planes (correct alpha averaging on downsample).

    internal sealed class DeviceBuffer
    {
        public int Width, Height;
        public byte[] R, G, B, A; // R/G/B premultiplied by alpha

        public DeviceBuffer(int w, int h)
        {
            Width = w; Height = h;
            R = new byte[w * h]; G = new byte[w * h];
            B = new byte[w * h]; A = new byte[w * h];
        }

        /// <summary>Source-over blend of a straight-alpha source pixel.</summary>
        public void BlendPixel(int x, int y, byte sr, byte sg, byte sb, byte sa)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || sa == 0) return;
            int i = y * Width + x;
            int da = A[i];
            int ia = 255 - sa;
            int oa = sa + da * ia / 255;
            if (oa <= 0) { A[i] = 0; return; }
            int sp = sr * sa / 255, sgp = sg * sa / 255, sbp = sb * sa / 255;
            R[i] = (byte)(sp + R[i] * ia / 255);
            G[i] = (byte)(sgp + G[i] * ia / 255);
            B[i] = (byte)(sbp + B[i] * ia / 255);
            A[i] = (byte)oa;
        }
    }

    internal sealed class Edge
    {
        public float Y0, Y1, X0, Slope;
        public int Winding; // +1 when the edge goes down
    }

    /// <summary>Per-pixel color evaluation for one fill style, in device space.</summary>
    internal abstract class StyleEval
    {
        public abstract void Sample(float x, float y, out byte r, out byte g, out byte b, out byte a);
    }

    internal sealed class SolidEval : StyleEval
    {
        public byte R, G, B, A;
        public override void Sample(float x, float y, out byte r, out byte g, out byte b, out byte a)
        { r = R; g = G; b = B; a = A; }
    }

    internal sealed class GradientEval : StyleEval
    {
        // device -> gradient space matrix (gradient space in px; the gradient spans +-819.2 px)
        public double M0, M1, M2, M3, M4, M5;
        public byte[] LutR = new byte[256], LutG = new byte[256], LutB = new byte[256], LutA = new byte[256];
        public int Mode; // 0 linear, 1 radial

        public override void Sample(float x, float y, out byte r, out byte g, out byte b, out byte a)
        {
            double gx = M0 * x + M1 * y + M2;
            double gy = M3 * x + M4 * y + M5;
            double t;
            if (Mode == 0) t = (gx + 819.2) / 1638.4;
            else t = Math.Sqrt(gx * gx + gy * gy) / 819.2;
            int ti;
            if (t <= 0) ti = 0;
            else if (t >= 1) ti = 255;
            else ti = (int)(t * 255.0 + 0.5);
            r = LutR[ti]; g = LutG[ti]; b = LutB[ti]; a = LutA[ti];
        }
    }

    internal sealed class BitmapEval : StyleEval
    {
        public double M0, M1, M2, M3, M4, M5; // device -> bitmap pixel space
        public byte[] Data; public int W, H;
        public bool Smooth; public bool Clip; // clip = clamp to edge (no tiling)
        public SwfColorTransform Color = SwfColorTransform.Identity;

        public override void Sample(float x, float y, out byte r, out byte g, out byte b, out byte a)
        {
            double u = M0 * x + M1 * y + M2;
            double v = M3 * x + M4 * y + M5;
            if (!Clip)
            {
                u = u - Math.Floor(u / W) * W;
                v = v - Math.Floor(v / H) * H;
            }
            else if (u < 0 || v < 0 || u >= W || v >= H)
            {
                r = 0; g = 0; b = 0; a = 0;
                return;
            }
            SampleAt(u, v, out r, out g, out b, out a);
            SwfRGBA transformed = Color.Apply(new SwfRGBA(r, g, b, a));
            r = transformed.R; g = transformed.G; b = transformed.B; a = transformed.A;
        }

        private void SampleAt(double u, double v, out byte r, out byte g, out byte b, out byte a)
        {
            if (!Smooth)
            {
                int x = (int)u, y = (int)v;
                if (x < 0) x = 0; else if (x >= W) x = W - 1;
                if (y < 0) y = 0; else if (y >= H) y = H - 1;
                int q = (y * W + x) * 4;
                r = Data[q]; g = Data[q + 1]; b = Data[q + 2]; a = Data[q + 3];
                return;
            }
            double x0d = Math.Floor(u - 0.5), y0d = Math.Floor(v - 0.5);
            int x0 = (int)x0d, y0 = (int)y0d;
            double fx = u - 0.5 - x0d, fy = v - 0.5 - y0d;
            double cr = 0, cg = 0, cb = 0, ca = 0;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int sx = x0 + dx, sy = y0 + dy;
                    if (Clip)
                    {
                        sx = Math.Max(0, Math.Min(W - 1, sx));
                        sy = Math.Max(0, Math.Min(H - 1, sy));
                    }
                    else
                    {
                        sx = (sx % W + W) % W;
                        sy = (sy % H + H) % H;
                    }
                    double wgt = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                    int q = (sy * W + sx) * 4;
                    double alphaWeight = Data[q + 3] * wgt;
                    cr += Data[q] * alphaWeight; cg += Data[q + 1] * alphaWeight;
                    cb += Data[q + 2] * alphaWeight; ca += alphaWeight;
                }
            }
            r = ca > 0 ? (byte)(cr / ca + 0.5) : (byte)0;
            g = ca > 0 ? (byte)(cg / ca + 0.5) : (byte)0;
            b = ca > 0 ? (byte)(cb / ca + 0.5) : (byte)0;
            a = (byte)(ca + 0.5);
        }
    }

    // ------------------------------------------------------------------

    public static class SwfRenderer
    {
        private const double GradientSpan = 819.2; // 16384 twips in px

        private static readonly bool DebugLog = Environment.GetEnvironmentVariable("F2U_DEBUG") != null;

        // Nested symbols are stateful too, so their per-frame display lists are accumulated
        // once per movie instead of re-derived for every rendered frame.
        private static SwfMovie _cacheMovie;
        private static bool _cacheLoop;
        private static readonly Dictionary<int, List<Dictionary<int, SwfPlacementState>>> SpriteStateCache
            = new Dictionary<int, List<Dictionary<int, SwfPlacementState>>>();

        private static SwfTimeline _replayTimeline;
        private static int _replayUpTo = -1;
        private static Dictionary<int, SwfPlacementState> _replayState;

        private static Dictionary<int, SwfPlacementState> SpriteStateAt(SwfMovie movie, SwfCharacter ch, int f, bool loop)
        {
            if (!ReferenceEquals(_cacheMovie, movie) || _cacheLoop != loop)
            {
                _cacheMovie = movie;
                _cacheLoop = loop;
                SpriteStateCache.Clear();
            }
            List<Dictionary<int, SwfPlacementState>> snapshots;
            if (!SpriteStateCache.TryGetValue(ch.Id, out snapshots))
            {
                snapshots = new List<Dictionary<int, SwfPlacementState>>();
                SpriteStateCache[ch.Id] = snapshots;
            }
            return SwfTimelineState.AtAge(ch.Sprite.Timeline, f, loop, snapshots);
        }

        // ================= public entry points =================

        /// <summary>Render a vector shape into a standalone RGBA texture (top-left = shape bounds min, minus padding).</summary>
        public static RenderedTexture RenderShapeToTexture(SwfMovie movie, SwfShape shape, RenderOptions opt)
        {
            int ss = Math.Max(1, opt.Supersample);
            SwfRect b = shape.Bounds;
            if (b.MaxX < b.MinX) b = new SwfRect { MinX = 0, MinY = 0, MaxX = 1, MaxY = 1 };
            int pad = 2;
            // NOTE: the canvas is kept tight around the shape. The Flash registration
            // point may fall outside it — the sprite offset (relative to the default
            // CENTER pivot) is compensated by the importer's __spr node, which avoids
            // Unity's unreliable custom-pivot/sprite-rect handling entirely.
            int texW = (int)Math.Ceiling(b.Width * ss) + pad * 2 * ss;
            int texH = (int)Math.Ceiling(b.Height * ss) + pad * 2 * ss;
            if (texW > opt.MaxTextureSize * ss) texW = opt.MaxTextureSize * ss;
            if (texH > opt.MaxTextureSize * ss) texH = opt.MaxTextureSize * ss;

            SwfMatrix m = SwfMatrix.Identity;
            m.A = ss; m.D = ss;
            m.Tx = (-b.MinX + pad) * ss;
            m.Ty = (-b.MinY + pad) * ss;

            DeviceBuffer dev = new DeviceBuffer(texW, texH);
            RenderShapeGeometry(movie, shape, m, SwfColorTransform.Identity, dev);

            RenderedTexture tex = new RenderedTexture(texW / ss, texH / ss);
            Downsample(dev, ss, tex);
            tex.OriginX = b.MinX - pad;
            tex.OriginY = b.MinY - pad;
            tex.Supersample = ss;
            return tex;
        }

        /// <summary>Composite a timeline frame into an RGBA image covering the given viewport (pixel space).</summary>
        public static RgbaImage RenderTimelineFrame(SwfMovie movie, SwfTimeline timeline, int frameIndex,
            SwfRect viewport, RenderOptions opt)
        {
            if (viewport.MaxX <= viewport.MinX) viewport = movie.Stage;
            int ss = Math.Max(1, opt.Supersample);
            int outW = (int)Math.Ceiling(viewport.Width);
            int outH = (int)Math.Ceiling(viewport.Height);
            DeviceBuffer dev = new DeviceBuffer(outW * ss, outH * ss);

            SwfMatrix world = SwfMatrix.Identity;
            world.A = ss; world.D = ss;
            world.Tx = -viewport.MinX * ss;
            world.Ty = -viewport.MinY * ss;

            Dictionary<int, SwfPlacementState> state;
            int last = Math.Min(frameIndex, timeline.Frames.Count - 1);
            // Flash timelines are stateful: placements persist until removed. Rendering frames in
            // order therefore continues from the previous replay instead of replaying from frame 0.
            int from = 0;
            if (ReferenceEquals(_replayTimeline, timeline) && _replayUpTo >= 0 && _replayUpTo <= last)
            {
                state = _replayState;
                from = _replayUpTo + 1;
            }
            else state = new Dictionary<int, SwfPlacementState>();
            for (int f = from; f <= last; f++)
                SwfTimelineState.Apply(timeline.Frames[f], state, f);
            _replayTimeline = timeline; _replayUpTo = last; _replayState = state;
            RenderStateList(movie, state, frameIndex, world, SwfColorTransform.Identity, dev, opt, 0);

            RgbaImage img = new RgbaImage(outW, outH);
            Downsample(dev, ss, img);
            return img;
        }

        /// <summary>Union bounds of everything a timeline shows up to (and including) a given frame.</summary>
        public static SwfRect ComputeFrameBounds(SwfMovie movie, SwfTimeline timeline, int frameIndex)
        {
            Dictionary<int, SwfPlacementState> state = new Dictionary<int, SwfPlacementState>();
            SwfRect bounds = SwfRect.Empty;
            for (int f = 0; f <= frameIndex && f < timeline.Frames.Count; f++)
            {
                SwfTimelineState.Apply(timeline.Frames[f], state);
                bounds = UnionStateBounds(movie, state, bounds, 0);
            }
            return bounds;
        }

        private static SwfRect UnionStateBounds(SwfMovie movie, Dictionary<int, SwfPlacementState> state, SwfRect bounds, int nest)
        {
            if (nest > 5) return bounds;
            List<int> depths = new List<int>(state.Keys);
            depths.Sort();
            foreach (int depth in depths)
            {
                SwfPlacementState st = state[depth];
                SwfCharacter ch = movie.GetCharacter(st.CharacterId);
                if (ch == null) continue;
                SwfRect cb = ch.Kind == SwfCharacterKind.Sprite
                    ? SwfParser.GetCharacterBounds(movie, ch, 0)
                    : ch.Bounds;
                if (cb.MaxX < cb.MinX) continue;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    float x = (i & 1) == 0 ? cb.MinX : cb.MaxX;
                    float y = (i & 2) == 0 ? cb.MinY : cb.MaxY;
                    float ox, oy;
                    st.Matrix.Apply(x, y, out ox, out oy);
                    if (ox < minX) minX = ox;
                    if (oy < minY) minY = oy;
                    if (ox > maxX) maxX = ox;
                    if (oy > maxY) maxY = oy;
                }
                SwfRect t;
                t.MinX = minX; t.MinY = minY; t.MaxX = maxX; t.MaxY = maxY;
                bounds.Union(t);
            }
            return bounds;
        }

        // ================= display list =================

        private static void RenderStateList(SwfMovie movie, Dictionary<int, SwfPlacementState> state, int frame,
            SwfMatrix world, SwfColorTransform parentCt, DeviceBuffer dev, RenderOptions opt, int nest)
        {
            if (nest > 12) return;
            List<int> depths = new List<int>(state.Keys);
            depths.Sort();

            int i = 0;
            while (i < depths.Count)
            {
                SwfPlacementState st = state[depths[i]];
                if (st.ClipDepth > st.Depth && !opt.IgnoreMasks)
                {
                    int j = i + 1;
                    List<int> group = new List<int>();
                    while (j < depths.Count && depths[j] <= st.ClipDepth)
                    {
                        if (state[depths[j]].ClipDepth <= depths[j]) group.Add(depths[j]); // no nested masks
                        j++;
                    }
                    RenderMaskGroup(movie, st, group, state, frame, world, parentCt, dev, opt, nest);
                    i = j;
                }
                else
                {
                    RenderPlacement(movie, st, frame, world, parentCt, dev, opt, nest);
                    i++;
                }
            }
        }

        private static void RenderMaskGroup(SwfMovie movie, SwfPlacementState maskState, List<int> groupDepths,
            Dictionary<int, SwfPlacementState> state, int frame, SwfMatrix world, SwfColorTransform parentCt,
            DeviceBuffer dev, RenderOptions opt, int nest)
        {
            DeviceBuffer maskBuf = new DeviceBuffer(dev.Width, dev.Height);
            RenderPlacement(movie, maskState, frame, world, parentCt, maskBuf, opt, nest);
            DeviceBuffer layer = new DeviceBuffer(dev.Width, dev.Height);
            foreach (int depth in groupDepths)
                RenderPlacement(movie, state[depth], frame, world, parentCt, layer, opt, nest);

            // layer premul * mask alpha, then source-over into dev
            int n = dev.Width * dev.Height;
            for (int p = 0; p < n; p++)
            {
                int ma = maskBuf.A[p];
                if (ma == 0) continue;
                int la = layer.A[p] * ma / 255;
                if (la == 0) continue;
                int x = p % dev.Width, y = p / dev.Width;
                int ia = 255 - la;
                int i2 = y * dev.Width + x;
                int oa = la + dev.A[i2] * ia / 255;
                if (oa <= 0) continue;
                dev.R[i2] = (byte)(layer.R[p] * ma / 255 + dev.R[i2] * ia / 255);
                dev.G[i2] = (byte)(layer.G[p] * ma / 255 + dev.G[i2] * ia / 255);
                dev.B[i2] = (byte)(layer.B[p] * ma / 255 + dev.B[i2] * ia / 255);
                dev.A[i2] = (byte)oa;
            }
        }

        private static void RenderPlacement(SwfMovie movie, SwfPlacementState st, int frame, SwfMatrix world,
            SwfColorTransform parentCt, DeviceBuffer dev, RenderOptions opt, int nest)
        {
            SwfCharacter ch = movie.GetCharacter(st.CharacterId);
            if (ch == null) return;
            SwfMatrix m = SwfMatrix.Multiply(world, st.Matrix);
            SwfColorTransform ct = SwfColorTransform.Combine(parentCt, st.Color);

            switch (ch.Kind)
            {
                case SwfCharacterKind.Bitmap:
                    DrawBitmap(dev, ch.Bitmap, m, ct);
                    break;
                case SwfCharacterKind.Shape:
                    RenderShapeGeometry(movie, ch.Shape, m, ct, dev);
                    break;
                case SwfCharacterKind.Sprite:
                    SwfTimeline tl = ch.Sprite.Timeline;
                    if (tl == null || tl.Frames.Count == 0) return;
                    int f = Math.Max(0, frame - st.PlacedFrame);
                    RenderStateList(movie, SpriteStateAt(movie, ch, f, opt.SpriteLoop), f, m, ct, dev, opt, nest + 1);
                    break;
            }
        }

        // ================= shape geometry =================

        private static void RenderShapeGeometry(SwfMovie movie, SwfShape shape, SwfMatrix m,
            SwfColorTransform ct, DeviceBuffer dev)
        {
            for (int styleIdx = 1; styleIdx <= shape.FillStyles.Count; styleIdx++)
            {
                List<Edge> edges = new List<Edge>();
                foreach (SwfShapeContour c in shape.Contours)
                {
                    bool f1 = c.FillStyle1 == styleIdx;
                    bool f0 = c.FillStyle0 == styleIdx;
                    if (!f1 && !f0) continue;
                    AddContourEdges(c, m, edges, f1, f0);
                }
                if (DebugLog)
                    Console.Error.WriteLine($"  fill style {styleIdx}: edges={edges.Count}");
                if (edges.Count == 0) continue;
                StyleEval eval = CreateFillEval(movie, shape, styleIdx, m, ct);
                if (eval == null)
                {
                    if (DebugLog)
                        Console.Error.WriteLine($"  fill style {styleIdx}: eval NULL");
                    continue;
                }
                FillEdges(dev, edges, eval);
                if (DebugLog)
                {
                    int nz = 0;
                    for (int i = 0; i < dev.A.Length; i++) if (dev.A[i] != 0) nz++;
                    Console.Error.WriteLine($"  fill style {styleIdx}: nonzero alpha pixels after fill = {nz}");
                }
            }

            for (int styleIdx = 1; styleIdx <= shape.LineStyles.Count; styleIdx++)
            {
                SwfLineStyle ls = shape.LineStyles[styleIdx - 1];
                SwfRGBA col = ct.Apply(ls.Color);
                if (col.A == 0) continue;
                double scale = Math.Sqrt(Math.Abs((double)m.A * m.D - (double)m.B * m.C));
                float radius = (float)(ls.Width / 2.0 * scale);
                if (radius < 0.35f) radius = 0.35f;
                foreach (SwfShapeContour c in shape.Contours)
                {
                    if (c.LineStyle != styleIdx) continue;
                    StrokeContour(c, m, radius, col, dev);
                }
            }
        }

        private static void AddContourEdges(SwfShapeContour c, SwfMatrix m, List<Edge> edges, bool forward, bool reverse)
        {
            List<float> px = new List<float>(), py = new List<float>();
            float ox, oy;
            m.Apply(c.StartX, c.StartY, out ox, out oy);
            px.Add(ox); py.Add(oy);

            foreach (SwfShapeSegment s in c.Segments)
            {
                if (!s.IsQuad)
                {
                    m.Apply(s.X1, s.Y1, out ox, out oy);
                    px.Add(ox); py.Add(oy);
                }
                else
                {
                    FlattenQuad(s, m, px, py);
                }
            }

            EmitPolylineEdges(px, py, edges, forward, reverse);
        }

        private static void FlattenQuad(SwfShapeSegment s, SwfMatrix m, List<float> px, List<float> py)
        {
            double scale = Math.Sqrt(Math.Abs((double)m.A * m.D - (double)m.B * m.C));
            if (scale <= 0) scale = 1;
            double dev = (Math.Abs((double)s.CX * 2 - s.X0 - s.X1) + Math.Abs((double)s.CY * 2 - s.Y0 - s.Y1)) * scale / 20.0;
            int segs = (int)(dev / 0.4) + 1;
            if (segs > 64) segs = 64;
            if (segs < 1) segs = 1;
            for (int i = 1; i <= segs; i++)
            {
                double t = (double)i / segs;
                double u = 1 - t;
                double qx = u * u * s.X0 + 2 * u * t * s.CX + t * t * s.X1;
                double qy = u * u * s.Y0 + 2 * u * t * s.CY + t * t * s.Y1;
                float ox, oy;
                m.Apply((float)qx, (float)qy, out ox, out oy);
                px.Add(ox); py.Add(oy);
            }
        }

        private static void EmitPolylineEdges(List<float> px, List<float> py, List<Edge> edges, bool forward, bool reverse)
        {
            if (forward) EmitDirected(px, py, edges);
            if (reverse)
            {
                List<float> rx = new List<float>(px.Count), ry = new List<float>(py.Count);
                for (int i = px.Count - 1; i >= 0; i--) { rx.Add(px[i]); ry.Add(py[i]); }
                EmitDirected(rx, ry, edges);
            }
        }

        private static void EmitDirected(List<float> px, List<float> py, List<Edge> edges)
        {
            for (int i = 0; i + 1 < px.Count; i++)
            {
                float y0 = py[i], y1 = py[i + 1];
                if (y0 == y1) continue;
                Edge e = new Edge();
                e.Y0 = y0; e.Y1 = y1;
                e.X0 = px[i];
                e.Slope = (px[i + 1] - px[i]) / (y1 - y0);
                // winding comes from the actual edge direction, not the fill slot
                e.Winding = y1 > y0 ? 1 : -1;
                edges.Add(e);
            }
        }

        private static void FillEdges(DeviceBuffer dev, List<Edge> edges, StyleEval eval)
        {
            int h = dev.Height;
            List<Edge>[] buckets = new List<Edge>[h];
            foreach (Edge e in edges)
            {
                float lo = Math.Min(e.Y0, e.Y1), hi = Math.Max(e.Y0, e.Y1);
                int b0 = (int)Math.Floor(lo);
                int b1 = (int)Math.Ceiling(hi) - 1;
                if (b0 < 0) b0 = 0;
                if (b1 > h - 1) b1 = h - 1;
                for (int y = b0; y <= b1; y++)
                {
                    if (buckets[y] == null) buckets[y] = new List<Edge>();
                    buckets[y].Add(e);
                }
            }

            List<KeyValuePair<float, int>> crossings = new List<KeyValuePair<float, int>>(64);
            for (int y = 0; y < h; y++)
            {
                List<Edge> bucket = buckets[y];
                if (bucket == null) continue;
                float yc = y + 0.5f;
                crossings.Clear();
                foreach (Edge e in bucket)
                {
                    float lo = Math.Min(e.Y0, e.Y1), hi = Math.Max(e.Y0, e.Y1);
                    if (yc < lo || yc >= hi) continue;
                    float x = e.X0 + (yc - e.Y0) * e.Slope;
                    crossings.Add(new KeyValuePair<float, int>(x, e.Winding));
                }
                if (crossings.Count < 2) continue;
                crossings.Sort(CompareCrossing);

                int winding = 0;
                float spanStart = 0;
                for (int i = 0; i < crossings.Count; i++)
                {
                    int before = winding;
                    winding += crossings[i].Value;
                    if (before == 0 && winding != 0) spanStart = crossings[i].Key;
                    else if (before != 0 && winding == 0)
                        FillSpan(dev, y, spanStart, crossings[i].Key, eval);
                }
            }
        }

        private static int CompareCrossing(KeyValuePair<float, int> a, KeyValuePair<float, int> b)
        { return a.Key < b.Key ? -1 : (a.Key > b.Key ? 1 : 0); }

        private static void FillSpan(DeviceBuffer dev, int y, float x0, float x1, StyleEval eval)
        {
            if (x1 <= x0) return;
            bool dbg = DebugLog && y < 6;
            if (dbg) Console.Error.WriteLine($"    FillSpan y={y} x0={x0:F1} x1={x1:F1}");
            // Fractional horizontal coverage: the border pixels of a span are blended with the
            // part of the pixel the span actually covers, which is what makes Flash's vector
            // edges smooth. Rounding to whole pixels (the old behaviour) stair-steps every
            // clipped-bitmap part and silently drops spans thinner than one pixel.
            int ix0 = (int)Math.Floor(x0);
            int ix1 = (int)Math.Ceiling(x1) - 1;
            if (ix1 < ix0) ix1 = ix0;
            if (ix0 < 0) ix0 = 0;
            if (ix1 >= dev.Width) ix1 = dev.Width - 1;
            if (y < 0 || y >= dev.Height) return;
            for (int x = ix0; x <= ix1; x++)
            {
                float cov = Math.Min(x + 1f, x1) - Math.Max(x, x0);
                if (cov <= 0f) continue;
                byte r, g, b, a;
                eval.Sample(x + 0.5f, y + 0.5f, out r, out g, out b, out a);
                if (cov < 1f) a = (byte)(a * cov + 0.5f);
                if (dbg && x < ix0 + 3) Console.Error.WriteLine($"      sample({x + 0.5f:F1}) cov={cov:F2} -> rgba=({r},{g},{b},{a})");
                dev.BlendPixel(x, y, r, g, b, a);
            }
        }

        private static void StrokeContour(SwfShapeContour c, SwfMatrix m, float radius, SwfRGBA color, DeviceBuffer dev)
        {
            List<float> px = new List<float>(), py = new List<float>();
            float ox, oy;
            m.Apply(c.StartX, c.StartY, out ox, out oy);
            px.Add(ox); py.Add(oy);
            foreach (SwfShapeSegment s in c.Segments)
            {
                if (!s.IsQuad)
                {
                    m.Apply(s.X1, s.Y1, out ox, out oy);
                    px.Add(ox); py.Add(oy);
                }
                else FlattenQuad(s, m, px, py);
            }
            for (int i = 0; i + 1 < px.Count; i++)
            {
                float x0 = px[i], y0 = py[i], x1 = px[i + 1], y1 = py[i + 1];
                float dx = x1 - x0, dy = y1 - y0;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                int steps = Math.Max(1, (int)(len / Math.Max(0.6f, radius * 0.7f)));
                for (int sIdx = i == 0 ? 0 : 1; sIdx <= steps; sIdx++)
                {
                    float t = (float)sIdx / steps;
                    StampDisc(dev, x0 + dx * t, y0 + dy * t, radius, color);
                }
            }
        }

        private static void StampDisc(DeviceBuffer dev, float cx, float cy, float radius, SwfRGBA color)
        {
            int x0 = (int)(cx - radius), x1 = (int)(cx + radius);
            int y0 = (int)(cy - radius), y1 = (int)(cy + radius);
            float r2 = radius * radius;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy <= r2)
                        dev.BlendPixel(x, y, color.R, color.G, color.B, color.A);
                }
        }

        // ================= fills =================

        private static StyleEval CreateFillEval(SwfMovie movie, SwfShape shape, int styleIdx, SwfMatrix m,
            SwfColorTransform ct)
        {
            SwfFillStyle fs = shape.FillStyles[styleIdx - 1];
            switch (fs.Type)
            {
                case SwfFillType.Solid:
                    {
                        SolidEval e = new SolidEval();
                        SwfRGBA c = ct.Apply(fs.Color);
                        e.R = c.R; e.G = c.G; e.B = c.B; e.A = c.A;
                        return e;
                    }
                case SwfFillType.LinearGradient:
                case SwfFillType.RadialGradient:
                case SwfFillType.FocalGradient:
                    {
                        GradientEval e = new GradientEval();
                        BuildGradientLut(fs, ct, e);
                        e.Mode = fs.Type == SwfFillType.LinearGradient ? 0 : 1;
                        SetCombined(e, fs.Matrix, m);
                        return e;
                    }
                case SwfFillType.TiledBitmap:
                case SwfFillType.ClippedBitmap:
                case SwfFillType.TiledBitmapSmooth:
                case SwfFillType.ClippedBitmapSmooth:
                    {
                        SwfCharacter bmpCh = movie.GetCharacter(fs.BitmapId);
                        if (bmpCh == null || bmpCh.Bitmap == null) return null;
                        BitmapEval e = new BitmapEval();
                        e.Color = ct;
                        e.Data = bmpCh.Bitmap.Rgba; e.W = bmpCh.Bitmap.Width; e.H = bmpCh.Bitmap.Height;
                        e.Smooth = fs.Type == SwfFillType.TiledBitmapSmooth || fs.Type == SwfFillType.ClippedBitmapSmooth;
                        e.Clip = fs.Type == SwfFillType.ClippedBitmap || fs.Type == SwfFillType.ClippedBitmapSmooth;
                        SetCombined(e, fs.Matrix, m);
                        return e;
                    }
                default:
                    return null;
            }
        }

        private static void SetCombined(StyleEval eval, SwfMatrix localMatrix, SwfMatrix deviceMatrix)
        {
            // SWF fill matrices map LOCAL fill space (bitmap px / gradient px) -> shape space,
            // so the combined device->local map is inverse(deviceMatrix * localMatrix).
            SwfMatrix combined = SwfMatrix.Multiply(deviceMatrix, localMatrix).Inverse();
            GradientEval ge = eval as GradientEval;
            if (ge != null)
            {
                ge.M0 = combined.A; ge.M1 = combined.C; ge.M2 = combined.Tx;
                ge.M3 = combined.B; ge.M4 = combined.D; ge.M5 = combined.Ty;
                return;
            }
            BitmapEval be = eval as BitmapEval;
            if (be != null)
            {
                be.M0 = combined.A; be.M1 = combined.C; be.M2 = combined.Tx;
                be.M3 = combined.B; be.M4 = combined.D; be.M5 = combined.Ty;
            }
        }

        private static void BuildGradientLut(SwfFillStyle fs, SwfColorTransform ct, GradientEval e)
        {
            List<SwfGradientRecord> recs = fs.Gradient;
            if (recs == null || recs.Count == 0)
            {
                for (int i = 0; i < 256; i++) { e.LutR[i] = 255; e.LutG[i] = 255; e.LutB[i] = 255; e.LutA[i] = 255; }
                return;
            }
            for (int i = 0; i < 256; i++)
            {
                float ratio = i;
                SwfRGBA c;
                if (ratio <= recs[0].Ratio) c = recs[0].Color;
                else if (ratio >= recs[recs.Count - 1].Ratio) c = recs[recs.Count - 1].Color;
                else
                {
                    int j = 1;
                    while (j < recs.Count && recs[j].Ratio < ratio) j++;
                    SwfGradientRecord a = recs[j - 1], b = recs[j];
                    float span = b.Ratio - a.Ratio;
                    float k = span <= 0 ? 0 : (ratio - a.Ratio) / span;
                    c = Lerp(a.Color, b.Color, k);
                }
                SwfRGBA ac = ct.Apply(c);
                e.LutR[i] = ac.R; e.LutG[i] = ac.G; e.LutB[i] = ac.B; e.LutA[i] = ac.A;
            }
        }

        private static SwfRGBA Lerp(SwfRGBA a, SwfRGBA b, float k)
        {
            return new SwfRGBA(
                (byte)(a.R + (b.R - a.R) * k + 0.5f),
                (byte)(a.G + (b.G - a.G) * k + 0.5f),
                (byte)(a.B + (b.B - a.B) * k + 0.5f),
                (byte)(a.A + (b.A - a.A) * k + 0.5f));
        }

        // ================= bitmaps =================

        private static void DrawBitmap(DeviceBuffer dev, SwfBitmap bmp, SwfMatrix m, SwfColorTransform ct)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? 0 : bmp.Width;
                float y = (i & 2) == 0 ? 0 : bmp.Height;
                float ox, oy;
                m.Apply(x, y, out ox, out oy);
                if (ox < minX) minX = ox;
                if (oy < minY) minY = oy;
                if (ox > maxX) maxX = ox;
                if (oy > maxY) maxY = oy;
            }
            int x0 = Math.Max(0, (int)minX), y0 = Math.Max(0, (int)minY);
            int x1 = Math.Min(dev.Width - 1, (int)Math.Ceiling(maxX));
            int y1 = Math.Min(dev.Height - 1, (int)Math.Ceiling(maxY));
            if (x1 < x0 || y1 < y0) return;

            SwfMatrix inv = m.Inverse();

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float sx = inv.A * (x + 0.5f) + inv.C * (y + 0.5f) + inv.Tx;
                    float sy = inv.B * (x + 0.5f) + inv.D * (y + 0.5f) + inv.Ty;
                    if (sx < 0 || sy < 0 || sx >= bmp.Width || sy >= bmp.Height) continue;
                    int ix = (int)sx, iy = (int)sy;
                    if (ix >= bmp.Width) ix = bmp.Width - 1;
                    if (iy >= bmp.Height) iy = bmp.Height - 1;
                    int q = (iy * bmp.Width + ix) * 4;
                    byte r = bmp.Rgba[q], g = bmp.Rgba[q + 1], b = bmp.Rgba[q + 2], a = bmp.Rgba[q + 3];
                    SwfRGBA tc = ct.Apply(new SwfRGBA(r, g, b, a));
                    r = tc.R; g = tc.G; b = tc.B; a = tc.A;
                    dev.BlendPixel(x, y, r, g, b, a);
                }
            }
        }

        // ================= misc =================

        private static void Downsample(DeviceBuffer dev, int ss, RgbaImage target)
        {
            int dw = Math.Min(target.Width, dev.Width / ss), dh = Math.Min(target.Height, dev.Height / ss);
            int area = ss * ss;
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int sr = 0, sg = 0, sb = 0, sa = 0;
                    for (int dy = 0; dy < ss; dy++)
                    {
                        int row = (y * ss + dy) * dev.Width;
                        for (int dx = 0; dx < ss; dx++)
                        {
                            int i = row + x * ss + dx;
                            sr += dev.R[i]; sg += dev.G[i]; sb += dev.B[i]; sa += dev.A[i];
                        }
                    }
                    sr /= area; sg /= area; sb /= area; sa /= area;
                    int q = (y * target.Width + x) * 4;
                    if (sa <= 0)
                    {
                        target.Pixels[q] = 0; target.Pixels[q + 1] = 0; target.Pixels[q + 2] = 0; target.Pixels[q + 3] = 0;
                    }
                    else
                    {
                        if (sr > sa) sr = sa;
                        if (sg > sa) sg = sa;
                        if (sb > sa) sb = sa;
                        target.Pixels[q] = (byte)(sr * 255 / sa);
                        target.Pixels[q + 1] = (byte)(sg * 255 / sa);
                        target.Pixels[q + 2] = (byte)(sb * 255 / sa);
                        target.Pixels[q + 3] = (byte)sa;
                    }
                }
            }
        }
    }
}
