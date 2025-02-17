﻿using System;
using System.Collections.Generic;
using System.Linq;
using Hai.ComboGesture.Scripts.Components;
using Hai.ComboGesture.Scripts.Editor.Internal.Model;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hai.ComboGesture.Scripts.Editor.Internal
{
    class AnimationNeutralizer
    {
        private readonly List<ManifestBinding> _originalBindings;
        private readonly ConflictFxLayerMode _compilerConflictFxLayerMode;
        private readonly HashSet<CurveKey> _ignoreCurveKeys;
        private readonly HashSet<CurveKey> _ignoreObjectReferences;
        private readonly Dictionary<CurveKey, float> _curveKeyToFallbackValue;
        private readonly Dictionary<CurveKey, Object> _objectReferenceToFallbackValue;
        private readonly List<CurveKey> _blinkBlendshapes;
        private readonly AssetContainer _assetContainer;
        private readonly bool _useExhaustiveAnimations;
        private readonly AnimationClip _emptyClip;
        private readonly bool _doNotFixSingleKeyframes;

        public AnimationNeutralizer(List<ManifestBinding> originalBindings,
            ConflictFxLayerMode compilerConflictFxLayerMode,
            AnimationClip compilerIgnoreParamList,
            AnimationClip compilerFallbackParamList,
            List<CurveKey> blinkBlendshapes,
            AssetContainer assetContainer,
            bool useExhaustiveAnimations,
            AnimationClip emptyClip,
            bool doNotFixSingleKeyframes)
        {
            _originalBindings = originalBindings;
            _compilerConflictFxLayerMode = compilerConflictFxLayerMode;
            _ignoreCurveKeys = compilerIgnoreParamList == null ? new HashSet<CurveKey>() : ExtractAllCurvesOf(compilerIgnoreParamList);
            _ignoreObjectReferences = compilerIgnoreParamList == null ? new HashSet<CurveKey>() : ExtractAllObjectReferencesOf(compilerIgnoreParamList);
            _curveKeyToFallbackValue = compilerFallbackParamList == null ? new Dictionary<CurveKey, float>() : ExtractFirstKeyframeValueOf(compilerFallbackParamList);
            _objectReferenceToFallbackValue = compilerFallbackParamList == null ? new Dictionary<CurveKey, Object>() : ExtractFirstKeyframeObjectReferenceOf(compilerFallbackParamList);
            _blinkBlendshapes = blinkBlendshapes;
            _assetContainer = assetContainer;
            _useExhaustiveAnimations = useExhaustiveAnimations;
            _emptyClip = emptyClip;
            _doNotFixSingleKeyframes = doNotFixSingleKeyframes;
        }

        private static HashSet<CurveKey> ExtractAllCurvesOf(AnimationClip clip)
        {
            return new HashSet<CurveKey>(AnimationUtility.GetCurveBindings(clip).Select(CurveKey.FromBinding));
        }

        private static HashSet<CurveKey> ExtractAllObjectReferencesOf(AnimationClip clip)
        {
            return new HashSet<CurveKey>(AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(CurveKey.FromBinding));
        }

        private static Dictionary<CurveKey, float> ExtractFirstKeyframeValueOf(AnimationClip clip)
        {
            var curveKeyToFallbackValue = new Dictionary<CurveKey, float>();
            foreach (var editorCurveBinding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, editorCurveBinding);

                if (curve.keys.Length > 0)
                {
                    curveKeyToFallbackValue.Add(CurveKey.FromBinding(editorCurveBinding), curve.keys[0].value);
                }
            }

            return curveKeyToFallbackValue;
        }

        private static Dictionary<CurveKey, Object> ExtractFirstKeyframeObjectReferenceOf(AnimationClip clip)
        {
            var curveKeyToFallbackValue = new Dictionary<CurveKey, Object>();
            foreach (var editorCurveBinding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, editorCurveBinding);

                if (curve.Length > 0)
                {
                    curveKeyToFallbackValue.Add(CurveKey.FromBinding(editorCurveBinding), curve[0].value);
                }
            }

            return curveKeyToFallbackValue;
        }

        internal List<ManifestBinding> NeutralizeManifestAnimationsInsideContainer()
        {
            var allQualifiedAnimations = QualifyAllAnimations();
            var allApplicableCurveKeys = FindAllApplicableCurveKeys(new HashSet<AnimationClip>(allQualifiedAnimations.Select(animation => animation.Clip).ToList()));
            var allApplicableObjectReferences = FindAllApplicableObjectReferences(new HashSet<AnimationClip>(allQualifiedAnimations.Select(animation => animation.Clip).ToList()));
            var animationRemapping = GenerateNeutralizedAnimationsIntoContainer(allQualifiedAnimations, allApplicableCurveKeys, allApplicableObjectReferences);

            var neutralizeManifestAnimations = _originalBindings
                .Select(binding =>
                {
                    // Since BlendTrees of different manifests can have different qualifications for a same animation,
                    // for the sake of simplicity, we do not deduplicate blend trees across multiple Manifests
                    // even if there would have been cases where they could have been deduplicated.
                    var qualifiedAnimations = binding.Manifest.AllQualifiedAnimations().ToList();
                    var biTreesReferences = binding.Manifest.AllBlendTreesFoundRecursively()
                        .ToDictionary(
                            originalTree => originalTree,
                            originalTree => new BlendTree { hideFlags = HideFlags.HideInHierarchy }
                        );

                    var blendToRemappedBlend = binding.Manifest.AllBlendTreesFoundRecursively()
                        .ToDictionary(
                            originalTree => originalTree,
                            originalTree => RemapAnimationsOfBlendTree(originalTree, animationRemapping, qualifiedAnimations, biTreesReferences)
                        );
                    foreach (var blendTree in blendToRemappedBlend.Values)
                    {
                        _assetContainer.AddBlendTree(blendTree);
                    }

                    return RemapManifest(binding, animationRemapping, blendToRemappedBlend);
                })
                .ToList();

            return neutralizeManifestAnimations;
        }

        internal AvatarMask GenerateAvatarMaskInsideContainerIfApplicableOrNull()
        {
            var allQualifiedAnimations = QualifyAllAnimations();
            var allApplicableObjectReferences = FindAllApplicableObjectReferences(new HashSet<AnimationClip>(allQualifiedAnimations.Select(animation => animation.Clip).ToList()));
            var avatarMaskNullable = GenerateAvatarMaskIntoContainerIfApplicableOrNull(allApplicableObjectReferences);

            return avatarMaskNullable;
        }

        private AvatarMask GenerateAvatarMaskIntoContainerIfApplicableOrNull(HashSet<CurveKey> allApplicableObjectReferences)
        {
            if (allApplicableObjectReferences.Count <= 0) return null;

            var mask = new AvatarMask {name = "zAutogeneratedAvm_AvatarMask"};

            MutateMaskToAllowNonEmptyAnimatedPaths(mask, allApplicableObjectReferences.Select(key => key.Path).ToList());

            _assetContainer.AddAvatarMask(mask);

            return mask;
        }

        private static BlendTree RemapAnimationsOfBlendTree(BlendTree originalTree, Dictionary<QualifiedAnimation, AnimationClip> remapping, List<QualifiedAnimation> qualifiedAnimations, Dictionary<BlendTree, BlendTree> biTreesReferences)
        {
            // Object.Instantiate(...) is triggering some weird issues about assertions failures.
            // Copy the blend tree manually
            var newTree = biTreesReferences[originalTree];
            newTree.name = "zAutogeneratedPup_" + originalTree.name + "_DO_NOT_EDIT";
            newTree.blendType = originalTree.blendType;
            newTree.blendParameter = originalTree.blendParameter;
            newTree.blendParameterY = originalTree.blendParameterY;
            newTree.minThreshold = originalTree.minThreshold;
            newTree.maxThreshold = originalTree.maxThreshold;
            newTree.useAutomaticThresholds = originalTree.useAutomaticThresholds;

            var copyOfChildren = originalTree.children;
            newTree.children = copyOfChildren
                .Select(childMotion =>
                {
                    var remappedMotion = RemapMotion(remapping, qualifiedAnimations, biTreesReferences, childMotion);
                    return new ChildMotion
                    {
                        motion = remappedMotion,
                        threshold = childMotion.threshold,
                        position = childMotion.position,
                        timeScale = childMotion.timeScale,
                        cycleOffset = childMotion.cycleOffset,
                        directBlendParameter = childMotion.directBlendParameter,
                        mirror = childMotion.mirror
                    };
                })
                .ToArray();

            return newTree;
        }

        private static Motion RemapMotion(Dictionary<QualifiedAnimation, AnimationClip> remapping, List<QualifiedAnimation> qualifiedAnimations, Dictionary<BlendTree, BlendTree> biTreesReferences, ChildMotion copyOfChild)
        {
            switch (copyOfChild.motion)
            {
                case AnimationClip clip:
                    return remapping[qualifiedAnimations.First(animation => animation.Clip == clip)];
                case BlendTree tree:
                    return biTreesReferences[tree];
                default:
                    return copyOfChild.motion;
            }
        }

        private HashSet<QualifiedAnimation> QualifyAllAnimations()
        {
            return new HashSet<QualifiedAnimation>(_originalBindings
                .SelectMany(binding => binding.Manifest.AllQualifiedAnimations())
                .ToList());
        }

        private static ManifestBinding RemapManifest(ManifestBinding manifestBinding, Dictionary<QualifiedAnimation, AnimationClip> remapping, Dictionary<BlendTree, BlendTree> blendToRemappedBlend)
        {
            var remappedManifest = manifestBinding.Manifest.NewFromRemappedAnimations(remapping, blendToRemappedBlend);
            return new ManifestBinding(manifestBinding.StageValue, remappedManifest, manifestBinding.LayerOrdinal);
        }

        private Dictionary<QualifiedAnimation, AnimationClip> GenerateNeutralizedAnimationsIntoContainer(HashSet<QualifiedAnimation> allQualifiedAnimations, HashSet<CurveKey> allApplicableCurveKeys, HashSet<CurveKey> allApplicableObjectReferences)
        {
            var shouldGenerateAnimatedAnimatorParameters = _compilerConflictFxLayerMode != ConflictFxLayerMode.KeepOnlyTransformsAndMuscles && _compilerConflictFxLayerMode != ConflictFxLayerMode.KeepOnlyTransforms;

            var technicalCommonEmptyClip = Object.Instantiate(_emptyClip);
            technicalCommonEmptyClip.name = "zAutogeneratedExp_EmptyTechnical_DO_NOT_EDIT";
            _assetContainer.AddAnimation(technicalCommonEmptyClip);

            var remapping = new Dictionary<QualifiedAnimation, AnimationClip>();

            foreach (var qualifiedAnimation in allQualifiedAnimations)
            {
                var neutralizedAnimation = CopyAndNeutralize(qualifiedAnimation.Clip, allApplicableCurveKeys, allApplicableObjectReferences, _useExhaustiveAnimations);

                var neutralizedAnimationHasNothingInIt = AnimationUtility.GetCurveBindings(neutralizedAnimation).Length == 0;
                if (shouldGenerateAnimatedAnimatorParameters)
                {
                    if (neutralizedAnimationHasNothingInIt)
                    {
                        Keyframe[] keyframes = {new Keyframe(0, 0), new Keyframe(1 / 60f, 0)};
                        var curve = new AnimationCurve(keyframes);
                        neutralizedAnimation.SetCurve("_ignored", typeof(GameObject), "m_IsActive", curve);
                    }

                    MutateCurvesForAnimatedAnimatorParameters(neutralizedAnimation, qualifiedAnimation);
                    _assetContainer.AddAnimation(neutralizedAnimation);
                }
                else if (neutralizedAnimationHasNothingInIt)
                {
                    // This will help the optimization phase later, as, this avoids creating a complex animator equality comparator for most cases
                    neutralizedAnimation = technicalCommonEmptyClip;
                }
                else
                {
                    _assetContainer.AddAnimation(neutralizedAnimation);
                }

                remapping.Add(qualifiedAnimation, neutralizedAnimation);
            }

            return remapping;
        }

        private void MutateMaskToAllowNonEmptyAnimatedPaths(AvatarMask mask, List<string> potentiallyAnimatedPaths)
        {
            mask.transformCount = potentiallyAnimatedPaths.Count;
            for (var index = 0; index < potentiallyAnimatedPaths.Count; index++)
            {
                var path = potentiallyAnimatedPaths[index];
                mask.SetTransformPath(index, path);
                mask.SetTransformActive(index, true);
            }
        }

        private static void MutateCurvesForAnimatedAnimatorParameters(AnimationClip neutralizedAnimation, QualifiedAnimation qualifiedAnimation)
        {
            var blinking = qualifiedAnimation.Qualification.IsBlinking ? 1 : 0;
            var wide = qualifiedAnimation.Qualification.Limitation == QualifiedLimitation.Wide ? 1 : 0;
            neutralizedAnimation.SetCurve("", typeof(Animator), "_Hai_GestureAnimBlink", AnimationCurve.Linear(0, blinking, 1 / 60f, blinking));
            neutralizedAnimation.SetCurve("", typeof(Animator), "_Hai_GestureAnimLSWide", AnimationCurve.Linear(0, wide, 1 / 60f, wide));
        }

        private AnimationClip CopyAndNeutralize(AnimationClip animationClipToBePreserved, HashSet<CurveKey> allApplicableCurveKeys, HashSet<CurveKey> allApplicableObjectReferences, bool useExhaustiveAnimations)
        {
            var copyOfAnimationClip = new AnimationClip {name = "zAutogeneratedExp_" + animationClipToBePreserved.name + "_DO_NOT_EDIT"};

            AnimationUtility.SetAnimationClipSettings(copyOfAnimationClip, AnimationUtility.GetAnimationClipSettings(animationClipToBePreserved));
            CopyCurveKeys(animationClipToBePreserved, copyOfAnimationClip);
            CopyObjectReferences(animationClipToBePreserved, copyOfAnimationClip);

            if (useExhaustiveAnimations)
            {
                var thisAnimationPaths = AnimationUtility.GetCurveBindings(animationClipToBePreserved)
                    .Select(CurveKey.FromBinding)
                    .ToList();
                AddMissingCurveKeys(allApplicableCurveKeys, thisAnimationPaths, copyOfAnimationClip);

                var thisAnimationObjectReferences = AnimationUtility.GetObjectReferenceCurveBindings(animationClipToBePreserved)
                    .Select(CurveKey.FromBinding)
                    .ToList();
                AddMissingObjectReferences(allApplicableObjectReferences, thisAnimationObjectReferences, copyOfAnimationClip);
            }

            return copyOfAnimationClip;
        }

        private void CopyCurveKeys(AnimationClip animationClipToBePreserved, AnimationClip copyOfAnimationClip)
        {
            var originalBindings = AnimationUtility.GetCurveBindings(animationClipToBePreserved);
            foreach (var binding in originalBindings)
            {
                var curveKey = CurveKey.FromBinding(binding);
                var canCopyCurve = _compilerConflictFxLayerMode == ConflictFxLayerMode.KeepBoth || !ShouldRemoveCurve(curveKey);
                if (canCopyCurve)
                {
                    var curve = AnimationUtility.GetEditorCurve(animationClipToBePreserved, binding);
                    if (!_doNotFixSingleKeyframes && curve.keys.Length == 1)
                    {
                        curve = new AnimationCurve(MakeSingleKeyframeIntoTwo(curve));
                    }

                    AnimationUtility.SetEditorCurve(copyOfAnimationClip, binding, curve);
                }
            }
        }

        private void CopyObjectReferences(AnimationClip animationClipToBePreserved, AnimationClip copyOfAnimationClip)
        {
            var canCopyCurve = _compilerConflictFxLayerMode == ConflictFxLayerMode.KeepBoth || _compilerConflictFxLayerMode == ConflictFxLayerMode.RemoveTransformsAndMuscles;
            if (!canCopyCurve)
            {
                return;
            }

            var originalBindings = AnimationUtility.GetObjectReferenceCurveBindings(animationClipToBePreserved);
            foreach (var binding in originalBindings)
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(animationClipToBePreserved, binding);
                if (!_doNotFixSingleKeyframes && curve.Length == 1)
                {
                    curve = new[]
                    {
                        new ObjectReferenceKeyframe
                        {
                            time = 0 / 60f,
                            value = curve[0].value
                        },
                        new ObjectReferenceKeyframe
                        {
                            time = curve[0].time == 0f ? 1 / 60f : curve[0].time,
                            value = curve[0].value
                        }
                    };
                }

                AnimationUtility.SetObjectReferenceCurve(copyOfAnimationClip, binding, curve);
            }
        }

        private static Keyframe[] MakeSingleKeyframeIntoTwo(AnimationCurve curve)
        {
            var originalKeyframe = curve.keys[0];
            var originalKeyframeIsZero = originalKeyframe.time == 0;
            var newKeyframe = new Keyframe
            {
                time = originalKeyframeIsZero ? 1 / 60f : 0f,
                value = originalKeyframe.value,
                inTangent = originalKeyframe.inTangent,
                outTangent = originalKeyframe.outTangent,
                tangentMode = originalKeyframe.tangentMode,
                weightedMode = originalKeyframe.weightedMode,
                inWeight = originalKeyframe.inWeight,
                outWeight = originalKeyframe.outWeight,
            };
            return originalKeyframeIsZero ? new[] {originalKeyframe, newKeyframe} : new[] {newKeyframe, originalKeyframe};
        }

        private bool ShouldRemoveCurve(CurveKey curveKey)
        {
            switch (_compilerConflictFxLayerMode)
            {
                case ConflictFxLayerMode.RemoveTransformsAndMuscles:
                    return curveKey.IsTransformOrMuscleCurve();
                case ConflictFxLayerMode.KeepOnlyTransformsAndMuscles:
                    return !curveKey.IsTransformOrMuscleCurve();
                case ConflictFxLayerMode.KeepOnlyTransforms:
                    return !curveKey.IsTransformCurve();
                case ConflictFxLayerMode.KeepBoth:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void AddMissingCurveKeys(HashSet<CurveKey> allApplicableCurveKeys, List<CurveKey> thisAnimationPaths, AnimationClip copyOfAnimationClip)
        {
            foreach (var curveKey in allApplicableCurveKeys)
            {
                if (!thisAnimationPaths.Contains(curveKey))
                {
                    var fallbackValue = _curveKeyToFallbackValue.ContainsKey(curveKey) ? _curveKeyToFallbackValue[curveKey] : 0;

                    Keyframe[] keyframes = {new Keyframe(0, fallbackValue), new Keyframe(1 / 60f, fallbackValue)};
                    var curve = new AnimationCurve(keyframes);
                    copyOfAnimationClip.SetCurve(curveKey.Path, curveKey.Type, curveKey.PropertyName, curve);
                }
            }
        }

        private void AddMissingObjectReferences(HashSet<CurveKey> allApplicableObjectReferences, List<CurveKey> thisObjectReferences, AnimationClip copyOfAnimationClip)
        {
            foreach (var curveKey in allApplicableObjectReferences)
            {
                if (!thisObjectReferences.Contains(curveKey))
                {
                    // No default-if-missing fallback is provided for material references
                    if (_objectReferenceToFallbackValue.ContainsKey(curveKey))
                    {
                        var fallbackValue = _objectReferenceToFallbackValue[curveKey];

                        ObjectReferenceKeyframe[] keyframes =
                        {
                            new ObjectReferenceKeyframe
                            {
                                time = 0f,
                                value = fallbackValue
                            },
                            new ObjectReferenceKeyframe
                            {
                                time = 1 / 60f,
                                value = fallbackValue
                            }
                        };
                        AnimationUtility.SetObjectReferenceCurve(copyOfAnimationClip, new EditorCurveBinding
                        {
                            path = curveKey.Path,
                            type = curveKey.Type,
                            propertyName = curveKey.PropertyName
                        }, keyframes);
                    }
                }
            }
        }

        private HashSet<CurveKey> FindAllApplicableCurveKeys(HashSet<AnimationClip> allAnimationClips)
        {
            var allCurveKeysFromAnimations = allAnimationClips
                .SelectMany(AnimationUtility.GetCurveBindings)
                .Select(CurveKey.FromBinding)
                .ToList();

            var curveKeys = new[]{allCurveKeysFromAnimations, _blinkBlendshapes}
                .SelectMany(keys => keys)
                .Where(curveKey => !_ignoreCurveKeys.Contains(curveKey))
                .Where(curveKey =>
                {
                    switch (_compilerConflictFxLayerMode)
                    {
                        case ConflictFxLayerMode.RemoveTransformsAndMuscles: return !curveKey.IsTransformOrMuscleCurve();
                        case ConflictFxLayerMode.KeepBoth: return true;
                        case ConflictFxLayerMode.KeepOnlyTransformsAndMuscles: return curveKey.IsTransformOrMuscleCurve();
                        case ConflictFxLayerMode.KeepOnlyTransforms: return curveKey.IsTransformCurve();
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                })
                .ToList();

            return new HashSet<CurveKey>(curveKeys);
        }

        private HashSet<CurveKey> FindAllApplicableObjectReferences(HashSet<AnimationClip> allAnimationClips)
        {
            if (_compilerConflictFxLayerMode == ConflictFxLayerMode.KeepOnlyTransforms || _compilerConflictFxLayerMode == ConflictFxLayerMode.KeepOnlyTransformsAndMuscles)
            {
                return new HashSet<CurveKey>();
            }

            var allObjectReferencesFromAnimations = allAnimationClips
                .SelectMany(AnimationUtility.GetObjectReferenceCurveBindings)
                .Select(CurveKey.FromBinding)
                .ToList();

            var objectReferences = allObjectReferencesFromAnimations
                .Where(curveKey => !_ignoreObjectReferences.Contains(curveKey))
                .ToList();

            return new HashSet<CurveKey>(objectReferences);
        }
    }
}
