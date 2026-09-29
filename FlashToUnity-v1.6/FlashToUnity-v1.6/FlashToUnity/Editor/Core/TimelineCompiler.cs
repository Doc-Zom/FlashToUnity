// FlashToUnity - compiles a SWF timeline into flat animation tracks.
// A track = one GameObject in the flattened hierarchy; keys are recorded per frame:
//   - active (exists on the display list)
//   - character shown (leaf content -> sprite swap) or 0 for pure container nodes
//   - local matrix (relative to the parent track) and color transform
// Track paths include their display depth, so independent segment compiles use
// the same paths as the master prefab, even when names collide in other frames.
// Pure C#, no Unity dependencies.

using System;
using System.Collections.Generic;

namespace FlashToUnity.Core
{
    public struct FlatFrameKey
    {
        public bool Active;
        public int CharId;                 // 0 = container / nothing to draw
        public SwfMatrix Matrix;           // local matrix relative to the parent track
        public SwfColorTransform Color;
    }

    public sealed class FlatTrack
    {
        public string Path;                // relative to prefab root
        public string GameObjectName;
        public string ParentPath;          // "" for root children
        public int Depth;                  // Flash display-list depth inside the parent timeline
        public int SortOrder;              // index in Flash draw order (see AssignRenderOrder)
        public List<FlatFrameKey> Keys = new List<FlatFrameKey>();
    }

    public sealed class CompiledClip
    {
        public string Name;
        public int StartFrame;             // on the main timeline
        public int FrameCount;
        public float Fps;
        public List<FlatTrack> Tracks = new List<FlatTrack>();
    }

    public enum SwfSplitMode
    {
        /// <summary>Frame labels when present, otherwise automatic detection.</summary>
        Auto = 0,
        /// <summary>Only split on frame labels; a timeline without labels stays one clip.</summary>
        LabelsOnly = 1,
        /// <summary>Split only at automatically detected segment boundaries.</summary>
        Detected = 2,
        /// <summary>Split only at the manually supplied frame numbers.</summary>
        Manual = 3,
        /// <summary>Never split: always one clip for the whole timeline.</summary>
        None = 4,
    }

    public sealed class CompileOptions
    {
        public bool SpriteLoop;
        public float Fps;
        public SwfSplitMode SplitMode = SwfSplitMode.Auto;
        public int[] ManualSplitFrames;      // segment start frames (0 is implicit)
        public int MinSegmentFrames = 4;
    }

    public static class TimelineCompiler
    {
        private sealed class NodeFrame
        {
            public string Path;
            public string ParentPath;
            public string Name;
            public int Depth;
            public int SortOrder;
        }

