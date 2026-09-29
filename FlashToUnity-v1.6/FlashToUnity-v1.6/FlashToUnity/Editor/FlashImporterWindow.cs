// FlashToUnity - editor window: drag & drop .swf files, configure, import.

using System;
using System.Collections.Generic;
using System.IO;
using FlashToUnity.Core;
using UnityEditor;
using UnityEngine;

namespace FlashToUnity.EditorTools
{
    public class FlashImporterWindow : EditorWindow
    {
        private readonly List<string> _swfFiles = new List<string>();
        private string _outputFolder = "Assets/FlashAnims";
        private float _pixelsPerUnit = 100f;
        private bool _prerender;
        private bool _spriteLoop;
        private int _supersample = 2;
        private bool _uncompressed = true;
        private SwfSplitMode _splitMode = SwfSplitMode.Auto;
        private string _manualSplits = "";
        private static readonly string[] SplitLabels =
        {
            "自动（帧标签优先，其次自动分段）",
            "仅帧标签",
            "自动分段（按内容变化检测）",
            "手动指定帧号",
            "不分割（整段一个片段）",
        };
        private static readonly SwfSplitMode[] SplitValues =
        {
            SwfSplitMode.Auto, SwfSplitMode.LabelsOnly, SwfSplitMode.Detected,
            SwfSplitMode.Manual, SwfSplitMode.None,
        };
        private FilterMode _filter = FilterMode.Bilinear;
        private Vector2 _scroll;
        private readonly List<string> _log = new List<string>();
        private bool _importing;

        [MenuItem("Tools/Flash 动画导入 (SWF → Unity)")]
        public static void Open()
        {
            FlashImporterWindow win = GetWindow<FlashImporterWindow>("Flash 动画导入");
            win.minSize = new Vector2(420f, 480f);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Flash 动画导入插件 " + SwfImporterVersion.Version, EditorStyles.miniBoldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. 选择 SWF 文件", EditorStyles.boldLabel);

            Rect drop = GUILayoutUtility.GetRect(0f, 52f, GUILayout.ExpandWidth(true));
            GUI.Box(drop, _swfFiles.Count == 0 ? "将 .swf 文件拖到这里，或点击选择" : "已添加 " + _swfFiles.Count + " 个文件（再次拖放可追加）",
                EditorStylesHelpBox());

            Event e = Event.current;
            switch (e.type)
            {
                case EventType.DragUpdated:
                    if (drop.Contains(e.mousePosition)) { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.Use(); }
                    break;
                case EventType.DragPerform:
                    if (drop.Contains(e.mousePosition))
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (string p in DragAndDrop.paths)
                            AddSwf(p);
                        e.Use();
                        Repaint();
                    }
                    break;
                case EventType.MouseDown:
                    if (drop.Contains(e.mousePosition))
                    {
                        string files = EditorUtility.OpenFilePanelWithFilters("选择 SWF 文件", "", new[] { "Flash 动画", "swf" });
                        if (!string.IsNullOrEmpty(files)) AddSwf(files);
                        e.Use();
                    }
                    break;
            }

