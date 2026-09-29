using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FlashToUnity.Runtime;

namespace FlashToUnity.EditorTools
{
    public sealed class FlashCharacterWindow : EditorWindow
    {
        GameObject source;
        FlashCharacterRig rig, previewRig;
        Vector2 scroll;
        int partIndex, attachmentIndex, clipIndex;
        float time;
        bool playing, ownsPreview;
        double lastTick;
        string characterName = "MyCharacter";

        [MenuItem("Tools/Flash 角色制作（换装与部件绑定）")]
        public static void Open() { GetWindow<FlashCharacterWindow>("Flash 角色制作").Show(); }
        public static void Open(FlashCharacterRig value) { var w=GetWindow<FlashCharacterWindow>("Flash 角色制作");w.StopPreview();w.rig=value;w.Show(); }
        void OnEnable() { EditorApplication.update+=Tick; Selection.selectionChanged+=Repaint;Undo.undoRedoPerformed+=Refresh; }
        void OnDisable() { StopPreview();EditorApplication.update-=Tick;Selection.selectionChanged-=Repaint;Undo.undoRedoPerformed-=Refresh; }
        void Refresh() { if(rig!=null)rig.Rebuild();Repaint();SceneView.RepaintAll(); }
        void Tick()
        {
            if(Application.isPlaying) { StopPreview();return; }
            if(!playing)return;
            var clips=Clips();if(clips.Length==0) {StopPreview();return;}
            var clip=clips[Mathf.Clamp(clipIndex,0,clips.Length-1)];
            time=(time+(float)(EditorApplication.timeSinceStartup-lastTick))%Mathf.Max(.001f,clip.length);
            lastTick=EditorApplication.timeSinceStartup;Sample(clip);Repaint();
        }
        AnimationClip[] Clips() { var animator=rig!=null && rig.SourceRoot!=null?rig.SourceRoot.GetComponentInChildren<Animator>(true):null;return animator!=null && animator.runtimeAnimatorController!=null?animator.runtimeAnimatorController.animationClips.Distinct().ToArray():new AnimationClip[0]; }
        void StopPreview() { playing=false;if(ownsPreview)AnimationMode.StopAnimationMode();ownsPreview=false;if(previewRig!=null)previewRig.Evaluate();previewRig=null; }
        void Sample(AnimationClip clip)
        {
            if(rig==null || EditorUtility.IsPersistent(rig) || Application.isPlaying)return;
            if(!ownsPreview) { if(AnimationMode.InAnimationMode())return;AnimationMode.StartAnimationMode();ownsPreview=true;previewRig=rig; }
            AnimationMode.BeginSampling();
            try { AnimationMode.SampleAnimationClip(rig.SourceRoot.gameObject,clip,time); }
            finally {AnimationMode.EndSampling();}
            rig.Evaluate();SceneView.RepaintAll();
        }
        void OnGUI()
        {
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("创建可换装角色",EditorStyles.boldLabel);
            source=(GameObject)EditorGUILayout.ObjectField("原动画 Prefab",source,typeof(GameObject),false);
            characterName=EditorGUILayout.TextField("新角色名称",characterName);
            using(new EditorGUI.DisabledScope(source==null))if(GUILayout.Button("创建独立角色与换装配置")) {
                StopPreview();
                try {
                    string name=string.Concat(characterName.Where(c=>!System.IO.Path.GetInvalidFileNameChars().Contains(c))).Trim();
                    if(string.IsNullOrEmpty(name))name="MyCharacter";
                    var prefab=FlashCharacterBuilder.Create(source,AssetDatabase.GenerateUniqueAssetPath("Assets/FlashCharacters/"+name));
                    var instance=(GameObject)PrefabUtility.InstantiatePrefab(prefab);Undo.RegisterCreatedObjectUndo(instance,"创建角色");rig=instance.GetComponent<FlashCharacterRig>();Selection.activeGameObject=instance;
                } catch(Exception e) { Debug.LogException(e);ShowNotification(new GUIContent(e.Message)); }
            }
            EditorGUILayout.Space();
            var next=(FlashCharacterRig)EditorGUILayout.ObjectField("当前角色",rig,typeof(FlashCharacterRig),true);
            if(next!=rig) {StopPreview();rig=next;partIndex=attachmentIndex=0;}
            if(GUILayout.Button("使用场景中选中的角色") && Selection.activeGameObject!=null) {StopPreview();rig=Selection.activeGameObject.GetComponentInParent<FlashCharacterRig>();}
            if(rig==null || rig.Profile==null) {EditorGUILayout.HelpBox("拖入转换后的 Prefab 创建角色，或选择已有角色。换装配置独立保存，原动画继续负责动作。",MessageType.Info);EditorGUILayout.EndScrollView();return;}
            if(EditorUtility.IsPersistent(rig)) {
                EditorGUILayout.HelpBox("当前是资源中的角色。实例化后可预览动作。",MessageType.Info);
                if(GUILayout.Button("实例化以预览")) {var go=(GameObject)PrefabUtility.InstantiatePrefab(rig.gameObject);Undo.RegisterCreatedObjectUndo(go,"实例化角色");rig=go.GetComponent<FlashCharacterRig>();Selection.activeGameObject=go;}
            }
            var profile=rig.Profile;
            EditorGUILayout.ObjectField("换装配置",profile,typeof(FlashCharacterProfile),false);
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("保存配置")) {EditorUtility.SetDirty(profile);AssetDatabase.SaveAssets();}
            if(GUILayout.Button("复制为独立配置")) {
                string path=AssetDatabase.GenerateUniqueAssetPath(AssetDatabase.GetAssetPath(profile).Replace(".asset","_Copy.asset"));
                var copy=Instantiate(profile);AssetDatabase.CreateAsset(copy,path);Undo.RecordObject(rig,"独立配置");rig.Profile=copy;EditorUtility.SetDirty(rig);PrefabUtility.RecordPrefabInstancePropertyModifications(rig);rig.Rebuild();profile=copy;
            }
            EditorGUILayout.EndHorizontal();
            Preview();
            Undo.RecordObject(profile,"调整 Flash 角色");EditorGUI.BeginChangeCheck();
            DrawParts(profile);DrawAttachments(profile);
            bool changed=EditorGUI.EndChangeCheck();
            if(changed) {EditorUtility.SetDirty(profile);rig.Rebuild();SceneView.RepaintAll();}
            if(GUILayout.Button("刷新原动画部件列表")) {
                StopPreview();Undo.RecordObject(rig,"刷新部件");rig.CaptureSource();
                foreach(var p in rig.Parts)if(!profile.Parts.Any(s=>s.Path==p.Path))profile.Parts.Add(new FlashPartSettings {Path=p.Path,DisplayName=p.Label});
                EditorUtility.SetDirty(profile);EditorUtility.SetDirty(rig);PrefabUtility.RecordPrefabInstancePropertyModifications(rig);rig.Rebuild();
            }
            foreach(var message in rig.Diagnostics)EditorGUILayout.HelpBox(message,MessageType.Warning);
            EditorGUILayout.EndScrollView();
        }
        void Preview()
        {
            var clips=Clips();if(clips.Length==0)return;
            EditorGUILayout.Space();EditorGUILayout.LabelField("动作预览",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(EditorUtility.IsPersistent(rig)||Application.isPlaying)) {
                int selected=EditorGUILayout.Popup("动作",Mathf.Clamp(clipIndex,0,clips.Length-1),clips.Select(c=>c.name).ToArray());
                if(selected!=clipIndex) {clipIndex=selected;time=0;}
                clipIndex=Mathf.Clamp(clipIndex,0,clips.Length-1);var clip=clips[clipIndex];
                EditorGUI.BeginChangeCheck();time=EditorGUILayout.Slider("时间（秒）",time,0,clip.length);if(EditorGUI.EndChangeCheck())Sample(clip);
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button(playing?"暂停":"播放")) {playing=!playing;lastTick=EditorApplication.timeSinceStartup;if(playing)Sample(clip);}
                if(GUILayout.Button("还原初始姿势")) {StopPreview();time=0;}
                EditorGUILayout.EndHorizontal();
                if(AnimationMode.InAnimationMode()&&!ownsPreview)EditorGUILayout.HelpBox("请先关闭其他动画预览，再使用这里的预览。",MessageType.Info);
            }
        }
        void DrawParts(FlashCharacterProfile profile)
        {
            EditorGUILayout.Space();EditorGUILayout.LabelField("换装与动作调整",EditorStyles.boldLabel);
            if(profile.Parts.Count==0)return;
            partIndex=EditorGUILayout.Popup("部件",Mathf.Clamp(partIndex,0,profile.Parts.Count-1),profile.Parts.Select((p,i)=>i+" · "+p.DisplayName).ToArray());
            var s=profile.Parts[partIndex];
            EditorGUILayout.SelectableLabel(s.Path,EditorStyles.wordWrappedMiniLabel,GUILayout.Height(32));
            s.DisplayName=EditorGUILayout.TextField("自定义名称",s.DisplayName);s.Hidden=EditorGUILayout.Toggle("隐藏部件",s.Hidden);
            var original=rig.SourceRoot==null?null:rig.SourceRoot.Find(s.Path);
            var renderer=original==null?null:original.GetComponent<SpriteRenderer>();
            using(new EditorGUI.DisabledScope(true))EditorGUILayout.ObjectField("当前原图",renderer==null?null:renderer.sprite,typeof(Sprite),false);
            s.Replacement=(Sprite)EditorGUILayout.ObjectField("替换图片",s.Replacement,typeof(Sprite),false);
            s.FitOriginalSize=EditorGUILayout.Toggle("匹配原图尺寸",s.FitOriginalSize);
            s.Offset=EditorGUILayout.Vector2Field("贴图位置",s.Offset);s.Rotation=EditorGUILayout.FloatField("贴图角度",s.Rotation);s.Scale=EditorGUILayout.Vector2Field("贴图比例",s.Scale);
            s.UseCustomAnchor=EditorGUILayout.Toggle("指定贴图绑定点",s.UseCustomAnchor);
            if(s.UseCustomAnchor)s.Anchor=EditorGUILayout.Vector2Field("绑定点（0～1）",s.Anchor);
            EditorGUILayout.HelpBox("贴图比例决定造型大小；动作缩放强度决定原动画的伸缩幅度。绑定点以图片左下角为 (0,0)、右上角为 (1,1)。",MessageType.Info);
            string[] paths=new[]{""}.Concat(rig.Parts.Select(p=>p.Path)).ToArray();
            string[] labels=new[]{"自身动作"}.Concat(rig.Parts.Select(p=>p.Label+" · "+p.Path)).ToArray();
            s.MotionSourcePath=paths[EditorGUILayout.Popup("跟随动作部件",Mathf.Max(0,Array.IndexOf(paths,s.MotionSourcePath??"")),labels)];
            s.MotionPositionGain=EditorGUILayout.Vector2Field("动作位移强度",s.MotionPositionGain);s.MotionScaleGain=EditorGUILayout.Vector2Field("动作缩放强度",s.MotionScaleGain);s.MotionRotationGain=EditorGUILayout.FloatField("动作旋转强度",s.MotionRotationGain);
            EditorGUILayout.LabelField("强度 1 保留原动作，0 固定在绑定姿势。",EditorStyles.miniLabel);
            EditorGUILayout.LabelField("逐张图片替换（优先于统一替换）",EditorStyles.boldLabel);
            for(int i=0;i<s.SpriteSwaps.Count;i++) {
                var swap=s.SpriteSwaps[i];EditorGUILayout.BeginHorizontal();swap.SourceSpriteName=EditorGUILayout.TextField(swap.SourceSpriteName);swap.Replacement=(Sprite)EditorGUILayout.ObjectField(swap.Replacement,typeof(Sprite),false);
                if(GUILayout.Button("删除",GUILayout.Width(45))) {s.SpriteSwaps.RemoveAt(i--);GUI.changed=true;}EditorGUILayout.EndHorizontal();
            }
            if(GUILayout.Button("添加当前原图的替换项")) {s.SpriteSwaps.Add(new FlashSpriteSwap {SourceSpriteName=renderer!=null&&renderer.sprite!=null?renderer.sprite.name:""});GUI.changed=true;}
        }
        void DrawAttachments(FlashCharacterProfile profile)
        {
            EditorGUILayout.Space();EditorGUILayout.LabelField("新增部件绑定",EditorStyles.boldLabel);
            if(GUILayout.Button("添加新部件（图片或 Prefab）")) {
                profile.Attachments.Add(new FlashAttachmentSettings {Id=Guid.NewGuid().ToString("N"),DisplayName="新部件",TargetPartPath=profile.Parts.Count>0?profile.Parts[Mathf.Clamp(partIndex,0,profile.Parts.Count-1)].Path:null});attachmentIndex=profile.Attachments.Count-1;GUI.changed=true;
            }
            if(profile.Attachments.Count==0)return;
            attachmentIndex=EditorGUILayout.Popup("新部件",Mathf.Clamp(attachmentIndex,0,profile.Attachments.Count-1),profile.Attachments.Select(a=>a.DisplayName).ToArray());
            var s=profile.Attachments[attachmentIndex];s.DisplayName=EditorGUILayout.TextField("名称",s.DisplayName);
            s.Prefab=(GameObject)EditorGUILayout.ObjectField("部件 Prefab",s.Prefab,typeof(GameObject),false);
            if(s.Prefab==null)s.Sprite=(Sprite)EditorGUILayout.ObjectField("部件图片",s.Sprite,typeof(Sprite),false);
            var targets=profile.Attachments.Where(a=>a!=s).ToArray();
            var labels=new[]{"请选择绑定目标"}.Concat(rig.Parts.Select(p=>"原部件 · "+p.Path)).Concat(targets.Select(a=>"新部件 · "+a.DisplayName)).ToArray();
            int current=0;
            if(!string.IsNullOrEmpty(s.TargetAttachmentId)) {int i=Array.FindIndex(targets,a=>a.Id==s.TargetAttachmentId);if(i>=0)current=1+rig.Parts.Count+i;}
            else {int i=rig.Parts.FindIndex(p=>p.Path==s.TargetPartPath);if(i>=0)current=1+i;}
            int choice=EditorGUILayout.Popup("绑定到",current,labels);
            if(choice!=current) {s.TargetAttachmentId=null;s.TargetPartPath=null;if(choice>0&&choice<=rig.Parts.Count)s.TargetPartPath=rig.Parts[choice-1].Path;else if(choice>rig.Parts.Count)s.TargetAttachmentId=targets[choice-rig.Parts.Count-1].Id;}
            s.Offset=EditorGUILayout.Vector2Field("位置",s.Offset);s.Rotation=EditorGUILayout.FloatField("角度",s.Rotation);s.Scale=EditorGUILayout.Vector2Field("比例",s.Scale);
            s.FollowRotation=EditorGUILayout.Toggle("跟随旋转",s.FollowRotation);s.FollowScale=EditorGUILayout.Toggle("跟随缩放",s.FollowScale);s.FollowVisibility=EditorGUILayout.Toggle("跟随显隐",s.FollowVisibility);s.FollowColor=EditorGUILayout.Toggle("跟随颜色",s.FollowColor);s.FollowSorting=EditorGUILayout.Toggle("跟随显示层级",s.FollowSorting);
            if(s.FollowSorting)s.SortingOffset=EditorGUILayout.IntField("层级偏移",s.SortingOffset);
            else {s.SortingLayerName=EditorGUILayout.TextField("Sorting Layer",s.SortingLayerName);s.SortingOrder=EditorGUILayout.IntField("显示顺序",s.SortingOrder);}
            if(s.Prefab==null || s.FollowColor)s.Tint=EditorGUILayout.ColorField("颜色",s.Tint);
            else EditorGUILayout.LabelField("颜色由附件 Prefab 自身控制。",EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("新部件可以绑定到原动画部件或其他新部件。Prefab 可携带自己的 Animator。不要建立循环绑定。",MessageType.Info);
            if(s.Prefab==null && s.Sprite==null)
                EditorGUILayout.HelpBox("尚未指定部件图片或 Prefab，绑定目标后仍需指定要显示的内容。",MessageType.Warning);
            else if(s.FollowVisibility && !EditorUtility.IsPersistent(rig)) {
                var anchor=rig.GetAttachmentAnchor(s.Id);
                var renderers=anchor==null?new SpriteRenderer[0]:anchor.GetComponentsInChildren<SpriteRenderer>(true);
                if(renderers.Length>0 && renderers.All(r=>r.forceRenderingOff))
                    EditorGUILayout.HelpBox("新部件当前未显示。若下方没有绑定错误，通常是目标在当前动作帧中隐藏；播放到目标出现的帧，或取消「跟随显隐」让新部件持续显示。",MessageType.Info);
            }
            if(GUILayout.Button("删除此新部件")) {profile.Attachments.RemoveAt(attachmentIndex);attachmentIndex=0;GUI.changed=true;}
        }
    }
    [CustomEditor(typeof(FlashCharacterRig))]
    public sealed class FlashCharacterRigInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI() {
            EditorGUILayout.HelpBox("原动画驱动 Motion；换装与新部件通过角色制作窗口编辑。",MessageType.Info);
            if(GUILayout.Button("打开角色制作窗口"))FlashCharacterWindow.Open((FlashCharacterRig)target);
            DrawDefaultInspector();
        }
    }
}