        public static List<CompiledClip> CompileMainTimeline(SwfMovie movie, CompileOptions opt)
        {
            string baseName = Sanitize(System.IO.Path.GetFileNameWithoutExtension(movie.SourceName));
            int total = movie.MainTimeline.FrameCount;

            // collect (startFrame, name) pairs according to the split mode
            List<KeyValuePair<int, string>> starts = new List<KeyValuePair<int, string>>();

            List<SwfFrameLabel> labels = new List<SwfFrameLabel>(movie.Labels);
            labels.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            bool useLabels = (opt.SplitMode == SwfSplitMode.Auto || opt.SplitMode == SwfSplitMode.LabelsOnly) && labels.Count > 0;
            bool useDetected = opt.SplitMode == SwfSplitMode.Detected
                               || (opt.SplitMode == SwfSplitMode.Auto && labels.Count == 0);
            bool useManual = opt.SplitMode == SwfSplitMode.Manual;

            if (opt.SplitMode != SwfSplitMode.None && useLabels)
            {
                int segStart = 0;
                string segName = null;
                foreach (SwfFrameLabel l in labels)
                {
                    if (l.Frame > total) break;
                    if (l.Frame > segStart)
                        starts.Add(new KeyValuePair<int, string>(segStart, Sanitize(segName ?? "intro")));
                    segStart = l.Frame;
                    segName = l.Name;
                }
                if (segStart < total)
                    starts.Add(new KeyValuePair<int, string>(segStart, Sanitize(segName ?? "anim")));
            }
            else if (opt.SplitMode != SwfSplitMode.None && (useDetected || useManual))
            {
                int[] frames = useManual ? (opt.ManualSplitFrames ?? new int[0])
                                         : DetectSegmentBoundaries(movie, opt.MinSegmentFrames).ToArray();
                List<int> pts = new List<int>();
                pts.Add(0);
                foreach (int f in frames)
                    if (f > 0 && f < total && !pts.Contains(f)) pts.Add(f);
                pts.Sort();
                for (int i = 0; i < pts.Count; i++)
                    starts.Add(new KeyValuePair<int, string>(pts[i], baseName + "_seg" + (i + 1)));
            }

            List<CompiledClip> clips = new List<CompiledClip>();
            if (starts.Count == 0)
            {
                clips.Add(CompileSegment(movie, baseName, 0, total, opt));
                return clips;
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i].Key;
                int to = (i + 1 < starts.Count) ? starts[i + 1].Key : total;
                int count = to - from;
                if (count <= 0) continue;
                clips.Add(CompileSegment(movie, MakeUniqueClipName(clips, starts[i].Value), from, count, opt));
            }
            return clips;
        }

        private static string MakeUniqueClipName(List<CompiledClip> existing, string name)
        {
            string n = name;
            int k = 2;
            while (true)
            {
                bool clash = false;
                foreach (CompiledClip c in existing) if (c.Name == n) { clash = true; break; }
                if (!clash) return n;
                n = name + "_" + k;
                k++;
            }
        }

        /// <summary>
        /// Heuristic segmentation for timelines that concatenate several animations
        /// without frame labels (e.g. a plant exported with bud / sprout / idle / mature
        /// stages in one range). A segment boundary is a frame whose VISIBLE CHARACTER
        /// SET is largely replaced and whose display-list depth layout is restructured:
        ///   - symmetric difference of visible character ids >= 4, AND
        ///   - at least 2 depths added/removed (or the character set almost fully swapped)
        /// Small part swaps (one body sprite replaced while everything else stays, as in
        /// the Wallnut's bite marks) are correctly NOT treated as boundaries.
        /// </summary>
        public static List<int> DetectSegmentBoundaries(SwfMovie movie, int minSegmentFrames)
        {
            if (minSegmentFrames < 1) minSegmentFrames = 1;
            List<int> boundaries = new List<int>();
            SwfTimeline tl = movie.MainTimeline;
            Dictionary<int, SwfPlacementState> state = new Dictionary<int, SwfPlacementState>();
            HashSet<int> charsBefore = new HashSet<int>();
            HashSet<int> depthsBefore = new HashSet<int>();

            for (int f = 0; f < tl.Frames.Count; f++)
            {
                HashSet<int> charsAfter = new HashSet<int>();
                HashSet<int> depthsAfter = new HashSet<int>();
                foreach (KeyValuePair<int, SwfPlacementState> kv in state)
                {
                    if (!kv.Value.HasContent) continue;
                    depthsAfter.Add(kv.Key);
                    charsAfter.Add(kv.Value.CharacterId);
                }

                // NOTE: at iteration f, charsAfter describes the display list AFTER
                // frame f-1's commands, so a change seen here is a boundary at f-1
                int boundary = f - 1;
                if (f > 0 && boundary > 0)
                {
                    int symdiff = 0;
                    foreach (int c in charsBefore) if (!charsAfter.Contains(c)) symdiff++;
                    foreach (int c in charsAfter) if (!charsBefore.Contains(c)) symdiff++;

                    int maxSize = Math.Max(charsBefore.Count, charsAfter.Count);
                    // "most of the visible content was replaced": an absolute floor of 4
                    // changed characters plus at least half of the larger set. Mid-animation
                    // multi-part sprite swaps (e.g. the zombie's limbs) stay below this.
                    bool wholesale = symdiff >= 4 && symdiff * 2 >= maxSize;
                    if (wholesale)
                    {
                        int last = boundaries.Count > 0 ? boundaries[boundaries.Count - 1] : 0;
                        int tail = tl.Frames.Count - boundary;
                        if (boundary - last >= minSegmentFrames && tail >= minSegmentFrames)
                            boundaries.Add(boundary);
                    }
                }

                charsBefore = charsAfter;
                depthsBefore = depthsAfter;
                SwfTimelineState.Apply(tl.Frames[f], state);
            }
            return boundaries;
        }

