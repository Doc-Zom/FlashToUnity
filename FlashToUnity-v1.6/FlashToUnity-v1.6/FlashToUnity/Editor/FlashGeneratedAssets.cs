using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FlashToUnity.EditorTools
{
    // Update generated content without invalidating references owned by authored characters.
    internal static class FlashGeneratedAssets
    {
        public static AnimationClip SaveClip(AnimationClip generated, string path)
        {
            generated.name = System.IO.Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(generated, path);
                return generated;
            }
            bool loop = AnimationUtility.GetAnimationClipSettings(existing).loopTime;
            EditorUtility.CopySerialized(generated, existing);
            var settings = AnimationUtility.GetAnimationClipSettings(existing);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(existing, settings);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(generated);
            return existing;
        }

        public static AnimatorController ResetController(string path)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null) return AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller.layers.Length == 0) controller.AddLayer("Base Layer");
            var layer = controller.layers[0];
            var machine = layer.stateMachine;
            foreach (var transition in machine.anyStateTransitions) machine.RemoveAnyStateTransition(transition);
            foreach (var transition in machine.entryTransitions) machine.RemoveEntryTransition(transition);
            foreach (var child in machine.states) machine.RemoveState(child.state);
            foreach (var child in machine.stateMachines) machine.RemoveStateMachine(child.stateMachine);
            machine.defaultState = null;
            controller.layers = new[] { layer };
            EditorUtility.SetDirty(machine);
            EditorUtility.SetDirty(controller);
            return controller;
        }
    }
}
