// FlashToUnity - Unity editor importer: SWF file -> textures + AnimationClips + prefab.
//
// Two conversion modes:
//  * Hierarchy mode (default): flattens the Flash display tree into GameObjects and
//    bakes per-frame transform / color / sprite-swap / active curves into one
//    AnimationClip per timeline segment. Modular and tiny in memory.
//  * Pre-rendered mode: composites every frame into a PNG (through the same
//    rasterizer the verification harness uses) and builds a sprite-swap clip.
//    Most faithful visuals, useful as a reference or for complex effects.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FlashToUnity.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FlashToUnity.EditorTools
{
    public class SwfImportSettings
    {
        public float PixelsPerUnit = 100f;
        public bool PrerenderFrames;      // false = hierarchy mode
        public bool SpriteLoop;           // loop nested sprite timelines instead of holding
        public int Supersample = 2;       // rasterizer SSAA for vector shapes / prerender
        public bool UncompressedTextures = true;
        public FilterMode TextureFilter = FilterMode.Bilinear;
        // timeline segmentation: several animations concatenated in one timeline are
        // split into independent clips (see SwfSplitMode)
        public SwfSplitMode SplitMode = SwfSplitMode.Auto;
        public int[] ManualSplitFrames;
        public int MinSegmentFrames = 4;
    }

    public static class SwfImporterVersion
    {
        public const string Version = "v1.6";
    }

    public static class SwfImporter
    {
    private static readonly bool LogCurves = System.Environment.GetEnvironmentVariable("F2U_LOG_CURVES") != null;

    private sealed class CharExport
    {
        public string TexturePath;    // asset path of the imported sprite texture
        public float OriginX;         // flash registration point (0,0) in texture px from top-left
        public float OriginY;
        public int Width, Height;
    }

        public static void Import(string swfPath, string outputRootFolder, SwfImportSettings opt, Action<string> log)
        {
            if (log == null) log = delegate { };
            string movieName = SanitizeFile(Path.GetFileNameWithoutExtension(swfPath));
            log("=== FlashToUnity " + SwfImporterVersion.Version + " ===");

            byte[] raw = File.ReadAllBytes(swfPath);
            SwfMovie movie = SwfParser.Parse(raw, Path.GetFileName(swfPath));
            foreach (string w in movie.Warnings) log("[SWF] " + w);
            if (movie.MainTimeline.Frames.Count == 0) throw new InvalidDataException("SWF 主时间轴为空");

            // Keep existing .meta files and user-authored assets. Stable asset identities
            // let character profiles and nested prefab instances survive reimport.
            string outFolder = outputRootFolder + "/" + movieName;
            EnsureFolder(outFolder);

            if (opt.PrerenderFrames) ImportPrerendered(movie, swfPath, outFolder, movieName, opt, log);
            else ImportHierarchy(movie, swfPath, outFolder, movieName, opt, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // =============================================================
        // hierarchy mode
        // =============================================================

        private static void ImportHierarchy(SwfMovie movie, string swfPath, string root, string movieName,
            SwfImportSettings opt, Action<string> log)
        {
            int totalFrames = movie.MainTimeline.Frames.Count;

            // master compile: full timeline, used for the prefab hierarchy
            CompileOptions copt = new CompileOptions
            {
                Fps = movie.FrameRate,
                SpriteLoop = opt.SpriteLoop,
                SplitMode = opt.SplitMode,
                ManualSplitFrames = opt.ManualSplitFrames,
                MinSegmentFrames = opt.MinSegmentFrames,
            };
            CompiledClip master = TimelineCompiler.CompileSegment(movie, movieName, 0, totalFrames, copt);

            // collect every character referenced by any track
            HashSet<int> usedChars = new HashSet<int>();
            foreach (FlatTrack t in master.Tracks)
                foreach (FlatFrameKey k in t.Keys)
                    if (k.CharId != 0) usedChars.Add(k.CharId);

            // 1. export textures
            string texFolder = root + "/Textures";
            EnsureFolder(texFolder);
            Dictionary<int, CharExport> exports = new Dictionary<int, CharExport>();
            int done = 0;
            foreach (int charId in usedChars)
            {
                SwfCharacter ch = movie.GetCharacter(charId);
                if (ch == null) continue;
                if (EditorUtility.DisplayCancelableProgressBar("Flash To Unity",
                    "导出贴图 " + (++done) + "/" + usedChars.Count, done / (float)usedChars.Count))
                    throw new OperationCanceledException("已取消");
                CharExport exp = ExportCharacter(movie, ch, texFolder, opt);
                if (exp != null) exports[charId] = exp;
            }
            AssetDatabase.Refresh();

            // 2. import sprite settings + load sprites
            Dictionary<int, Sprite> sprites = new Dictionary<int, Sprite>();
            foreach (KeyValuePair<int, CharExport> kv in exports)
            {
                ConfigureTextureImporter(kv.Value.TexturePath, opt);
            }
            AssetDatabase.Refresh();
            foreach (KeyValuePair<int, CharExport> kv in exports)
            {
                Sprite sp = AssetDatabase.LoadAssetAtPath<Sprite>(kv.Value.TexturePath);
                if (sp != null) sprites[kv.Key] = sp;
                else log("[警告] 字符 #" + kv.Key + " 的贴图导入失败: " + kv.Value.TexturePath);
            }

            // 3. build the GameObject hierarchy from master tracks.
            // Each track becomes FOUR nodes:
            //   <name>              outer: localPosition + rot1
            //   <name>/__scl        scale node: localScale
            //   <name>/__scl/__rot  rotation node: rot2 (child tracks hang under it)
            //   <name>/__scl/__rot/__spr  sprite node: SpriteRenderer + constant offset so
            //                       the FLASH REGISTRATION POINT (not the texture center)
            //                       sits at this node's origin. Uses the default CENTER
            //                       pivot — Unity's custom-pivot/sprite-rect handling is
            //                       unreliable (alignment must be Custom, and even then
            //                       textureRect may be rewritten).
            EditorUtility.DisplayProgressBar("Flash To Unity", "构建层级...", 0.4f);
            GameObject rootGo = new GameObject(movieName);
            try
            {
                Dictionary<string, GameObject> goByPath = new Dictionary<string, GameObject>();
                Dictionary<string, string> unityPathByTrack = new Dictionary<string, string>();
                Dictionary<string, FlatTrack> masterByPath = new Dictionary<string, FlatTrack>();
                foreach (FlatTrack t in master.Tracks) masterByPath[t.Path] = t;
                foreach (FlatTrack t in master.Tracks)
                {
                    string parentUnityRot = string.IsNullOrEmpty(t.ParentPath)
                        ? null
                        : unityPathByTrack[t.ParentPath] + "/" + SclName + "/" + RotName;
                    GameObject parentGo = parentUnityRot == null
                        ? rootGo
                        : goByPath[t.ParentPath].transform.Find(SclName + "/" + RotName).gameObject;

                    GameObject outer = new GameObject(t.GameObjectName);
                    outer.transform.SetParent(parentGo.transform, false);
                    GameObject scl = new GameObject(SclName);
                    scl.transform.SetParent(outer.transform, false);
                    GameObject rot = new GameObject(RotName);
                    rot.transform.SetParent(scl.transform, false);
                    GameObject spr = new GameObject(SprName);
                    spr.transform.SetParent(rot.transform, false);

                    SpriteRenderer sr = spr.AddComponent<SpriteRenderer>();
                    sr.sortingOrder = t.SortOrder;
                    // renderers stay ENABLED permanently — visibility is handled purely
                    // by the animated alpha (a disabled renderer ignores alpha entirely)
                    sr.enabled = true;

                    goByPath[t.Path] = outer;
                    unityPathByTrack[t.Path] = (parentUnityRot == null ? "" : parentUnityRot + "/") + t.GameObjectName;

                    // The saved prefab must show frame zero, not all future poses at once.
                    ApplyKeyToTransform(outer.transform, scl.transform, rot.transform, t.Keys[0].Matrix, opt.PixelsPerUnit);
                    // NOTE: GameObjects are NEVER deactivated. Runtime Animators proved
                    // not to apply m_IsActive/m_Enabled discrete curves (verified with a
                    // play-mode probe: parts activated by the curve stayed inactive), and
                    // an inactive GameObject cannot be shown by the alpha curve either.
                    // Visibility is handled purely by the alpha channel instead.
                    // renderer defaults from the first leaf frame
                    for (int i = 0; i < t.Keys.Count; i++)
                    {
                        FlatFrameKey kk = t.Keys[i];
                        if (kk.Active && kk.CharId != 0)
                        {
                            Sprite sp0;
                            if (sprites.TryGetValue(kk.CharId, out sp0))
                            {
                                sr.sprite = sp0;
                                spr.transform.localPosition = SpriteOffset(kk.CharId, exports, opt.PixelsPerUnit);
                            }
                            sr.color = KeyColor(kk.Color);
                            break;
                        }
                    }
                    Color initialColor = KeyColor(t.Keys[0].Color);
                    if (!t.Keys[0].Active || t.Keys[0].CharId == 0) initialColor.a = 0;
                    sr.color = initialColor;
                }

                // 4. clips (segmentation happens here: labels > auto-detected > manual > none)
                List<CompiledClip> clips = TimelineCompiler.CompileMainTimeline(movie, copt);
                {
                    StringBuilder sb = new StringBuilder("[分段] " + movieName + ": " + clips.Count + " 个片段");
                    foreach (CompiledClip c in clips)
                        sb.Append("  ").Append(c.Name).Append("(").Append(c.StartFrame).Append("-")
                          .Append(c.StartFrame + c.FrameCount - 1).Append(")");
                    log(sb.ToString());
                    if (clips.Count == 1 && opt.SplitMode != SwfSplitMode.None)
                    {
                        List<int> cand = TimelineCompiler.DetectSegmentBoundaries(movie, opt.MinSegmentFrames);
                        if (cand.Count > 0)
                            log("[提示] 检测到候选分段点: " + string.Join(", ", Array.ConvertAll(cand.ToArray(), v => v.ToString()))
                                + "（未生效，可将分段方式设为手动并填入这些帧号）");
                    }
                }
                if (clips.Count == 1 && clips[0].FrameCount == totalFrames && clips[0].Name == movieName)
                {
                    clips[0] = master; // reuse the master compile
                }

                string animFolder = root + "/Animations";
                EnsureFolder(animFolder);
                AnimatorController controller = FlashGeneratedAssets.ResetController(
                    animFolder + "/" + movieName + ".controller");

                var clipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int ci = 0; ci < clips.Count; ci++)
                {
                    CompiledClip clip = clips[ci];
                    if (EditorUtility.DisplayCancelableProgressBar("Flash To Unity",
                        "生成动画 " + clip.Name + " (" + (ci + 1) + "/" + clips.Count + ")", (ci + 1) / (float)clips.Count))
                        throw new OperationCanceledException("已取消");
                    string baseName = SanitizeFile(clip.Name), assetName = baseName;
                    int suffix = 2;
                    while (!clipNames.Add(assetName)) assetName = baseName + "_" + suffix++;
                    AnimationClip unityClip = BuildClip(clip, master, sprites, exports, opt, animFolder + "/" + assetName + ".anim", log);
                    if (unityClip != null) controller.AddMotion(unityClip);
                }

                Animator animator = rootGo.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                // parts are shown/hidden by alpha curves; with the default culling
                // mode a culled (fully invisible) branch can stop receiving animation,
                // which would prevent its re-activation. 2D characters are cheap: always animate.
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // 5. save prefab
                string prefabPath = root + "/" + movieName + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(rootGo, prefabPath);
                log("[完成] Prefab: " + prefabPath + "  (" + master.Tracks.Count + " 个部件, " + clips.Count + " 个动画片段)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGo);
                EditorUtility.ClearProgressBar();
            }
        }

        private const string SclName = "__scl";
        private const string RotName = "__rot";
        private const string SprName = "__spr";

        private static AnimationClip BuildClip(CompiledClip clip, CompiledClip master,
            Dictionary<int, Sprite> sprites, Dictionary<int, CharExport> exports, SwfImportSettings opt,
            string clipAssetPath, Action<string> log)
        {
            Dictionary<string, FlatTrack> masterTracks = new Dictionary<string, FlatTrack>();
            foreach (FlatTrack t in master.Tracks) masterTracks[t.Path] = t;
            Dictionary<string, FlatTrack> trackByPath = new Dictionary<string, FlatTrack>();
            foreach (FlatTrack t in clip.Tracks) trackByPath[t.Path] = t;

            AnimationClip ac = new AnimationClip();
            ac.frameRate = clip.Fps > 0 ? clip.Fps : 12f;
            float dt = 1f / ac.frameRate;

            // unity paths: outer node = track path; scale/rot nodes nest under it;
            // child tracks hang under the parent's rot node.
            // Built over the MASTER tracks (the prefab's full part set, parents first)
            // so that parts belonging to other clips can be resolved too.
            Dictionary<string, string> unityPathByTrack = new Dictionary<string, string>();
            foreach (FlatTrack t in master.Tracks)
            {
                string parentRot = string.IsNullOrEmpty(t.ParentPath)
                    ? ""
                    : unityPathByTrack[t.ParentPath] + "/" + SclName + "/" + RotName;
                unityPathByTrack[t.Path] = (parentRot.Length == 0 ? "" : parentRot + "/") + t.GameObjectName;
            }

            // per-track chain visibility: a part is only visible when it AND all of its
            // ancestors are on the display list (a removed container hides its subtree)
            Dictionary<string, bool[]> chainActiveByTrack = new Dictionary<string, bool[]>();
            foreach (FlatTrack t in clip.Tracks)
            {
                bool[] chain = new bool[clip.FrameCount];
                for (int i = 0; i < clip.FrameCount; i++) chain[i] = true;
                for (string p = t.Path; !string.IsNullOrEmpty(p); )
                {
                    FlatTrack pt;
                    // IMPORTANT: prefer the CLIP's own track — its keys are clip-local.
                    // The master track's keys are indexed by TIMELINE frame, so using
                    // them here would read the wrong frames for any segment that does
                    // not start at frame 0 (parts of the segment's first frame were
                    // then treated as hidden and forced to alpha 0).
                    if (trackByPath.TryGetValue(p, out pt))
                    {
                        for (int i = 0; i < clip.FrameCount && i < pt.Keys.Count; i++)
                            if (!pt.Keys[i].Active) chain[i] = false;
                    }
                    else if (masterTracks.TryGetValue(p, out pt))
                    {
                        for (int i = 0; i < clip.FrameCount; i++)
                        {
                            int k = clip.StartFrame + i;
                            if (k < pt.Keys.Count && !pt.Keys[k].Active) chain[i] = false;
                        }
                    }
                    int slash = p.LastIndexOf('/');
                    p = slash < 0 ? null : p.Substring(0, slash);
                }
                chainActiveByTrack[t.Path] = chain;
            }

            int emitted = 0;
            foreach (FlatTrack ct in clip.Tracks)
            {
                FlatTrack mt;
                if (!masterTracks.TryGetValue(ct.Path, out mt)) continue;
                string outerPath = unityPathByTrack[ct.Path];
                string sclPath = outerPath + "/" + SclName;
                string rotPath = sclPath + "/" + RotName;
                string sprPath = rotPath + "/" + SprName;

                // --- transform curves (only where values actually change) ---
                var posX = new List<Keyframe>(); var posY = new List<Keyframe>();
                var rot1Z = new List<Keyframe>(); var rot1W = new List<Keyframe>();
                var rot2Z = new List<Keyframe>(); var rot2W = new List<Keyframe>();
                var scX = new List<Keyframe>(); var scY = new List<Keyframe>();
                var colorR = new List<Keyframe>(); var colorG = new List<Keyframe>();
                var colorB = new List<Keyframe>(); var colorA = new List<Keyframe>();
                var spriteKeys = new List<ObjectReferenceKeyframe>();
                bool colorUsed = false;
                bool[] chainActive = chainActiveByTrack[ct.Path];

                float? lastPx = null, lastPy = null, lastR1 = null, lastR2 = null, lastSx = null, lastSy = null;
                Color lastColor = Color.clear;
                Sprite lastSprite = null;
                bool spriteSet = false;

                int n = ct.Keys.Count;
                // explicit frame-0 alpha key: parts absent at frame 0 must START
                // invisible (a curve holds its first key value before that key)
                {
                    FlatFrameKey k0c = ct.Keys[0];
                    float a0 = (k0c.Active && chainActive[0]) ? KeyColor(k0c.Color).a : 0f;
                    colorA.Add(Step(0f, a0));
                    lastColor = new Color(0f, 0f, 0f, a0);
                    colorUsed = true;
                }
                // IMPORTANT: key times must be computed as (frame / frameRate) — NOT
                // frame * (1/frameRate): the accumulated float error makes keys land
                // ~1e-7 AFTER the same frame's sampling time, which shifts the whole
                // animation one frame late (verified: sampled values came from the
                // previous key).
                float endTime = Mathf.Max(0f, (clip.FrameCount - 1) / ac.frameRate);
                for (int i = 0; i < n; i++)
                {
                    FlatFrameKey k = ct.Keys[i];
                    float time = Mathf.Min(i / ac.frameRate, endTime);

                    // alpha is keyed on EVERY frame including absent ones — skipped
                    // frames would let the curve hold its last value and the part
                    // would never disappear (or appear)
                    if (!k.Active)
                    {
                        if (lastColor.a != 0f)
                        {
                            colorA.Add(Step(time, 0f));
                            lastColor = new Color(lastColor.r, lastColor.g, lastColor.b, 0f);
                            colorUsed = true;
                        }
                        continue;
                    }

                    // transform baked as pos+rot1 (outer), scale (scale node), rot2 (rot node).
                    // world == F * Matrix * F, hence negated y position and negated angles.
                    float px, py, rot1, sx, sy, rot2;
                    k.Matrix.DecomposeAffine(out px, out py, out rot1, out sx, out sy, out rot2);
                    px /= opt.PixelsPerUnit; py /= opt.PixelsPerUnit;
                    if (px != lastPx) { posX.Add(Step(time, px)); lastPx = px; }
                    if (py != lastPy) { posY.Add(Step(time, -py)); lastPy = py; }
                    if (rot1 != lastR1)
                    {
                        float half = -rot1 * 0.5f;
                        rot1Z.Add(Step(time, Mathf.Sin(half)));
                        rot1W.Add(Step(time, Mathf.Cos(half)));
                        lastR1 = rot1;
                    }
                    if (sx != lastSx) { scX.Add(Step(time, sx)); lastSx = sx; }
                    if (sy != lastSy) { scY.Add(Step(time, sy)); lastSy = sy; }
                    if (rot2 != lastR2)
                    {
                        float half2 = -rot2 * 0.5f;
                        rot2Z.Add(Step(time, Mathf.Sin(half2)));
                        rot2W.Add(Step(time, Mathf.Cos(half2)));
                        lastR2 = rot2;
                    }

                    // sprite swap
                    Sprite sp = null;
                    if (k.CharId != 0) sprites.TryGetValue(k.CharId, out sp);
                    if (!spriteSet || !ReferenceEquals(sp, lastSprite))
                    {
                        spriteKeys.Add(new ObjectReferenceKeyframe { time = time, value = sp });
                        lastSprite = sp;
                        spriteSet = true;
                    }

                    // color — ALSO the visibility mechanism: discrete state curves
                    // (m_IsActive/m_Enabled) proved unreliable at runtime, but plain
                    // float curves (transform/color) demonstrably apply. So visibility
                    // is driven by ALPHA KEYS SPANNING EVERY FRAME:
                    //   part off the display list (or chain hidden) -> alpha 0
                    //   part on the display list -> alpha = placement alpha
                    if (k.CharId != 0)
                    {
                        Color c = KeyColor(k.Color);
                        if (!chainActive[i]) c.a = 0f;
                        if (!c.Equals(lastColor))
                        {
                            colorR.Add(Step(time, c.r)); colorG.Add(Step(time, c.g));
                            colorB.Add(Step(time, c.b)); colorA.Add(Step(time, c.a));
                            lastColor = c;
                            colorUsed = true;
                        }
                    }
                }

                // single-key transform/color curves are constant -> the prefab's static
                // transform already carries that value, so skip them entirely
                Action<EditorCurveBinding, List<Keyframe>, bool> addCurve = delegate (EditorCurveBinding binding, List<Keyframe> keys, bool padSingle)
                {
                    if (keys.Count == 0) return;
                    if (keys.Count == 1)
                    {
                        if (!padSingle) return;
                        keys.Add(new Keyframe(endTime, keys[0].value, float.PositiveInfinity, float.PositiveInfinity));
                    }
                    AnimationCurve curve = new AnimationCurve(keys.ToArray());
                    AnimationUtility.SetEditorCurve(ac, binding, curve);
                    if (LogCurves)
                        Debug.Log("[F2U-curve] " + binding.path + " | " + binding.propertyName + " | keys=" + curve.keys.Length
                            + " | first=" + curve.keys[0].time.ToString("F3") + "->" + curve.keys[0].value.ToString("F3"));
                    emitted++;
                };

                // Every value this clip actually uses must be driven by a curve, including
                // values that never change: the prefab's static transform/sprite belongs to
                // whichever segment came first, so a part that is constant inside THIS clip
                // but differs from that default would otherwise sit in the wrong place.
                addCurve(EditorCurveBinding.FloatCurve(outerPath, typeof(Transform), "m_LocalPosition.x"), posX, true);
                addCurve(EditorCurveBinding.FloatCurve(outerPath, typeof(Transform), "m_LocalPosition.y"), posY, true);
                addCurve(EditorCurveBinding.FloatCurve(outerPath, typeof(Transform), "m_LocalRotation.z"), rot1Z, true);
                addCurve(EditorCurveBinding.FloatCurve(outerPath, typeof(Transform), "m_LocalRotation.w"), rot1W, true);
                addCurve(EditorCurveBinding.FloatCurve(sclPath, typeof(Transform), "m_LocalScale.x"), scX, true);
                addCurve(EditorCurveBinding.FloatCurve(sclPath, typeof(Transform), "m_LocalScale.y"), scY, true);
                addCurve(EditorCurveBinding.FloatCurve(rotPath, typeof(Transform), "m_LocalRotation.z"), rot2Z, true);
                addCurve(EditorCurveBinding.FloatCurve(rotPath, typeof(Transform), "m_LocalRotation.w"), rot2W, true);

                // sprite swap (m_Sprite is discrete: hold the single value across the clip too)
                if (spriteKeys.Count == 1)
                    spriteKeys.Add(new ObjectReferenceKeyframe { time = endTime, value = spriteKeys[0].value });
                if (spriteKeys.Count > 1)
                {
                    var binding = EditorCurveBinding.PPtrCurve(sprPath, typeof(SpriteRenderer), "m_Sprite");
                    AnimationUtility.SetObjectReferenceCurve(ac, binding, spriteKeys.ToArray());
                    emitted++;
                }

                // sprite offset: keep the FLASH REGISTRATION POINT at this node's origin
                // (the sprite's default CENTER pivot would otherwise misplace every part
                // whose registration point is not the texture center)
                var offX = new List<Keyframe>(); var offY = new List<Keyframe>();
                float? lastOffX = null, lastOffY = null;
                for (int i = 0; i < n; i++)
                {
                    FlatFrameKey k = ct.Keys[i];
                    if (!k.Active || k.CharId == 0) continue;
                    float time = Mathf.Min(i / ac.frameRate, endTime);
                    float ox, oy;
                    SpriteOffset(k.CharId, exports, opt.PixelsPerUnit, out ox, out oy);
                    if (ox != lastOffX) { offX.Add(Step(time, ox)); lastOffX = ox; }
                    if (oy != lastOffY) { offY.Add(Step(time, oy)); lastOffY = oy; }
                }
                addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(Transform), "m_LocalPosition.x"), offX, true);
                addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(Transform), "m_LocalPosition.y"), offY, true);

                if (colorUsed)
                {
                    // NOTE padSingle=true for alpha: parts that enter/leave the display
                    // list produce a single alpha key; without padding the curve is
                    // skipped and the renderer keeps its initial alpha (0 for parts
                    // absent at frame 0) — the part stays invisible FOREVER.
                    addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(SpriteRenderer), "m_Color.r"), colorR, true);
                    addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(SpriteRenderer), "m_Color.g"), colorG, true);
                    addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(SpriteRenderer), "m_Color.b"), colorB, true);
                    addCurve(EditorCurveBinding.FloatCurve(sprPath, typeof(SpriteRenderer), "m_Color.a"), colorA, true);
                }
            }

            // A clip only animates the parts that are on the display list during ITS
            // frame range. Every other part of the prefab would otherwise render at its
            // prefab-default transform and alpha (1!) — i.e. all the other segments'
            // parts would leak into this clip. Hide them explicitly with a constant
            // alpha-0 curve.
            {
                HashSet<string> animated = new HashSet<string>();
                foreach (FlatTrack t in clip.Tracks) animated.Add(t.Path);
                float hideEnd = Mathf.Max((clip.FrameCount - 1) / ac.frameRate, 0.0001f);
                foreach (FlatTrack mt in master.Tracks)
                {
                    if (animated.Contains(mt.Path)) continue;
                    string p2;
                    if (!unityPathByTrack.TryGetValue(mt.Path, out p2)) continue;
                    string hidePath = p2 + "/" + SclName + "/" + RotName + "/" + SprName;
                    AnimationCurve hide = new AnimationCurve(
                        new Keyframe(0f, 0f, float.PositiveInfinity, float.PositiveInfinity),
                        new Keyframe(hideEnd, 0f, float.PositiveInfinity, float.PositiveInfinity));
                    AnimationUtility.SetEditorCurve(ac,
                        EditorCurveBinding.FloatCurve(hidePath, typeof(SpriteRenderer), "m_Color.a"), hide);
                    emitted++;
                }
            }

            if (emitted == 0)
            {
                log("[跳过] " + clip.Name + " 没有任何动画数据");
                UnityEngine.Object.DestroyImmediate(ac);
                return null;
            }

            return FlashGeneratedAssets.SaveClip(ac, clipAssetPath);
        }

        private static Keyframe Step(float time, float value)
        {
            return new Keyframe(time, value, float.PositiveInfinity, float.PositiveInfinity);
        }

        /// <summary>
        /// Flash: out = tex*mult + add. A SpriteRenderer can only multiply (out = tex*color),
        /// so the add term is folded into the multiplier: color = mult + add/255. Dark texels
        /// then miss part of the offset (a constant lift is not expressible), which is the
        /// standard approximation and still far better than dropping the term (tints, flashes).
        /// </summary>
        private static Color KeyColor(SwfColorTransform ct)
        {
            return new Color(
                Clamp04(ct.RMult + ct.RAdd / 255f),
                Clamp04(ct.GMult + ct.GAdd / 255f),
                Clamp04(ct.BMult + ct.BAdd / 255f),
                Mathf.Clamp01(ct.AMult + ct.AAdd / 255f));
        }

        private static float Clamp04(float v)
        {
            return v < 0f ? 0f : (v > 4f ? 4f : v);
        }

        private static void ApplyKeyToTransform(Transform outer, Transform scl, Transform rot, SwfMatrix m, float ppu)
        {
            float px, py, rot1, sx, sy, rot2;
            m.DecomposeAffine(out px, out py, out rot1, out sx, out sy, out rot2);
            outer.localPosition = new Vector3(px / ppu, -py / ppu, 0f);
            outer.localRotation = Quaternion.Euler(0f, 0f, -rot1 * Mathf.Rad2Deg);
            outer.localScale = Vector3.one;
            scl.localPosition = Vector3.zero;
            scl.localRotation = Quaternion.identity;
            scl.localScale = new Vector3(sx, sy, 1f);
            rot.localPosition = Vector3.zero;
            rot.localRotation = Quaternion.Euler(0f, 0f, -rot2 * Mathf.Rad2Deg);
            rot.localScale = Vector3.one;
        }

        // =============================================================
        // pre-rendered mode
        // =============================================================

        private static void ImportPrerendered(SwfMovie movie, string swfPath, string root, string movieName,
            SwfImportSettings opt, Action<string> log)
        {
            int totalFrames = movie.MainTimeline.Frames.Count;
            SwfRect bounds = SwfRenderer.ComputeFrameBounds(movie, movie.MainTimeline, totalFrames - 1);
            if (bounds.MaxX <= bounds.MinX) bounds = movie.Stage;
            float pad = 4f;
            SwfRect viewport = new SwfRect
            {
                MinX = bounds.MinX - pad, MinY = bounds.MinY - pad,
                MaxX = bounds.MaxX + pad, MaxY = bounds.MaxY + pad
            };

            string frameFolder = root + "/Frames";
            EnsureFolder(frameFolder);
            RenderOptions ropt = new RenderOptions { Supersample = opt.Supersample, SpriteLoop = opt.SpriteLoop };

            List<string> frameAssets = new List<string>(totalFrames);
            for (int f = 0; f < totalFrames; f++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Flash To Unity",
                    "渲染整帧 " + (f + 1) + "/" + totalFrames, (f + 1) / (float)totalFrames))
                    throw new OperationCanceledException("已取消");
                RgbaImage img = SwfRenderer.RenderTimelineFrame(movie, movie.MainTimeline, f, viewport, ropt);
                string assetPath = frameFolder + "/" + movieName + "_f" + f.ToString("D4") + ".png";
                File.WriteAllBytes(absoluteProjectPath(assetPath), PngWriter.EncodeRgba(img.Width, img.Height, img.Pixels));
                frameAssets.Add(assetPath);
            }
            AssetDatabase.Refresh();

            foreach (string assetPath in frameAssets)
                ConfigureTextureImporter(assetPath, opt);
            AssetDatabase.Refresh();

            AnimationClip ac = new AnimationClip();
            ac.frameRate = movie.FrameRate > 0 ? movie.FrameRate : 12f;
            var keys = new List<ObjectReferenceKeyframe>();
            for (int f = 0; f < frameAssets.Count; f++)
            {
                Sprite sp = AssetDatabase.LoadAssetAtPath<Sprite>(frameAssets[f]);
                if (sp != null) keys.Add(new ObjectReferenceKeyframe { time = f / ac.frameRate, value = sp });
            }
            if (keys.Count == 0)
            {
                log("[错误] 预渲染帧贴图导入失败");
                return;
            }
            // Unity extends object-reference curves by one frame after the last key.
            // An extra hold key would add an unintended half frame to the animation.
            AnimationUtility.SetObjectReferenceCurve(ac,
                EditorCurveBinding.PPtrCurve(SprName, typeof(SpriteRenderer), "m_Sprite"), keys.ToArray());

            string animFolder = root + "/Animations";
            EnsureFolder(animFolder);
            ac = FlashGeneratedAssets.SaveClip(ac, animFolder + "/" + movieName + "_prerendered.anim");

            GameObject rootGo = new GameObject(movieName);
            try
            {
                var spriteGo = new GameObject(SprName);
                spriteGo.transform.SetParent(rootGo.transform, false);
                spriteGo.transform.localPosition = new Vector3(
                    (viewport.MinX + Mathf.Ceil(viewport.Width) * 0.5f) / opt.PixelsPerUnit,
                    -(viewport.MinY + Mathf.Ceil(viewport.Height) * 0.5f) / opt.PixelsPerUnit, 0);
                SpriteRenderer sr = spriteGo.AddComponent<SpriteRenderer>();
                sr.sprite = keys[0].value as Sprite;
                Animator animator = rootGo.AddComponent<Animator>();
                AnimatorController controller = FlashGeneratedAssets.ResetController(
                    animFolder + "/" + movieName + "_prerendered.controller");
                controller.AddMotion(ac);
                animator.runtimeAnimatorController = controller;

                string prefabPath = root + "/" + movieName + "_prerendered.prefab";
                PrefabUtility.SaveAsPrefabAsset(rootGo, prefabPath);
                log("[完成] 预渲染 Prefab: " + prefabPath + "  (" + totalFrames + " 帧)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGo);
                EditorUtility.ClearProgressBar();
            }
        }

        // =============================================================
        // character -> texture
        // =============================================================

        private static CharExport ExportCharacter(SwfMovie movie, SwfCharacter ch, string texFolder, SwfImportSettings opt)
        {
            byte[] png;
            float originX, originY;
            int width, height;

            if (ch.Kind == SwfCharacterKind.Bitmap)
            {
                png = PngWriter.EncodeRgba(ch.Bitmap.Width, ch.Bitmap.Height, ch.Bitmap.Rgba);
                originX = 0f; originY = 0f;
                width = ch.Bitmap.Width; height = ch.Bitmap.Height;
            }
            else if (ch.Kind == SwfCharacterKind.Shape)
            {
                RenderOptions ropt = new RenderOptions { Supersample = opt.Supersample };
                RenderedTexture tex = SwfRenderer.RenderShapeToTexture(movie, ch.Shape, ropt);
                if (tex.Width <= 0 || tex.Height <= 0) return null;
                png = PngWriter.EncodeRgba(tex.Width, tex.Height, tex.Pixels);
                originX = tex.OriginX; originY = tex.OriginY;
                width = tex.Width; height = tex.Height;
            }
            else return null;

            string assetPath = texFolder + "/" + (ch.Kind == SwfCharacterKind.Bitmap ? "img" : "shape") + ch.Id + ".png";
            File.WriteAllBytes(absoluteProjectPath(assetPath), png);

            CharExport exp = new CharExport();
            exp.TexturePath = assetPath;
            exp.Width = width; exp.Height = height;
            // where the FLASH REGISTRATION POINT (0,0) sits inside the texture, in px
            // from the top-left; the sprite uses the default CENTER pivot and the
            // __spr node compensates with a constant offset
            exp.OriginX = -originX;
            exp.OriginY = -originY;
            return exp;
        }

        /// <summary>
        /// Local position for the __spr node so that the texture's FLASH REGISTRATION
        /// POINT lands on the node origin while the sprite uses its default CENTER pivot.
        /// With a center pivot, the texture center sits at the node origin; the texture
        /// top-left is then at (-(W/2 - ox), -(H/2 - oy)) px relative to it (y-down),
        /// so shifting the node by +that amount (y flipped for Unity) pins the
        /// registration point to the node origin.
        /// </summary>
        private static Vector2 SpriteOffset(int charId, Dictionary<int, CharExport> exports, float ppu)
        {
            float ox, oy;
            SpriteOffset(charId, exports, ppu, out ox, out oy);
            return new Vector2(ox, oy);
        }

        private static void SpriteOffset(int charId, Dictionary<int, CharExport> exports, float ppu,
            out float offX, out float offY)
        {
            CharExport exp;
            if (!exports.TryGetValue(charId, out exp) || exp == null || ppu <= 0f)
            {
                offX = 0f; offY = 0f;
                return;
            }
            offX = (exp.Width * 0.5f - exp.OriginX) / ppu;
            offY = -(exp.Height * 0.5f - exp.OriginY) / ppu;
        }

        private static void ConfigureTextureImporter(string assetPath, SwfImportSettings opt)
        {
            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            // NOTE: the sprite keeps its DEFAULT CENTER pivot. Do not set custom pivots:
            // Unity's importer requires spriteAlignment=Custom for them to apply, and
            // even then the resulting sprite rect/pivot combination proved unreliable.
            // Registration-point compensation happens on the __spr node instead.
            TextureImporterSettings ts = new TextureImporterSettings();
            imp.ReadTextureSettings(ts);
            bool changed = false;
            if (ts.textureType != TextureImporterType.Sprite) { ts.textureType = TextureImporterType.Sprite; changed = true; }
            if (ts.spriteMode != (int)SpriteImportMode.Single) { ts.spriteMode = (int)SpriteImportMode.Single; changed = true; }
            if (ts.spriteAlignment != (int)SpriteAlignment.Center) { ts.spriteAlignment = (int)SpriteAlignment.Center; changed = true; }
            if (!Mathf.Approximately(ts.spritePixelsPerUnit, opt.PixelsPerUnit)) { ts.spritePixelsPerUnit = opt.PixelsPerUnit; changed = true; }
            if (ts.filterMode != opt.TextureFilter) { ts.filterMode = opt.TextureFilter; changed = true; }
            if (ts.mipmapEnabled) { ts.mipmapEnabled = false; changed = true; }
            if (ts.npotScale != TextureImporterNPOTScale.None) { ts.npotScale = TextureImporterNPOTScale.None; changed = true; }
            if (!ts.alphaIsTransparency) { ts.alphaIsTransparency = true; changed = true; }
            if (imp.textureCompression != TextureImporterCompression.Uncompressed && opt.UncompressedTextures)
            { imp.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
            if (changed)
            {
                imp.SetTextureSettings(ts);
                imp.SaveAndReimport();
            }
        }

        // =============================================================
        // helpers
        // =============================================================

        private static string absoluteProjectPath(string assetPath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string UniqueAssetName(string folder, string baseName, string ext)
        {
            string p = folder + "/" + baseName + ext;
            int n = 2;
            while (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p) != null || File.Exists(absoluteProjectPath(p)))
            {
                p = folder + "/" + baseName + "_" + n + ext;
                n++;
            }
            return p.Substring(folder.Length + 1);
        }

        private static string SanitizeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "FlashAnim";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }
    }
}
