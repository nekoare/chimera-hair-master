#nullable enable
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 色変換前処理用のブラー／シャープフィルタ
    /// Photoshop の Gaussian Blur / Unsharp Mask 相当。
    /// UV 使用領域マスクを渡すとマスク外のピクセルはスキップ（元のまま保持）される。
    /// </summary>
    public static class TextureBlurSharpener
    {
        /// <summary>
        /// 一体化スライダー（-1 〜 +1）からテクスチャを前処理する
        /// strength=0 は元テクスチャをコピーして返す（呼び出し側が Destroy 責任を持つのを維持するため）
        /// </summary>
        /// <param name="source">入力テクスチャ</param>
        /// <param name="strength">-1(最大ブラー) 〜 0(無変換) 〜 +1(最大シャープ)</param>
        /// <param name="uvMask">UV 使用領域マスク（null ならテクスチャ全体に適用）</param>
        public static Texture2D? Process(Texture2D source, float strength, bool[]? uvMask)
        {
            if (source == null) return null;
            if (Mathf.Abs(strength) < 0.001f) return CopyTexture(source);

            var shader = ShaderUtils.GetBlurSharpenShader();
            if (shader == null || !ShaderUtils.IsGPUSupported())
            {
                Debug.LogWarning("[ChimeraHairMaster] BlurSharpen: GPU未対応のため前処理をスキップ");
                return CopyTexture(source);
            }

            // --- パラメータ決定（strength → sigma/amount）
            bool isSharpen = strength > 0f;
            float absStr = Mathf.Abs(strength);
            float sigma = 1.5f; // 内部でセパラブルブラーに使う σ（固定）
            int kernelRadius = Mathf.Max(1, Mathf.CeilToInt(sigma * 2f)); // 2σ cutoff

            // ブラー側は「ダウンサンプル factor」で強度を制御（-1 でほぼ単色化レベル）
            // absStr=0.2 ごとに factor を 2 倍、最大 32 倍まで
            int downsampleLog = isSharpen ? 0 : Mathf.Clamp(Mathf.FloorToInt(absStr * 5f), 0, 5);
            int downsampleFactor = 1 << downsampleLog;

            int width = source.width;
            int height = source.height;
            int workW = Mathf.Max(4, width / downsampleFactor);
            int workH = Mathf.Max(4, height / downsampleFactor);
            bool useMaskOnWork = downsampleFactor == 1 && uvMask != null; // ダウンサンプルするとマスクが合わなくなるのでスキップ

            RenderTexture? srcRT = null;
            RenderTexture? workSrcRT = null;
            RenderTexture? interRT = null;
            RenderTexture? blurredRT = null;
            RenderTexture? targetRT = null;
            ComputeBuffer? maskBuffer = null;

            try
            {
                // 元解像度ソース（シャープ時の参照用）
                srcRT = CreateRT(width, height);
                Graphics.Blit(source, srcRT);

                // 作業解像度ソース（ブラー時は縮小、シャープ時は元解像度）
                if (downsampleFactor > 1)
                {
                    workSrcRT = CreateRT(workW, workH);
                    Graphics.Blit(srcRT, workSrcRT); // bilinear でダウンサンプル
                }
                else
                {
                    workSrcRT = srcRT;
                }

                interRT = CreateRT(workW, workH);
                targetRT = CreateRT(width, height);

                // UV マスク（packed uint[]、作業解像度が元解像度と同じ時だけ有効）
                int wordCount = Mathf.Max(1, (workW * workH + 31) / 32);
                uint[] packed = new uint[wordCount];
                if (useMaskOnWork && uvMask != null && uvMask.Length == workW * workH)
                {
                    for (int i = 0; i < uvMask.Length; i++)
                        if (uvMask[i]) packed[i >> 5] |= 1u << (i & 31);
                }
                else
                {
                    for (int i = 0; i < wordCount; i++) packed[i] = 0xFFFFFFFFu;
                }
                maskBuffer = new ComputeBuffer(wordCount, sizeof(uint));
                maskBuffer.SetData(packed);

                int kH = shader.FindKernel("CSBlurHorizontal");
                int kV = shader.FindKernel("CSBlurVertical");
                int kS = shader.FindKernel("CSUnsharpComposite");

                shader.SetInt("_Width", workW);
                shader.SetInt("_Height", workH);
                shader.SetInt("_UseUVMask", useMaskOnWork ? 1 : 0);
                shader.SetFloat("_Sigma", sigma);
                shader.SetInt("_KernelRadius", kernelRadius);

                int groupsX = Mathf.CeilToInt(workW / 16f);
                int groupsY = Mathf.CeilToInt(workH / 16f);

                // 水平 pass: workSrc → inter
                shader.SetTexture(kH, "_Source", workSrcRT);
                shader.SetTexture(kH, "_Intermediate", interRT);
                shader.SetBuffer(kH, "_UVMask", maskBuffer);
                shader.Dispatch(kH, groupsX, groupsY, 1);

                if (isSharpen)
                {
                    // 垂直 pass: inter → blurred（シャープ用にblurを作る）
                    blurredRT = CreateRT(workW, workH);
                    shader.SetTexture(kV, "_Source", workSrcRT);
                    shader.SetTexture(kV, "_Intermediate", interRT);
                    shader.SetTexture(kV, "_Target", blurredRT);
                    shader.SetBuffer(kV, "_UVMask", maskBuffer);
                    shader.Dispatch(kV, groupsX, groupsY, 1);

                    // アンシャープマスク合成: src + (src - blurred) * amount
                    float amount = absStr; // strength=+1 で amount=1.0
                    shader.SetFloat("_SharpenAmount", amount);
                    shader.SetTexture(kS, "_Source", workSrcRT);
                    shader.SetTexture(kS, "_Blurred", blurredRT);
                    shader.SetTexture(kS, "_Target", targetRT);
                    shader.SetBuffer(kS, "_UVMask", maskBuffer);
                    shader.Dispatch(kS, groupsX, groupsY, 1);
                }
                else
                {
                    // 垂直 pass: inter → 一旦作業解像度の blurred へ
                    blurredRT = CreateRT(workW, workH);
                    shader.SetTexture(kV, "_Source", workSrcRT);
                    shader.SetTexture(kV, "_Intermediate", interRT);
                    shader.SetTexture(kV, "_Target", blurredRT);
                    shader.SetBuffer(kV, "_UVMask", maskBuffer);
                    shader.Dispatch(kV, groupsX, groupsY, 1);

                    // アップサンプルして元解像度の target に
                    Graphics.Blit(blurredRT, targetRT);
                }

                var output = new Texture2D(width, height, TextureFormat.RGBA32, true);
                output.name = source.name + "_BlurSharpen";
                var prev = RenderTexture.active;
                RenderTexture.active = targetRT;
                output.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                output.Apply(true);
                RenderTexture.active = prev;

                ColorProcessor.CopyTextureSettings(source, output);
                ShaderUtils.EnableMipStreaming(output);
                return output;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] BlurSharpen エラー: {ex}");
                return CopyTexture(source);
            }
            finally
            {
                RenderTexture.active = null;
                if (workSrcRT != null && workSrcRT != srcRT) { workSrcRT.Release(); Object.DestroyImmediate(workSrcRT); }
                if (srcRT != null) { srcRT.Release(); Object.DestroyImmediate(srcRT); }
                if (interRT != null) { interRT.Release(); Object.DestroyImmediate(interRT); }
                if (blurredRT != null) { blurredRT.Release(); Object.DestroyImmediate(blurredRT); }
                if (targetRT != null) { targetRT.Release(); Object.DestroyImmediate(targetRT); }
                maskBuffer?.Dispose();
            }
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
            copy.name = source.name + "_BlurSharpenCopy";
            copy.SetPixels(readable.GetPixels());
            copy.Apply(true);
            ColorProcessor.CopyTextureSettings(source, copy);
            ShaderUtils.EnableMipStreaming(copy);
            if (readable != source) Object.DestroyImmediate(readable);
            return copy;
        }
    }
}
