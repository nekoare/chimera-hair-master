#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 毛束パターン統一をビルドパイプラインに適用するヘルパ
    /// Phase 2 approach B: 2 バンド分解 (B_high, B_mid) + 各バンドゲイン転送
    /// σ_high = settings.sigma, σ_low = settings.sigma * 3
    /// </summary>
    public static class StrandPatternApplier
    {
        // σ_low の倍率（σ_high に対する比率）
        private const float LowSigmaRatio = 3.0f;

        public class Result
        {
            public Dictionary<Texture2D, Texture2D> OldToNew = new Dictionary<Texture2D, Texture2D>();
            public List<Texture2D> CreatedTextures = new List<Texture2D>();
            /// <summary>reference の高バンド（source - blur(σ_h))</summary>
            public Texture2D? RefBandHigh;
            /// <summary>reference の hp8 (source - blur(σ_l))</summary>
            public Texture2D? RefHP8;
            public float RefStdHigh;
            public float RefStdMid;
            public int ReferenceRendererIndex = -1;
        }

        public static Result? ApplyToCache(
            ChimeraHairMaster component,
            Dictionary<Texture2D, Texture2D> processedCache)
        {
            if (component == null || processedCache == null) return null;
            var settings = component.strandPatternSettings;
            if (settings == null || !settings.enabled) return null;
            if (processedCache.Count == 0) return null;

            int refIndex = ResolveReferenceRendererIndex(component);
            if (refIndex < 0) return null;

            var refRenderer = component.targetRenderers[refIndex];
            if (refRenderer == null) return null;

            Texture2D? refProcessed = null;
            bool[]? refMask = null;
            if (!TryGetReferenceProcessedAndMask(component, refIndex, processedCache, out refProcessed, out refMask))
            {
                Debug.LogWarning($"[ChimeraHairMaster] 毛束パターン: お手本 Renderer '{refRenderer.name}' の MainTex 情報取得失敗");
                return null;
            }

            float sigmaHigh = settings.sigma;
            float sigmaLow = settings.sigma * LowSigmaRatio;

            var result = new Result();
            result.ReferenceRendererIndex = refIndex;

            var refBandHigh = StrandPatternExtractor.ExtractHighFrequency(refProcessed!, sigmaHigh);
            if (refBandHigh == null) { Debug.LogWarning("[ChimeraHairMaster] 毛束パターン: refBandHigh 抽出失敗"); return null; }
            result.RefBandHigh = refBandHigh;

            var refHP8 = StrandPatternExtractor.ExtractHighFrequency(refProcessed!, sigmaLow);
            if (refHP8 == null) { Debug.LogWarning("[ChimeraHairMaster] 毛束パターン: refHP8 抽出失敗"); DisposeResult(result); return null; }
            result.RefHP8 = refHP8;

            var (refStdHigh, refStdMid) = StrandPatternComposer.ComputeBandStds(refBandHigh, refHP8, refMask!);
            result.RefStdHigh = refStdHigh;
            result.RefStdMid = refStdMid;
            if (refStdHigh < 1e-5f && refStdMid < 1e-5f)
            {
                Debug.LogWarning("[ChimeraHairMaster] 毛束パターン: reference detail std がほぼゼロ（flat なテクスチャ）");
                DisposeResult(result);
                return null;
            }

            var processedToOriginal = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in processedCache)
            {
                if (kv.Value != null && !processedToOriginal.ContainsKey(kv.Value))
                    processedToOriginal[kv.Value] = kv.Key;
            }

            var done = new HashSet<Texture2D>();
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                if (r == refIndex) continue;
                var rend = component.targetRenderers[r];
                if (rend == null) continue;
                var mats = rend.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m == null || !m.HasProperty("_MainTex")) continue;
                    if (!component.IsSubmeshIncluded(r, s)) continue;
                    var currentTex = m.GetTexture("_MainTex") as Texture2D;
                    if (currentTex == null) continue;
                    if (currentTex == refProcessed) continue;
                    if (done.Contains(currentTex)) continue;
                    if (!processedToOriginal.TryGetValue(currentTex, out var original)) continue;

                    var targetMask = MeshUVRasterizer.Rasterize(rend, new[] { s }, currentTex.width, currentTex.height);
                    if (targetMask == null) continue;

                    var tBandHigh = StrandPatternExtractor.ExtractHighFrequency(currentTex, sigmaHigh);
                    if (tBandHigh == null) continue;
                    var tHP8 = StrandPatternExtractor.ExtractHighFrequency(currentTex, sigmaLow);
                    if (tHP8 == null) { Object.DestroyImmediate(tBandHigh); continue; }

                    try
                    {
                        var (tStdHigh, tStdMid) = StrandPatternComposer.ComputeBandStds(tBandHigh, tHP8, targetMask);
                        // 各バンドで std がほぼゼロなら ratio=1（変更なし）にして安全側
                        float ratioHigh = (tStdHigh >= 1e-5f) ? refStdHigh / tStdHigh : 1f;
                        float ratioMid = (tStdMid >= 1e-5f) ? refStdMid / tStdMid : 1f;

                        var composed = StrandPatternComposer.ComposeBands(
                            currentTex, tBandHigh, tHP8, targetMask,
                            ratioHigh, ratioMid, settings.strengthFine, settings.strengthShade);
                        if (composed == null) continue;

                        processedCache[original] = composed;
                        result.OldToNew[currentTex] = composed;
                        result.CreatedTextures.Add(composed);
                        done.Add(currentTex);
                    }
                    finally
                    {
                        Object.DestroyImmediate(tBandHigh);
                        Object.DestroyImmediate(tHP8);
                    }
                }
            }

            return result;
        }

        public static void UpdateMaterialReferences(ChimeraHairMaster component, Result result)
        {
            if (component == null || result == null || result.OldToNew.Count == 0) return;

            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    var mat = mats[mi];
                    if (mat == null) continue;
                    foreach (var slot in component.colorChangeTargets)
                    {
                        if (!slot.applyColorChange) continue;
                        if (!mat.HasProperty(slot.propertyName)) continue;
                        var tex = mat.GetTexture(slot.propertyName) as Texture2D;
                        if (tex == null) continue;
                        if (result.OldToNew.TryGetValue(tex, out var newTex))
                        {
                            mat.SetTexture(slot.propertyName, newTex);
                            changed = true;
                        }
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = mats;
                }
            }
        }

        public static void DisposeResult(Result? result)
        {
            if (result == null) return;
            if (result.RefBandHigh != null)
            {
                Object.DestroyImmediate(result.RefBandHigh);
                result.RefBandHigh = null;
            }
            if (result.RefHP8 != null)
            {
                Object.DestroyImmediate(result.RefHP8);
                result.RefHP8 = null;
            }
        }

        private static int ResolveReferenceRendererIndex(ChimeraHairMaster component)
        {
            if (component.targetRenderers == null || component.targetRenderers.Count == 0) return -1;
            int idx = component.strandPatternSettings.referenceRendererIndex;
            if (idx < 0 || idx >= component.targetRenderers.Count) idx = 0;
            if (component.targetRenderers[idx] == null) idx = 0;
            return component.targetRenderers[idx] != null ? idx : -1;
        }

        private static bool TryGetReferenceProcessedAndMask(
            ChimeraHairMaster component,
            int rIndex,
            Dictionary<Texture2D, Texture2D> processedCache,
            out Texture2D? refProcessed,
            out bool[]? refMask)
        {
            refProcessed = null;
            refMask = null;
            var renderer = component.targetRenderers[rIndex];
            if (renderer == null) return false;
            var mats = renderer.sharedMaterials;
            for (int s = 0; s < mats.Length; s++)
            {
                var mat = mats[s];
                if (mat == null || !mat.HasProperty("_MainTex")) continue;
                if (!component.IsSubmeshIncluded(rIndex, s)) continue;
                var tex = mat.GetTexture("_MainTex") as Texture2D;
                if (tex == null) continue;

                if (processedCache.ContainsValue(tex))
                {
                    refProcessed = tex;
                }
                else if (processedCache.TryGetValue(tex, out var processed))
                {
                    refProcessed = processed;
                }
                else
                {
                    continue;
                }

                refMask = MeshUVRasterizer.Rasterize(renderer, new[] { s }, refProcessed.width, refProcessed.height);
                return refMask != null;
            }
            return false;
        }

        /// <summary>σ_low / σ_high の比率定数を外部から取得する用（Preview の同期に使う）</summary>
        public static float GetLowSigmaRatio() => LowSigmaRatio;
    }
}
