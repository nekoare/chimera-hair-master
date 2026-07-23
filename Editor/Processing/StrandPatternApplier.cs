#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 塗り感統一の共通ヘルパ
    /// - お手本 Renderer の参照データ事前計算 (PrepareRefData)
    /// - 1 テクスチャに strand pattern を適用 (TryComposeStrand)
    /// Phase 2 approach B: 2 バンド分解 (B_high, B_mid) + 各バンドゲイン転送
    /// σ_high = settings.sigma, σ_low = settings.sigma * LowSigmaRatio
    /// </summary>
    public static class StrandPatternApplier
    {
        // σ_low の倍率（σ_high に対する比率）
        private const float LowSigmaRatio = 3.0f;

        /// <summary>σ_low / σ_high の比率定数を外部から取得する用</summary>
        public static float GetLowSigmaRatio() => LowSigmaRatio;

        /// <summary>
        /// component から指定 Renderer 単位のブラー／シャープ強度を取得（無設定時は 0）
        /// </summary>
        private static float GetRendererBlurSharp(ChimeraHairMaster component, int rendererIndex)
        {
            if (component?.rendererBlurSharpAdjustments == null) return 0f;
            foreach (var adj in component.rendererBlurSharpAdjustments)
            {
                if (adj.rendererIndex == rendererIndex) return adj.blurSharp;
            }
            return 0f;
        }

        /// <summary>
        /// お手本 Renderer の参照データ。strand 合成に必要な band テクスチャ・std・各種パラメータを保持する。
        /// 利用後は Dispose で内部 RT を解放すること。
        /// </summary>
        public class RefData
        {
            public int RefIndex = -1;
            public Texture2D? RefBandHigh;
            public Texture2D? RefHP8;
            public float RefStdHigh;
            public float RefStdMid;
            public float SigmaHigh;
            public float SigmaLow;
            public float StrengthFine;
            public float StrengthShade;

            public bool IsValid => RefBandHigh != null && RefHP8 != null;

            public void Dispose()
            {
                if (RefBandHigh != null)
                {
                    Object.DestroyImmediate(RefBandHigh);
                    RefBandHigh = null;
                }
                if (RefHP8 != null)
                {
                    Object.DestroyImmediate(RefHP8);
                    RefHP8 = null;
                }
            }
        }

        /// <summary>
        /// お手本 Renderer から strand 適用に必要な band 情報・std を事前計算する。
        /// 機能無効、お手本 Renderer 解決失敗、band 抽出失敗、ref が flat な場合は null。
        /// 注: ref テクスチャには blur のみ適用。brightnessOffset / colorMask は意図的に適用しない（塗り感のテンプレートとしての一貫性のため）。
        /// パフォーマンス: cachedColorTransform を渡すと、お手本に blur/sharp がない場合に色変換を再実行せず再利用する。
        /// </summary>
        public static RefData? PrepareRefData(
            ChimeraHairMaster component,
            ColorTransformSettings baseSettings,
            MeshUVSampler.PixelCache? pixelCache = null,
            Dictionary<Texture2D, Texture2D>? cachedColorTransform = null)
        {
            var settings = component?.strandPatternSettings;
            if (settings == null || !settings.enabled) return null;
            if (component!.targetRenderers == null || component.targetRenderers.Count == 0) return null;

            int refIndex = settings.referenceRendererIndex;
            if (refIndex < 0 || refIndex >= component.targetRenderers.Count || component.targetRenderers[refIndex] == null)
                refIndex = 0;
            var refRenderer = component.targetRenderers[refIndex];
            if (refRenderer == null) return null;

            float refBlurSharp = GetRendererBlurSharp(component, refIndex);
            float sigmaHigh = settings.sigma;
            float sigmaLow = sigmaHigh * LowSigmaRatio;

            var refMats = refRenderer.sharedMaterials;
            for (int s = 0; s < refMats.Length; s++)
            {
                var rmat = refMats[s];
                if (rmat == null || !rmat.HasProperty("_MainTex")) continue;
                if (!component.IsSubmeshIncluded(refIndex, s)) continue;
                var refTex = rmat.GetTexture("_MainTex") as Texture2D;
                if (refTex == null) continue;

                bool[] uvMask = MeshUVRasterizer.Rasterize(refRenderer, new[] { s }, refTex.width, refTex.height);

                Texture2D? refProcessed;
                bool ownsRefProcessed;

                // 最適化: お手本に blur/sharp がなく外部キャッシュに color-transform 済みのテクスチャがある場合は再利用
                // (BuildColorTransformCache の結果や、ColorTransformPass の textureCache を再利用する想定)
                if (Mathf.Abs(refBlurSharp) < 0.001f
                    && cachedColorTransform != null
                    && cachedColorTransform.TryGetValue(refTex, out var cachedRef)
                    && cachedRef != null)
                {
                    refProcessed = cachedRef;
                    ownsRefProcessed = false;
                }
                else
                {
                    var perTexSettings = MeshUVSampler.PrepareSettingsWithUVStats(baseSettings, refRenderer, new[] { s }, refTex, pixelCache);

                    Texture2D colorInput = refTex;
                    Texture2D? preprocessed = null;
                    if (Mathf.Abs(refBlurSharp) > 0.001f)
                    {
                        preprocessed = TextureBlurSharpener.Process(refTex, refBlurSharp, uvMask);
                        if (preprocessed != null) colorInput = preprocessed;
                    }

                    refProcessed = ColorProcessor.ProcessTexture(colorInput, perTexSettings, compressResult: false);
                    if (preprocessed != null) Object.DestroyImmediate(preprocessed);
                    if (refProcessed == null) continue;

                    if (uvMask != null)
                    {
                        var dilated = ColorProcessor.DilateTexture(refProcessed, uvMask, 8, compressResult: false);
                        if (dilated != null)
                        {
                            Object.DestroyImmediate(refProcessed);
                            refProcessed = dilated;
                        }
                    }
                    ownsRefProcessed = true;
                }

                var bandHigh = StrandPatternExtractor.ExtractHighFrequency(refProcessed, sigmaHigh);
                var hp8 = bandHigh != null ? StrandPatternExtractor.ExtractHighFrequency(refProcessed, sigmaLow) : null;
                if (bandHigh == null || hp8 == null)
                {
                    if (bandHigh != null) Object.DestroyImmediate(bandHigh);
                    if (hp8 != null) Object.DestroyImmediate(hp8);
                    if (ownsRefProcessed) Object.DestroyImmediate(refProcessed);
                    continue;
                }

                var refMaskFull = MeshUVRasterizer.Rasterize(refRenderer, new[] { s }, refProcessed.width, refProcessed.height);
                var (stdHigh, stdMid) = StrandPatternComposer.ComputeBandStds(bandHigh, hp8, refMaskFull);
                if (ownsRefProcessed) Object.DestroyImmediate(refProcessed);

                if (stdHigh < 1e-5f && stdMid < 1e-5f)
                {
                    Object.DestroyImmediate(bandHigh);
                    Object.DestroyImmediate(hp8);
                    Debug.LogWarning("[ChimeraHairMaster] 塗り感統一: reference detail std がほぼゼロ（flat なテクスチャ）");
                    continue;
                }

                Debug.Log($"[ChimeraHairMaster] 塗り感統一: ref=Renderer[{refIndex}] σ_high={sigmaHigh:F2} σ_low={sigmaLow:F2} stdHigh={stdHigh:F4} stdMid={stdMid:F4}");

                return new RefData
                {
                    RefIndex = refIndex,
                    RefBandHigh = bandHigh,
                    RefHP8 = hp8,
                    RefStdHigh = stdHigh,
                    RefStdMid = stdMid,
                    SigmaHigh = sigmaHigh,
                    SigmaLow = sigmaLow,
                    StrengthFine = settings.strengthFine,
                    StrengthShade = settings.strengthShade,
                };
            }

            Debug.LogWarning($"[ChimeraHairMaster] 塗り感統一: お手本 Renderer '{refRenderer.name}' から有効な _MainTex 情報を取得できず");
            return null;
        }

        /// <summary>
        /// 1 つのターゲットテクスチャ (_MainTex 想定) に strand pattern を適用した新規テクスチャを返す。
        /// 失敗時は null（呼び出し側は元テクスチャを維持）。
        /// 呼び出し側で renderer == ref / slot != _MainTex のフィルタをすること。
        /// </summary>
        public static Texture2D? TryComposeStrand(
            Texture2D currentTex,
            SkinnedMeshRenderer renderer,
            IReadOnlyCollection<int> submeshIndices,
            RefData refData,
            TextureFormat? compressionFormat = null,
            bool compressResult = true)
        {
            if (currentTex == null || renderer == null || refData == null || !refData.IsValid) return null;

            var strandMask = MeshUVRasterizer.Rasterize(renderer, submeshIndices, currentTex.width, currentTex.height);
            if (strandMask == null) return null;

            var tBandHigh = StrandPatternExtractor.ExtractHighFrequency(currentTex, refData.SigmaHigh);
            if (tBandHigh == null) return null;

            var tHP8 = StrandPatternExtractor.ExtractHighFrequency(currentTex, refData.SigmaLow);
            if (tHP8 == null)
            {
                Object.DestroyImmediate(tBandHigh);
                return null;
            }

            try
            {
                var (tStdHigh, tStdMid) = StrandPatternComposer.ComputeBandStds(tBandHigh, tHP8, strandMask);
                var (ratioHigh, ratioMid) = StrandPatternComposer.ComputeBandRatios(
                    refData.RefStdHigh, refData.RefStdMid, tStdHigh, tStdMid);

                return StrandPatternComposer.ComposeBands(
                    currentTex, tBandHigh, tHP8, strandMask,
                    ratioHigh, ratioMid,
                    refData.StrengthFine, refData.StrengthShade,
                    compressionFormat, compressResult);
            }
            finally
            {
                Object.DestroyImmediate(tBandHigh);
                Object.DestroyImmediate(tHP8);
            }
        }
    }
}
