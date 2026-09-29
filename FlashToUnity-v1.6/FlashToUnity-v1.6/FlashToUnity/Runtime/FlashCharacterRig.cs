using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlashToUnity.Runtime
{
    [ExecuteAlways]
    public sealed class FlashCharacterRig : MonoBehaviour
    {
        public Transform SourceRoot;
        public FlashCharacterProfile Profile;
        public List<FlashPartReference> Parts = new List<FlashPartReference>();
        public List<string> Diagnostics { get { return diagnostics; } }
        readonly List<string> diagnostics=new List<string>();
        readonly Dictionary<string,Part> parts=new Dictionary<string,Part>();
        readonly Dictionary<string,Attachment> attachments=new Dictionary<string,Attachment>();
        readonly Dictionary<SpriteRenderer,bool> sourceVisibility=new Dictionary<SpriteRenderer,bool>();
        Transform presentation, builtSource;
        FlashCharacterProfile builtProfile;
        bool dirty=true;
        class Chain
        {
            public Transform First,Second,Third,Adjustment;
            public bool Visible;
            public Color Color=Color.white;
            public int Order,Layer,GameLayer;
        }
        class Part : Chain { public SpriteRenderer Source,Output; public SpriteRenderer[] Descendants; public FlashPartReference Reference; }
        class Attachment : Chain
        {
            public FlashAttachmentSettings Settings;
            public SpriteRenderer[] Renderers;
            public Color[] Colors;
            public int[] Orders;
            public bool[] Off;
            public int Visit;
            public bool Invalid;
            public bool Valid;
        }
        void OnEnable() { dirty=true; }
        void OnValidate() { dirty=true; }
        void LateUpdate() { Evaluate(); }
        void OnDisable() { ClearPresentation(); }
        void OnDestroy() { ClearPresentation(); }
        static string PathOf(Transform root,Transform child)
        {
            if(child==root)return "";
            var names=new List<string>();
            while(child!=null && child!=root) { names.Add(child.name);child=child.parent; }
            names.Reverse();return string.Join("/",names.ToArray());
        }
        Transform SourceAnchor(SpriteRenderer sr) { return sr.transform==SourceRoot ? sr.transform : sr.transform.parent; }
        Matrix4x4 SourceMatrix(SpriteRenderer sr) { return transform.worldToLocalMatrix*SourceAnchor(sr).localToWorldMatrix; }
        public void CaptureSource(bool resetBindPose=false)
        {
            if(SourceRoot==null)return;
            var previous=new Dictionary<string,FlashPartReference>();
            foreach(var p in Parts) if(p!=null && p.Path!=null && !previous.ContainsKey(p.Path))previous.Add(p.Path,p);
            var result=new List<FlashPartReference>();
            foreach(var sr in SourceRoot.GetComponentsInChildren<SpriteRenderer>(true)) {
                if(presentation!=null && sr.transform.IsChildOf(presentation))continue;
                string path=PathOf(SourceRoot,sr.transform); FlashPartReference reference;
                if(!previous.TryGetValue(path,out reference))reference=new FlashPartReference {Path=path,Label=path.Replace("/__scl", "").Replace("/__rot", "").Replace("/__spr", "")};
                if(resetBindPose || !previous.ContainsKey(path)) {
                    Vector2 p,s;float l,r;FlashAffine.Decompose(SourceMatrix(sr),out p,out s,out l,out r);
                    reference.BindPosition=p;reference.BindScale=s;reference.BindRotation=l;reference.BindRotation2=r;
                }
                result.Add(reference);
            }
            Parts=result;dirty=true;
        }
        static void Dispose(GameObject go) { if(Application.isPlaying)UnityEngine.Object.Destroy(go);else UnityEngine.Object.DestroyImmediate(go); }
        public void ClearPresentation()
        {
            foreach(var pair in sourceVisibility)if(pair.Key!=null)pair.Key.forceRenderingOff=pair.Value;
            sourceVisibility.Clear();parts.Clear();attachments.Clear();
            if(presentation!=null) { presentation.gameObject.SetActive(false);Dispose(presentation.gameObject); }
            presentation=null;builtSource=null;builtProfile=null;dirty=true;
        }
        static Transform Child(string name,Transform parent)
        {
            var go=new GameObject(name);go.hideFlags=HideFlags.HideInHierarchy|HideFlags.DontSaveInEditor|HideFlags.DontSaveInBuild;
            go.transform.SetParent(parent,false);return go.transform;
        }
        void MakeChain(Chain chain,string name)
        {
            chain.First=Child(name,presentation);chain.Second=Child("Scale",chain.First);chain.Third=Child("Rotation",chain.Second);chain.Adjustment=Child("Adjustment",chain.Third);
        }
        public void Rebuild()
        {
            ClearPresentation(); diagnostics.Clear();
            if(!isActiveAndEnabled || !gameObject.scene.IsValid() || SourceRoot==null || SourceRoot==transform) {
                if(SourceRoot==transform)diagnostics.Add("SourceRoot must be a distinct source hierarchy.");
                return;
            }
            CaptureSource();presentation=Child("Character presentation",transform);builtSource=SourceRoot;builtProfile=Profile;
            foreach(var reference in Parts) {
                var t=string.IsNullOrEmpty(reference.Path)?SourceRoot:SourceRoot.Find(reference.Path);var sr=t==null?null:t.GetComponent<SpriteRenderer>();
                if(sr==null) { diagnostics.Add("Missing source part: "+reference.Path);continue; }
                if(parts.ContainsKey(reference.Path)) { diagnostics.Add("Duplicate part path: "+reference.Path);continue; }
                var p=new Part { Source=sr,Reference=reference,Descendants=SourceAnchor(sr).GetComponentsInChildren<SpriteRenderer>(true) };MakeChain(p,"Part "+reference.Label);
                p.Output=Child("Sprite",p.Adjustment).gameObject.AddComponent<SpriteRenderer>();
                parts.Add(reference.Path,p);sourceVisibility[sr]=sr.forceRenderingOff;sr.forceRenderingOff=true;
            }
            if(Profile!=null && Profile.Attachments!=null)foreach(var settings in Profile.Attachments) {
                if(settings==null)continue;
                if(string.IsNullOrEmpty(settings.Id)) { diagnostics.Add("Attachment has no ID.");continue; }
                Attachment existing;
                if(attachments.TryGetValue(settings.Id,out existing)) { existing.Invalid=true;diagnostics.Add("Duplicate attachment ID: "+settings.Id);continue; }
                var a=new Attachment {Settings=settings};MakeChain(a,"Attachment "+settings.Id);
                GameObject content;
                if(settings.Prefab!=null)content=Instantiate(settings.Prefab,a.Adjustment,false);
                else { content=Child("Sprite",a.Adjustment).gameObject; content.AddComponent<SpriteRenderer>().sprite=settings.Sprite; }
                foreach(var t in content.GetComponentsInChildren<Transform>(true))t.gameObject.hideFlags=HideFlags.HideInHierarchy|HideFlags.DontSaveInEditor|HideFlags.DontSaveInBuild;
                a.Renderers=content.GetComponentsInChildren<SpriteRenderer>(true);a.Colors=new Color[a.Renderers.Length];a.Orders=new int[a.Renderers.Length];a.Off=new bool[a.Renderers.Length];
                int baseline=a.Renderers.Length==0?0:a.Renderers[0].sortingOrder;
                for(int i=0;i<a.Renderers.Length;i++) {a.Colors[i]=a.Renderers[i].color;a.Orders[i]=a.Renderers[i].sortingOrder-baseline;a.Off[i]=a.Renderers[i].forceRenderingOff;}
                attachments.Add(settings.Id,a);
            }
            dirty=false;Evaluate();
        }
        public Transform GetPartAnchor(string path) { Part p;return path!=null && parts.TryGetValue(path,out p)?p.Adjustment:null; }
        public Transform GetAttachmentAnchor(string id) { Attachment a;return id!=null && attachments.TryGetValue(id,out a)?a.Adjustment:null; }
        FlashPartSettings Settings(string path)
        {
            if(Profile!=null && Profile.Parts!=null)foreach(var s in Profile.Parts)if(s!=null && s.Path==path)return s;
            return null;
        }
        void Diagnose(string message) { if(!diagnostics.Contains(message))diagnostics.Add(message); }
        static void Adjustment(Transform t,Vector2 p,Vector2 s,float r) { t.localPosition=p;t.localScale=new Vector3(s.x,s.y,1);t.localRotation=Quaternion.Euler(0,0,r); }
        public void Evaluate()
        {
            if(!isActiveAndEnabled || !gameObject.scene.IsValid())return;
            if(dirty || builtSource!=SourceRoot || builtProfile!=Profile) { Rebuild();return; }
            if(Profile!=null && Profile.Parts!=null)foreach(var settings in Profile.Parts)
                if(settings!=null && settings.Path!=null && !parts.ContainsKey(settings.Path))Diagnose("Missing configured part: "+settings.Path);
            foreach(var pair in parts) {
                var p=pair.Value;if(p.Source==null) {dirty=true;continue;}
                var settings=Settings(pair.Key);var motion=p;bool valid=true;
                if(settings!=null && !string.IsNullOrEmpty(settings.MotionSourcePath) && !parts.TryGetValue(settings.MotionSourcePath,out motion)) {motion=p;valid=false;Diagnose("Missing motion source: "+settings.MotionSourcePath);}
                var matrix=SourceMatrix(motion.Source);
                if(settings!=null && (settings.MotionPositionGain!=Vector2.one || settings.MotionScaleGain!=Vector2.one || settings.MotionRotationGain!=1)) {
                    Vector2 pos,scale;float l,r;FlashAffine.Decompose(matrix,out pos,out scale,out l,out r);var bind=motion.Reference;
                    pos=bind.BindPosition+Vector2.Scale(pos-bind.BindPosition,settings.MotionPositionGain);
                    // Interpolate the symmetric stretch, not the SVD axes: those
                    // axes can flip by 180 degrees during an otherwise smooth motion.
                    var stretch=FlashAffine.Compose(Vector2.zero,scale,-r,r);
                    var bindStretch=FlashAffine.Compose(Vector2.zero,bind.BindScale,-bind.BindRotation2,bind.BindRotation2);
                    float shearGain=(settings.MotionScaleGain.x+settings.MotionScaleGain.y)*.5f;
                    stretch.m00=bindStretch.m00+(stretch.m00-bindStretch.m00)*settings.MotionScaleGain.x;
                    stretch.m11=bindStretch.m11+(stretch.m11-bindStretch.m11)*settings.MotionScaleGain.y;
                    stretch.m01=bindStretch.m01+(stretch.m01-bindStretch.m01)*shearGain;
                    stretch.m10=bindStretch.m10+(stretch.m10-bindStretch.m10)*shearGain;
                    float bindAngle=bind.BindRotation+bind.BindRotation2;
                    float angle=bindAngle+Mathf.DeltaAngle(bindAngle,l+r)*settings.MotionRotationGain;
                    matrix=Matrix4x4.TRS(pos,Quaternion.Euler(0,0,angle),Vector3.one)*stretch;
                }
                FlashAffine.Apply(p.First,p.Second,p.Third,matrix);
                Adjustment(p.Adjustment,settings==null?Vector2.zero:settings.Offset,settings==null?Vector2.one:settings.Scale,settings==null?0:settings.Rotation);
                var source=p.Source;var original=source.sprite;var sprite=original;
                if(settings!=null) {
                    if(settings.Replacement!=null)sprite=settings.Replacement;
                    if(original!=null && settings.SpriteSwaps!=null)foreach(var swap in settings.SpriteSwaps)if(swap!=null && swap.SourceSpriteName==original.name && swap.Replacement!=null) {sprite=swap.Replacement;break;}
                }
                p.Output.gameObject.layer=source.gameObject.layer;
                p.Output.sprite=sprite;p.Output.color=source.color;p.Output.flipX=source.flipX;p.Output.flipY=source.flipY;
                p.Output.sharedMaterials=source.sharedMaterials;p.Output.sortingLayerID=source.sortingLayerID;p.Output.sortingOrder=source.sortingOrder;
                p.Output.maskInteraction=source.maskInteraction;p.Output.spriteSortPoint=source.spriteSortPoint;
                var t=p.Output.transform;
                if(SourceAnchor(source)==source.transform) { t.localPosition=Vector3.zero;t.localRotation=Quaternion.identity;t.localScale=Vector3.one; }
                else {t.localPosition=source.transform.localPosition;t.localRotation=source.transform.localRotation;t.localScale=source.transform.localScale;}
                if(sprite!=null && original!=null && sprite!=original && settings!=null && settings.FitOriginalSize) {
                    var size=original.rect.size/original.pixelsPerUnit;var replaceSize=sprite.rect.size/sprite.pixelsPerUnit;
                    t.localScale=Vector3.Scale(t.localScale,new Vector3(size.x/replaceSize.x,size.y/replaceSize.y,1));
                }
                if(sprite!=null && settings!=null && settings.UseCustomAnchor) {
                    Vector2 offset=(sprite.pivot-Vector2.Scale(settings.Anchor,sprite.rect.size))/sprite.pixelsPerUnit;
                    if(source.flipX)offset.x=-offset.x;if(source.flipY)offset.y=-offset.y;
                    t.localPosition=t.localRotation*Vector3.Scale(offset,t.localScale);
                } else if(sprite!=null && original!=null && sprite!=original) {
                    // Keep the original rectangle center when replacement art has a different pivot.
                    Vector2 oldCenter=(original.rect.size*.5f-original.pivot)/original.pixelsPerUnit;
                    Vector2 newCenter=(sprite.rect.size*.5f-sprite.pivot)/sprite.pixelsPerUnit;
                    if(source.flipX) {oldCenter.x=-oldCenter.x;newCenter.x=-newCenter.x;}if(source.flipY) {oldCenter.y=-oldCenter.y;newCenter.y=-newCenter.y;}
                    t.localPosition+=t.localRotation*(Vector3.Scale(oldCenter,source.transform.localScale)-Vector3.Scale(newCenter,t.localScale));
                }
                bool oldOff;sourceVisibility.TryGetValue(source,out oldOff);
                // MovieClip containers carry motion and timeline visibility but no
                // sprite of their own. Their attachments must still be visible.
                bool timelineVisible=source.color.a>0;
                // Existing imports encode a MovieClip as CharId=0 and alpha=0
                // even while its child shapes are on the display list.
                if(original==null && !timelineVisible)foreach(var child in p.Descendants) {
                    bool childOff;sourceVisibility.TryGetValue(child,out childOff);
                    if(child!=source && child!=null && child.sprite!=null && child.enabled && child.gameObject.activeInHierarchy && child.color.a>0 && !childOff) {timelineVisible=true;break;}
                }
                p.Visible=valid && source.enabled && source.gameObject.activeInHierarchy && !oldOff && timelineVisible && (settings==null || !settings.Hidden);
                p.Output.forceRenderingOff=!p.Visible || sprite==null;source.forceRenderingOff=true;
                p.Color=source.color;p.Order=source.sortingOrder;p.Layer=source.sortingLayerID;p.GameLayer=source.gameObject.layer;
            }
            foreach(var a in attachments.Values)a.Visit=0;
            foreach(var a in attachments.Values)EvaluateAttachment(a);
        }
        bool EvaluateAttachment(Attachment a)
        {
            if(a.Visit==2)return a.Valid;
            if(a.Visit==1) {Diagnose("Attachment cycle: "+a.Settings.Id);Hide(a);return false;}
            a.Visit=1;a.Valid=false;var s=a.Settings;Chain target=null;bool valid=!a.Invalid;
            bool partTarget=s.TargetPartPath!=null && (s.TargetPartPath.Length>0 || parts.ContainsKey("")),attachmentTarget=!string.IsNullOrEmpty(s.TargetAttachmentId);
            if(partTarget && !attachmentTarget) {Part p;if(parts.TryGetValue(s.TargetPartPath,out p))target=p;}
            else if(attachmentTarget && !partTarget) { Attachment parent;if(attachments.TryGetValue(s.TargetAttachmentId,out parent) && EvaluateAttachment(parent))target=parent; }
            if(target==null || !valid) {Diagnose("Invalid attachment target: "+s.Id);Hide(a);a.Visit=2;return false;}
            Matrix4x4 matrix=transform.worldToLocalMatrix*target.Adjustment.localToWorldMatrix;
            if(!s.FollowRotation || !s.FollowScale) {
                Vector2 pos,scale;float l,r;FlashAffine.Decompose(matrix,out pos,out scale,out l,out r);
                if(!s.FollowScale)scale=Vector2.one;
                // M = R(l+r) * [R(-r) D R(r)]. Keep the stretch basis
                // when removing rotation; dropping both rotations swaps axes.
                if(!s.FollowRotation)l=-r;
                matrix=FlashAffine.Compose(pos,scale,l,r);
            }
            FlashAffine.Apply(a.First,a.Second,a.Third,matrix);Adjustment(a.Adjustment,s.Offset,s.Scale,s.Rotation);
            a.Visible=!s.FollowVisibility || target.Visible;a.Color=(s.FollowColor?target.Color:Color.white)*s.Tint;
            a.Order=s.FollowSorting?target.Order+s.SortingOffset:s.SortingOrder;a.Layer=s.FollowSorting?target.Layer:SortingLayer.NameToID(s.SortingLayerName);a.GameLayer=target.GameLayer;
            for(int i=0;i<a.Renderers.Length;i++) {var sr=a.Renderers[i];if(sr==null)continue;sr.forceRenderingOff=a.Off[i] || !a.Visible;
                if(s.Prefab==null)sr.gameObject.layer=a.GameLayer;
                if(s.FollowColor)sr.color=a.Colors[i]*a.Color;
                else if(s.Prefab==null)sr.color=s.Tint;
                sr.sortingOrder=a.Order+a.Orders[i];sr.sortingLayerID=a.Layer;
            }
            a.Valid=true;a.Visit=2;return true;
        }
        static void Hide(Attachment a) { a.Visible=false;foreach(var sr in a.Renderers)if(sr!=null)sr.forceRenderingOff=true; }
    }
}