            if (_swfFiles.Count > 0)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(110f));
                for (int i = _swfFiles.Count - 1; i >= 0; i--)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(" " + _swfFiles[i], EditorStyles.miniLabel);
                    if (GUILayout.Button("×", GUILayout.Width(22f))) _swfFiles.RemoveAt(i);
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                if (GUILayout.Button("清空列表")) _swfFiles.Clear();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. 导入设置", EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(new GUIContent("输出目录（Assets 下）"), _outputFolder);
            _pixelsPerUnit = EditorGUILayout.FloatField(new GUIContent("Pixels Per Unit"), _pixelsPerUnit);
            _prerender = EditorGUILayout.Toggle(
                new GUIContent("整帧预渲染模式", "层级动画：拆成部件与 Transform 动画，适合换装和绑定；整帧预渲染：每帧生成一张图片，以切换图片播放动画。两种模式均受支持的 SWF 特性限制。"),
                _prerender);
            _spriteLoop = EditorGUILayout.Toggle(new GUIContent("子时间轴循环播放"), _spriteLoop);
            _supersample = EditorGUILayout.IntSlider(new GUIContent("矢量超采样"), _supersample, 1, 4);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("动作分段（一段里拼了多个动作时拆成独立片段）", EditorStyles.boldLabel);
            int splitIdx = 0;
            for (int i = 0; i < SplitValues.Length; i++) if (SplitValues[i] == _splitMode) { splitIdx = i; break; }
            splitIdx = EditorGUILayout.Popup(new GUIContent("分段方式"), splitIdx, SplitLabels);
            _splitMode = SplitValues[splitIdx];
            if (_splitMode == SwfSplitMode.Manual)
            {
                _manualSplits = EditorGUILayout.TextField(new GUIContent("分段起始帧（逗号分隔）", "例如 29,79：从第 29、79 帧各开始一个新片段（第 0 帧自动作为首段起点）"), _manualSplits);
            }
            if (GUILayout.Button("预览自动分段（对列表中的所有文件运行检测）"))
            {
                PreviewSegments();
            }
            _uncompressed = EditorGUILayout.Toggle(new GUIContent("不压缩贴图"), _uncompressed);
            _filter = (FilterMode)EditorGUILayout.EnumPopup(new GUIContent("贴图过滤"), _filter);

            EditorGUILayout.Space();
            GUI.enabled = !_importing && _swfFiles.Count > 0;
            if (GUILayout.Button("开始导入", GUILayout.Height(34f)))
            {
                StartImport();
            }
            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("导入日志", EditorStyles.boldLabel);
            _scrollLog = EditorGUILayout.BeginScrollView(_scrollLog, GUILayout.ExpandHeight(true));
            foreach (string line in _log)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        private Vector2 _scrollLog;

        private static GUIStyle EditorStylesHelpBox()
        {
            return HelpBoxStyle;
        }

        private static GUIStyle _helpBoxStyle;
        private static GUIStyle HelpBoxStyle
        {
            get
            {
                if (_helpBoxStyle == null)
                {
                    _helpBoxStyle = new GUIStyle(GUI.skin.box);
                    _helpBoxStyle.alignment = TextAnchor.MiddleCenter;
                }
                return _helpBoxStyle;
            }
        }

        private void AddSwf(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".swf")
            {
                _log.Add("[跳过] 不是 .swf 文件: " + path);
                return;
            }
            if (!_swfFiles.Contains(path)) _swfFiles.Add(path);
        }

        private void PreviewSegments()
        {
            foreach (string swf in _swfFiles)
            {
                try
                {
                    byte[] raw = System.IO.File.ReadAllBytes(swf);
                    FlashToUnity.Core.SwfMovie movie = FlashToUnity.Core.SwfParser.Parse(raw, System.IO.Path.GetFileName(swf));
                    List<int> b = FlashToUnity.Core.TimelineCompiler.DetectSegmentBoundaries(movie, 4);
                    int total = movie.MainTimeline.FrameCount;
                    List<int> starts = new List<int>();
                    starts.Add(0);
                    starts.AddRange(b);
                    var parts = new List<string>();
                    for (int i = 0; i < starts.Count; i++)
                    {
                        int from = starts[i];
                        int to = (i + 1 < starts.Count) ? starts[i + 1] : total;
                        parts.Add(from + "-" + (to - 1));
                    }
                    _log.Add("[预览] " + System.IO.Path.GetFileName(swf) + ": " + starts.Count + " 段 -> " + string.Join(", ", parts.ToArray()));
                    if (movie.Labels.Count > 0)
                        _log.Add("        （该文件有帧标签，自动模式下将优先按标签分段）");
                }
                catch (System.Exception ex)
                {
                    _log.Add("[预览失败] " + System.IO.Path.GetFileName(swf) + ": " + ex.Message);
                }
            }
            Repaint();
        }

        private void StartImport()
        {
            _importing = true;
            _log.Add("========== 开始导入 " + System.DateTime.Now.ToString("HH:mm:ss") + " ==========");
            try
            {
                SwfImportSettings opt = new SwfImportSettings
                {
                    PixelsPerUnit = Mathf.Max(1f, _pixelsPerUnit),
                    PrerenderFrames = _prerender,
                    SpriteLoop = _spriteLoop,
                    Supersample = Mathf.Max(1, _supersample),
                    UncompressedTextures = _uncompressed,
                    TextureFilter = _filter,
                    SplitMode = _splitMode,
                    ManualSplitFrames = ParseFrames(_manualSplits),
                };
                foreach (string swf in _swfFiles.ToArray())
                {
                    _log.Add("---- 导入 " + Path.GetFileName(swf));
                    try
                    {
                        // Import() creates or updates the per-SWF subfolder.
                        SwfImporter.Import(swf, _outputFolder, opt, delegate (string line) { _log.Add(line); });
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Add("[取消] 用户取消了导入");
                        break;
                    }
                    catch (System.Exception ex)
                    {
                        _log.Add("[失败] " + ex.Message);
                        Debug.LogException(ex);
                    }
                }
                _log.Add("========== 导入结束 ==========");
            }
            finally
            {
                _importing = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private static int[] ParseFrames(string text)
        {
            if (string.IsNullOrEmpty(text)) return new int[0];
            var list = new List<int>();
            foreach (string part in text.Split(',', ';', ' '))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                int v;
                if (int.TryParse(t, out v) && v > 0) list.Add(v);
            }
            return list.ToArray();
        }
    }
}
