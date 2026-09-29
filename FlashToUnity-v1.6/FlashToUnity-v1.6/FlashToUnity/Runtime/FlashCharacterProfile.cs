using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlashToUnity.Runtime
{
    [CreateAssetMenu(menuName="Flash To Unity/Character Profile")]
    public sealed class FlashCharacterProfile : ScriptableObject
    {
        public List<FlashPartSettings> Parts = new List<FlashPartSettings>();
        public List<FlashAttachmentSettings> Attachments = new List<FlashAttachmentSettings>();
    }
    [Serializable] public sealed class FlashSpriteSwap { public string SourceSpriteName; public Sprite Replacement; }
    [Serializable] public sealed class FlashPartSettings
    {
        public string Path, DisplayName, MotionSourcePath;
        public bool Hidden;
        public Sprite Replacement;
        public List<FlashSpriteSwap> SpriteSwaps = new List<FlashSpriteSwap>();
        public Vector2 Offset, Scale = Vector2.one;
        public float Rotation;
        public bool FitOriginalSize = true, UseCustomAnchor;
        public Vector2 Anchor = Vector2.one * .5f;
        public Vector2 MotionPositionGain = Vector2.one, MotionScaleGain = Vector2.one;
        public float MotionRotationGain = 1;
    }
    [Serializable] public sealed class FlashAttachmentSettings
    {
        public string Id, DisplayName, TargetPartPath, TargetAttachmentId;
        public GameObject Prefab;
        public Sprite Sprite;
        public Vector2 Offset, Scale = Vector2.one;
        public float Rotation;
        public bool FollowRotation=true, FollowScale=true, FollowVisibility=true, FollowColor=false, FollowSorting=true;
        public int SortingOffset=1, SortingOrder;
        public string SortingLayerName="Default";
        public Color Tint=Color.white;
    }
    [Serializable] public sealed class FlashPartReference
    {
        public string Path, Label;
        public Vector2 BindPosition, BindScale = Vector2.one;
        public float BindRotation, BindRotation2;
    }
}
