#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// MeshDeformationStandalone用のNDMFプレビュー。
    /// 編集終了後にデルタをプロキシメッシュに適用して変形結果を表示する。
    /// </summary>
    internal class MeshDeformationStandalonePreview : IRenderFilter
    {
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var avatars = context.GetAvatarRoots();
            var resultSet = new List<RenderGroup>();

            foreach (var avatar in avatars)
            {
                try
                {
                    var components = context.GetComponentsInChildren<MeshDeformationStandalone>(avatar, true);
                    if (!components.Any()) continue;

                    var editingIds = Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds;
                    var targetRenderers = new HashSet<Renderer>();

                    foreach (var component in components)
                    {
                        // 変形データの変更を監視
                        context.Observe(component, c => ComputeDeformHash(c));

                        // 編集状態を監視（編集開始/終了でリビルドをトリガー）
                        int editingIdx = context.Observe(component, c => c.deformEditingRendererIndex);
                        bool isActuallyEditing = editingIdx >= 0 && editingIds.Count > 0;

                        // 編集中はプロキシ対象外（SceneEditorが直接操作中）
                        if (isActuallyEditing) continue;

                        // 変形データがないコンポーネントはスキップ
                        if (component.rendererDeformations == null) continue;
                        bool hasDeltas = component.rendererDeformations.Any(
                            d => d.deltas != null && d.deltas.Count > 0);
                        if (!hasDeltas) continue;

                        var renderer = context.Observe(component, c => c.targetRenderer);
                        if (renderer != null)
                            targetRenderers.Add(renderer);
                    }

                    if (targetRenderers.Count > 0)
                    {
                        resultSet.Add(RenderGroup.For(targetRenderers)
                            .WithData((avatar, components.ToArray())));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChimeraHairMaster] Standalone preview GetTargetGroups failed: {ex}");
                }
            }

            return resultSet.ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            try
            {
                var renderData = group.GetData<(GameObject, MeshDeformationStandalone[])>();
                var components = renderData.Item2;

                foreach (var component in components)
                {
                    context.Observe(component, c => ComputeDeformHash(c));
                }

                var editingIds = Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds;
                var remappedMeshes = new Dictionary<int, Mesh>();

                foreach (var component in components)
                {
                    if (component.rendererDeformations == null) continue;

                    foreach (var deformation in component.rendererDeformations)
                    {
                        if (deformation.deltas == null || deformation.deltas.Count == 0) continue;
                        if (deformation.rendererIndex != 0) continue;

                        var renderer = component.targetRenderer;
                        if (renderer == null || renderer.sharedMesh == null) continue;
                        if (renderer.sharedMesh.vertexCount != deformation.expectedVertexCount) continue;

                        int rendererId = renderer.GetInstanceID();

                        // 編集中のRendererはスキップ
                        if (editingIds.Contains(rendererId)) continue;

                        // 既にMeshDeformPassが適用済みの場合スキップ
                        bool isAsset = AssetDatabase.Contains(renderer.sharedMesh);
                        if (!isAsset && renderer.sharedMesh.name.EndsWith("_Deformed")) continue;

                        // メッシュをコピーしてデルタを適用
                        var baseMesh = Object.Instantiate(renderer.sharedMesh);
                        baseMesh.name = renderer.sharedMesh.name + "_DeformPreview";

                        var vertices = baseMesh.vertices;
                        foreach (var delta in deformation.deltas)
                        {
                            if (delta.vertexIndex >= 0 && delta.vertexIndex < vertices.Length)
                                vertices[delta.vertexIndex] += delta.offset;
                        }
                        baseMesh.vertices = vertices;
                        baseMesh.RecalculateNormals();
                        baseMesh.RecalculateBounds();

                        remappedMeshes[rendererId] = baseMesh;
                    }
                }

                if (remappedMeshes.Count == 0)
                {
                    return Task.FromResult<IRenderFilterNode>(new DeformPreviewNode(null));
                }

                return Task.FromResult<IRenderFilterNode>(new DeformPreviewNode(remappedMeshes));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Standalone preview Instantiate failed: {ex}");
                return Task.FromResult<IRenderFilterNode>(new DeformPreviewNode(null));
            }
        }

        /// <summary>
        /// 変形データのハッシュ値を計算（変更検知用）
        /// </summary>
        private static int ComputeDeformHash(MeshDeformationStandalone component)
        {
            if (component.rendererDeformations == null) return 0;

            int hash = 17;
            int editingIdx = component.deformEditingRendererIndex;
            bool isEditing = editingIdx >= 0
                && Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds.Count > 0;

            foreach (var deformation in component.rendererDeformations)
            {
                // 編集中のRendererのデルタはハッシュから除外
                if (isEditing && deformation.rendererIndex == editingIdx)
                    continue;

                hash = hash * 31 + deformation.rendererIndex;
                hash = hash * 31 + deformation.deltas.Count;
                foreach (var delta in deformation.deltas)
                {
                    hash = hash * 31 + delta.vertexIndex;
                    hash = hash * 31 + delta.offset.GetHashCode();
                }
            }

            return hash;
        }

        /// <summary>
        /// メッシュ変形のみのプレビューノード
        /// </summary>
        private class DeformPreviewNode : IRenderFilterNode, IDisposable
        {
            private readonly Dictionary<int, Mesh>? _remappedMeshes;

            public RenderAspects WhatChanged => _remappedMeshes != null
                ? RenderAspects.Mesh
                : 0;

            public DeformPreviewNode(Dictionary<int, Mesh>? remappedMeshes)
            {
                _remappedMeshes = remappedMeshes;
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_remappedMeshes == null || proxy is not SkinnedMeshRenderer smr) return;

                int originalId = original.GetInstanceID();
                if (_remappedMeshes.TryGetValue(originalId, out var mesh) && smr.sharedMesh != mesh)
                {
                    smr.sharedMesh = mesh;
                }
            }

            public void Dispose()
            {
                if (_remappedMeshes != null)
                {
                    foreach (var mesh in _remappedMeshes.Values)
                    {
                        if (mesh != null)
                            Object.DestroyImmediate(mesh);
                    }
                    _remappedMeshes.Clear();
                }
            }
        }
    }
}
