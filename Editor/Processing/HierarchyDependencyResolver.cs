#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 一時 GameObject から、対象 Renderer に必要な bone・PhysBone・Constraint だけを残し、
    /// 不要な GameObject を再帰削除するユーティリティ。
    ///
    /// module-creator (Ao_425, MIT) の RendererDepedency.cs と CleanUpHierarchy.cs の思想を参考に、
    /// CHM 用に簡略化した自前実装。
    /// </summary>
    internal static class HierarchyDependencyResolver
    {
        /// <summary>
        /// 一時 root 配下から、targetRenderers に必要な依存だけを残してクリーンアップする。
        /// </summary>
        /// <param name="tempRoot">対象 GameObject（アバター root のクローン）</param>
        /// <param name="targetRenderers">残すべき SkinnedMeshRenderer 群（tempRoot 配下のもの）</param>
        public static void CleanUp(GameObject tempRoot, IEnumerable<SkinnedMeshRenderer> targetRenderers)
        {
            var renderers = targetRenderers.Where(r => r != null).ToArray();

            // 1. weighted bones 収集
            var weightedBones = CollectWeightedBones(renderers);

            // 2. 残すべき Component 集合
            var componentsToSave = new HashSet<Component>();

            // Renderer 自身と rootBone / probeAnchor
            foreach (var smr in renderers)
            {
                componentsToSave.Add(smr);
                if (smr.rootBone != null) componentsToSave.Add(smr.rootBone);
                if (smr.probeAnchor != null) componentsToSave.Add(smr.probeAnchor);
            }

            // 3. weighted bones 自身を Component として保持
            foreach (var bone in weightedBones)
            {
                componentsToSave.Add(bone);
            }

            // 4. PhysBone / PhysBoneCollider
            CollectPhysBoneDependencies(tempRoot, weightedBones, componentsToSave);

            // 5. Constraint
            CollectConstraintDependencies(tempRoot, weightedBones, componentsToSave);

            // 6. 不要 GameObject を再帰削除
            CheckAndDeleteRecursive(tempRoot, componentsToSave);
        }

        // ========== weighted bones 収集 ==========

        private static HashSet<Transform> CollectWeightedBones(IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var result = new HashSet<Transform>();
            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null) continue;
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) continue;

                var boneWeights = smr.sharedMesh.boneWeights;
                foreach (var bw in boneWeights)
                {
                    AddIfWeighted(result, bones, bw.boneIndex0, bw.weight0);
                    AddIfWeighted(result, bones, bw.boneIndex1, bw.weight1);
                    AddIfWeighted(result, bones, bw.boneIndex2, bw.weight2);
                    AddIfWeighted(result, bones, bw.boneIndex3, bw.weight3);
                }
            }
            return result;
        }

        private static void AddIfWeighted(HashSet<Transform> result, Transform[] bones, int index, float weight)
        {
            if (weight <= 0f) return;
            if (index < 0 || index >= bones.Length) return;
            var t = bones[index];
            if (t != null) result.Add(t);
        }

        // ========== PhysBone 依存 ==========

        private static void CollectPhysBoneDependencies(GameObject root, HashSet<Transform> weightedBones, HashSet<Component> componentsToSave)
        {
            var physBones = root.GetComponentsInChildren<VRCPhysBone>(true);
            foreach (var pb in physBones)
            {
                var pbTarget = GetPhysBoneTarget(pb);
                if (pbTarget == null) continue;

                // この PhysBone が weighted bone を 1 個でも含んでいれば残す
                var affectedTransforms = GetSelfAndAllChildren(pbTarget);
                bool affectsWeightedBone = affectedTransforms.Any(t => weightedBones.Contains(t));
                if (!affectsWeightedBone) continue;

                componentsToSave.Add(pb);

                // single chain で affectedTransforms をたどって、weighted bone を含むものは追加で残す
                foreach (var t in affectedTransforms)
                {
                    if (weightedBones.Contains(t))
                    {
                        AddSingleChain(t, componentsToSave);
                    }
                }

                // PhysBone Collider
                if (pb.colliders != null)
                {
                    foreach (var collider in pb.colliders)
                    {
                        if (collider == null) continue;
                        componentsToSave.Add(collider);
                        var colTarget = GetPhysBoneColliderTarget(collider);
                        if (colTarget != null) componentsToSave.Add(colTarget);
                    }
                }
            }
        }

        private static Transform? GetPhysBoneTarget(VRCPhysBoneBase pb)
        {
            return pb.rootTransform != null ? pb.rootTransform : pb.transform;
        }

        private static Transform? GetPhysBoneColliderTarget(VRCPhysBoneColliderBase collider)
        {
            return collider.rootTransform != null ? collider.rootTransform : collider.transform;
        }

        private static void AddSingleChain(Transform start, HashSet<Component> componentsToSave)
        {
            var current = start;
            while (current != null)
            {
                componentsToSave.Add(current);
                if (current.childCount != 1) break;
                current = current.GetChild(0);
            }
        }

        // ========== Constraint 依存 ==========

        private static void CollectConstraintDependencies(GameObject root, HashSet<Transform> weightedBones, HashSet<Component> componentsToSave)
        {
            var rootTransform = root.transform;

            // weighted bones の親階層も valid 扱い
            var validBones = new HashSet<Transform>();
            foreach (var bone in weightedBones)
            {
                AddSelfAndParents(rootTransform, bone, validBones);
            }

            // 全 Constraint 情報を集める
            var infos = CollectAllConstraintInfo(root);
            if (infos.Count == 0) return;

            const int MaxIterations = 32;
            for (int iter = 0; iter < MaxIterations; iter++)
            {
                var newlyValidSources = new HashSet<Transform>();

                foreach (var info in infos)
                {
                    if (info.Target == null) continue;

                    bool targetCovered =
                        validBones.Contains(info.Target) ||
                        info.TargetChildren.Any(c => validBones.Contains(c));

                    if (!targetCovered) continue;

                    componentsToSave.Add(info.Target);

                    foreach (var src in info.SourceParents)
                    {
                        if (src == null) continue;
                        componentsToSave.Add(src);
                        newlyValidSources.Add(src);
                    }
                }

                if (!newlyValidSources.Except(validBones).Any()) break;
                validBones.UnionWith(newlyValidSources);
            }
        }

        private struct ConstraintInfo
        {
            public Transform Target;
            public HashSet<Transform> TargetChildren;
            public HashSet<Transform> SourceParents;
        }

        private static List<ConstraintInfo> CollectAllConstraintInfo(GameObject root)
        {
            var result = new List<ConstraintInfo>();
            var rootTransform = root.transform;

            // Unity Constraints
            var unityConstraints = root.GetComponentsInChildren<IConstraint>(true);
            foreach (var uc in unityConstraints)
            {
                var component = uc as Component;
                if (component == null) continue;
                var target = component.transform;
                var sources = new HashSet<Transform>();
                for (int i = 0; i < uc.sourceCount; i++)
                {
                    var s = uc.GetSource(i).sourceTransform;
                    if (s != null) sources.Add(s);
                }
                result.Add(BuildInfo(rootTransform, target, sources));
            }

            // VRC Constraints
            var vrcConstraints = root.GetComponentsInChildren<VRCConstraintBase>(true);
            foreach (var vc in vrcConstraints)
            {
                var target = vc.TargetTransform != null ? vc.TargetTransform : vc.transform;
                var sources = new HashSet<Transform>();
                foreach (var s in vc.Sources)
                {
                    if (s.SourceTransform != null) sources.Add(s.SourceTransform);
                }
                result.Add(BuildInfo(rootTransform, target, sources));
            }

            return result;
        }

        private static ConstraintInfo BuildInfo(Transform rootTransform, Transform target, HashSet<Transform> sources)
        {
            var sourceParents = new HashSet<Transform>();
            foreach (var s in sources)
            {
                AddSelfAndParents(rootTransform, s, sourceParents);
            }
            return new ConstraintInfo
            {
                Target = target,
                TargetChildren = GetSelfAndAllChildren(target).ToHashSet(),
                SourceParents = sourceParents,
            };
        }

        private static void AddSelfAndParents(Transform root, Transform target, HashSet<Transform> result)
        {
            var current = target;
            while (current != null && current != root)
            {
                result.Add(current);
                current = current.parent;
            }
            if (current == root) result.Add(current);
        }

        // ========== 不要 GameObject 削除 ==========

        private static void CheckAndDeleteRecursive(GameObject root, HashSet<Component> componentsToSave)
        {
            var gameObjectsToSave = new HashSet<GameObject>();
            foreach (var c in componentsToSave)
            {
                if (c != null) gameObjectsToSave.Add(c.gameObject);
            }
            CheckAndDeleteRecursiveImpl(root, gameObjectsToSave, componentsToSave);
        }

        private static void CheckAndDeleteRecursiveImpl(GameObject go, HashSet<GameObject> gameObjectsToSave, HashSet<Component> componentsToSave)
        {
            // 子から先に処理
            var children = new List<Transform>();
            foreach (Transform child in go.transform) children.Add(child);
            foreach (var child in children)
            {
                CheckAndDeleteRecursiveImpl(child.gameObject, gameObjectsToSave, componentsToSave);
            }

            // 残すべき or 子が残っている場合は削除しない（component は不要なものを除去）
            if (gameObjectsToSave.Contains(go) || go.transform.childCount != 0)
            {
                go.SetActive(true);
                go.tag = "Untagged";
                RemoveUnnecessaryComponents(go, componentsToSave);
                return;
            }

            Object.DestroyImmediate(go, true);
        }

        private static void RemoveUnnecessaryComponents(GameObject go, HashSet<Component> componentsToSave)
        {
            var components = go.GetComponents<Component>();
            foreach (var c in components)
            {
                if (c == null) continue;
                if (c is Transform) continue;
                if (componentsToSave.Contains(c)) continue;
                Object.DestroyImmediate(c, true);
            }
        }

        // ========== 補助 ==========

        private static IEnumerable<Transform> GetSelfAndAllChildren(Transform t)
        {
            yield return t;
            foreach (Transform child in t)
            {
                foreach (var x in GetSelfAndAllChildren(child)) yield return x;
            }
        }
    }
}
