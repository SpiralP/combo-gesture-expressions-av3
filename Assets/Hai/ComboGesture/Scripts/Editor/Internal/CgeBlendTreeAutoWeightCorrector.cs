﻿using System;
using System.Collections.Generic;
using System.Linq;
using Hai.ComboGesture.Scripts.Editor.Internal.Model;
using UnityEditor.Animations;
using UnityEngine;

namespace Hai.ComboGesture.Scripts.Editor.Internal
{
    internal class CgeBlendTreeAutoWeightCorrector : List<ManifestBinding>
    {
        public const string AutoGestureWeightParam = "_AutoGestureWeight";
        private readonly List<ManifestBinding> _activityManifests;
        private readonly bool _useGestureWeightCorrection;
        private readonly AssetContainer _assetContainer;

        public CgeBlendTreeAutoWeightCorrector(List<ManifestBinding> activityManifests, bool useGestureWeightCorrection, AssetContainer assetContainer)
        {
            _activityManifests = activityManifests;
            _useGestureWeightCorrection = useGestureWeightCorrection;
            _assetContainer = assetContainer;
        }

        public List<ManifestBinding> MutateAndCorrectExistingBlendTrees()
        {
            var mappings = _activityManifests
                .Where(binding => binding.Manifest.Kind() == ManifestKind.Permutation)
                .SelectMany(binding => binding.Manifest.AllBlendTreesFoundRecursively())
                .Distinct()
                .Where(tree => tree.blendParameter == AutoGestureWeightParam || tree.blendParameterY == AutoGestureWeightParam)
                .Select(originalTree =>
                {
                    var newTreeForLeftSide = CopyTreeIdentically(originalTree, Side.Left);
                    var newTreeForRightSide = CopyTreeIdentically(originalTree, Side.Right);
                    _assetContainer.AddBlendTree(newTreeForLeftSide);
                    _assetContainer.AddBlendTree(newTreeForRightSide);
                    return new AutoWeightTreeMapping(originalTree, newTreeForLeftSide, newTreeForRightSide);
                })
                .ToDictionary(mapping => mapping.Original, mapping => mapping);


            return _activityManifests
                .Select(binding =>
                {
                    if (binding.Manifest.Kind() != ManifestKind.Permutation)
                    {
                        return binding;
                    }

                    return RemapManifest(binding, mappings);
                }).ToList();
        }

        private ManifestBinding RemapManifest(ManifestBinding manifestBinding, Dictionary<BlendTree, AutoWeightTreeMapping> autoWeightRemapping)
        {
            var remappedManifest = manifestBinding.Manifest.UsingRemappedWeights(autoWeightRemapping);
            return new ManifestBinding(manifestBinding.StageValue, remappedManifest, manifestBinding.LayerOrdinal);
        }

        private BlendTree CopyTreeIdentically(BlendTree originalTree, Side side)
        {
            var newTree = new BlendTree();
            var remappedAutoWeight = side == Side.Left ? "GestureLeftWeight" : "GestureRightWeight";

            // Object.Instantiate(...) is triggering some weird issues about assertions failures.
            // Copy the blend tree manually
            newTree.name = "zAutogeneratedPup_" + originalTree.name + "_DO_NOT_EDIT";
            newTree.blendType = originalTree.blendType;
            newTree.blendParameter = HandleWeightCorrection(
                originalTree.blendParameter == AutoGestureWeightParam ? remappedAutoWeight : originalTree.blendParameter
            );
            newTree.blendParameterY = HandleWeightCorrection(
                originalTree.blendParameterY == AutoGestureWeightParam ? remappedAutoWeight : originalTree.blendParameterY
            );
            newTree.minThreshold = originalTree.minThreshold;
            newTree.maxThreshold = originalTree.maxThreshold;
            newTree.useAutomaticThresholds = originalTree.useAutomaticThresholds;

            var copyOfChildren = originalTree.children;
            while (newTree.children.Length > 0) {
                newTree.RemoveChild(0);
            }

            var blendType = newTree.blendType;
            foreach (var copyOfChild in copyOfChildren)
            {
                var remappedMotion = copyOfChild.motion;

                switch (blendType)
                {
                    case BlendTreeType.Direct:
                        newTree.AddChild(remappedMotion);
                        break;
                    case BlendTreeType.Simple1D:
                        newTree.AddChild(remappedMotion, copyOfChild.threshold);
                        break;
                    case BlendTreeType.SimpleDirectional2D:
                    case BlendTreeType.FreeformDirectional2D:
                    case BlendTreeType.FreeformCartesian2D:
                        newTree.AddChild(remappedMotion, copyOfChild.position);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return newTree;
        }
        private string HandleWeightCorrection(string originalTreeBlendParameter)
        {
            // FIXME this is duplicate code
            if (!_useGestureWeightCorrection)
            {
                return originalTreeBlendParameter;
            }

            switch (originalTreeBlendParameter)
            {
                case "GestureLeftWeight":
                    return SharedLayerUtils.HaiGestureComboLeftWeightProxy;
                case "GestureRightWeight":
                    return SharedLayerUtils.HaiGestureComboRightWeightProxy;
                default:
                    return originalTreeBlendParameter;
            }
        }
    }

    internal enum Side
    {
        Left, Right
    }
}