        public static CompiledClip CompileSegment(SwfMovie movie, string clipName, int startFrame, int frameCount, CompileOptions opt)
        {
            if (frameCount <= 0) frameCount = 1;
            CompiledClip clip = new CompiledClip();
            clip.Name = clipName;
            clip.StartFrame = startFrame;
            clip.FrameCount = frameCount;
            clip.Fps = opt.Fps > 0 ? opt.Fps : movie.FrameRate;

            // pass 0: per-frame placement states (accumulated: Flash timelines are stateful)
            List<Dictionary<int, SwfPlacementState>> states = new List<Dictionary<int, SwfPlacementState>>(frameCount);
            Dictionary<int, SwfPlacementState> rolling = new Dictionary<int, SwfPlacementState>();
            for (int pre = 0; pre < startFrame && pre < movie.MainTimeline.Frames.Count; pre++)
                SwfTimelineState.Apply(movie.MainTimeline.Frames[pre], rolling, pre);
            for (int i = 0; i < frameCount; i++)
            {
                int f = startFrame + i;
                if (f >= 0 && f < movie.MainTimeline.Frames.Count)
                    SwfTimelineState.Apply(movie.MainTimeline.Frames[f], rolling, f);
                states.Add(SwfTimelineState.Clone(rolling));
            }

            // Cache nested display-list snapshots while compiling.
            Dictionary<int, List<Dictionary<int, SwfPlacementState>>> spriteFrames =
                new Dictionary<int, List<Dictionary<int, SwfPlacementState>>>();
            // Depth-prefixed names are deterministic across master/segment compiles,
            // and cannot collide with a literal instance name such as "part_d1".

            // pass 2: walk every frame, collecting per-path values.
            // IMPORTANT: per-frame values are stored as struct copies (NodeState);
            // sharing the registry objects between frames would alias them.
            Dictionary<string, NodeFrame> registry = new Dictionary<string, NodeFrame>();
            List<List<KeyValuePair<string, NodeState>>> frames = new List<List<KeyValuePair<string, NodeState>>>(frameCount);
            for (int i = 0; i < states.Count; i++)
            {
                Dictionary<string, NodeState> snap = new Dictionary<string, NodeState>();
                Walk(movie, states[i], startFrame + i, "", 0, 0, SwfColorTransform.Identity,
                    snap, registry, opt, spriteFrames);
                frames.Add(new List<KeyValuePair<string, NodeState>>(snap));
            }

            // pass 3: build tracks, one key per frame (missing = inactive)
            List<FlatTrack> tracks = new List<FlatTrack>(registry.Count);
            foreach (KeyValuePair<string, NodeFrame> kv in registry)
            {
                FlatTrack t = new FlatTrack();
                t.Path = kv.Key;
                t.GameObjectName = kv.Value.Name;
                t.ParentPath = kv.Value.ParentPath ?? "";
                t.Depth = kv.Value.Depth;
                t.SortOrder = kv.Value.SortOrder;
                t.Keys = new List<FlatFrameKey>(frameCount);
                for (int i = 0; i < frameCount; i++)
                {
                    FlatFrameKey empty = new FlatFrameKey();
                    empty.Active = false;
                    empty.CharId = 0;
                    empty.Matrix = SwfMatrix.Identity;
                    empty.Color = SwfColorTransform.Identity;
                    t.Keys.Add(empty);
                }
                tracks.Add(t);
            }
            tracks.Sort((a, b) =>
            {
                int c = PathDepth(a.Path).CompareTo(PathDepth(b.Path));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.ParentPath, b.ParentPath);
                if (c != 0) return c;
                c = a.SortOrder.CompareTo(b.SortOrder);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Path, b.Path);
            });

            Dictionary<string, FlatTrack> trackMap = new Dictionary<string, FlatTrack>();
            foreach (FlatTrack t in tracks) trackMap[t.Path] = t;

            AssignRenderOrder(tracks, trackMap);

            for (int i = 0; i < frames.Count; i++)
            {
                foreach (KeyValuePair<string, NodeState> kv in frames[i])
                {
                    FlatTrack t;
                    if (!trackMap.TryGetValue(kv.Key, out t)) continue;
                    FlatFrameKey key = new FlatFrameKey();
                    key.Active = true;
                    key.CharId = kv.Value.CharId;
                    key.Matrix = kv.Value.Matrix;
                    key.Color = kv.Value.Color;
                    t.Keys[i] = key;
                }
            }

            clip.Tracks = tracks;
            return clip;
        }

        /// <summary>
        /// sortingOrder must follow Flash's draw order: at every level children are drawn by
        /// ascending depth, and a nested symbol draws its whole contents as one unit at the
        /// position of that symbol. Deriving the number arithmetically from the depths
        /// (old parentOrder*64+depth) interleaves children with their uncles, which misplaced
        /// most parts in real files (e.g. 200 of 504 frames in Zombie.fla).
        /// A depth-first walk gives each track the next index, so any subset of visible parts
        /// keeps Flash's exact stacking.
        /// </summary>
        private static void AssignRenderOrder(List<FlatTrack> tracks, Dictionary<string, FlatTrack> trackMap)
        {
            Dictionary<string, List<FlatTrack>> childrenByParent = new Dictionary<string, List<FlatTrack>>();
            foreach (FlatTrack t in tracks)
            {
                List<FlatTrack> list;
                if (!childrenByParent.TryGetValue(t.ParentPath, out list))
                    childrenByParent[t.ParentPath] = list = new List<FlatTrack>();
                list.Add(t);
            }
            foreach (List<FlatTrack> list in childrenByParent.Values)
                list.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth)
                                                        : string.CompareOrdinal(a.Path, b.Path));
            int[] counter = new int[1];
            OrderSubtree("", childrenByParent, counter, 0);
        }

        private static void OrderSubtree(string parentPath, Dictionary<string, List<FlatTrack>> childrenByParent,
            int[] counter, int nest)
        {
            if (nest > 24) return;
            List<FlatTrack> children;
            if (!childrenByParent.TryGetValue(parentPath, out children)) return;
            foreach (FlatTrack t in children)
            {
                t.SortOrder = counter[0]++;
                OrderSubtree(t.Path, childrenByParent, counter, nest + 1);
            }
        }

        // -------- recursive walk --------

        private struct NodeState
        {
            public int CharId;
            public SwfMatrix Matrix;
            public SwfColorTransform Color;
        }

        /// <summary>Display list a nested symbol shows at its own frame f. Flash timelines are
        /// stateful (a placement lives until removed), so this accumulates frames 0..f and caches
        /// the per-frame snapshots per symbol.</summary>
        private static Dictionary<int, SwfPlacementState> SpriteStateAt(SwfCharacter ch, int f, bool loop,
            Dictionary<int, List<Dictionary<int, SwfPlacementState>>> cache)
        {
            List<Dictionary<int, SwfPlacementState>> snapshots;
            if (!cache.TryGetValue(ch.Id, out snapshots))
            {
                snapshots = new List<Dictionary<int, SwfPlacementState>>();
                cache[ch.Id] = snapshots;
            }
            return SwfTimelineState.AtAge(ch.Sprite.Timeline, f, loop, snapshots);
        }

        private static void Walk(SwfMovie movie, Dictionary<int, SwfPlacementState> state, int frame,
            string parentPath, int parentOrder, int nest, SwfColorTransform inheritedCt,
            Dictionary<string, NodeState> snap, Dictionary<string, NodeFrame> registry,
            CompileOptions opt,
            Dictionary<int, List<Dictionary<int, SwfPlacementState>>> spriteFrames)
        {
            if (nest > 8) return;
            List<int> depths = new List<int>(state.Keys);
            depths.Sort();

            foreach (int depth in depths)
            {
                SwfPlacementState st = state[depth];
                SwfCharacter ch = movie.GetCharacter(st.CharacterId);
                if (ch == null) continue;

                // color transforms apply recursively to sprite children, so accumulate
                // down the chain and bake the final value onto leaf tracks
                SwfColorTransform ownCt = SwfColorTransform.Combine(inheritedCt, st.Color);

                string baseName = ChooseBaseName(st, ch, depth);

                string path = parentPath.Length == 0 ? baseName : parentPath + "/" + baseName;
                int sortOrder = MixOrder(parentOrder, depth);

                if (!registry.ContainsKey(path))
                {
                    NodeFrame meta = new NodeFrame();
                    meta.Path = path;
                    meta.ParentPath = parentPath;
                    meta.Name = LeafName(path);
                    meta.Depth = depth;
                    meta.SortOrder = sortOrder;
                    registry[path] = meta;
                }

                NodeState ns = new NodeState();
                ns.Matrix = st.Matrix;
                ns.Color = ownCt;
                ns.CharId = 0;
                snap[path] = ns;

                if (ch.Kind == SwfCharacterKind.Sprite)
                {
                    SwfTimeline tl = ch.Sprite.Timeline;
                    if (tl == null || tl.Frames.Count == 0) continue;
                    int f = Math.Max(0, frame - st.PlacedFrame);
                    Dictionary<int, SwfPlacementState> inner = SpriteStateAt(ch, f, opt.SpriteLoop, spriteFrames);
                    Walk(movie, inner, f, path, sortOrder, nest + 1, ownCt, snap, registry, opt, spriteFrames);
                }
                else
                {
                    NodeState leaf = snap[path];
                    leaf.CharId = ch.Id;
                    snap[path] = leaf;
                }
            }
        }

        private static string ChooseBaseName(SwfPlacementState st, SwfCharacter ch, int depth)
        {
            string name = null;
            if (!string.IsNullOrEmpty(st.Name)) name = st.Name;
            else if (!string.IsNullOrEmpty(ch.Name)) name = ch.Name;
            if (string.IsNullOrEmpty(name))
                name = (ch.Kind == SwfCharacterKind.Bitmap ? "img" : ch.Kind == SwfCharacterKind.Shape ? "shape" : "spr")
                       + ch.Id + "_d" + depth;
            return "d" + depth + "_" + Sanitize(name);
        }

        private static string LeafName(string path)
        {
            int idx = path.LastIndexOf('/');
            return idx < 0 ? path : path.Substring(idx + 1);
        }

        private static int PathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int n = 1;
            for (int i = 0; i < path.Length; i++) if (path[i] == '/') n++;
            return n;
        }

        private static int MixOrder(int parentOrder, int depth)
        {
            int order = parentOrder * 64 + depth;
            if (order > 30000) order = 30000;
            return order;
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "node";
            char[] arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                char c = arr[i];
                if (c == '/' || c == '\\' || c == '.' || c == ':' || c == ' ' || char.IsControl(c)) arr[i] = '_';
            }
            return new string(arr);
        }
    }
}
