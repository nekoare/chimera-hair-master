#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// NDMFプレビューシステムを使用したScene上でのリアルタイムプレビュー
    ///
    /// プレビューでは色変換のみを適用し、アトラス化・UV変換はビルド時のみ行う
    /// </summary>
    internal class ChimeraHairMasterPreview : IRenderFilter
    {
        #region アトラスキャッシュ

        /// <summary>
        /// アトラス生成結果のキャッシュ（コンポーネントInstanceID → キャッシュエントリ）
        /// previewMaterial のプロパティ変更時にアトラス再生成をスキップするために使用
        /// </summary>
        private static readonly Dictionary<int, AtlasCacheEntry> _atlasCache = new();

        private class AtlasCacheEntry
        {
            public int LayoutHash;
            public int InputHash;
            public Dictionary<string, Texture2D> AtlasTextures = new();
            public Dictionary<int, Mesh> RemappedMeshes = new();

            /// <summary>
            /// アトラス生成をスキップしたためプレビューマテリアルで null クリアすべきプロパティ名
            /// （TextureAtlasBuilder.AtlasResult.ClearedProperties 由来。完全キャッシュヒット時にも必要）
            /// </summary>
            public List<string> ClearedProperties = new();

            /// <summary>
            /// メッシュが破棄されずに残っているか（破棄済み参照の再利用防止）
            /// </summary>
            public bool MeshesAlive
            {
                get
                {
                    foreach (var mesh in RemappedMeshes.Values)
                        if (mesh == null) return false;
                    return true;
                }
            }

            /// <summary>
            /// テクスチャ・メッシュが破棄されずに残っているか
            /// </summary>
            public bool IsAlive
            {
                get
                {
                    if (!MeshesAlive) return false;
                    foreach (var tex in AtlasTextures.Values)
                        if (tex == null) return false;
                    return true;
                }
            }

            public void DisposeTextures()
            {
                foreach (var tex in AtlasTextures.Values)
                    if (tex != null) Object.DestroyImmediate(tex);
                AtlasTextures.Clear();
            }

            public void Dispose()
            {
                DisposeTextures();

                foreach (var mesh in RemappedMeshes.Values)
                    if (mesh != null) Object.DestroyImmediate(mesh);
                RemappedMeshes.Clear();
            }
        }

        /// <summary>
        /// レイアウトに影響する入力のハッシュを計算
        /// UV配置・マテリアル選択・ソーステクスチャ等、メッシュUVリマップに影響するパラメータ
        /// </summary>
        private static int ComputeLayoutHash(ChimeraHairMaster component, int previewResolution)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + previewResolution;

                // materialSelections（統合対象の選択状態）
                if (component.materialSelections != null)
                {
                    foreach (var entry in component.materialSelections)
                    {
                        hash = hash * 31 + entry.rendererIndex;
                        hash = hash * 31 + entry.submeshIndex;
                        hash = hash * 31 + entry.isIncluded.GetHashCode();
                        hash = hash * 31 + (entry.meshCutMask != null ? entry.meshCutMask.GetInstanceID() : 0);
                    }
                }

                // islandPlacements（アイランド配置情報）
                if (component.islandPlacements != null)
                {
                    hash = hash * 31 + component.islandPlacements.Count;
                    foreach (var island in component.islandPlacements)
                    {
                        hash = hash * 31 + island.rendererIndex;
                        hash = hash * 31 + island.submeshIndex;
                        hash = hash * 31 + island.localIslandIndex;
                        hash = hash * 31 + island.originalBounds.GetHashCode();
                        hash = hash * 31 + island.atlasPosition.GetHashCode();
                        hash = hash * 31 + island.atlasScale.GetHashCode();
                    }
                }

                // メッシュ変形データ
                // MeshDeformPassがビルドパスとして処理するが、
                // ハッシュに含めることでデルタ変更時にプレビュー再構築をトリガーする
                if (component.enableMeshDeformation && component.rendererDeformations != null)
                {
                    hash = hash * 31 + component.enableMeshDeformation.GetHashCode();

                    // 編集中のRendererのデルタはハッシュから除外
                    // （編集中はプロキシ対象外なので再構築不要）
                    int editingIdx = component.deformEditingRendererIndex;
                    bool isActuallyEditing = editingIdx >= 0
                        && Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds.Count > 0;

                    foreach (var deformation in component.rendererDeformations)
                    {
                        if (isActuallyEditing && deformation.rendererIndex == editingIdx)
                            continue;

                        hash = hash * 31 + deformation.rendererIndex;
                        hash = hash * 31 + deformation.deltas.Count;
                        foreach (var delta in deformation.deltas)
                        {
                            hash = hash * 31 + delta.vertexIndex;
                            hash = hash * 31 + delta.offset.GetHashCode();
                        }
                    }
                }

                // ソーステクスチャの識別（InstanceID）
                for (int r = 0; r < component.targetRenderers.Count; r++)
                {
                    var renderer = component.targetRenderers[r];
                    if (renderer == null) continue;
                    var mats = renderer.sharedMaterials;
                    for (int s = 0; s < mats.Length; s++)
                    {
                        var mat = mats[s];
                        if (mat == null) continue;
                        if (!component.IsSubmeshIncluded(r, s)) continue;
                        hash = hash * 31 + mat.GetInstanceID();
                        if (component.colorChangeTargets != null)
                        {
                            foreach (var slot in component.colorChangeTargets)
                            {
                                if (mat.HasProperty(slot.propertyName))
                                {
                                    var tex = mat.GetTexture(slot.propertyName);
                                    hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                                }
                            }
                        }
                        // アトラスはフォールバックの _MainTex / _BumpMap / _ShadowStrengthMask も参照するため、
                        // これらを同一マテリアル上で差し替えてもプレビューが無効化されるよう hash に含める
                        foreach (var atlasProp in new[] { "_MainTex", "_BumpMap", "_ShadowStrengthMask" })
                        {
                            if (mat.HasProperty(atlasProp))
                            {
                                var t = mat.GetTexture(atlasProp);
                                hash = hash * 31 + (t != null ? t.GetInstanceID() : 0);
                            }
                        }
                    }
                }

                return hash;
            }
        }

        /// <summary>
        /// アトラス生成に影響する入力の完全ハッシュを計算（レイアウト + 色パラメータ）
        /// previewMaterialHash は含めない（マテリアルプロパティ変更時にキャッシュヒットさせるため）
        /// </summary>
        private static int ComputeAtlasInputHash(ChimeraHairMaster component, int previewResolution)
        {
            unchecked
            {
                int hash = ComputeLayoutHash(component, previewResolution);

                // AO/ノーマルアトラスの解像度設定（変更時にテクスチャ再生成をトリガー。
                // UVリマップには影響しないため LayoutHash ではなくこちらに含める）
                // ※ preserveAtlasAlpha は圧縮時のみ影響し、プレビューは非圧縮なので含めない
                hash = hash * 31 + (int)component.normalAtlasResolution;
                hash = hash * 31 + (int)component.aoAtlasResolution;

                hash = hash * 31 + component.enableColorTransform.GetHashCode();

                if (component.enableColorTransform)
                {
                    hash = hash * 31 + component.targetColor.GetHashCode();
                    hash = hash * 31 + component.colorTransformMode.GetHashCode();
                    hash = hash * 31 + component.hueShiftAlgorithm.GetHashCode();
                    hash = hash * 31 + component.saturationPreserve.GetHashCode();
                    hash = hash * 31 + component.valuePreserve.GetHashCode();
                    hash = hash * 31 + component.oklabHueRetain.GetHashCode();
                    hash = hash * 31 + component.oklabSaturationToTarget.GetHashCode();
                    hash = hash * 31 + component.oklabLToTarget.GetHashCode();
                    hash = hash * 31 + component.oklabLDarkEndRatio.GetHashCode();
                    hash = hash * 31 + component.rgbDeltaIntensity.GetHashCode();
                    hash = hash * 31 + component.rgbDeltaSoftClipZone.GetHashCode();
                    // gradientCurve: 全カラーキー（色+位置）+ 全アルファキー（値+位置）
                    if (component.gradientCurve != null)
                    {
                        var colorKeys = component.gradientCurve.colorKeys;
                        hash = hash * 31 + colorKeys.Length;
                        foreach (var ck in colorKeys)
                        {
                            hash = hash * 31 + ck.color.GetHashCode();
                            hash = hash * 31 + ck.time.GetHashCode();
                        }

                        var alphaKeys = component.gradientCurve.alphaKeys;
                        hash = hash * 31 + alphaKeys.Length;
                        foreach (var ak in alphaKeys)
                        {
                            hash = hash * 31 + ak.alpha.GetHashCode();
                            hash = hash * 31 + ak.time.GetHashCode();
                        }

                        hash = hash * 31 + (int)component.gradientCurve.mode;
                    }
                }

                // 明度調整
                if (component.rendererBrightnessAdjustments != null)
                {
                    foreach (var adj in component.rendererBrightnessAdjustments)
                    {
                        hash = hash * 31 + adj.rendererIndex;
                        hash = hash * 31 + adj.brightnessOffset.GetHashCode();
                    }
                }

                // ブラー／シャープ調整
                if (component.rendererBlurSharpAdjustments != null)
                {
                    foreach (var adj in component.rendererBlurSharpAdjustments)
                    {
                        hash = hash * 31 + adj.rendererIndex;
                        hash = hash * 31 + adj.blurSharp.GetHashCode();
                    }
                }

                // colorChangeTargets の設定
                if (component.colorChangeTargets != null)
                {
                    foreach (var slot in component.colorChangeTargets)
                    {
                        hash = hash * 31 + (slot.propertyName?.GetHashCode() ?? 0);
                        hash = hash * 31 + slot.applyColorChange.GetHashCode();
                    }
                }

                // colorMasks（色合わせ無視マスク）
                if (component.colorMasks != null)
                {
                    foreach (var entry in component.colorMasks)
                    {
                        hash = hash * 31 + entry.rendererIndex;
                        hash = hash * 31 + entry.submeshIndex;
                        hash = hash * 31 + (entry.mask != null ? entry.mask.GetInstanceID() : 0);
                    }
                    // 同一アセットへの上書き保存（塗り直し）は InstanceID が変わらないため内容ハッシュも見る。
                    // 再評価のトリガーは Inspector のポーリング（colorMaskContentsHash 更新）が担う
                    hash = hash * 31 + Processing.ColorMaskApplier.ComputeMaskContentsHash(component);
                }

                // 毛束パターン統一設定（有効時のみ内部パラメータを hash に寄与）
                if (component.strandPatternSettings != null)
                {
                    hash = hash * 31 + component.strandPatternSettings.enabled.GetHashCode();
                    if (component.strandPatternSettings.enabled)
                    {
                        hash = hash * 31 + component.strandPatternSettings.referenceRendererIndex;
                        hash = hash * 31 + component.strandPatternSettings.strengthFine.GetHashCode();
                        hash = hash * 31 + component.strandPatternSettings.strengthShade.GetHashCode();
                        hash = hash * 31 + component.strandPatternSettings.sigma.GetHashCode();
                    }
                }

                return hash;
            }
        }

        /// <summary>
        /// プレビューに影響するプロパティのハッシュを計算
        /// context.Observe で使用し、非プレビュープロパティ（bounds, probeAnchor等）の
        /// 変更による不要な再インスタンス化を防止する
        /// </summary>
        private static int ComputePreviewRelevantHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = ComputeAtlasInputHash(component, (int)component.previewResolution);
                hash = hash * 31 + component.enableMeshMerge.GetHashCode();
                hash = hash * 31 + component.unifyMatCap.GetHashCode();
                hash = hash * 31 + (component.baseMaterial != null ? component.baseMaterial.GetInstanceID() : 0);
                hash = hash * 31 + (component.previewMaterial != null ? component.previewMaterial.GetInstanceID() : 0);
                return hash;
            }
        }

        [InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload()
        {
            ClearAllAtlasCache();
            ClearAllColorTransformCache();

            // リロード後は dict が空になり上記クリアは生成済みリソースを破棄できないため、
            // リロード前（参照がまだ生きている時点）にも破棄する
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // シーン切替時にもキャッシュをクリア（InstanceIDが変わるため）
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing -= OnSceneClosing;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing += OnSceneClosing;
        }

        private static void OnBeforeAssemblyReload()
        {
            ClearAllAtlasCache();
            ClearAllColorTransformCache();
            ClearDominantColorCache();
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode)
        {
            ClearAllAtlasCache();
            ClearAllColorTransformCache();
            ClearDominantColorCache();
        }

        private static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            ClearAllAtlasCache();
            ClearAllColorTransformCache();
            ClearDominantColorCache();
        }

        private static void ClearAllAtlasCache()
        {
            foreach (var entry in _atlasCache.Values)
                entry.Dispose();
            _atlasCache.Clear();
        }

        #endregion

        #region 色変換テクスチャキャッシュ（enableMeshMerge=false用）

        /// <summary>
        /// 色変換済みテクスチャのキャッシュ（コンポーネントInstanceID → キャッシュエントリ）
        /// enableMeshMerge=false 時の ProcessComponent で使用
        /// previewMaterialHash 変更時（lilToon編集時）に色変換のGPU処理をスキップするために使用
        /// </summary>
        private static readonly Dictionary<int, ColorCacheEntry> _colorTransformCache = new();

        private class ColorCacheEntry
        {
            public int ColorHash;
            /// <summary>
            /// 元テクスチャ → 色変換済みテクスチャのマッピング
            /// キャッシュが所有し、PreviewNodeのDispose対象外
            /// </summary>
            public Dictionary<Texture2D, Texture2D> TransformedTextures = new();

            /// <summary>
            /// テクスチャが破棄されずに残っているか（破棄済み参照の再利用防止）
            /// </summary>
            public bool IsAlive
            {
                get
                {
                    foreach (var tex in TransformedTextures.Values)
                        if (tex == null) return false;
                    return true;
                }
            }

            public void Dispose()
            {
                foreach (var tex in TransformedTextures.Values)
                    if (tex != null) Object.DestroyImmediate(tex);
                TransformedTextures.Clear();
            }
        }

        /// <summary>
        /// 色変換に影響するパラメータのハッシュを計算（enableMeshMerge=false用）
        /// </summary>
        private static int ComputeColorHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + component.enableColorTransform.GetHashCode();

                if (component.enableColorTransform)
                {
                    hash = hash * 31 + component.targetColor.GetHashCode();
                    hash = hash * 31 + component.colorTransformMode.GetHashCode();
                    hash = hash * 31 + component.hueShiftAlgorithm.GetHashCode();
                    hash = hash * 31 + component.saturationPreserve.GetHashCode();
                    hash = hash * 31 + component.valuePreserve.GetHashCode();
                    hash = hash * 31 + component.oklabHueRetain.GetHashCode();
                    hash = hash * 31 + component.oklabSaturationToTarget.GetHashCode();
                    hash = hash * 31 + component.oklabLToTarget.GetHashCode();
                    hash = hash * 31 + component.oklabLDarkEndRatio.GetHashCode();
                    hash = hash * 31 + component.rgbDeltaIntensity.GetHashCode();
                    hash = hash * 31 + component.rgbDeltaSoftClipZone.GetHashCode();

                    if (component.gradientCurve != null)
                    {
                        var colorKeys = component.gradientCurve.colorKeys;
                        hash = hash * 31 + colorKeys.Length;
                        foreach (var ck in colorKeys)
                        {
                            hash = hash * 31 + ck.color.GetHashCode();
                            hash = hash * 31 + ck.time.GetHashCode();
                        }

                        var alphaKeys = component.gradientCurve.alphaKeys;
                        hash = hash * 31 + alphaKeys.Length;
                        foreach (var ak in alphaKeys)
                        {
                            hash = hash * 31 + ak.alpha.GetHashCode();
                            hash = hash * 31 + ak.time.GetHashCode();
                        }

                        hash = hash * 31 + (int)component.gradientCurve.mode;
                    }
                }

                // colorChangeTargets
                if (component.colorChangeTargets != null)
                {
                    foreach (var slot in component.colorChangeTargets)
                    {
                        hash = hash * 31 + (slot.propertyName?.GetHashCode() ?? 0);
                        hash = hash * 31 + slot.applyColorChange.GetHashCode();
                    }
                }

                // ブラー／シャープ調整（色変換の前処理なので色変換結果そのものが変わる）
                if (component.rendererBlurSharpAdjustments != null)
                {
                    foreach (var adj in component.rendererBlurSharpAdjustments)
                    {
                        hash = hash * 31 + adj.rendererIndex;
                        hash = hash * 31 + adj.blurSharp.GetHashCode();
                    }
                }

                // ソーステクスチャの識別（同じ色設定でもテクスチャが変わればキャッシュ無効）
                for (int r = 0; r < component.targetRenderers.Count; r++)
                {
                    var renderer = component.targetRenderers[r];
                    if (renderer == null) continue;
                    var mats = renderer.sharedMaterials;
                    for (int s = 0; s < mats.Length; s++)
                    {
                        var mat = mats[s];
                        if (mat == null) continue;
                        if (!component.IsSubmeshIncluded(r, s)) continue;

                        if (component.colorChangeTargets != null)
                        {
                            foreach (var slot in component.colorChangeTargets)
                            {
                                if (mat.HasProperty(slot.propertyName))
                                {
                                    var tex = mat.GetTexture(slot.propertyName);
                                    hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                                }
                            }
                        }
                        if (mat.HasProperty("_MainTex"))
                        {
                            var tex = mat.GetTexture("_MainTex");
                            hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                        }
                    }
                }

                return hash;
            }
        }

        private static void ClearAllColorTransformCache()
        {
            foreach (var entry in _colorTransformCache.Values)
                entry.Dispose();
            _colorTransformCache.Clear();
        }

        #endregion

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var avatars = context.GetAvatarRoots();
            var resultSet = new List<RenderGroup>();

            foreach (var avatar in avatars)
            {
                try
                {
                    // アバター内のChimeraHairMasterコンポーネントを取得
                    var components = context.GetComponentsInChildren<ChimeraHairMaster>(avatar, true);
                    if (!components.Any()) continue;

                    // 有効なコンポーネントのみ（enableColorTransformも監視）
                    var enabledComponents = components
                        .Where(c => context.Observe(c, x => x.isEnabled && x.previewEnabled))
                        .Select(c => {
                            // enableColorTransformの変更も監視（プレビュー更新のため）
                            context.Observe(c, x => x.enableColorTransform);
                            // 明度調整リストの変更も監視
                            // （List 参照をそのまま返すと参照等価で「変化なし」になり
                            // in-place の Add/Remove/値変更を検知できないため、内容ハッシュで比較）
                            context.Observe(c, x => ComputeBrightnessAdjustmentsHash(x));
                            // ブラー／シャープ調整リストの変更も監視
                            context.Observe(c, x => ComputeBlurSharpAdjustmentsHash(x));
                            // プレビュー解像度の変更も監視
                            context.Observe(c, x => x.previewResolution);
                            return c;
                        })
                        .ToArray();
                    if (!enabledComponents.Any()) continue;

                    // 対象のRendererを収集
                    // メッシュ変形編集中のRendererのみプロキシ対象から除外する
                    var editingIds = Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds;
                    var targetRenderers = new HashSet<Renderer>();
                    foreach (var component in enabledComponents)
                    {
                        // deformEditingRendererIndexを監視して編集開始/終了でリビルドをトリガー
                        context.Observe(component, c => c.deformEditingRendererIndex);

                        // List 参照は参照等価で比較されるため、in-place の Add/Remove では
                        // invalidate されず RenderGroup 構成が更新されない。
                        // コピーを返してシーケンス比較することで内容変更を検知させる
                        var renderers = context.Observe(component,
                            c => c.targetRenderers?.ToList(),
                            (a, b) => ReferenceEquals(a, b)
                                      || (a != null && b != null && a.SequenceEqual(b)));
                        if (renderers == null) continue;

                        foreach (var renderer in renderers)
                        {
                            if (renderer == null) continue;
                            // 編集中のRendererだけスキップ（他のRendererはプレビューを通す）
                            if (editingIds.Contains(renderer.GetInstanceID())) continue;
                            targetRenderers.Add(renderer);
                        }
                    }

                    if (targetRenderers.Count > 0)
                    {
                        resultSet.Add(RenderGroup.For(targetRenderers).WithData((avatar, enabledComponents)));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChimeraHairMaster] Failed to get target groups: {ex}");
                }
            }

            return resultSet.ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            try
            {
                var renderData = group.GetData<(GameObject, ChimeraHairMaster[])>();
                var avatar = renderData.Item1;
                var components = renderData.Item2;

                // プレビュー関連プロパティのみ監視
                // meshMergeMode, probeAnchor, bounds等の非プレビュープロパティは除外
                foreach (var component in components)
                {
                    context.Observe(component, c => ComputePreviewRelevantHash(c));
                    // previewMaterialHashを明示的に監視（マテリアル内部変更検知用）
                    var hash = context.Observe(component, c => c.previewMaterialHash);
                }

                // 有効なコンポーネントをフィルタ
                var enabledComponents = components
                    .Where(c => c != null && c.isEnabled && c.previewEnabled)
                    .ToArray();

                if (!enabledComponents.Any())
                {
                    return Task.FromResult<IRenderFilterNode>(new PreviewNode(null, null));
                }

                // 生成されたリソースを保持
                var generatedTextures = new List<Texture>();
                var processedMaterials = new Dictionary<Material, Material>();
                Dictionary<int, Mesh>? remappedMeshes = null;
                // このノードが所有する（Disposeで破棄すべき）メッシュ
                // アトラスキャッシュ所有のメッシュは含めない
                List<Mesh>? ownedMeshes = null;

                // 各コンポーネントを処理
                foreach (var component in enabledComponents)
                {
                    // メッシュ統合無効時は常に従来モード（アトラス不要）
                    if (!component.enableMeshMerge)
                    {
                        ProcessComponent(component, processedMaterials, generatedTextures);
                    }
                    // islandPlacements がある場合はアトラスモード
                    else if (component.islandPlacements != null && component.islandPlacements.Count > 0)
                    {
                        remappedMeshes ??= new Dictionary<int, Mesh>();
                        ProcessComponentAtlas(component, processedMaterials, generatedTextures, remappedMeshes);
                    }
                    else
                    {
                        // 従来モード（islandPlacements 未設定時のフォールバック）
                        ProcessComponent(component, processedMaterials, generatedTextures);
                    }
                }

                // この時点で remappedMeshes に入っているのはアトラスUVへリマップされた
                // Renderer だけ。以降の変形ループは元UVのまま頂点だけ動かしたメッシュを足すので、
                // 「UVがアトラス空間にあるか」を後で per-Renderer に判定するためここで確定させる。
                var atlasRemappedIds = remappedMeshes != null
                    ? new HashSet<int>(remappedMeshes.Keys)
                    : new HashSet<int>();

                // メッシュ変形プレビュー
                // MeshDeformPassがビルドパスとして先に実行される場合があるため、
                // renderer.sharedMeshがアセットかどうかで適用済みを判定し二重適用を防ぐ
                foreach (var component in enabledComponents)
                {
                    if (!component.enableMeshDeformation) continue;
                    if (component.rendererDeformations == null) continue;

                    var editingIds = Deformation.MeshDeformationSceneEditor.ActiveEditingRendererIds;

                    foreach (var deformation in component.rendererDeformations)
                    {
                        if (deformation.deltas == null || deformation.deltas.Count == 0) continue;
                        if (deformation.rendererIndex < 0 || deformation.rendererIndex >= component.targetRenderers.Count) continue;

                        var renderer = component.targetRenderers[deformation.rendererIndex];
                        if (renderer == null || renderer.sharedMesh == null) continue;
                        if (renderer.sharedMesh.vertexCount != deformation.expectedVertexCount) continue;

                        int rendererId = renderer.GetInstanceID();
                        bool isAsset = AssetDatabase.Contains(renderer.sharedMesh);
                        bool isEditing = editingIds.Contains(rendererId);
                        bool hasRemapped = remappedMeshes != null && remappedMeshes.ContainsKey(rendererId);

                        // 編集中のRendererはスキップ（Scene Editorが直接操作中）
                        if (isEditing) continue;

                        // MeshDeformPassが既にメッシュを差し替え済みの場合スキップ
                        // （MeshDeformPassは差し替えたメッシュに "_Deformed" サフィックスを付ける）
                        // !isAsset だけでは Modular Avatar 等の他プラグインの Instantiate と区別できないため、
                        // 名前でも判定する
                        bool alreadyDeformedByPass = !isAsset && !hasRemapped
                            && renderer.sharedMesh.name.EndsWith("_Deformed");
                        if (alreadyDeformedByPass) continue;

                        remappedMeshes ??= new Dictionary<int, Mesh>();

                        Mesh baseMesh;
                        if (remappedMeshes.TryGetValue(rendererId, out var existingMesh))
                        {
                            // キャッシュのメッシュを直接変更するとデルタが蓄積するため、
                            // 必ずコピーしてから変形する
                            baseMesh = Object.Instantiate(existingMesh);
                            baseMesh.name = existingMesh.name + "_Deformed";
                        }
                        else
                        {
                            baseMesh = Object.Instantiate(renderer.sharedMesh);
                            baseMesh.name = renderer.sharedMesh.name + "_DeformPreview";
                        }
                        baseMesh.hideFlags = HideFlags.HideAndDontSave;

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
                        // 変形用に Instantiate したメッシュはキャッシュ所有ではないため、
                        // このノードの破棄対象として記録する
                        ownedMeshes ??= new List<Mesh>();
                        ownedMeshes.Add(baseMesh);
                    }
                }

                if (processedMaterials.Count == 0 && (remappedMeshes == null || remappedMeshes.Count == 0))
                {
                    return Task.FromResult<IRenderFilterNode>(new PreviewNode(null, null));
                }

                // ObjectRegistryに登録（Color-Changerと同様）
                foreach (var kvp in processedMaterials)
                {
                    ObjectRegistry.RegisterReplacedObject(kvp.Key, kvp.Value);
                }

                // アトラスモードのメッシュはキャッシュが所有するため、PreviewNodeでは破棄しない
                // 変形用に Instantiate したメッシュ（ownedMeshes）のみ PreviewNode が破棄する
                return Task.FromResult<IRenderFilterNode>(new PreviewNode(processedMaterials, generatedTextures, remappedMeshes, ownedMeshes, atlasRemappedIds));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Failed to instantiate preview: {ex}");
                return Task.FromResult<IRenderFilterNode>(new PreviewNode(null, null));
            }
        }

        private void ProcessComponent(
            ChimeraHairMaster component,
            Dictionary<Material, Material> processedMaterials,
            List<Texture> generatedTextures)
        {
            if (component.targetRenderers == null || component.targetRenderers.Count == 0) return;

            // try 内で生成し finally で解放するリソース
            Material? baseMaterialForPreview = null;
            bool autoBaseMaterial = false;
            Dictionary<Texture2D, Texture2D>? newColorCacheTextures = null;
            bool colorCacheSaved = false;
            Processing.StrandPatternApplier.RefData? strandRef = null;

            try
            {
                // previewMaterialがある場合は、それをベースにして色変換テクスチャを適用
                // previewMaterialがなく、baseMaterialがある場合は自動生成
                baseMaterialForPreview = component.previewMaterial;

                if (baseMaterialForPreview == null && component.baseMaterial != null)
                {
                    // previewMaterialを自動生成
                    baseMaterialForPreview = CreateMaterialWithoutTextures(component.baseMaterial);
                    baseMaterialForPreview.name = component.baseMaterial.name + "_CHM_Preview_Auto";
                    CopyMatCapTextures(component.baseMaterial, baseMaterialForPreview);
                    autoBaseMaterial = true;

                    CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: プレビュー用マテリアルを自動生成しました。");
                }

                // baseMaterialもpreviewMaterialもない場合
                if (baseMaterialForPreview == null)
                {
                    if (component.enableMeshMerge)
                    {
                        Debug.LogWarning($"[ChimeraHairMaster] {component.name}: 基準マテリアルが設定されていません。プレビューをスキップします。");
                        return;
                    }
                    // enableMeshMerge=false の場合は baseMaterial なしでも続行可能（元マテリアルベース）
                }

                // 色合わせが無効な場合は色変換をスキップ
                bool enableColorTransform = component.enableColorTransform;

                // 色変換テクスチャキャッシュ（色パラメータ変更時のみ再実行、lilToon編集時はスキップ）
                int componentId = component.GetInstanceID();
                int colorHash = enableColorTransform ? ComputeColorHash(component) : 0;
                bool colorCacheHit = false;
                Dictionary<Texture2D, Texture2D>? cachedColorTransforms = null;

                // 色変換設定を取得（色合わせが有効な場合のみ使用）
                Processing.ColorTransformSettings? settings = null;

                if (enableColorTransform)
                {
                    // キャッシュチェック
                    colorCacheHit = _colorTransformCache.TryGetValue(componentId, out var colorCache)
                                    && colorCache != null && colorCache.ColorHash == colorHash
                                    && colorCache.IsAlive;

                    if (colorCacheHit)
                    {
                        // キャッシュヒット: 色変換済みテクスチャを再利用（GPU処理スキップ）
                        cachedColorTransforms = colorCache!.TransformedTextures;
                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: 色変換キャッシュヒット（GPU処理スキップ）");
                    }
                    else
                    {
                        // キャッシュミス: 色変換設定を準備
                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: 色変換キャッシュミス（色変換実行）");

                        Color sourceColor = Color.white;
                        if (component.targetRenderers.Count > 0 && component.targetRenderers[0] != null)
                        {
                            sourceColor = GetDominantColorFromRenderer(component.targetRenderers[0]);
                        }

                        settings = Processing.ColorTransformSettings.FromComponent(component, sourceColor);

                        // 旧キャッシュを破棄
                        // Dispose だけして dict に残すと、新エントリ保存前に例外が出た場合に
                        // 「旧ハッシュ + 破棄済みテクスチャ」のエントリが偽キャッシュヒットするため、必ず Remove する
                        if (_colorTransformCache.TryGetValue(componentId, out var oldCache))
                        {
                            oldCache.Dispose();
                            _colorTransformCache.Remove(componentId);
                        }

                        newColorCacheTextures = new Dictionary<Texture2D, Texture2D>();
                    }
                }

                // 毛束パターン: お手本 Renderer の参照データを事前計算
                // 色変換キャッシュ (cachedColorTransforms = 直前 repaint の cache hit) を渡せば、
                // お手本に blur/sharp がない場合は color transform の再実行を省略できる
                if (enableColorTransform)
                {
                    // settings が null の cache hit ケースでは PrepareRefData 用に再構築
                    var refSettings = settings;
                    if (refSettings == null)
                    {
                        Color srcColor = component.targetRenderers.Count > 0 && component.targetRenderers[0] != null
                            ? GetDominantColorFromRenderer(component.targetRenderers[0])
                            : Color.white;
                        refSettings = Processing.ColorTransformSettings.FromComponent(component, srcColor);
                    }
                    strandRef = Processing.StrandPatternApplier.PrepareRefData(component, refSettings, null, cachedColorTransforms);
                }

                // 各Rendererのマテリアルを処理
                for (int rendererIndex = 0; rendererIndex < component.targetRenderers.Count; rendererIndex++)
                {
                    var renderer = component.targetRenderers[rendererIndex];
                    if (renderer == null) continue;

                    // Renderer単位の明度オフセットとブラー／シャープ強度を取得
                    float brightnessOffset = GetRendererBrightnessOffset(component, rendererIndex);
                    float blurSharp = GetRendererBlurSharp(component, rendererIndex);

                    var materials = renderer.sharedMaterials;
                    for (int submeshIndex = 0; submeshIndex < materials.Length; submeshIndex++)
                    {
                        var mat = materials[submeshIndex];

                        // 統合対象外のサブメッシュはスキップ
                        if (!component.IsSubmeshIncluded(rendererIndex, submeshIndex))
                        {
                            continue;
                        }
                    {
                        if (mat == null) continue;

                        // 明度オフセットまたは色合わせ無視マスクがある場合はサブメッシュ固有キーを使用
                        var colorMask = component.GetColorMask(rendererIndex, submeshIndex);
                        var materialKey = (Mathf.Abs(brightnessOffset) > 0.001f || colorMask != null)
                            ? null  // オフセットまたはマスクがある場合は常に新規作成
                            : mat;

                        if (materialKey != null && processedMaterials.ContainsKey(materialKey)) continue;

                        Material newMat;

                        if (!component.enableMeshMerge)
                        {
                            // メッシュ統合なし: 元マテリアルコピー（テクスチャ全保持）+ 数値設定のみ上書き
                            newMat = new Material(mat);
                            newMat.hideFlags = HideFlags.HideAndDontSave;
                            newMat.name = mat.name + "_CHM_Preview";
                            if (baseMaterialForPreview != null)
                            {
                                // 輪郭線はシェーダ切替（Outline版シェーダの有無）で表現されるため、
                                // 数値コピー前に previewMaterial の輪郭線ON/OFFへシェーダを揃える
                                TextureAtlasPass.SyncOutlineVariant(baseMaterialForPreview, newMat);
                                TextureAtlasPass.ApplyShaderSettings(baseMaterialForPreview, newMat,
                                    excludeOverlayAndEmission: true,
                                    excludeMatCap: !component.unifyMatCap);

                                // マットキャップ統一時はテクスチャもbaseMaterialから上書き（Noneも含む）
                                if (component.unifyMatCap)
                                {
                                    TextureAtlasPass.OverwriteMatCapTextures(baseMaterialForPreview, newMat);
                                }
                            }
                        }
                        else
                        {
                            // 通常モード: プレビューマテリアルの完全コピー
                            // 色変換処理が後続で対象スロットを上書きする
                            if (baseMaterialForPreview == null) continue;
                            newMat = new Material(baseMaterialForPreview);
                            newMat.hideFlags = HideFlags.HideAndDontSave;
                            newMat.name = baseMaterialForPreview.name + "_Preview";
                        }

                        // 色合わせが無効な場合は元のテクスチャをそのまま設定
                        if (!enableColorTransform)
                        {
                            // 各テクスチャスロットに元のテクスチャを設定
                            foreach (var slot in component.colorChangeTargets)
                            {
                                if (!mat.HasProperty(slot.propertyName)) continue;

                                var tex = mat.GetTexture(slot.propertyName);
                                if (tex != null)
                                {
                                    newMat.SetTexture(slot.propertyName, tex);
                                }
                            }

                            // メインテクスチャも設定
                            if (mat.HasProperty("_MainTex"))
                            {
                                var tex = mat.GetTexture("_MainTex");
                                if (tex != null)
                                {
                                    newMat.SetTexture("_MainTex", tex);
                                }
                            }

                            processedMaterials[mat] = newMat;
                            continue;
                        }

                        // 各テクスチャスロットに対して色変換を適用（キャッシュ対応）
                        bool hasProcessedTexture = false;
                        foreach (var slot in component.colorChangeTargets)
                        {
                            if (!mat.HasProperty(slot.propertyName)) continue;
                            if (!slot.applyColorChange) continue;

                            var tex = mat.GetTexture(slot.propertyName) as Texture2D;
                            if (tex == null) continue;

                            // キャッシュまたは新規変換で色変換済みテクスチャを取得
                            Texture2D? baseTex = GetOrCreateColorTransformedTexture(
                                tex, cachedColorTransforms, newColorCacheTextures, settings,
                                renderer, new[] { submeshIndex }, blurSharp);
                            if (baseTex == null) continue;

                            Texture2D currentTex = baseTex;

                            // 明度オフセットを適用
                            if (Mathf.Abs(brightnessOffset) > 0.001f)
                            {
                                var offsetApplied = Processing.ColorProcessor.ApplyBrightnessOffset(baseTex, brightnessOffset, compressResult: false);
                                if (offsetApplied != null)
                                {
                                    currentTex = offsetApplied;
                                    generatedTextures.Add(offsetApplied);
                                }
                            }

                            // 色合わせ無視マスクを適用
                            {
                                var masked = Processing.ColorMaskApplier.TryApply(component, rendererIndex, submeshIndex, tex, currentTex, compressResult: false);
                                if (masked != null)
                                {
                                    currentTex = masked;
                                    generatedTextures.Add(masked);
                                }
                            }

                            // 毛束パターン: MainTex スロットかつお手本 Renderer 以外に適用
                            if (strandRef != null && strandRef.IsValid && slot.propertyName == "_MainTex"
                                && rendererIndex != strandRef.RefIndex)
                            {
                                var composed = Processing.StrandPatternApplier.TryComposeStrand(
                                    currentTex, renderer, new[] { submeshIndex }, strandRef, compressResult: false);
                                if (composed != null)
                                {
                                    currentTex = composed;
                                    generatedTextures.Add(composed);
                                }
                            }

                            newMat.SetTexture(slot.propertyName, currentTex);
                            hasProcessedTexture = true;
                        }

                        // メインテクスチャがcolorChangeTargetsに含まれていない場合も処理
                        if (!hasProcessedTexture && mat.HasProperty("_MainTex") && !component.IsColorChangeExplicitlyDisabled("_MainTex"))
                        {
                            var tex = mat.GetTexture("_MainTex") as Texture2D;
                            if (tex != null)
                            {
                                Texture2D? baseTex = GetOrCreateColorTransformedTexture(
                                    tex, cachedColorTransforms, newColorCacheTextures, settings,
                                    renderer, new[] { submeshIndex }, blurSharp);

                                if (baseTex != null)
                                {
                                    Texture2D currentTex = baseTex;

                                    if (Mathf.Abs(brightnessOffset) > 0.001f)
                                    {
                                        var offsetApplied = Processing.ColorProcessor.ApplyBrightnessOffset(baseTex, brightnessOffset, compressResult: false);
                                        if (offsetApplied != null)
                                        {
                                            currentTex = offsetApplied;
                                            generatedTextures.Add(offsetApplied);
                                        }
                                    }

                                    // 色合わせ無視マスクを適用
                                    {
                                        var masked = Processing.ColorMaskApplier.TryApply(component, rendererIndex, submeshIndex, tex, currentTex, compressResult: false);
                                        if (masked != null)
                                        {
                                            currentTex = masked;
                                            generatedTextures.Add(masked);
                                        }
                                    }

                                    // 毛束パターン: お手本 Renderer 以外に適用
                                    if (strandRef != null && strandRef.IsValid && rendererIndex != strandRef.RefIndex)
                                    {
                                        var composed = Processing.StrandPatternApplier.TryComposeStrand(
                                            currentTex, renderer, new[] { submeshIndex }, strandRef, compressResult: false);
                                        if (composed != null)
                                        {
                                            currentTex = composed;
                                            generatedTextures.Add(composed);
                                        }
                                    }

                                    newMat.SetTexture("_MainTex", currentTex);
                                }
                            }
                        }

                        processedMaterials[mat] = newMat;
                    }
                }
            }

                // 色変換キャッシュの保存とクリーンアップ
                if (newColorCacheTextures != null)
                {
                    _colorTransformCache[componentId] = new ColorCacheEntry
                    {
                        ColorHash = colorHash,
                        TransformedTextures = newColorCacheTextures
                    };
                    colorCacheSaved = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Failed to process component: {ex}");
            }
            finally
            {
                // 毛束パターン: reference detail を破棄（例外時も含めて必ず解放）
                strandRef?.Dispose();

                // キャッシュ保存に到達しなかった場合、構築途中の色変換テクスチャを破棄
                if (!colorCacheSaved && newColorCacheTextures != null)
                {
                    foreach (var tex in newColorCacheTextures.Values)
                        if (tex != null) Object.DestroyImmediate(tex);
                }

                // 自動生成したプレビュー用ベースマテリアルはテンプレートとしてのみ使用するため破棄
                if (autoBaseMaterial && baseMaterialForPreview != null)
                    Object.DestroyImmediate(baseMaterialForPreview);
            }
        }

        /// <summary>
        /// 色変換済みテクスチャをキャッシュから取得、またはキャッシュミス時に新規生成してキャッシュに保存
        /// </summary>
        private static Texture2D? GetOrCreateColorTransformedTexture(
            Texture2D originalTex,
            Dictionary<Texture2D, Texture2D>? cachedTransforms,
            Dictionary<Texture2D, Texture2D>? newCacheTextures,
            Processing.ColorTransformSettings? settings,
            SkinnedMeshRenderer? renderer = null,
            IReadOnlyCollection<int>? submeshIndices = null,
            float blurSharp = 0f)
        {
            // キャッシュヒット: 既存の変換済みテクスチャを返す
            // blurSharp は colorHash に含まれているので、変更時はキャッシュ全体が無効化される
            // 同じ colorHash 内で異なる blurSharp が混在すると先勝ちになるが、実用上は稀
            if (cachedTransforms != null && cachedTransforms.TryGetValue(originalTex, out var cached))
            {
                return cached;
            }

            // キャッシュミス時に新規テクスチャも構築中: 既に変換済みならそれを返す
            if (newCacheTextures != null && newCacheTextures.TryGetValue(originalTex, out var alreadyProcessed))
            {
                return alreadyProcessed;
            }

            // 色変換を実行
            if (settings == null) return null;

            // テクスチャ毎の Oklab/RGBDelta 事前計算（Renderer/Submesh 情報があれば）
            // ※ 代表色抽出は元テクスチャから（ブラー/シャープ前）
            var perTextureSettings = renderer != null && submeshIndices != null
                ? Processing.MeshUVSampler.PrepareSettingsWithUVStats(settings, renderer, submeshIndices, originalTex)
                : settings;

            // UV マスク（ブラー/シャープ前処理と dilation で共用）
            bool[]? uvMask = null;
            if (renderer != null && submeshIndices != null)
            {
                uvMask = Processing.MeshUVRasterizer.Rasterize(
                    renderer, submeshIndices, originalTex.width, originalTex.height);
            }

            // 色変換の入力（ブラー/シャープ前処理）
            Texture2D colorTransformInput = originalTex;
            Texture2D? preprocessed = null;
            if (Mathf.Abs(blurSharp) > 0.001f)
            {
                preprocessed = Processing.TextureBlurSharpener.Process(originalTex, blurSharp, uvMask);
                if (preprocessed != null) colorTransformInput = preprocessed;
            }

            var processedTex = Processing.ColorProcessor.ProcessTexture(colorTransformInput, perTextureSettings, true, compressResult: false);

            if (preprocessed != null) Object.DestroyImmediate(preprocessed);

            // UV 使用領域外を edge dilation で塗り足し
            if (processedTex != null && uvMask != null)
            {
                var dilated = Processing.ColorProcessor.DilateTexture(processedTex, uvMask, 8, compressResult: false);
                if (dilated != null)
                {
                    Object.DestroyImmediate(processedTex);
                    processedTex = dilated;
                }
            }

            if (processedTex != null && newCacheTextures != null)
            {
                newCacheTextures[originalTex] = processedTex;
            }

            return processedTex;
        }

        /// <summary>
        /// アトラスモードでコンポーネントを処理
        /// ビルドパイプライン（ColorTransformPass + TextureAtlasPass）と同じフローを再現:
        /// 1. 色変換テクスチャキャッシュを構築
        /// 2. TextureAtlasBuilder でアトラス生成
        /// 3. アトラスマテリアルを作成
        /// 4. メッシュ UV をリマップ
        ///
        /// パフォーマンス最適化:
        /// - アトラス生成結果を静的キャッシュに保持
        /// - previewMaterial のプロパティ変更時はマテリアル再作成のみ（アトラス再生成スキップ）
        /// </summary>
        private void ProcessComponentAtlas(
            ChimeraHairMaster component,
            Dictionary<Material, Material> processedMaterials,
            List<Texture> generatedTextures,
            Dictionary<int, Mesh> remappedMeshes)
        {
            if (component.targetRenderers == null || component.targetRenderers.Count == 0) return;

            Material? baseMaterialForPreview = null;
            bool autoBaseMaterial = false;

            try
            {
                baseMaterialForPreview = component.previewMaterial;

                if (baseMaterialForPreview == null && component.baseMaterial != null)
                {
                    baseMaterialForPreview = CreateMaterialWithoutTextures(component.baseMaterial);
                    baseMaterialForPreview.name = component.baseMaterial.name + "_CHM_Preview_Auto";
                    CopyMatCapTextures(component.baseMaterial, baseMaterialForPreview);
                    autoBaseMaterial = true;
                }

                if (baseMaterialForPreview == null)
                {
                    Debug.LogWarning($"[ChimeraHairMaster] {component.name}: 基準マテリアルが設定されていません。アトラスプレビューをスキップします。");
                    return;
                }

                int resolution = (int)component.previewResolution;
                int componentId = component.GetInstanceID();
                int layoutHash = ComputeLayoutHash(component, resolution);
                int inputHash = ComputeAtlasInputHash(component, resolution);

                // 3段階キャッシュチェック
                // ハッシュ一致でもテクスチャ/メッシュが破棄済み（UnloadUnusedAssets等）の場合はヒット扱いにしない
                bool hasCache = _atlasCache.TryGetValue(componentId, out var cachedEntry) && cachedEntry != null;
                bool fullCacheHit = hasCache && cachedEntry!.InputHash == inputHash && cachedEntry.IsAlive;
                bool layoutCacheHit = hasCache && !fullCacheHit && cachedEntry!.LayoutHash == layoutHash && cachedEntry.MeshesAlive;

                Dictionary<string, Texture2D> atlasTextures;
                Dictionary<int, Mesh> cachedMeshes;
                List<string> clearedProperties;

                if (fullCacheHit)
                {
                    // 完全キャッシュヒット: アトラス生成と UV リマップをスキップ
                    CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: アトラスキャッシュヒット（マテリアル再作成のみ）");
                    atlasTextures = cachedEntry!.AtlasTextures;
                    cachedMeshes = cachedEntry.RemappedMeshes;
                    clearedProperties = cachedEntry.ClearedProperties;
                }
                else
                {
                    // テクスチャ再生成が必要（レイアウトキャッシュヒットまたは完全ミス）
                    bool needUVRemap = !layoutCacheHit;

                    if (layoutCacheHit)
                    {
                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: レイアウトキャッシュヒット（テクスチャのみ再生成、UVリマップスキップ）");
                        // テクスチャのみ破棄（メッシュは再利用）
                        cachedEntry!.DisposeTextures();
                    }
                    else
                    {
                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: アトラスキャッシュミス（フル生成）");
                        // 旧キャッシュを完全破棄
                        if (hasCache)
                        {
                            cachedEntry!.Dispose();
                            _atlasCache.Remove(componentId);
                        }
                    }

                    // 1. 色変換テクスチャキャッシュを構築（ビルドの ColorTransformPass 相当）
                    var colorTransformTextures = new List<Texture>();
                    Dictionary<Texture2D, Texture2D>? processedTextureCache = null;
                    // renderer×submesh 固有の処理結果（strand 等）。共有 cache より優先して参照される
                    Dictionary<(int rendererIndex, int submeshIndex, Texture2D texture), Texture2D>? perIslandTextureCache = null;
                    if (component.enableColorTransform)
                    {
                        processedTextureCache = BuildColorTransformCache(component, colorTransformTextures);
                    }

                    // 1.25. 毛束パターン統一: お手本 Renderer の参照データを cache 非依存で事前計算
                    // (cache に依存していた旧実装ではお手本に per-renderer 調整があると strand が無効化されていた)
                    Processing.StrandPatternApplier.RefData? strandRef = null;
                    if (component.enableColorTransform && processedTextureCache != null
                        && component.strandPatternSettings != null && component.strandPatternSettings.enabled)
                    {
                        Color srcColor = component.targetRenderers.Count > 0 && component.targetRenderers[0] != null
                            ? GetDominantColorFromRenderer(component.targetRenderers[0])
                            : Color.white;
                        var refSettings = Processing.ColorTransformSettings.FromComponent(component, srcColor);
                        // BuildColorTransformCache の結果を再利用してお手本の color-transform を省略可能にする
                        strandRef = Processing.StrandPatternApplier.PrepareRefData(component, refSettings, null, processedTextureCache);

                        // cache 経路の Renderer (per-renderer 調整なし) に strand を適用
                        // strand は Renderer/サブメッシュごとに UV が異なるため、共有 cache へ書き戻さず
                        // per-island キャッシュ（renderer×submesh×元テクスチャ）へ格納する。
                        // これにより同じ original を複数 Renderer で共有していても各自に正しい模様が乗る
                        // （旧実装は最初の Renderer の UV で計算した結果を全 Renderer が共有していた）。
                        if (strandRef != null && strandRef.IsValid)
                        {
                            perIslandTextureCache = new Dictionary<(int, int, Texture2D), Texture2D>();
                            for (int r = 0; r < component.targetRenderers.Count; r++)
                            {
                                if (r == strandRef.RefIndex) continue;
                                var rend = component.targetRenderers[r];
                                if (rend == null) continue;
                                var rmats = rend.sharedMaterials;
                                for (int s = 0; s < rmats.Length; s++)
                                {
                                    var rm = rmats[s];
                                    if (rm == null || !rm.HasProperty("_MainTex")) continue;
                                    if (!component.IsSubmeshIncluded(r, s)) continue;
                                    // このサブメッシュが後段のマテリアル一時差し替えで strand 込みフルチェーン処理される場合はスキップ。
                                    // ※ 判定は (renderer, submesh) 単位。同じ Renderer 内でも「マスク無しサブメッシュ」は
                                    //    差し替え側でスキップされるため、こちらの per-island strand で塗り感統一を適用する必要がある。
                                    if (IsHandledByMaterialSwap(component, r, s)) continue;
                                    var origTex = rm.GetTexture("_MainTex") as Texture2D;
                                    if (origTex == null) continue;
                                    if (perIslandTextureCache.ContainsKey((r, s, origTex))) continue;
                                    if (!processedTextureCache.TryGetValue(origTex, out var processedTex)) continue;

                                    var composed = Processing.StrandPatternApplier.TryComposeStrand(
                                        processedTex, rend, new[] { s }, strandRef, compressResult: false);
                                    if (composed != null)
                                    {
                                        // 旧 processedTex は colorTransformTextures が保持しているのでそのまま破棄しない
                                        perIslandTextureCache[(r, s, origTex)] = composed;
                                        colorTransformTextures.Add(composed);
                                    }
                                }
                            }
                        }
                    }

                    // 1.5. 明度オフセットがあるRendererのマテリアルを一時的に差し替え
                    var savedMaterials = new Dictionary<SkinnedMeshRenderer, Material[]>();
                    var tempTextures = new List<Texture>();
                    var tempMaterials = new List<Material>();

                    if (component.enableColorTransform)
                    {
                        Color sourceColor = Color.white;
                        if (component.targetRenderers.Count > 0 && component.targetRenderers[0] != null)
                        {
                            sourceColor = GetDominantColorFromRenderer(component.targetRenderers[0]);
                        }
                        var settings = Processing.ColorTransformSettings.FromComponent(component, sourceColor);

                        for (int r = 0; r < component.targetRenderers.Count; r++)
                        {
                            var renderer = component.targetRenderers[r];
                            if (renderer == null) continue;

                            float brightnessOffset = GetRendererBrightnessOffset(component, r);
                            float blurSharp = GetRendererBlurSharp(component, r);
                            bool hasAnyMask = component.colorMasks?.Any(e => e.rendererIndex == r && e.mask != null) ?? false;
                            if (Mathf.Abs(brightnessOffset) < 0.001f && !hasAnyMask && Mathf.Abs(blurSharp) < 0.001f) continue;

                            savedMaterials[renderer] = renderer.sharedMaterials;
                            var mats = renderer.sharedMaterials;
                            var newMats = new Material[mats.Length];

                            for (int s = 0; s < mats.Length; s++)
                            {
                                var mat = mats[s];
                                if (mat == null) continue;
                                if (!component.IsSubmeshIncluded(r, s))
                                {
                                    newMats[s] = mat;
                                    continue;
                                }

                                // このサブメッシュに修正が必要か確認
                                var colorMask = component.GetColorMask(r, s);
                                if (Mathf.Abs(brightnessOffset) < 0.001f && colorMask == null && Mathf.Abs(blurSharp) < 0.001f)
                                {
                                    newMats[s] = mat;
                                    continue;
                                }

                                var tempMat = new Material(mat);
                                tempMaterials.Add(tempMat);

                                foreach (var slot in component.colorChangeTargets)
                                {
                                    if (!slot.applyColorChange) continue;
                                    if (!tempMat.HasProperty(slot.propertyName)) continue;

                                    var tex = tempMat.GetTexture(slot.propertyName) as Texture2D;
                                    if (tex == null) continue;

                                    var perTexSettings = Processing.MeshUVSampler.PrepareSettingsWithUVStats(
                                        settings, renderer, new[] { s }, tex);

                                    // UV マスク（前処理/dilation 共用）
                                    var uvMask = Processing.MeshUVRasterizer.Rasterize(
                                        renderer, new[] { s }, tex.width, tex.height);

                                    // ブラー／シャープ前処理
                                    Texture2D colorInput = tex;
                                    Texture2D? preprocessed = null;
                                    if (Mathf.Abs(blurSharp) > 0.001f)
                                    {
                                        preprocessed = Processing.TextureBlurSharpener.Process(tex, blurSharp, uvMask);
                                        if (preprocessed != null) colorInput = preprocessed;
                                    }

                                    var processed = Processing.ColorProcessor.ProcessTexture(colorInput, perTexSettings, true, compressResult: false);
                                    if (preprocessed != null) Object.DestroyImmediate(preprocessed);
                                    if (processed == null) continue;

                                    // 明度オフセットを適用
                                    if (Mathf.Abs(brightnessOffset) > 0.001f)
                                    {
                                        var offsetApplied = Processing.ColorProcessor.ApplyBrightnessOffset(processed, brightnessOffset, compressResult: false);
                                        if (offsetApplied != null)
                                        {
                                            Object.DestroyImmediate(processed);
                                            processed = offsetApplied;
                                        }
                                    }

                                    // UV 使用領域外を edge dilation で塗り足し
                                    {
                                        var dilated = Processing.ColorProcessor.DilateTexture(processed, uvMask, 8, compressResult: false);
                                        if (dilated != null)
                                        {
                                            Object.DestroyImmediate(processed);
                                            processed = dilated;
                                        }
                                    }

                                    // 色合わせ無視マスクを適用
                                    {
                                        var masked = Processing.ColorMaskApplier.TryApply(component, r, s, tex, processed, compressResult: false);
                                        if (masked != null)
                                        {
                                            Object.DestroyImmediate(processed);
                                            processed = masked;
                                        }
                                    }

                                    // 毛束パターン: MainTex スロットのみ対応（お手本 Renderer 自身はスキップ）
                                    if (strandRef != null && strandRef.IsValid
                                        && slot.propertyName == "_MainTex"
                                        && r != strandRef.RefIndex)
                                    {
                                        var composed = Processing.StrandPatternApplier.TryComposeStrand(
                                            processed, renderer, new[] { s }, strandRef, compressResult: false);
                                        if (composed != null)
                                        {
                                            Object.DestroyImmediate(processed);
                                            processed = composed;
                                        }
                                    }

                                    tempMat.SetTexture(slot.propertyName, processed);
                                    tempTextures.Add(processed);
                                }

                                // _MainTex もチェック
                                if (tempMat.HasProperty("_MainTex"))
                                {
                                    var tex = tempMat.GetTexture("_MainTex") as Texture2D;
                                    bool alreadyProcessed = component.colorChangeTargets
                                        .Any(ct => ct.propertyName == "_MainTex" && ct.applyColorChange);
                                    if (tex != null && !alreadyProcessed && !component.IsColorChangeExplicitlyDisabled("_MainTex"))
                                    {
                                        var perTexSettings = Processing.MeshUVSampler.PrepareSettingsWithUVStats(
                                            settings, renderer, new[] { s }, tex);

                                        var uvMask = Processing.MeshUVRasterizer.Rasterize(
                                            renderer, new[] { s }, tex.width, tex.height);

                                        Texture2D colorInput = tex;
                                        Texture2D? preprocessed = null;
                                        if (Mathf.Abs(blurSharp) > 0.001f)
                                        {
                                            preprocessed = Processing.TextureBlurSharpener.Process(tex, blurSharp, uvMask);
                                            if (preprocessed != null) colorInput = preprocessed;
                                        }

                                        var processed = Processing.ColorProcessor.ProcessTexture(colorInput, perTexSettings, true, compressResult: false);
                                        if (preprocessed != null) Object.DestroyImmediate(preprocessed);
                                        if (processed != null)
                                        {
                                            // 明度オフセットを適用
                                            if (Mathf.Abs(brightnessOffset) > 0.001f)
                                            {
                                                var offsetApplied = Processing.ColorProcessor.ApplyBrightnessOffset(processed, brightnessOffset, compressResult: false);
                                                if (offsetApplied != null)
                                                {
                                                    Object.DestroyImmediate(processed);
                                                    processed = offsetApplied;
                                                }
                                            }

                                            // UV 使用領域外を edge dilation で塗り足し
                                            {
                                                var dilated = Processing.ColorProcessor.DilateTexture(processed, uvMask, 8, compressResult: false);
                                                if (dilated != null)
                                                {
                                                    Object.DestroyImmediate(processed);
                                                    processed = dilated;
                                                }
                                            }

                                            // 色合わせ無視マスクを適用
                                            {
                                                var masked = Processing.ColorMaskApplier.TryApply(component, r, s, tex, processed, compressResult: false);
                                                if (masked != null)
                                                {
                                                    Object.DestroyImmediate(processed);
                                                    processed = masked;
                                                }
                                            }

                                            // 毛束パターン: お手本 Renderer 自身はスキップ
                                            if (strandRef != null && strandRef.IsValid
                                                && r != strandRef.RefIndex)
                                            {
                                                var composed = Processing.StrandPatternApplier.TryComposeStrand(
                                                    processed, renderer, new[] { s }, strandRef, compressResult: false);
                                                if (composed != null)
                                                {
                                                    Object.DestroyImmediate(processed);
                                                    processed = composed;
                                                }
                                            }

                                            tempMat.SetTexture("_MainTex", processed);
                                            tempTextures.Add(processed);
                                        }
                                    }
                                }

                                newMats[s] = tempMat;
                            }

                            renderer.sharedMaterials = newMats;
                        }
                    }

                    // 2. TextureAtlasBuilder でアトラス生成
                    TextureAtlasBuilder.AtlasResult atlasResult;
                    try
                    {
                        atlasResult = TextureAtlasBuilder.Build(component, resolution, processedTextureCache ?? new Dictionary<Texture2D, Texture2D>(), isPreview: true, perIslandTextureCache);
                    }
                    finally
                    {
                        // 必ずマテリアルを復元
                        foreach (var kvp in savedMaterials)
                        {
                            kvp.Key.sharedMaterials = kvp.Value;
                        }

                        // 一時テクスチャ・マテリアルを破棄
                        foreach (var tex in tempTextures)
                        {
                            if (tex != null) Object.DestroyImmediate(tex);
                        }
                        foreach (var mat in tempMaterials)
                        {
                            if (mat != null) Object.DestroyImmediate(mat);
                        }

                        // 色変換中間テクスチャも破棄（アトラスに焼き込み済み）
                        foreach (var tex in colorTransformTextures)
                        {
                            if (tex != null) Object.DestroyImmediate(tex);
                        }

                        // 毛束パターン: reference detail を破棄
                        // （cache 経路で生成した合成済みテクスチャは colorTransformTextures が保持し下記の cleanup で解放される）
                        strandRef?.Dispose();
                    }

                    if (atlasResult.AtlasTextures.Count == 0)
                    {
                        Debug.LogWarning($"[ChimeraHairMaster] {component.name}: アトラステクスチャが生成されませんでした。従来モードにフォールバックします。");
                        // layoutCacheHit 経路では DisposeTextures 済みのため、
                        // エントリを残すと後で破棄済みテクスチャが偽キャッシュヒットする。必ず除去する
                        if (hasCache && _atlasCache.ContainsKey(componentId))
                        {
                            cachedEntry!.Dispose();
                            _atlasCache.Remove(componentId);
                        }
                        ProcessComponent(component, processedMaterials, generatedTextures);
                        return;
                    }

                    // キャッシュ所有テクスチャが UnloadUnusedAssets で回収されないよう保護し、
                    // シーン保存対象からも除外する
                    foreach (var tex in atlasResult.AtlasTextures.Values)
                    {
                        if (tex != null) tex.hideFlags = HideFlags.HideAndDontSave;
                    }

                    if (needUVRemap)
                    {
                        // 完全ミス: UV リマップも実行
                        var newMeshes = new Dictionary<int, Mesh>();
                        for (int i = 0; i < component.targetRenderers.Count; i++)
                        {
                            var renderer = component.targetRenderers[i];
                            if (renderer == null || renderer.sharedMesh == null) continue;

                            // マスクカットを適用（UVリマップ前、元のUV座標でサンプリング）
                            Mesh meshForRemap = renderer.sharedMesh;
                            bool hasMaskCut = false;
                            foreach (var entry in component.materialSelections)
                            {
                                if (!entry.isIncluded || entry.meshCutMask == null) continue;
                                if (entry.rendererIndex != i) continue;

                                if (!hasMaskCut)
                                {
                                    meshForRemap = Object.Instantiate(renderer.sharedMesh);
                                    hasMaskCut = true;
                                }
                                MeshCutter.ApplyPreviewCut(meshForRemap, entry.submeshIndex, entry.meshCutMask);
                            }

                            var remapped = MeshUVRemapper.RemapUVsByIslands(
                                meshForRemap, atlasResult.IslandPlacements, i);

                            if (hasMaskCut)
                                Object.DestroyImmediate(meshForRemap);

                            if (remapped != null)
                            {
                                remapped.hideFlags = HideFlags.HideAndDontSave;
                                newMeshes[renderer.GetInstanceID()] = remapped;
                            }
                        }

                        // 新規キャッシュエントリ
                        var newCacheEntry = new AtlasCacheEntry
                        {
                            LayoutHash = layoutHash,
                            InputHash = inputHash,
                            AtlasTextures = atlasResult.AtlasTextures,
                            RemappedMeshes = newMeshes,
                            ClearedProperties = atlasResult.ClearedProperties
                        };
                        _atlasCache[componentId] = newCacheEntry;

                        atlasTextures = newCacheEntry.AtlasTextures;
                        cachedMeshes = newCacheEntry.RemappedMeshes;
                        clearedProperties = newCacheEntry.ClearedProperties;

                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: アトラスプレビュー生成完了 " +
                                  $"(アトラス数: {atlasTextures.Count}, リマップメッシュ数: {cachedMeshes.Count})");
                    }
                    else
                    {
                        // レイアウトキャッシュヒット: テクスチャのみ更新、メッシュ再利用
                        cachedEntry!.InputHash = inputHash;
                        cachedEntry.AtlasTextures = atlasResult.AtlasTextures;
                        cachedEntry.ClearedProperties = atlasResult.ClearedProperties;

                        atlasTextures = cachedEntry.AtlasTextures;
                        cachedMeshes = cachedEntry.RemappedMeshes;
                        clearedProperties = cachedEntry.ClearedProperties;

                        CHMLog.Verbose($"[ChimeraHairMaster] {component.name}: テクスチャ再生成完了（メッシュ再利用、アトラス数: {atlasTextures.Count}）");
                    }
                }

                // === ここからはキャッシュヒット/ミス共通 ===

                // メッシュ参照をコピー（キャッシュが所有、PreviewNode は参照のみ）
                foreach (var kvp in cachedMeshes)
                {
                    remappedMeshes[kvp.Key] = kvp.Value;
                }

                // アトラスマテリアルを作成（baseMaterialForPreview の完全コピー + アトラステクスチャ上書き）
                var atlasMat = new Material(baseMaterialForPreview);
                atlasMat.hideFlags = HideFlags.HideAndDontSave;
                atlasMat.name = baseMaterialForPreview.name + "_Preview_Atlas";

                foreach (var kvp in atlasTextures)
                {
                    if (atlasMat.HasProperty(kvp.Key))
                    {
                        atlasMat.SetTexture(kvp.Key, kvp.Value);
                    }
                }

                // アトラス生成をスキップしたプロパティをクリア
                // （previewMaterial 由来のテクスチャが旧UVのまま残るのを防ぐ。ビルドと同じ挙動）
                foreach (var propertyName in clearedProperties)
                {
                    if (atlasMat.HasProperty(propertyName))
                    {
                        atlasMat.SetTexture(propertyName, null);
                    }
                }

                // 全 original material → atlasMat のコピーにマッピング
                for (int r = 0; r < component.targetRenderers.Count; r++)
                {
                    var renderer = component.targetRenderers[r];
                    if (renderer == null) continue;

                    var materials = renderer.sharedMaterials;
                    for (int s = 0; s < materials.Length; s++)
                    {
                        var mat = materials[s];
                        if (mat == null) continue;

                        if (!component.IsSubmeshIncluded(r, s)) continue;

                        if (!processedMaterials.ContainsKey(mat))
                        {
                            var matCopy = new Material(atlasMat);
                            matCopy.hideFlags = HideFlags.HideAndDontSave;
                            matCopy.name = atlasMat.name;
                            processedMaterials[mat] = matCopy;
                        }
                    }
                }

                // テンプレートマテリアルを破棄（コピー済みのため不要）
                Object.DestroyImmediate(atlasMat);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Failed to process atlas component: {ex}");

                // 例外時はキャッシュエントリが中途半端な状態（破棄済みテクスチャ等）の可能性があるため除去
                int failedComponentId = component.GetInstanceID();
                if (_atlasCache.TryGetValue(failedComponentId, out var brokenEntry))
                {
                    brokenEntry?.Dispose();
                    _atlasCache.Remove(failedComponentId);
                }
            }
            finally
            {
                // 自動生成したプレビュー用ベースマテリアルはテンプレートとしてのみ使用するため破棄
                if (autoBaseMaterial && baseMaterialForPreview != null)
                    Object.DestroyImmediate(baseMaterialForPreview);
            }
        }

        /// <summary>
        /// プレビュー用の色変換テクスチャキャッシュを構築
        /// ビルドの ColorTransformPass と同じロジックで、元テクスチャ → 色変換済みテクスチャのマッピングを作成
        /// TextureAtlasBuilder.Build() に渡して、アトラス生成時に色変換済みテクスチャを使用させる
        /// </summary>
        private Dictionary<Texture2D, Texture2D> BuildColorTransformCache(
            ChimeraHairMaster component,
            List<Texture> generatedTextures)
        {
            var cache = new Dictionary<Texture2D, Texture2D>();

            Color sourceColor = Color.white;
            if (component.targetRenderers.Count > 0 && component.targetRenderers[0] != null)
            {
                sourceColor = GetDominantColorFromRenderer(component.targetRenderers[0]);
            }

            var settings = Processing.ColorTransformSettings.FromComponent(component, sourceColor);

            // 統合対象の全テクスチャを色変換
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                for (int s = 0; s < materials.Length; s++)
                {
                    var mat = materials[s];
                    if (mat == null) continue;
                    if (!component.IsSubmeshIncluded(r, s)) continue;

                    foreach (var slot in component.colorChangeTargets)
                    {
                        if (!slot.applyColorChange) continue;
                        if (!mat.HasProperty(slot.propertyName)) continue;

                        var tex = mat.GetTexture(slot.propertyName) as Texture2D;
                        if (tex == null || cache.ContainsKey(tex)) continue;

                        var perTexSettings = Processing.MeshUVSampler.PrepareSettingsWithUVStats(
                            settings, renderer, new[] { s }, tex);
                        var processed = Processing.ColorProcessor.ProcessTexture(tex, perTexSettings, true, compressResult: false);
                        if (processed != null)
                        {
                            // UV 使用領域外を edge dilation で塗り足し
                            var uvMask = Processing.MeshUVRasterizer.Rasterize(
                                renderer, new[] { s }, processed.width, processed.height);
                            var dilated = Processing.ColorProcessor.DilateTexture(processed, uvMask, 8, compressResult: false);
                            if (dilated != null)
                            {
                                Object.DestroyImmediate(processed);
                                processed = dilated;
                            }
                            cache[tex] = processed;
                            generatedTextures.Add(processed);
                        }
                    }

                    // _MainTex がcolorChangeTargetsに含まれていない場合もチェック
                    if (mat.HasProperty("_MainTex"))
                    {
                        var tex = mat.GetTexture("_MainTex") as Texture2D;
                        if (tex != null && !cache.ContainsKey(tex) && !component.IsColorChangeExplicitlyDisabled("_MainTex"))
                        {
                            var perTexSettings = Processing.MeshUVSampler.PrepareSettingsWithUVStats(
                                settings, renderer, new[] { s }, tex);
                            var processed = Processing.ColorProcessor.ProcessTexture(tex, perTexSettings, true, compressResult: false);
                            if (processed != null)
                            {
                                // UV 使用領域外を edge dilation で塗り足し
                                var uvMask = Processing.MeshUVRasterizer.Rasterize(
                                    renderer, new[] { s }, processed.width, processed.height);
                                var dilated = Processing.ColorProcessor.DilateTexture(processed, uvMask, 8, compressResult: false);
                                if (dilated != null)
                                {
                                    Object.DestroyImmediate(processed);
                                    processed = dilated;
                                }
                                cache[tex] = processed;
                                generatedTextures.Add(processed);
                            }
                        }
                    }
                }
            }

            return cache;
        }

        /// <summary>
        /// Renderer単位のブラー／シャープ強度を取得
        /// </summary>
        /// <summary>
        /// (renderer, submesh) がアトラスプレビューの「1.5 マテリアル一時差し替え」経路で
        /// strand 込みフルチェーン処理されるか。明度/ブラー・シャープは Renderer 単位、マスクは (r,s) 単位で判定する
        /// （差し替え側 L1321 の per-submesh 条件と一致させる）。
        /// true の (r,s) は per-island strand 側では扱わない（二重適用回避）。
        /// </summary>
        private bool IsHandledByMaterialSwap(ChimeraHairMaster component, int rendererIndex, int submeshIndex)
        {
            if (Mathf.Abs(GetRendererBrightnessOffset(component, rendererIndex)) > 0.001f) return true;
            if (Mathf.Abs(GetRendererBlurSharp(component, rendererIndex)) > 0.001f) return true;
            return component.GetColorMask(rendererIndex, submeshIndex) != null;
        }

        private float GetRendererBlurSharp(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBlurSharpAdjustments == null) return 0f;
            foreach (var adj in component.rendererBlurSharpAdjustments)
            {
                if (adj.rendererIndex == rendererIndex) return adj.blurSharp;
            }
            return 0f;
        }

        /// <summary>
        /// Renderer単位の明度オフセットを取得
        /// </summary>
        private float GetRendererBrightnessOffset(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBrightnessAdjustments == null) return 0f;

            foreach (var adjustment in component.rendererBrightnessAdjustments)
            {
                if (adjustment.rendererIndex == rendererIndex)
                {
                    return adjustment.brightnessOffset;
                }
            }

            return 0f;
        }

        /// <summary>
        /// 明度調整リストの内容ハッシュ（Observe 用。List 参照の等価比較を避ける）
        /// </summary>
        private static int ComputeBrightnessAdjustmentsHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = 17;
                if (component.rendererBrightnessAdjustments == null) return hash;
                foreach (var adj in component.rendererBrightnessAdjustments)
                {
                    if (adj == null) continue;
                    hash = hash * 31 + adj.rendererIndex;
                    hash = hash * 31 + adj.brightnessOffset.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>
        /// ブラー／シャープ調整リストの内容ハッシュ（Observe 用）
        /// </summary>
        private static int ComputeBlurSharpAdjustmentsHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = 17;
                if (component.rendererBlurSharpAdjustments == null) return hash;
                foreach (var adj in component.rendererBlurSharpAdjustments)
                {
                    if (adj == null) continue;
                    hash = hash * 31 + adj.rendererIndex;
                    hash = hash * 31 + adj.blurSharp.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>
        /// ExtractDominantColor（フル GetPixels の CPU 走査）の結果キャッシュ。
        /// 同一テクスチャ・同一内容なら結果は不変。
        /// imageContentsHash で再インポート/ペイントによる内容変更を検知する
        /// </summary>
        private static readonly Dictionary<(int instanceId, Hash128 contentsHash), Color> _dominantColorCache = new();

        private static void ClearDominantColorCache()
        {
            _dominantColorCache.Clear();
        }

        /// <summary>
        /// Rendererから支配的な色を取得
        /// </summary>
        private Color GetDominantColorFromRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) return Color.white;

            var materials = renderer.sharedMaterials;
            foreach (var mat in materials)
            {
                if (mat == null) continue;

                // メインテクスチャから色を抽出
                if (mat.HasProperty("_MainTex"))
                {
                    var tex = mat.GetTexture("_MainTex") as Texture2D;
                    if (tex != null)
                    {
                        var key = (tex.GetInstanceID(), tex.imageContentsHash);
                        if (!_dominantColorCache.TryGetValue(key, out var dominant))
                        {
                            dominant = Processing.ColorProcessor.ExtractDominantColor(tex);
                            _dominantColorCache[key] = dominant;
                        }
                        return dominant;
                    }
                }

                // マテリアルのカラーを使用
                if (mat.HasProperty("_Color"))
                {
                    return mat.GetColor("_Color");
                }
            }

            return Color.white;
        }

        /// <summary>
        /// テクスチャを除いて数値パラメータとトグルのみをコピーした新規マテリアルを作成
        /// すべてのテクスチャスロットを明示的にnullに設定
        /// </summary>
        private static Material CreateMaterialWithoutTextures(Material source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source), "Source material cannot be null");
            }

            // 同じシェーダーで新規マテリアルを作成
            var newMat = new Material(source.shader);
            newMat.hideFlags = HideFlags.HideAndDontSave;
            
            // レンダーキューをコピー
            newMat.renderQueue = source.renderQueue;
            
            // シェーダーキーワードをコピー
            newMat.shaderKeywords = source.shaderKeywords;
            
            // プロパティをコピー（テクスチャ以外）
            var shader = source.shader;
            int propertyCount = shader.GetPropertyCount();
            
            for (int i = 0; i < propertyCount; i++)
            {
                var propertyType = shader.GetPropertyType(i);
                var propertyName = shader.GetPropertyName(i);
                
                switch (propertyType)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        newMat.SetColor(propertyName, source.GetColor(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        newMat.SetVector(propertyName, source.GetVector(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        newMat.SetFloat(propertyName, source.GetFloat(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        newMat.SetInt(propertyName, source.GetInt(propertyName));
                        break;
                    // Textureは明示的にnullに設定
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        newMat.SetTexture(propertyName, null);
                        // テクスチャのScale/Offsetはコピー
                        newMat.SetTextureScale(propertyName, source.GetTextureScale(propertyName));
                        newMat.SetTextureOffset(propertyName, source.GetTextureOffset(propertyName));
                        break;
                }
            }
            
            return newMat;
        }

        /// <summary>
        /// マットキャップテクスチャのみをコピー
        /// </summary>
        private static void CopyMatCapTextures(Material source, Material dest)
        {
            // マットキャップ画像のみコピーする。
            // マスク（_MatCapBlendMask等）とカスタムノーマル（_MatCapBumpMap等）はUV0参照のため、
            // 統合後UVとずれたままコピーされてしまう（インスペクタの CreatePreviewMaterial と同じ方針）
            string[] matCapProps = new string[]
            {
                "_MatCapTex",
                "_MatCap2ndTex"
            };

            foreach (var prop in matCapProps)
            {
                if (source.HasProperty(prop) && dest.HasProperty(prop))
                {
                    var tex = source.GetTexture(prop);
                    if (tex != null)
                    {
                        dest.SetTexture(prop, tex);
                    }
                }
            }
        }

        /// <summary>
        /// プレビューノード（Color-Changerと同様のパターン）
        /// </summary>
        private class PreviewNode : IRenderFilterNode, IDisposable
        {
            private readonly Dictionary<Material, Material>? _processedMaterials;
            private readonly List<Texture>? _generatedTextures;

            // アトラスモード: Renderer InstanceID → アトラスUVにリマップ済みメッシュ
            private readonly Dictionary<int, Mesh>? _remappedMeshes;

            // マスクプレビュー用: アトラスマスクを元UV空間に合成したRenderer単位テクスチャ
            private Dictionary<int, RenderTexture>? _compositedMasks;
            private Material? _blitMaterial;
            // マスク適用前のマテリアルプロパティ保存（Material InstanceID → 元の値）
            private string? _activeMaskSlot;
            private Dictionary<int, (float useTexValue, Texture? texture, Texture? blendMask, Color color, float blendMode)>? _savedMaskSlotStates;

            // アトラスモードの場合は Mesh も変更対象
            private readonly bool _isAtlasMode;

            // UVがアトラス空間へリマップされた Renderer の InstanceID 集合。
            // _isAtlasMode（remappedMeshes が非空か）はメッシュ変形の元UVメッシュでも true に
            // なるため、マスク可視化で「アトラスUVか」を判定する用途にはこちらを使う。
            private readonly HashSet<int>? _atlasRemappedIds;

            // このノードが所有する（Disposeで破棄する）メッシュ
            // アトラスキャッシュ所有のメッシュは含まれない
            private readonly List<Mesh>? _ownedMeshes;

            public RenderAspects WhatChanged => _isAtlasMode
                ? RenderAspects.Texture | RenderAspects.Material | RenderAspects.Mesh
                : RenderAspects.Texture | RenderAspects.Material;

            public PreviewNode(
                Dictionary<Material, Material>? processedMaterials,
                List<Texture>? generatedTextures,
                Dictionary<int, Mesh>? remappedMeshes = null,
                List<Mesh>? ownedMeshes = null,
                HashSet<int>? atlasRemappedIds = null)
            {
                _processedMaterials = processedMaterials;
                _generatedTextures = generatedTextures;
                _remappedMeshes = remappedMeshes;
                _isAtlasMode = remappedMeshes != null && remappedMeshes.Count > 0;
                _atlasRemappedIds = atlasRemappedIds;
                _ownedMeshes = ownedMeshes;
            }

            // OnFrame 用の非アロケートバッファ（メインスレッド専用）
            private static readonly List<Material> _onFrameMaterialBuffer = new();

            public void OnFrame(Renderer original, Renderer proxy)
            {
                try
                {
                    if (proxy == null)
                        return;

                    // マテリアルを置換
                    // sharedMaterials の get/set は毎回配列を複製するため、
                    // 非アロケートで読み取り、置換が必要なスロットがある場合のみ書き戻す
                    // （置換済みのフレームでは何も起きない）
                    if (_processedMaterials != null && _processedMaterials.Count > 0)
                    {
                        var buffer = _onFrameMaterialBuffer;
                        proxy.GetSharedMaterials(buffer);

                        bool changed = false;
                        for (int i = 0; i < buffer.Count; i++)
                        {
                            var mat = buffer[i];
                            if (mat != null && _processedMaterials.TryGetValue(mat, out var processedMat))
                            {
                                buffer[i] = processedMat;
                                changed = true;
                            }
                        }

                        if (changed)
                        {
                            proxy.sharedMaterials = buffer.ToArray();
                        }
                    }

                    // アトラスモード: メッシュ UV をアトラス座標に置換
                    if (_remappedMeshes != null && proxy is SkinnedMeshRenderer smr)
                    {
                        int originalId = original.GetInstanceID();
                        if (_remappedMeshes.TryGetValue(originalId, out var mesh) && smr.sharedMesh != mesh)
                        {
                            smr.sharedMesh = mesh;
                        }
                    }

                    // マスクツール編集中の処理
                    var maskTex = MaskToolLauncher.ActiveMaskTexture;
                    var maskSlot = MaskToolLauncher.ActiveMaskSlotName;
                    var islandMappings = MaskToolLauncher.ActiveMaskIslandMappings;

                    if (maskTex != null && islandMappings != null && !string.IsNullOrEmpty(maskSlot))
                    {
                        // スロット変更の検出 → 旧スロットを復元してから新スロットを適用
                        if (_activeMaskSlot != null && _activeMaskSlot != maskSlot)
                        {
                            RestoreAllMaskSlotStates();
                        }
                        _activeMaskSlot = maskSlot;

                        // この Renderer の UV がアトラス空間にあるかで分岐する。
                        // _isAtlasMode（ノード全体）ではなく Renderer 単位で見ないと、
                        // merge-OFF + メッシュ変形（元UVのまま頂点だけ動く）のとき
                        // アトラス分岐に誤って入り、マスクが誤配置される。
                        int maskOriginalId = original.GetInstanceID();
                        bool rendererIsAtlasUV = _atlasRemappedIds != null && _atlasRemappedIds.Contains(maskOriginalId);

                        if (rendererIsAtlasUV)
                        {
                            // アトラスモード: メッシュUVがアトラス空間なので直接マスクを設定
                            var proxyMats = proxy.sharedMaterials;
                            foreach (var mat in proxyMats)
                            {
                                if (mat != null)
                                {
                                    SaveMaskSlotState(mat, maskSlot);
                                    SetMaskVisualization(mat, maskSlot, maskTex);
                                }
                            }
                        }
                        else
                        {
                            // 従来モード: アトラスマスクを元UV空間に合成してから設定
                            if (islandMappings.TryGetValue(maskOriginalId, out var mappings))
                            {
                                var compositedMask = CompositeMaskForRenderer(maskOriginalId, mappings, maskTex);

                                var proxyMats = proxy.sharedMaterials;
                                var affectedSubmeshes = new HashSet<int>();
                                foreach (var m in mappings)
                                    affectedSubmeshes.Add(m.submeshIndex);

                                foreach (var submeshIdx in affectedSubmeshes)
                                {
                                    if (submeshIdx < proxyMats.Length && proxyMats[submeshIdx] != null)
                                    {
                                        SaveMaskSlotState(proxyMats[submeshIdx], maskSlot);
                                        SetMaskVisualization(proxyMats[submeshIdx], maskSlot, compositedMask);
                                    }
                                }
                            }
                        }
                    }
                    else if (_activeMaskSlot != null)
                    {
                        // マスクツールが閉じた → 変更済みマテリアルを元に戻す
                        RestoreAllMaskSlotStates();
                        CleanupCompositedMasks();
                        _activeMaskSlot = null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChimeraHairMaster] Error in OnFrame: {ex}");
                }
            }

            /// <summary>
            /// マスクテクスチャをlilToonの_Main2ndテクスチャ機能で可視化する
            /// 出力先スロットに関係なく、常に_Main2nd Multiplyオーバーレイで表示する
            /// → 塗った部分(黒)が暗くなり、未塗り(白)は変化なし
            ///
            /// 合成テクスチャは元UV空間に変換済みのため、Scale/Offset補正は不要
            /// </summary>
            private static void SetMaskVisualization(Material mat, string maskSlot, RenderTexture maskTex)
            {
                // 常に_Main2ndオーバーレイで可視化（出力先スロットに関係なく統一）
                if (mat.HasProperty("_UseMain2ndTex"))
                    mat.SetFloat("_UseMain2ndTex", 1f);

                if (mat.HasProperty("_Main2ndTex"))
                    mat.SetTexture("_Main2ndTex", maskTex);

                if (mat.HasProperty("_Main2ndBlendMask"))
                    mat.SetTexture("_Main2ndBlendMask", null);

                if (mat.HasProperty("_Color2nd"))
                    mat.SetColor("_Color2nd", new Color(1f, 1f, 1f, 1f));

                if (mat.HasProperty("_Main2ndTexBlendMode"))
                    mat.SetFloat("_Main2ndTexBlendMode", 3f); // Multiply
            }

            /// <summary>
            /// マスク適用前の_Main2nd関連プロパティを保存（初回のみ）
            /// </summary>
            private void SaveMaskSlotState(Material mat, string maskSlot)
            {
                int matId = mat.GetInstanceID();

                _savedMaskSlotStates ??= new Dictionary<int, (float, Texture?, Texture?, Color, float)>();
                if (_savedMaskSlotStates.ContainsKey(matId))
                    return;

                // 常に_Main2ndの状態を保存（可視化は常に_Main2ndオーバーレイを使用）
                _savedMaskSlotStates[matId] = (
                    mat.HasProperty("_UseMain2ndTex") ? mat.GetFloat("_UseMain2ndTex") : 0f,
                    mat.HasProperty("_Main2ndTex") ? mat.GetTexture("_Main2ndTex") : null,
                    mat.HasProperty("_Main2ndBlendMask") ? mat.GetTexture("_Main2ndBlendMask") : null,
                    mat.HasProperty("_Color2nd") ? mat.GetColor("_Color2nd") : Color.white,
                    mat.HasProperty("_Main2ndTexBlendMode") ? mat.GetFloat("_Main2ndTexBlendMode") : 0f
                );
            }

            /// <summary>
            /// 保存済みの_Main2nd関連プロパティを全て復元。
            /// SetMaskVisualization の対象には「置換されなかったスロットの元マテリアル資産」も
            /// 含まれるため、_processedMaterials ではなく保存記録（InstanceID）を起点に復元する。
            /// _processedMaterials だけを走査すると、元マテリアル直接変更分が永久に復元されない。
            /// </summary>
            private void RestoreAllMaskSlotStates()
            {
                if (_savedMaskSlotStates == null || _savedMaskSlotStates.Count == 0)
                    return;

                foreach (var kvp in _savedMaskSlotStates)
                {
                    var mat = EditorUtility.InstanceIDToObject(kvp.Key) as Material;
                    if (mat == null) continue;
                    var saved = kvp.Value;

                    if (mat.HasProperty("_UseMain2ndTex")) mat.SetFloat("_UseMain2ndTex", saved.useTexValue);
                    if (mat.HasProperty("_Main2ndTex")) mat.SetTexture("_Main2ndTex", saved.texture);
                    if (mat.HasProperty("_Main2ndBlendMask")) mat.SetTexture("_Main2ndBlendMask", saved.blendMask);
                    if (mat.HasProperty("_Color2nd")) mat.SetColor("_Color2nd", saved.color);
                    if (mat.HasProperty("_Main2ndTexBlendMode")) mat.SetFloat("_Main2ndTexBlendMode", saved.blendMode);
                }

                _savedMaskSlotStates.Clear();
            }

            /// <summary>
            /// 合成マスクRTとBlitマテリアルを解放
            /// </summary>
            private void CleanupCompositedMasks()
            {
                if (_compositedMasks != null)
                {
                    foreach (var rt in _compositedMasks.Values)
                    {
                        if (rt != null)
                        {
                            // Release は GPU メモリのみ解放するため、オブジェクト本体も破棄する
                            rt.Release();
                            Object.DestroyImmediate(rt);
                        }
                    }
                    _compositedMasks.Clear();
                }
            }

            /// <summary>
            /// アトラスマスクRTから各アイランド領域を切り出し、元UV空間に再配置した合成テクスチャを生成
            /// GL描画で各アイランドのアトラス領域→元UV領域へのマッピングを行う
            /// </summary>
            private RenderTexture CompositeMaskForRenderer(
                int rendererInstanceId,
                List<(int submeshIndex, Rect originalBounds, Vector2 atlasPosition, Vector2 atlasScale)> mappings,
                RenderTexture atlasMask)
            {
                _compositedMasks ??= new Dictionary<int, RenderTexture>();

                // キャッシュRTを取得/作成
                if (!_compositedMasks.TryGetValue(rendererInstanceId, out var composited)
                    || composited == null
                    || composited.width != atlasMask.width
                    || composited.height != atlasMask.height)
                {
                    if (composited != null)
                    {
                        composited.Release();
                        Object.DestroyImmediate(composited);
                    }

                    composited = new RenderTexture(atlasMask.width, atlasMask.height, 0, RenderTextureFormat.ARGB32);
                    composited.hideFlags = HideFlags.HideAndDontSave;
                    composited.filterMode = FilterMode.Bilinear;
                    composited.Create();
                    _compositedMasks[rendererInstanceId] = composited;
                }

                // Blitマテリアルの初期化（テクスチャをそのまま描画するシンプルなシェーダー）
                if (_blitMaterial == null)
                {
                    _blitMaterial = new Material(Shader.Find("Hidden/Internal-GUITexture"));
                    _blitMaterial.hideFlags = HideFlags.HideAndDontSave;
                }

                // 白でクリア（Multiply用: 白=変化なし）
                var prev = RenderTexture.active;
                RenderTexture.active = composited;
                GL.Clear(true, true, Color.white);

                // GL描画: アトラスマスクの各アイランド領域を元UV位置にコピー
                _blitMaterial.SetTexture("_MainTex", atlasMask);
                _blitMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadOrtho();

                foreach (var mapping in mappings)
                {
                    // ソース: アトラスマスク上のアイランド領域
                    float u0 = mapping.atlasPosition.x;
                    float v0 = mapping.atlasPosition.y;
                    float u1 = u0 + mapping.atlasScale.x;
                    float v1 = v0 + mapping.atlasScale.y;

                    // 出力先: 元UV空間でのアイランド位置
                    float x0 = mapping.originalBounds.xMin;
                    float y0 = mapping.originalBounds.yMin;
                    float x1 = mapping.originalBounds.xMax;
                    float y1 = mapping.originalBounds.yMax;

                    GL.Begin(GL.QUADS);
                    GL.TexCoord2(u0, v0); GL.Vertex3(x0, y0, 0);
                    GL.TexCoord2(u1, v0); GL.Vertex3(x1, y0, 0);
                    GL.TexCoord2(u1, v1); GL.Vertex3(x1, y1, 0);
                    GL.TexCoord2(u0, v1); GL.Vertex3(x0, y1, 0);
                    GL.End();
                }

                GL.PopMatrix();
                RenderTexture.active = prev;

                return composited;
            }

            public void Dispose()
            {
                // マスク可視化で元マテリアル資産を直接変更している場合があるため、
                // ノード破棄時にも必ず復元する（未変更なら no-op）
                RestoreAllMaskSlotStates();

                // このノードが所有するメッシュのみ破棄（アトラスキャッシュ所有のメッシュは破棄しない）
                if (_ownedMeshes != null)
                {
                    foreach (var mesh in _ownedMeshes)
                    {
                        if (mesh != null) Object.DestroyImmediate(mesh);
                    }
                    _ownedMeshes.Clear();
                }
                _remappedMeshes?.Clear();

                // マスク関連リソースの破棄
                CleanupCompositedMasks();

                if (_blitMaterial != null)
                {
                    Object.DestroyImmediate(_blitMaterial);
                    _blitMaterial = null;
                }

                // テクスチャを破棄
                if (_generatedTextures != null)
                {
                    foreach (var tex in _generatedTextures)
                    {
                        if (tex != null)
                        {
                            Object.DestroyImmediate(tex);
                        }
                    }
                    _generatedTextures.Clear();
                }

                // マテリアルを破棄
                if (_processedMaterials != null)
                {
                    foreach (var mat in _processedMaterials.Values)
                    {
                        if (mat != null)
                        {
                            Object.DestroyImmediate(mat);
                        }
                    }
                    _processedMaterials.Clear();
                }
            }
        }
    }
}
