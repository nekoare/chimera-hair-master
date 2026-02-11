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
            ColorTransformSettings settings = ColorTransformSettings.FromComponent(component, sourceColor);

            Debug.Log($"[ChimeraHairMaster] 色変換モード: {settings.Mode}, ソース色: {sourceColor}, ターゲット色: {settings.TargetColor}");

            // 輝度統一が有効な場合、事前にすべてのテクスチャを収集して明度を調整
            // 注：輝度統一はHueShiftモード専用の機能
            Dictionary<Texture2D, Texture2D> brightnessAdjustedCache = null;
            if (settings.Mode == ColorTransformMode.HueShift && settings.BrightnessUnifyMode != BrightnessUnifyMode.Off)
            {
                brightnessAdjustedCache = ApplyBrightnessUnification(component, settings.BrightnessUnifyMode);
            }

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

            // 各Rendererのテクスチャを処理（統合対象のサブメッシュのみ）
            foreach (var kvp in submeshesByRenderer)
            {
                int rendererIndex = kvp.Key;
                var submeshIndices = kvp.Value;

                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null) continue;

                // Renderer単位の明度オフセットを取得
                float brightnessOffset = GetRendererBrightnessOffset(component, rendererIndex);

                ProcessRendererTextures(context, component, renderer, submeshIndices, settings, textureCache, brightnessAdjustedCache, brightnessOffset);
            }

            Debug.Log($"[ChimeraHairMaster] 色変換処理完了: {component.gameObject.name}, 処理テクスチャ数: {textureCache.Count}");
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
        /// 輝度統一を適用
        /// </summary>
        private Dictionary<Texture2D, Texture2D> ApplyBrightnessUnification(ChimeraHairMaster component, BrightnessUnifyMode mode)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            var textureBrightnessMap = new Dictionary<Texture2D, float>();

            // 統合対象のサブメッシュを取得
            var includedSubmeshes = component.GetIncludedSubmeshes();

            // すべてのテクスチャを収集して平均明度を計算（統合対象のみ）
            foreach (var (rendererIndex, submeshIndex) in includedSubmeshes)
            {
                if (rendererIndex >= component.targetRenderers.Count) continue;

                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                if (submeshIndex >= materials.Length) continue;

                var mat = materials[submeshIndex];
                if (mat == null) continue;

                foreach (var slot in component.colorChangeTargets)
                {
                    if (!slot.applyColorChange) continue;
                    if (!mat.HasProperty(slot.propertyName)) continue;

                    var texture = mat.GetTexture(slot.propertyName) as Texture2D;
                    if (texture == null || textureBrightnessMap.ContainsKey(texture)) continue;

                    float avgBrightness = ColorProcessor.CalculateAverageBrightness(texture);
                    textureBrightnessMap[texture] = avgBrightness;
                }
            }

            if (textureBrightnessMap.Count == 0) return result;

            // 最も明るいテクスチャと最も暗いテクスチャの平均明度を取得
            float minBrightness = textureBrightnessMap.Values.Min();
            float maxBrightness = textureBrightnessMap.Values.Max();

            // モードに基づいて目標明度を決定
            float targetBrightness;
            switch (mode)
            {
                case BrightnessUnifyMode.ToBrightest:
                    targetBrightness = maxBrightness;
                    Debug.Log($"[ChimeraHairMaster] 輝度統一: 明るい方に合わせる ({targetBrightness:F3})");
                    break;
                case BrightnessUnifyMode.ToDarkest:
                    targetBrightness = minBrightness;
                    Debug.Log($"[ChimeraHairMaster] 輝度統一: 暗い方に合わせる ({targetBrightness:F3})");
                    break;
                default:
                    return result; // Offの場合は何もしない
            }

            // 各テクスチャの明度を調整
            foreach (var kvp in textureBrightnessMap)
            {
                var adjustedTexture = ColorProcessor.AdjustBrightness(kvp.Key, targetBrightness);
                result[kvp.Key] = adjustedTexture;
            }

            return result;
        }

        private void ProcessRendererTextures(
            BuildContext context,
            ChimeraHairMaster component,
            SkinnedMeshRenderer renderer,
            List<int> submeshIndices,
            ColorTransformSettings settings,
            Dictionary<Texture2D, Texture2D> textureCache,
            Dictionary<Texture2D, Texture2D> brightnessAdjustedCache = null,
            float brightnessOffset = 0f)
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

                    // 輝度調整済みテクスチャがあればそれを使用
                    Texture2D sourceTexture = texture;
                    if (brightnessAdjustedCache != null && brightnessAdjustedCache.TryGetValue(texture, out var adjustedTexture))
                    {
                        sourceTexture = adjustedTexture;
                    }

                    // Renderer単位のキャッシュキー（明度オフセット込み）
                    // 明度オフセットがある場合はRenderer固有のキーを使用
                    string cacheKey = Mathf.Abs(brightnessOffset) > 0.001f
                        ? $"{texture.GetInstanceID()}_{brightnessOffset:F3}"
                        : texture.GetInstanceID().ToString();

                    // キャッシュを確認
                    Texture2D processedTexture = null;
                    bool foundInCache = false;

                    foreach (var kvp in textureCache)
                    {
                        if (kvp.Key == texture && Mathf.Abs(brightnessOffset) < 0.001f)
                        {
                            processedTexture = kvp.Value;
                            foundInCache = true;
                            break;
                        }
                    }

                    if (!foundInCache)
                    {
                        // 色変換を実行
                        processedTexture = ColorProcessor.ProcessTexture(sourceTexture, settings);

                        // 明度オフセットを適用
                        if (processedTexture != null && Mathf.Abs(brightnessOffset) > 0.001f)
                        {
                            var offsetApplied = ColorProcessor.ApplyBrightnessOffset(processedTexture, brightnessOffset);
                            if (offsetApplied != null)
                            {
                                // 元のテクスチャを破棄
                                Object.DestroyImmediate(processedTexture);
                                processedTexture = offsetApplied;
                            }
                        }

                        if (processedTexture != null && Mathf.Abs(brightnessOffset) < 0.001f)
                        {
                            // 共有キャッシュに追加（オフセットなしの場合のみ）
                            textureCache[texture] = processedTexture;
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
