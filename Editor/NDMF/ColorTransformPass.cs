using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// 色変換処理パス
    /// </summary>
    public class ColorTransformPass : Pass<ColorTransformPass>
    {
        public override string DisplayName => "CHM: Color Transform";

        /// <summary>
        /// 処理済みテクスチャのキャッシュ（コンポーネントごと）
        /// </summary>
        internal static Dictionary<ChimeraHairMaster, Dictionary<Texture2D, Texture2D>> ProcessedTextureCache
            = new Dictionary<ChimeraHairMaster, Dictionary<Texture2D, Texture2D>>();

        protected override void Execute(BuildContext context)
        {
            // キャッシュをクリア
            ProcessedTextureCache.Clear();

            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;

                ProcessColorTransform(context, component);
            }
        }

        private void ProcessColorTransform(BuildContext context, ChimeraHairMaster component)
        {
            // このコンポーネント用のキャッシュを初期化
            var textureCache = new Dictionary<Texture2D, Texture2D>();
            ProcessedTextureCache[component] = textureCache;

            // Pass 単位のピクセル読み込みキャッシュ（GetReadableTexture + GetPixels の重複を避ける）
            var pixelCache = new MeshUVSampler.PixelCache();

            // 色合わせが無効な場合はスキップ（空のキャッシュを登録して終了）
            if (!component.enableColorTransform)
            {
                Debug.Log($"[ChimeraHairMaster] 色変換スキップ（無効）: {component.gameObject.name}");
                return;
            }

            Debug.Log($"[ChimeraHairMaster] 色変換処理開始: {component.gameObject.name}");

            // 基準色を決定
            Color sourceColor = DetermineSourceColor(component);

            // 設定を作成
            ColorTransformSettings baseSettings = ColorTransformSettings.FromComponent(component, sourceColor);

            Debug.Log($"[ChimeraHairMaster] 色変換モード: {baseSettings.Mode}, ソース色: {sourceColor}, ターゲット色: {baseSettings.TargetColor}");

            // 統合対象のサブメッシュを取得
            var includedSubmeshes = component.GetIncludedSubmeshes();

            // サブメッシュごとにグループ化（rendererIndex別）
            var submeshesByRenderer = new Dictionary<int, List<int>>();
            foreach (var (rendererIndex, submeshIndex) in includedSubmeshes)
            {
                if (!submeshesByRenderer.ContainsKey(rendererIndex))
                    submeshesByRenderer[rendererIndex] = new List<int>();
                submeshesByRenderer[rendererIndex].Add(submeshIndex);
            }

            // 塗り感統一: お手本 Renderer の処理済みテクスチャと band 情報を事前計算
            // プレビューと同じ inline 方式を採用することで、per-renderer 調整（明度/ブラー/マスク）下でも適用可能にする
            StrandPatternApplier.RefData strandRef = null;
            try
            {
                strandRef = StrandPatternApplier.PrepareRefData(component, baseSettings, pixelCache);

                // 各Rendererのテクスチャを処理（統合対象のサブメッシュのみ）
                foreach (var kvp in submeshesByRenderer)
                {
                    int rendererIndex = kvp.Key;
                    var submeshIndices = kvp.Value;

                    var renderer = component.targetRenderers[rendererIndex];
                    if (renderer == null) continue;

                    // Renderer単位の明度オフセットとブラー／シャープ強度を取得
                    float brightnessOffset = GetRendererBrightnessOffset(component, rendererIndex);
                    float blurSharp = GetRendererBlurSharp(component, rendererIndex);

                    ProcessRendererTextures(context, component, rendererIndex, renderer, submeshIndices, baseSettings, textureCache, pixelCache, brightnessOffset, blurSharp, strandRef);
                }

                Debug.Log($"[ChimeraHairMaster] 色変換処理完了: {component.gameObject.name}, 処理テクスチャ数: {textureCache.Count}");
            }
            finally
            {
                // 塗り感統一の参照テクスチャを解放
                strandRef?.Dispose();
                // Pass 内で作成した readable テクスチャを解放
                pixelCache.Dispose();
            }
        }

        /// <summary>
        /// 基準色（ソース色）を決定
        /// 最初のRendererのテクスチャから色を抽出
        /// </summary>
        private Color DetermineSourceColor(ChimeraHairMaster component)
        {
            if (component.targetRenderers.Count > 0 && component.targetRenderers[0] != null)
            {
                var materials = component.targetRenderers[0].sharedMaterials;
                foreach (var mat in materials)
                {
                    if (mat != null && mat.HasProperty("_MainTex"))
                    {
                        var tex = mat.GetTexture("_MainTex") as Texture2D;
                        if (tex != null)
                        {
                            return ColorProcessor.ExtractDominantColor(tex);
                        }
                    }
                }
            }
            return Color.white;
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
        /// Renderer単位のブラー／シャープ強度を取得
        /// </summary>
        private float GetRendererBlurSharp(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBlurSharpAdjustments == null) return 0f;

            foreach (var adj in component.rendererBlurSharpAdjustments)
            {
                if (adj.rendererIndex == rendererIndex)
                {
                    return adj.blurSharp;
                }
            }

            return 0f;
        }

        private void ProcessRendererTextures(
            BuildContext context,
            ChimeraHairMaster component,
            int rendererIndex,
            SkinnedMeshRenderer renderer,
            List<int> submeshIndices,
            ColorTransformSettings baseSettings,
            Dictionary<Texture2D, Texture2D> textureCache,
            MeshUVSampler.PixelCache pixelCache,
            float brightnessOffset = 0f,
            float blurSharp = 0f,
            StrandPatternApplier.RefData strandRef = null)
        {
            var materials = renderer.sharedMaterials;
            var newMaterials = new Material[materials.Length];

            // 明度オフセットがある場合はログ出力
            if (Mathf.Abs(brightnessOffset) > 0.001f)
            {
                Debug.Log($"[ChimeraHairMaster] Renderer '{renderer.name}' に明度オフセット {brightnessOffset:F2} を適用");
            }

            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null)
                {
                    newMaterials[i] = null;
                    continue;
                }

                // 統合対象外のサブメッシュはスキップ（元のマテリアルをそのまま使用）
                if (!submeshIndices.Contains(i))
                {
                    newMaterials[i] = material;
                    continue;
                }

                // マテリアルを複製
                var newMaterial = new Material(material);
                newMaterial.name = material.name + "_CHM";

                // 色変更対象のテクスチャを処理
                foreach (var slot in component.colorChangeTargets)
                {
                    if (!slot.applyColorChange) continue;
                    if (!newMaterial.HasProperty(slot.propertyName)) continue;

                    var texture = newMaterial.GetTexture(slot.propertyName) as Texture2D;
                    if (texture == null) continue;

                    // Renderer単位のキャッシュキー（明度オフセット・ブラー/シャープ込み）
                    bool hasOffset = Mathf.Abs(brightnessOffset) > 0.001f;
                    bool hasBlurSharp = Mathf.Abs(blurSharp) > 0.001f;
                    // 塗り感統一: _MainTex かつお手本以外の Renderer は per-renderer で結果が変わるためキャッシュ共有不可
                    bool applyStrand = strandRef != null
                        && slot.propertyName == "_MainTex"
                        && rendererIndex != strandRef.RefIndex;
                    bool useSharedCache = !hasOffset && !hasBlurSharp && !applyStrand;
                    bool compressIntermediate = !applyStrand;

                    // テクスチャ毎に Oklab/RGBDelta 用の事前計算を行った settings を構築
                    // ※ 代表色抽出は元テクスチャから（ブラー/シャープの影響を受けない）
                    var settings = MeshUVSampler.PrepareSettingsWithUVStats(baseSettings, renderer, submeshIndices, texture, pixelCache);

                    // UV 使用領域マスクを生成（dilation と前処理ブラー/シャープで共用）
                    bool[] uvMask = MeshUVRasterizer.Rasterize(
                        renderer, submeshIndices, texture.width, texture.height);

                    // キャッシュを確認（共有可能な場合のみ）
                    Texture2D processedTexture = null;
                    bool foundInCache = false;
                    if (useSharedCache && textureCache.TryGetValue(texture, out var cached))
                    {
                        processedTexture = cached;
                        foundInCache = true;
                    }

                    if (!foundInCache)
                    {
                        // 色変換の入力テクスチャ（ブラー/シャープ前処理を適用）
                        Texture2D colorTransformInput = texture;
                        Texture2D preprocessed = null;
                        if (hasBlurSharp)
                        {
                            preprocessed = TextureBlurSharpener.Process(texture, blurSharp, uvMask);
                            if (preprocessed != null)
                            {
                                colorTransformInput = preprocessed;
                            }
                        }

                        // 色変換を実行
                        processedTexture = ColorProcessor.ProcessTexture(colorTransformInput, settings, compressResult: compressIntermediate);

                        // 前処理用の中間テクスチャを破棄
                        if (preprocessed != null)
                        {
                            Object.DestroyImmediate(preprocessed);
                        }

                        // 明度オフセットを適用
                        if (processedTexture != null && hasOffset)
                        {
                            var offsetApplied = ColorProcessor.ApplyBrightnessOffset(processedTexture, brightnessOffset, compressResult: compressIntermediate);
                            if (offsetApplied != null)
                            {
                                Object.DestroyImmediate(processedTexture);
                                processedTexture = offsetApplied;
                            }
                        }

                        // UV 使用領域外を edge dilation で塗り足し
                        if (processedTexture != null)
                        {
                            var dilated = ColorProcessor.DilateTexture(processedTexture, uvMask, 8, compressResult: compressIntermediate);
                            if (dilated != null)
                            {
                                Object.DestroyImmediate(processedTexture);
                                processedTexture = dilated;
                            }
                        }

                        if (processedTexture != null && useSharedCache)
                        {
                            textureCache[texture] = processedTexture;
                        }
                    }

                    // 色合わせ無視マスクを適用
                    // 注: useSharedCache=true 時は processedTexture が textureCache 内に登録済みのため、
                    // 旧 processedTexture を破棄せず参照だけ差し替える（cache に未マスク版が残るのは仕様）
                    if (processedTexture != null)
                    {
                        var masked = Processing.ColorMaskApplier.TryApply(component, rendererIndex, i, texture, processedTexture, compressResult: compressIntermediate);
                        if (masked != null)
                        {
                            processedTexture = masked;
                        }
                    }

                    // 塗り感統一を inline 適用（_MainTex かつお手本以外の Renderer のみ）
                    // useSharedCache を false に強制しているので processedTexture は per-renderer 固有のインスタンス
                    if (applyStrand && processedTexture != null)
                    {
                        var composed = StrandPatternApplier.TryComposeStrand(
                            processedTexture,
                            renderer,
                            submeshIndices,
                            strandRef,
                            texture.format,
                            compressResult: !component.enableMeshMerge);
                        if (composed != null && composed != processedTexture)
                        {
                            Object.DestroyImmediate(processedTexture);
                            processedTexture = composed;
                        }
                    }

                    // 処理済みテクスチャを設定
                    if (processedTexture != null)
                    {
                        newMaterial.SetTexture(slot.propertyName, processedTexture);
                    }
                }

                newMaterials[i] = newMaterial;
            }

            // 新しいマテリアルを適用
            renderer.sharedMaterials = newMaterials;
        }

    }
}
