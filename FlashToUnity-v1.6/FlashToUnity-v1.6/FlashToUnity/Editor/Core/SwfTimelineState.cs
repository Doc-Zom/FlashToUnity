// FlashToUnity - per-depth placement state on a timeline (shared by renderer and compiler).

using System.Collections.Generic;

namespace FlashToUnity.Core
{
    public sealed class SwfPlacementState
    {
        public int Depth;
        public int CharacterId;
        public SwfMatrix Matrix = SwfMatrix.Identity;
        public SwfColorTransform Color = SwfColorTransform.Identity;
        public int ClipDepth;
        public string Name;
        public bool HasContent;
        public int PlacedFrame; // frame on the containing timeline when this instance was created
    }

    public static class SwfTimelineState
    {
        /// <summary>Apply one frame's place/remove commands to a depth-keyed state map (order matters).
        /// Copy-on-write: existing entries are replaced, never mutated, so earlier
        /// per-frame snapshots taken via Clone() stay valid.</summary>
        public static void Apply(List<SwfPlaceCommand> commands, Dictionary<int, SwfPlacementState> state, int frame = 0)
        {
            foreach (SwfPlaceCommand c in commands)
            {
                if (c.IsRemove)
                {
                    state.Remove(c.Depth);
                    continue;
                }
                SwfPlacementState st;
                bool exists = state.TryGetValue(c.Depth, out st);
                if (c.Move && exists)
                {
                    // copy-on-write: carry the old placement over into a fresh entry
                    SwfPlacementState ns = new SwfPlacementState();
                    ns.Depth = st.Depth;
                    ns.CharacterId = st.CharacterId;
                    ns.HasContent = st.HasContent;
                    ns.Matrix = st.Matrix;
                    ns.Color = st.Color;
                    ns.ClipDepth = st.ClipDepth;
                    ns.Name = st.Name;
                    ns.PlacedFrame = st.PlacedFrame;
                    st = ns;
                    state[c.Depth] = ns;
                }
                else if (c.HasCharacter)
                {
                    st = new SwfPlacementState { Depth = c.Depth };
                    state[c.Depth] = st;
                }
                else
                {
                    continue;
                }
                if (c.HasCharacter)
                {
                    st.CharacterId = c.CharacterId;
                    st.HasContent = true;
                    st.PlacedFrame = frame;
                }
                if (c.HasMatrix) st.Matrix = c.Matrix;
                if (c.HasColor) st.Color = c.Color;
                if (c.HasClipDepth) st.ClipDepth = c.ClipDepth;
                if (!string.IsNullOrEmpty(c.Name)) st.Name = c.Name;
            }
        }

        /// <summary>Shallow copy of the current state. Safe because Apply never mutates entries.</summary>
        public static Dictionary<int, SwfPlacementState> Clone(Dictionary<int, SwfPlacementState> state)
        {
            Dictionary<int, SwfPlacementState> copy = new Dictionary<int, SwfPlacementState>(state.Count);
            foreach (KeyValuePair<int, SwfPlacementState> kv in state)
                copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>Replay elapsed ticks, keeping instance ages independent of a parent's
        /// wrapped or held frame. Cache entries are immutable display-list snapshots.</summary>
        public static Dictionary<int, SwfPlacementState> AtAge(SwfTimeline timeline, int age, bool loop,
            List<Dictionary<int, SwfPlacementState>> snapshots)
        {
            if (age < 0) age = 0;
            if (age < snapshots.Count) return snapshots[age];
            var rolling = snapshots.Count == 0 ? new Dictionary<int, SwfPlacementState>()
                : Clone(snapshots[snapshots.Count - 1]);
            for (int tick = snapshots.Count; tick <= age; tick++)
            {
                int count = timeline.Frames.Count;
                // A single-frame movie clip holds its display list; it never rewinds
                // its child timelines, even when looping is requested.
                if (count > 0 && (tick < count || (loop && count > 1)))
                {
                    if (tick > 0 && tick % count == 0)
                    {
                        // Rewind reconstructs the first display list. Late placements
                        // disappear, but a surviving instance keeps its independent clock.
                        var previous = rolling;
                        rolling = new Dictionary<int, SwfPlacementState>();
                        Apply(timeline.Frames[0], rolling, tick);
                        foreach (var pair in rolling)
                        {
                            SwfPlacementState survivor;
                            if (previous.TryGetValue(pair.Key, out survivor)
                                && survivor.CharacterId == pair.Value.CharacterId)
                                pair.Value.PlacedFrame = survivor.PlacedFrame;
                        }
                    }
                    else Apply(timeline.Frames[tick % count], rolling, tick);
                }
                snapshots.Add(Clone(rolling));
            }
            return snapshots[age];
        }
    }
}
