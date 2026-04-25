#nullable enable
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 毛束パターン合成（Phase 2 approach B: ラプラシアンピラミッド + 2 バンドゲイン転送）
    /// target 自身の高/中バンドを reference と同じコントラスト振幅に rescale する
    /// </summary>
    public static class StrandPatternComposer
    {
        /// <summary>
        /// σ_h と σ_l による 2 バンド分解: B_high = source - blur(σ_h), B_mid = blur(σ_h) - blur(σ_l)
        /// 派生時は target の bandHigh と hp8 の 2 つの highpass テクスチャを渡す
        /// </summary>
        public static Texture2D? ComposeBands(
            Texture2D target,
            Texture2D bandHigh,
            Texture2D hp8,
            bool[] uvMask,
            float ratioHigh,
            float ratioMid,
            float strengthFine,
            float strengthShade)
        {
            if (target == null || bandHigh == null || hp8 == null || uvMask == null) return null;
            if (uvMask.Length != target.width * target.height) return null;
            if (target.width != bandHigh.width || target.height != bandHigh.height) return null;
            if (target.width != hp8.width || target.height != hp8.height) return null;

            var shader = ShaderUtils.GetStrandPatternComposeShader();
            if (shader == null || !ShaderUtils.IsGPUSupported())
            {
                Debug.LogWarning("[ChimeraHairMaster] StrandPatternCompose: GPU未対応のため合成をスキップ");
                return null;
            }

            int width = target.width;
            int height = target.height;
            float scaleHigh = (ratioHigh - 1f) * Mathf.Clamp01(strengthFine);
            float scaleMid = (ratioMid - 1f) * Mathf.Clamp01(strengthShade);

            // 両 scale が実効ゼロなら target そのまま
            if (Mathf.Abs(scaleHigh) < 1e-4f && Mathf.Abs(scaleMid) < 1e-4f)
            {
                return CopyTexture(target);
            }

            int wordCount = Mathf.Max(1, (width * height + 31) / 32);
            uint[] packed = new uint[wordCount];
            for (int i = 0; i < uvMask.Length; i++)
            {
                if (uvMask[i]) packed[i >> 5] |= 1u << (i & 31);
            }

            var readableTarget = ColorProcessor.GetReadableTexture(target);

            RenderTexture? targetRT = null;
            ComputeBuffer? maskBuffer = null;

            try
            {
                maskBuffer = new ComputeBuffer(wordCount, sizeof(uint));
                maskBuffer.SetData(packed);

                targetRT = CreateRT(width, height);
                Graphics.Blit(readableTarget, targetRT);

                int kC = shader.FindKernel("CSCompose");
                shader.SetInt("_Width", width);
                shader.SetInt("_Height", height);
                shader.SetFloat("_ScaleHigh", scaleHigh);
                shader.SetFloat("_ScaleMid", scaleMid);

                shader.SetTexture(kC, "_Target", targetRT);
                shader.SetTexture(kC, "_BandHigh", bandHigh);
                shader.SetTexture(kC, "_BandHP8", hp8);
                shader.SetBuffer(kC, "_Mask", maskBuffer);

                int groupsX = Mathf.CeilToInt(width / 16f);
                int groupsY = Mathf.CeilToInt(height / 16f);
                shader.Dispatch(kC, groupsX, groupsY, 1);

                var output = new Texture2D(width, height, TextureFormat.RGBA32, true);
                output.name = target.name + "_StrandComposed";
                var prev = RenderTexture.active;
                RenderTexture.active = targetRT;
                output.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                output.Apply(true);
                RenderTexture.active = prev;

                ShaderUtils.EnableMipStreaming(output);
                return output;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] StrandPatternCompose エラー: {ex}");
                return null;
            }
            finally
            {
                RenderTexture.active = null;
                if (targetRT != null) { targetRT.Release(); Object.DestroyImmediate(targetRT); }
                maskBuffer?.Dispose();
                if (readableTarget != target) Object.DestroyImmediate(readableTarget);
            }
        }

        /// <summary>
        /// 2 バンドの std を計算（mask 内、平均 0 仮定）
        /// stdHigh: B_high の std
        /// stdMid: B_mid (= hp8 - bandHigh の差) の std
        /// </summary>
        public static (float stdHigh, float stdMid) ComputeBandStds(
            Texture2D bandHigh, Texture2D hp8, bool[] uvMask)
        {
            if (bandHigh == null || hp8 == null || uvMask == null) return (0f, 0f);
            if (uvMask.Length != bandHigh.width * bandHigh.height) return (0f, 0f);
            if (bandHigh.width != hp8.width || bandHigh.height != hp8.height) return (0f, 0f);

            var bhPixels = bandHigh.GetPixels();
            var hpPixels = hp8.GetPixels();
            double sumSqHigh = 0, sumSqMid = 0;
            int count = 0;
            for (int i = 0; i < bhPixels.Length; i++)
            {
                if (!uvMask[i]) continue;
                // decode signed
                double bh_r = (bhPixels[i].r - 0.5) * 2.0;
                double bh_g = (bhPixels[i].g - 0.5) * 2.0;
                double bh_b = (bhPixels[i].b - 0.5) * 2.0;
                double hp_r = (hpPixels[i].r - 0.5) * 2.0;
                double hp_g = (hpPixels[i].g - 0.5) * 2.0;
                double hp_b = (hpPixels[i].b - 0.5) * 2.0;
                // luma weighted
                double lumaHigh = 0.299 * bh_r + 0.587 * bh_g + 0.114 * bh_b;
                double lumaMid = 0.299 * (hp_r - bh_r) + 0.587 * (hp_g - bh_g) + 0.114 * (hp_b - bh_b);
                sumSqHigh += lumaHigh * lumaHigh;
                sumSqMid += lumaMid * lumaMid;
                count++;
            }
            if (count == 0) return (0f, 0f);
            return ((float)System.Math.Sqrt(sumSqHigh / count),
                    (float)System.Math.Sqrt(sumSqMid / count));
        }

        private static RenderTexture CreateRT(int width, int height)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                sRGB = true,
            };
            var rt = new RenderTexture(desc);
            rt.Create();
            return rt;
        }

        private static Texture2D CopyTexture(Texture2D source)
        {
            var readable = ColorProcessor.GetReadableTexture(source);
            var copy = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, true);
            copy.name = source.name + "_Copy";
            copy.SetPixels(readable.GetPixels());
            copy.Apply(true);
            ShaderUtils.EnableMipStreaming(copy);
            if (readable != source) Object.DestroyImmediate(readable);
            return copy;
        }
    }
}
