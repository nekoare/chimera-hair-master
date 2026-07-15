using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// プレビュー用マテリアルを生成するクラス
    /// </summary>
    public static class PreviewGenerator
    {
        /// <summary>
        /// プレビュー結果
        /// </summary>
        public class PreviewResult
        {
            /// <summary>
            /// 生成されたマテリアル
            /// </summary>
            public Material Material { get; set; }

            /// <summary>
            /// 生成されたアトラステクスチャ
            /// </summary>
            public Dictionary<string, Texture2D> Textures { get; set; }
                = new Dictionary<string, Texture2D>();

            /// <summary>
            /// UV変換情報
            /// </summary>
            public Dictionary<SkinnedMeshRenderer, TextureAtlasBuilder.UVTransform> UVTransforms { get; set; }
                = new Dictionary<SkinnedMeshRenderer, TextureAtlasBuilder.UVTransform>();

            /// <summary>
            /// エラーメッセージ（成功時はnull）
            /// </summary>
            public string Error { get; set; }

            public bool IsSuccess => string.IsNullOrEmpty(Error);
        }

        /// <summary>
        /// プレビューを生成
        /// </summary>
        public static PreviewResult Generate(ChimeraHairMaster component)
        {
            var result = new PreviewResult();

            // バリデーション
            if (component == null)
            {
                result.Error = "コンポーネントがnullです";
                return result;
            }

            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
            {
                result.Error = "対象Rendererが設定されていません";
                return result;
            }

            if (component.baseMaterial == null)
            {
                result.Error = "基準マテリアルが設定されていません";
                return result;
            }

            try
            {
                // 1. 色変換を実行（各テクスチャに対して）
                var processedTextureCache = ProcessColorTransform(component);

                // 2. アトラスを生成
                int resolution = (int)component.textureResolution;
                var atlasResult = TextureAtlasBuilder.Build(component, resolution, processedTextureCache);

                if (atlasResult.AtlasTextures.Count == 0)
                {
                    result.Error = "テクスチャアトラスの生成に失敗しました";
                    CleanupTextures(processedTextureCache);
                    return result;
                }

                result.Textures = atlasResult.AtlasTextures;
                result.UVTransforms = atlasResult.UVTransforms;

                // 3. 出力マテリアルを生成
                result.Material = CreateOutputMaterial(component.baseMaterial, atlasResult.AtlasTextures);

                // キャッシュをクリーンアップ
                CleanupTextures(processedTextureCache);

                return result;
            }
            catch (System.Exception ex)
            {
                result.Error = $"プレビュー生成中にエラーが発生しました: {ex.Message}";
                Debug.LogException(ex);
                return result;
            }
        }

        /// <summary>
        /// 色変換を実行
        /// </summary>
        private static Dictionary<Texture2D, Texture2D> ProcessColorTransform(ChimeraHairMaster component)
        {
            var cache = new Dictionary<Texture2D, Texture2D>();

            // 色合わせが無効な場合は空のキャッシュを返す
            if (!component.enableColorTransform)
            {
                return cache;
            }

            // ターゲット色を取得
            Color targetColor = component.targetColor;

            // 各Rendererのテクスチャを処理
            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;

                var mat = GetMainMaterial(renderer);
                if (mat == null) continue;

                // メインテクスチャを処理
                if (mat.HasProperty("_MainTex"))
                {
                    var tex = mat.GetTexture("_MainTex") as Texture2D;
                    if (tex != null && !cache.ContainsKey(tex))
                    {
                        // ソース色を抽出
                        Color sourceColor = ColorProcessor.ExtractDominantColor(tex);

                        // 色変換設定を作成
                        var settings = new ColorTransformSettings
                        {
                            Mode = component.colorTransformMode,
                            TargetColor = targetColor,
                            SourceColor = sourceColor,
                            GradientCurve = component.gradientCurve,
                            SaturationPreserve = component.saturationPreserve,
                            ValuePreserve = component.valuePreserve,
                            HueShiftAlgorithm = component.hueShiftAlgorithm,
                            OklabHueRetain = component.oklabHueRetain,
                            OklabSaturationToTarget = component.oklabSaturationToTarget,
                            OklabLToTarget = component.oklabLToTarget,
                            OklabLDarkEndRatio = component.oklabLDarkEndRatio,
                            RgbDeltaIntensity = component.rgbDeltaIntensity,
                            RgbDeltaSoftClipZone = component.rgbDeltaSoftClipZone,
                        };

                        // 色変換を実行
                        var processedTex = ColorProcessor.ProcessTexture(tex, settings, compressResult: false);
                        if (processedTex != null)
                        {
                            cache[tex] = processedTex;
                        }
                    }
                }
            }

            return cache;
        }

        /// <summary>
        /// 出力マテリアルを生成
        /// </summary>
        private static Material CreateOutputMaterial(Material baseMaterial, Dictionary<string, Texture2D> atlasTextures)
        {
            // 基準マテリアルをコピー
            var outputMaterial = new Material(baseMaterial);
            outputMaterial.name = baseMaterial.name + "_ChimeraPreview";

            // アトラステクスチャを設定
            foreach (var kvp in atlasTextures)
            {
                if (outputMaterial.HasProperty(kvp.Key))
                {
                    outputMaterial.SetTexture(kvp.Key, kvp.Value);
                }
            }

            return outputMaterial;
        }

        /// <summary>
        /// メインマテリアルを取得
        /// </summary>
        private static Material GetMainMaterial(SkinnedMeshRenderer renderer)
        {
            var materials = renderer.sharedMaterials;
            return materials.Length > 0 ? materials[0] : null;
        }

        /// <summary>
        /// 一時テクスチャをクリーンアップ
        /// </summary>
        private static void CleanupTextures(Dictionary<Texture2D, Texture2D> cache)
        {
            foreach (var tex in cache.Values)
            {
                if (tex != null)
                {
                    Object.DestroyImmediate(tex);
                }
            }
            cache.Clear();
        }

        /// <summary>
        /// プレビュー結果をクリーンアップ
        /// </summary>
        public static void Cleanup(PreviewResult result)
        {
            if (result == null) return;

            // テクスチャを破棄
            foreach (var tex in result.Textures.Values)
            {
                if (tex != null)
                {
                    Object.DestroyImmediate(tex);
                }
            }
            result.Textures.Clear();

            // マテリアルを破棄
            if (result.Material != null)
            {
                Object.DestroyImmediate(result.Material);
                result.Material = null;
            }
        }

        /// <summary>
        /// コンポーネントにプレビュー結果を適用
        /// </summary>
        public static void ApplyToComponent(ChimeraHairMaster component, PreviewResult result)
        {
            if (component == null || result == null || !result.IsSuccess) return;

            // 既存の出力マテリアルをクリーンアップ
            if (component.outputMaterial != null)
            {
                Object.DestroyImmediate(component.outputMaterial);
            }

            // 新しい出力マテリアルを設定
            component.outputMaterial = result.Material;

            // マテリアルをアセットとして保存はしない（一時的なプレビュー）
            EditorUtility.SetDirty(component);
        }
    }
}
