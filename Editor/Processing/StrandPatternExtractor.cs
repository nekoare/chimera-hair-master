#nullable enable
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 毛束パターンテンプレ抽出
    /// お手本テクスチャから高周波成分（毛束模様のディテール）を抽出する。
    /// 出力は (src - blurred) * 0.5 + 0.5 のオフセットエンコード（RGBA32, α=1）。
    /// </summary>
    public static class StrandPatternExtractor
    {
        // 既定の Gaussian sigma（呼び出し側で上書き可能）
        public const float DefaultSigma = 5.0f;

        /// <summary>
        /// 入力テクスチャから高周波成分を抽出したテクスチャを返す。
        /// 呼び出し側が Object.DestroyImmediate で破棄すること。
        /// </summary>
        /// <param name="source">入力テクスチャ</param>
        /// <param name="sigma">Gaussian カーネルの標準偏差（毛束スケール）</param>
        /// <returns>高周波テクスチャ。GPU非対応などで失敗した場合は null</returns>
        public static Texture2D? ExtractHighFrequency(Texture2D source, float sigma = DefaultSigma)
        {
            if (source == null) return null;

            var shader = ShaderUtils.GetStrandPatternHighPassShader();
            if (shader == null || !ShaderUtils.IsGPUSupported())
            {
                Debug.LogWarning("[ChimeraHairMaster] StrandPatternHighPass: GPU未対応のため抽出をスキップ");
                return null;
            }

            float clampedSigma = Mathf.Clamp(sigma, 0.5f, 30f);
            int width = source.width;
            int height = source.height;
            int kernelRadius = Mathf.Max(1, Mathf.CeilToInt(clampedSigma * 2f));

            var readableSource = ColorProcessor.GetReadableTexture(source);

            RenderTexture? srcRT = null;
            RenderTexture? interRT = null;
            RenderTexture? blurredRT = null;
            RenderTexture? targetRT = null;

            try
            {
                srcRT = CreateRT(width, height);
                Graphics.Blit(readableSource, srcRT);

                interRT = CreateRT(width, height);
                blurredRT = CreateRT(width, height);
                targetRT = CreateRT(width, height);

                int kH = shader.FindKernel("CSBlurHorizontal");
                int kV = shader.FindKernel("CSBlurVertical");
                int kE = shader.FindKernel("CSExtractHighFrequency");

                shader.SetInt("_Width", width);
                shader.SetInt("_Height", height);
                shader.SetFloat("_Sigma", clampedSigma);
                shader.SetInt("_KernelRadius", kernelRadius);

                int groupsX = Mathf.CeilToInt(width / 16f);
                int groupsY = Mathf.CeilToInt(height / 16f);

                // 水平 blur: src → inter
                shader.SetTexture(kH, "_Source", srcRT);
                shader.SetTexture(kH, "_Intermediate", interRT);
                shader.Dispatch(kH, groupsX, groupsY, 1);

                // 垂直 blur: inter → blurred
                shader.SetTexture(kV, "_Source", srcRT);
                shader.SetTexture(kV, "_Intermediate", interRT);
                shader.SetTexture(kV, "_Blurred", blurredRT);
                shader.Dispatch(kV, groupsX, groupsY, 1);

                // 高周波抽出: src - blurred → target（オフセットエンコード）
                shader.SetTexture(kE, "_Source", srcRT);
                shader.SetTexture(kE, "_Blurred", blurredRT);
                shader.SetTexture(kE, "_Target", targetRT);
                shader.Dispatch(kE, groupsX, groupsY, 1);

                // linear=true でマーク: Composer の SRV 読み込み時に sRGB 自動変換を回避する
                // （中立値 128/255 が gamma decode されると約 0.216 になって detail が負バイアス）
                var output = new Texture2D(width, height, TextureFormat.RGBA32, false, /*linear*/ true);
                output.name = source.name + "_HighPass";
                var prev = RenderTexture.active;
                RenderTexture.active = targetRT;
                output.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                output.Apply(false);
                RenderTexture.active = prev;

                return output;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] StrandPatternHighPass エラー: {ex}");
                return null;
            }
            finally
            {
                RenderTexture.active = null;
                if (srcRT != null) { srcRT.Release(); Object.DestroyImmediate(srcRT); }
                if (interRT != null) { interRT.Release(); Object.DestroyImmediate(interRT); }
                if (blurredRT != null) { blurredRT.Release(); Object.DestroyImmediate(blurredRT); }
                if (targetRT != null) { targetRT.Release(); Object.DestroyImmediate(targetRT); }
                if (readableSource != source) Object.DestroyImmediate(readableSource);
            }
        }

        private static RenderTexture CreateRT(int width, int height)
        {
            // BlurSharpener と同じ色空間扱い（UAV アクセスで byte 値そのまま処理）
            // → Composer 側の sRGB 扱いと整合させる（MVP の近似）
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                sRGB = true,
            };
            var rt = new RenderTexture(desc);
            rt.Create();
            return rt;
        }
    }
}
