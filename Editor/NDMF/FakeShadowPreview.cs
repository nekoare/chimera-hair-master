#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// FakeShadowのNDMFプレビューフィルタ。
    ///
    /// NDMFプレビューは新規Rendererを追加できないため、ビルド（複製レンダラー方式）とは
    /// 構成を変えて同等の見た目を再現する:
    /// - 顔: 選択マテリアルをステンシル書き込みクローンに差し替え
    /// - 影: 髪プロキシのメッシュ末尾にサブメッシュを追記（頂点複製なし・インデックス連結のみ）し、
    ///   追加のマテリアルスロットに影マテリアルを割り当てる
    /// - 髪queueの引き上げもプロキシ上のマテリアルクローンで再現
    ///
    /// ChimeraHairMasterPreview（色変換・アトラス）の後段にチェーンされるため、
    /// 影は色変換・アトラス適用後の最終プレビュー状態に乗る（ビルドのパス順と整合）。
    /// マテリアル設定値の生成は FakeShadowSetup と共通
    /// </summary>
    internal class FakeShadowPreview : IRenderFilter
    {
        /// <summary>
        /// このフィルタが生成した追記済みメッシュ → 元メッシュ（InstanceID）。
        /// 上流フィルタ（ChimeraHairMasterPreview）が毎フレーム自分のメッシュへ書き戻して
        /// 相互上書き（ping-pong）にならないよう、上流側の書き込みガードから参照される
        /// </summary>
        private static readonly Dictionary<int, int> _appendedMeshSources = new();

        /// <summary>current が source から生成した影サブメッシュ追記版かどうか</summary>
        internal static bool IsShadowAppendedVariant(Mesh? current, Mesh source)
        {
            if (current == null || source == null) return false;
            return _appendedMeshSources.TryGetValue(current.GetInstanceID(), out var sourceId)
                   && sourceId == source.GetInstanceID();
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var avatars = context.GetAvatarRoots();
            var resultSet = new List<RenderGroup>();

            foreach (var avatar in avatars)
            {
                try
                {
                    var components = context.GetComponentsInChildren<ChimeraHairMaster>(avatar, true);
                    if (!components.Any()) continue;

                    var enabledComponents = components
                        .Where(c => context.Observe(c, x => x.isEnabled && x.previewEnabled && x.enableFakeShadow))
                        .ToArray();
                    if (!enabledComponents.Any()) continue;

                    // メッシュ変形編集中のRendererはプロキシ対象から除外（本体プレビューと同じ扱い）
                    var editingIds = Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds;

                    var targetRenderers = new HashSet<Renderer>();
                    foreach (var component in enabledComponents)
                    {
                        context.Observe(component, c => c.deformEditingRendererIndex);

                        var renderers = context.Observe(component,
                            c => c.targetRenderers?.ToList(),
                            (a, b) => ReferenceEquals(a, b)
                                      || (a != null && b != null && a.SequenceEqual(b)));
                        var faceRenderer = context.Observe(component, c => c.fakeShadowFaceRenderer);

                        if (renderers != null)
                        {
                            foreach (var renderer in renderers)
                            {
                                if (renderer == null) continue;
                                if (editingIds.Contains(renderer.GetInstanceID())) continue;
                                targetRenderers.Add(renderer);
                            }
                        }

                        if (faceRenderer != null) targetRenderers.Add(faceRenderer);
                    }

                    if (targetRenderers.Count > 0)
                    {
                        // データ等価性はグループ維持/ノード再利用の判定に使われる（内容の変更検知は
                        // Observe が担うため、ここは構成が同じかどうかの比較でよい）
                        resultSet.Add(RenderGroup.For(targetRenderers).WithData(
                            enabledComponents,
                            (a, b) => a.SequenceEqual(b)));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChimeraHairMaster] Failed to get fake shadow target groups: {ex}");
                }
            }

            return resultSet.ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            try
            {
                var components = group.GetData<ChimeraHairMaster[]>();

                var hairConfigs = new Dictionary<int, ShadowConfig>();
                var faceConfigs = new Dictionary<int, ShadowConfig>();
                var ownedMaterials = new List<Material>();
                var processedFaceIds = new HashSet<int>();

                foreach (var component in components)
                {
                    if (component == null) continue;

                    // FakeShadowパラメータの変更を監視（変更でノード再生成）
                    context.Observe(component, c => ComputeFakeShadowHash(c));

                    if (!component.isEnabled || !component.previewEnabled || !component.enableFakeShadow) continue;
                    if (!FakeShadowSetup.Validate(component, out _)) continue;

                    int shadowQueue = FakeShadowSetup.ComputeShadowRenderQueue(component);
                    if (shadowQueue < 0) continue;

                    var faceRenderer = component.fakeShadowFaceRenderer;
                    int faceId = faceRenderer.GetInstanceID();
                    // 同じ顔Rendererへの多重セットアップは先勝ち（ビルドと同じ）
                    if (!processedFaceIds.Add(faceId)) continue;

                    // 既存のステンシル書き込みがある顔マテリアルはそのRefを尊重（ビルドと同じ）
                    int stencilRef = FakeShadowSetup.ResolveStencilRef(component);

                    var shadowMaterial = FakeShadowSetup.CreateShadowMaterial(component, shadowQueue, stencilRef);
                    if (shadowMaterial == null) continue;
                    shadowMaterial.hideFlags = HideFlags.HideAndDontSave;
                    ownedMaterials.Add(shadowMaterial);

                    var config = new ShadowConfig
                    {
                        ShadowMaterial = shadowMaterial,
                        ShadowQueue = shadowQueue,
                        StencilRef = stencilRef,
                        FaceMaterialIndexes = new HashSet<int>(component.fakeShadowFaceMaterialIndexes),
                    };
                    if (component.fakeShadowExcludedRenderers != null)
                    {
                        foreach (var excluded in component.fakeShadowExcludedRenderers)
                        {
                            if (excluded != null) config.ExcludedRendererIds.Add(excluded.GetInstanceID());
                        }
                    }

                    faceConfigs[faceId] = config;

                    if (component.targetRenderers != null)
                    {
                        foreach (var renderer in component.targetRenderers)
                        {
                            if (renderer == null) continue;
                            hairConfigs[renderer.GetInstanceID()] = config;
                        }
                    }
                }

                if (faceConfigs.Count == 0)
                {
                    return Task.FromResult<IRenderFilterNode>(new FakeShadowNode(null, null, null));
                }

                return Task.FromResult<IRenderFilterNode>(new FakeShadowNode(hairConfigs, faceConfigs, ownedMaterials));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Failed to instantiate fake shadow preview: {ex}");
                return Task.FromResult<IRenderFilterNode>(new FakeShadowNode(null, null, null));
            }
        }

        /// <summary>FakeShadowプレビューに影響するパラメータの内容ハッシュ</summary>
        private static int ComputeFakeShadowHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + component.enableFakeShadow.GetHashCode();
                hash = hash * 31 + (component.fakeShadowFaceRenderer != null
                    ? component.fakeShadowFaceRenderer.GetInstanceID() : 0);
                if (component.fakeShadowFaceMaterialIndexes != null)
                {
                    foreach (var index in component.fakeShadowFaceMaterialIndexes)
                    {
                        hash = hash * 31 + index;
                    }
                }
                hash = hash * 31 + component.fakeShadowColor.GetHashCode();
                hash = hash * 31 + component.fakeShadowDirection.GetHashCode();
                hash = hash * 31 + component.fakeShadowOffset.GetHashCode();
                hash = hash * 31 + component.fakeShadowStencilRef;
                hash = hash * 31 + component.fakeShadowDepthBias.GetHashCode();
                if (component.fakeShadowExcludedRenderers != null)
                {
                    foreach (var excluded in component.fakeShadowExcludedRenderers)
                    {
                        hash = hash * 31 + (excluded != null ? excluded.GetInstanceID() : 0);
                    }
                }
                return hash;
            }
        }

        private class ShadowConfig
        {
            public Material ShadowMaterial = null!;
            public int ShadowQueue;
            public int StencilRef;
            public HashSet<int> FaceMaterialIndexes = new();

            /// <summary>影を落とさない髪Renderer（InstanceID）。queue引き上げは除外髪にも適用する</summary>
            public HashSet<int> ExcludedRendererIds = new();
        }

        private class FakeShadowNode : IRenderFilterNode, IDisposable
        {
            // Renderer InstanceID → 設定（髪／顔）
            private readonly Dictionary<int, ShadowConfig>? _hairConfigs;
            private readonly Dictionary<int, ShadowConfig>? _faceConfigs;
            private readonly List<Material>? _ownedShadowMaterials;

            // OnFrame の遅延生成キャッシュ（このノードが所有し Dispose で破棄する）
            private readonly Dictionary<(Material, int), Material> _faceClones = new();
            private readonly Dictionary<(Material, int), Material> _queueShifted = new();
            private readonly HashSet<Material> _ownedMaterialSet = new();
            // 上流フィルタの出力メッシュ → 影サブメッシュ追記版
            private readonly Dictionary<Mesh, Mesh> _appendedMeshes = new();
            private readonly HashSet<Mesh> _appendedMeshSet = new();

            public RenderAspects WhatChanged => RenderAspects.Material | RenderAspects.Mesh;

            public FakeShadowNode(
                Dictionary<int, ShadowConfig>? hairConfigs,
                Dictionary<int, ShadowConfig>? faceConfigs,
                List<Material>? ownedShadowMaterials)
            {
                _hairConfigs = hairConfigs;
                _faceConfigs = faceConfigs;
                _ownedShadowMaterials = ownedShadowMaterials;
            }

            // OnFrame 用の非アロケートバッファ（メインスレッド専用）
            private static readonly List<Material> _materialBuffer = new();

            public void OnFrame(Renderer original, Renderer proxy)
            {
                try
                {
                    if (original == null || proxy == null) return;
                    if (_faceConfigs == null || _hairConfigs == null) return;

                    int originalId = original.GetInstanceID();

                    if (_faceConfigs.TryGetValue(originalId, out var faceConfig))
                    {
                        ApplyFace(proxy, faceConfig);
                    }

                    if (_hairConfigs.TryGetValue(originalId, out var hairConfig))
                    {
                        ApplyHair(proxy, hairConfig, originalId);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChimeraHairMaster] Error in fake shadow preview OnFrame: {ex}");
                }
            }

            /// <summary>顔プロキシ: 選択マテリアルをステンシル書き込みクローンに差し替える</summary>
            private void ApplyFace(Renderer proxy, ShadowConfig config)
            {
                var buffer = _materialBuffer;
                proxy.GetSharedMaterials(buffer);

                bool changed = false;
                foreach (var index in config.FaceMaterialIndexes)
                {
                    if (index < 0 || index >= buffer.Count) continue;
                    var material = buffer[index];
                    if (material == null || _ownedMaterialSet.Contains(material)) continue;
                    if (!material.HasProperty("_StencilRef")) continue;
                    // 既にステンシル書き込み済みのマテリアルは触らない（ビルドと同じ）
                    if (FakeShadowSetup.IsStencilWriter(material)) continue;

                    var key = (material, config.StencilRef);
                    if (!_faceClones.TryGetValue(key, out var clone))
                    {
                        // 既存ステンシル上書きの警告はビルド側で出す（プレビューはノード再生成のたびに
                        // 呼ばれるためログが流れすぎる）
                        clone = FakeShadowSetup.CreateStencilWriterFaceMaterial(
                            material, config.StencilRef, warnExistingStencilWrite: false);
                        clone.hideFlags = HideFlags.HideAndDontSave;
                        _faceClones[key] = clone;
                        _ownedMaterialSet.Add(clone);
                    }

                    buffer[index] = clone;
                    changed = true;
                }

                if (changed) proxy.sharedMaterials = buffer.ToArray();
            }

            /// <summary>
            /// 髪プロキシ: メッシュ末尾に影サブメッシュを追記し、追加スロットに影マテリアルを割り当てる。
            /// 髪本体のqueueが影以下の場合はクローンで引き上げる。
            /// 除外指定された髪は影サブメッシュを追記しない（queue引き上げのみ＝他の髪の影との整合）
            /// </summary>
            private void ApplyHair(Renderer proxy, ShadowConfig config, int originalId)
            {
                if (proxy is not SkinnedMeshRenderer smr) return;

                var currentMesh = smr.sharedMesh;
                if (currentMesh == null) return;

                bool castShadow = !config.ExcludedRendererIds.Contains(originalId);

                // 影サブメッシュの追記（上流フィルタの出力メッシュ単位でキャッシュ）
                if (castShadow && !_appendedMeshSet.Contains(currentMesh))
                {
                    if (!_appendedMeshes.TryGetValue(currentMesh, out var appended))
                    {
                        appended = FakeShadowSetup.AppendShadowSubmesh(
                            currentMesh, CollectShadowSubmeshIndices(smr, currentMesh));
                        appended.hideFlags = HideFlags.HideAndDontSave;
                        _appendedMeshes[currentMesh] = appended;
                        _appendedMeshSet.Add(appended);
                        _appendedMeshSources[appended.GetInstanceID()] = currentMesh.GetInstanceID();
                    }
                    smr.sharedMesh = appended;
                }

                // 影を落とす髪は追記版メッシュの末尾サブメッシュが影スロット。
                // 除外髪は影スロットなし（全スロットが髪本体扱い）
                int shadowSlot = castShadow ? smr.sharedMesh.subMeshCount - 1 : int.MaxValue;

                var buffer = _materialBuffer;
                proxy.GetSharedMaterials(buffer);
                bool changed = false;

                // 髪本体スロット: queue引き上げ
                int hairSlotCount = Mathf.Min(buffer.Count, shadowSlot);
                for (int i = 0; i < hairSlotCount; i++)
                {
                    var material = buffer[i];
                    if (material == null || material == config.ShadowMaterial) continue;
                    if (_ownedMaterialSet.Contains(material)) continue;
                    if (material.renderQueue > config.ShadowQueue) continue;

                    var key = (material, config.ShadowQueue + 1);
                    if (!_queueShifted.TryGetValue(key, out var shifted))
                    {
                        shifted = FakeShadowSetup.CreateQueueShiftedMaterial(material, config.ShadowQueue + 1);
                        shifted.hideFlags = HideFlags.HideAndDontSave;
                        _queueShifted[key] = shifted;
                        _ownedMaterialSet.Add(shifted);
                    }

                    buffer[i] = shifted;
                    changed = true;
                }

                // 影スロット: 影マテリアルを割り当て（除外髪はスロットなし）
                if (castShadow)
                {
                    if (buffer.Count <= shadowSlot)
                    {
                        while (buffer.Count < shadowSlot) buffer.Add(null!);
                        buffer.Add(config.ShadowMaterial);
                        changed = true;
                    }
                    else if (buffer[shadowSlot] != config.ShadowMaterial)
                    {
                        buffer[shadowSlot] = config.ShadowMaterial;
                        changed = true;
                    }
                }

                if (changed) proxy.sharedMaterials = buffer.ToArray();
            }

            /// <summary>影として描くサブメッシュ（非nullマテリアルのスロット）を収集する</summary>
            private static List<int> CollectShadowSubmeshIndices(SkinnedMeshRenderer smr, Mesh mesh)
            {
                var materials = smr.sharedMaterials;
                var result = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (s < materials.Length && materials[s] != null) result.Add(s);
                }
                return result;
            }

            public void Dispose()
            {
                foreach (var mesh in _appendedMeshes.Values)
                {
                    if (mesh != null)
                    {
                        _appendedMeshSources.Remove(mesh.GetInstanceID());
                        Object.DestroyImmediate(mesh);
                    }
                }
                _appendedMeshes.Clear();
                _appendedMeshSet.Clear();

                foreach (var material in _faceClones.Values)
                {
                    if (material != null) Object.DestroyImmediate(material);
                }
                _faceClones.Clear();

                foreach (var material in _queueShifted.Values)
                {
                    if (material != null) Object.DestroyImmediate(material);
                }
                _queueShifted.Clear();

                if (_ownedShadowMaterials != null)
                {
                    foreach (var material in _ownedShadowMaterials)
                    {
                        if (material != null) Object.DestroyImmediate(material);
                    }
                    _ownedShadowMaterials.Clear();
                }

                _ownedMaterialSet.Clear();
            }
        }
    }
}
