using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using FlashToUnity.Runtime;

namespace FlashToUnity.EditorTools
{
    public static class FlashCharacterBuilder
    {
        public static GameObject Create(GameObject sourcePrefab, string folder)
        {
            if (sourcePrefab == null || !PrefabUtility.IsPartOfPrefabAsset(sourcePrefab))
                throw new ArgumentException("请选择导入生成的 Prefab 资产。");
            if (sourcePrefab.GetComponent<FlashCharacterRig>() != null)
                throw new ArgumentException("该资产已经是原创角色。请选择其原动画 Prefab，或复制现有角色及配置。");
            if (sourcePrefab.GetComponentsInChildren<SpriteRenderer>(true).Length == 0)
                throw new ArgumentException("Prefab 中没有 SpriteRenderer 部件。");
            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Contains(".."))
                throw new ArgumentException("原创角色必须保存在工程 Assets 下的独立目录。");
            string sourceFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(sourcePrefab)).Replace('\\', '/');
            if (folder == sourceFolder || folder.StartsWith(sourceFolder + "/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请把原创角色保存在原导入目录之外，例如 Assets/FlashCharacters。");
            if (AssetDatabase.IsValidFolder(folder) || Directory.Exists(folder))
                throw new ArgumentException("目标目录已存在，请使用新的角色目录。");
            EnsureFolder(folder);
            var root = new GameObject(Path.GetFileName(folder));
            try
            {
                var source = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
                source.name = "Motion";
                source.transform.SetParent(root.transform, false);
                var profile = ScriptableObject.CreateInstance<FlashCharacterProfile>();
                profile.name = root.name + "_Appearance";
                AssetDatabase.CreateAsset(profile, folder + "/" + profile.name + ".asset");
                var rig = root.AddComponent<FlashCharacterRig>();
                rig.SourceRoot = source.transform; rig.Profile = profile;
                rig.CaptureSource(true);
                foreach (var part in rig.Parts)
                    profile.Parts.Add(new FlashPartSettings { Path = part.Path, DisplayName = part.Label });
                EditorUtility.SetDirty(profile);
                // Never serialize the temporary preview or renderer suppression into the prefab.
                rig.ClearPresentation();
                var result = PrefabUtility.SaveAsPrefabAsset(root, folder + "/" + root.name + ".prefab");
                AssetDatabase.SaveAssets();
                return result;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        internal static void EnsureFolder(string folder)
        {
            string[] segments = folder.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }
    }
}
