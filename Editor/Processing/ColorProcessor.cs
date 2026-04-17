#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 色変換処理を行うプロセッサ
    /// </summary>
    public static class ColorProcessor
    {
        #region Public Methods

        /// <summary>
        /// テクスチャの色変換を実行（自動でGPU/CPUを選択）
        /// </summary>
        /// <param name="sourceTexture">元テクスチャ</param>
        /// <param name="settings">色変換設定</param>
        /// <param name="preferGPU">GPU処理を優先するか</param>
        /// <returns>変換後のテクスチャ</returns>
        public static Texture2D? ProcessTexture(Texture2D sourceTexture, ColorTransformSettings settings, bool preferGPU = true)
        {
            if (sourceTexture == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source texture is null");
                return null;
            }

            // DXT等の圧縮アーティファクトを回避するため、元画像ファイルから非圧縮版を読み込む
            Texture2D? uncompressedSource = TextureUtils.LoadUncompressed(sourceTexture);
            Texture2D actualSource = uncompressedSource ?? sourceTexture;

            Texture2D? result = null;

            try
            {
                // GPU処理を試みる
                if (preferGPU && ShaderUtils.IsGPUSupported())
                {
                    result = ProcessTextureGPU(actualSource, settings);
                    if (result == null)
                    {
                        Debug.LogWarning("[ChimeraHairMaster] GPU処理に失敗しました。CPU処理にフォールバックします。");
                    }
                }

                // CPU処理
                if (result == null)
                {
                    result = ProcessTextureCPU(actualSource, settings);
                }

                // 元テクスチャと同じ圧縮形式を適用
                if (result != null)
                {
                    CompressToMatch(result, sourceTexture.format);
                }
            }
            finally
            {
                // 非圧縮版の一時テクスチャを破棄
                if (uncompressedSource != null)
                {
                    Object.DestroyImmediate(uncompressedSource);
                }
            }

            return result;
        }

        /// <summary>
        /// テクスチャの色変換を実行（GPU）
        /// </summary>
        public static Texture2D? ProcessTextureGPU(Texture2D sourceTexture, ColorTransformSettings settings)
        {
            if (sourceTexture == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source texture is null");
                return null;
            }

            ComputeShader? shader = ShaderUtils.GetColorTransformShader();
            if (shader == null)
            {
                Debug.LogWarning("[ChimeraHairMaster] ComputeShader not found");
                return null;
            }

            RenderTexture? sourceRT = null;
            RenderTexture? targetRT = null;
            RenderTexture? gradientRT = null;

            try
            {
                int kernel = shader.FindKernel("CSMain");

                // ソースRenderTextureを作成（UAVフラグ付き）
                var sourceDesc = new RenderTextureDescriptor(
                    sourceTexture.width, sourceTexture.height,
                    RenderTextureFormat.ARGB32, 0
                );
                sourceDesc.enableRandomWrite = true;
                sourceDesc.sRGB = true;
                sourceRT = new RenderTexture(sourceDesc);
                sourceRT.Create();
                Graphics.Blit(sourceTexture, sourceRT);

                // ターゲットRenderTextureを作成（UAVフラグ付き）
                var targetDesc = new RenderTextureDescriptor(
                    sourceTexture.width, sourceTexture.height,
                    RenderTextureFormat.ARGB32, 0
                );
                targetDesc.enableRandomWrite = true;
                targetDesc.sRGB = true;
                targetRT = new RenderTexture(targetDesc);
                targetRT.Create();

                // シェーダーパラメータを設定
                shader.SetTexture(kernel, "_Source", sourceRT);
                shader.SetTexture(kernel, "_Target", targetRT);

                // 色変換モード
                shader.SetInt("_TransformMode", (int)settings.Mode);
                shader.SetInt("_HueShiftAlgorithm", (int)settings.HueShiftAlgorithm);

                // ターゲット色をHSVに変換（HSV algorithm 用）
                Color.RGBToHSV(settings.TargetColor, out float targetH, out float targetS, out float targetV);
                shader.SetVector("_TargetColorHSV", new Vector3(targetH, targetS, targetV));

                shader.SetFloat("_SaturationPreserve", settings.SaturationPreserve);
                shader.SetFloat("_ValuePreserve", settings.ValuePreserve);

                // Oklab algorithm 用パラメータ
                Vector3 targetLch = OklabConverter.SRGBToOklch(settings.TargetColor);
                shader.SetVector("_TargetColorOklch", new Vector4(targetLch.x, targetLch.y, targetLch.z, 0f));
                shader.SetFloat("_OklabHueRetain", settings.OklabHueRetain);
                shader.SetFloat("_OklabSaturationToTarget", settings.OklabSaturationToTarget);
                shader.SetFloat("_OklabLToTarget", settings.OklabLToTarget);
                shader.SetFloat("_OklabLDarkEndRatio", settings.OklabLDarkEndRatio);
                shader.SetFloat("_OklabSourceLP95", settings.OklabSourceLP95);
                shader.SetFloat("_OklabSourceLP05", settings.OklabSourceLP05);
                shader.SetFloat("_OklabSourceHDominant", settings.OklabSourceHDominant);

                // RGBDelta モード用パラメータ
                Vector3 sourceLin = OklabConverter.SRGBToLinear(settings.SourceColor);
                Vector3 targetLin = OklabConverter.SRGBToLinear(settings.TargetColor);
                Vector3 diffLin = (targetLin - sourceLin) * settings.RgbDeltaIntensity;
                shader.SetVector("_RgbDeltaLinear", new Vector4(diffLin.x, diffLin.y, diffLin.z, 0f));
                shader.SetFloat("_RgbDeltaSoftClipZone", settings.RgbDeltaSoftClipZone);

                // グラデーションモード用のテクスチャ
                if (settings.Mode == ColorTransformMode.Gradient)
                {
                    gradientRT = CreateGradientRenderTexture(settings.GradientCurve, 256);
                    shader.SetTexture(kernel, "_GradientTexture", gradientRT);
                    shader.SetInt("_GradientResolution", 256);
                }
                else
                {
                    // ダミーテクスチャ（UAVフラグ付き）
                    var dummyDesc = new RenderTextureDescriptor(1, 1, RenderTextureFormat.ARGB32, 0);
                    dummyDesc.enableRandomWrite = true;
                    gradientRT = new RenderTexture(dummyDesc);
                    gradientRT.Create();
                    shader.SetTexture(kernel, "_GradientTexture", gradientRT);
                    shader.SetInt("_GradientResolution", 1);
                }

                // ComputeShaderを実行
                int threadGroupX = Mathf.CeilToInt(sourceTexture.width / 16.0f);
                int threadGroupY = Mathf.CeilToInt(sourceTexture.height / 16.0f);
                shader.Dispatch(kernel, threadGroupX, threadGroupY, 1);

                // 結果をTexture2Dに変換
                Texture2D outputTexture = new Texture2D(
                    sourceTexture.width, sourceTexture.height,
                    TextureFormat.RGBA32, true
                );
                outputTexture.name = sourceTexture.name + "_ColorTransformed";

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = targetRT;
                outputTexture.ReadPixels(new Rect(0, 0, targetRT.width, targetRT.height), 0, 0);
                outputTexture.Apply(true);
                RenderTexture.active = previous;

                // VRChat向けにミップストリーミングを有効化
                ShaderUtils.EnableMipStreaming(outputTexture);

                return outputTexture;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] GPU処理中にエラー: {ex}");
                return null;
            }
            finally
            {
                // RenderTexture.activeをnullにリセット（Releaseする前に必要）
                RenderTexture.active = null;
                
                // クリーンアップ
                if (sourceRT != null)
                {
                    sourceRT.Release();
                    Object.DestroyImmediate(sourceRT);
                }
                if (targetRT != null)
                {
                    targetRT.Release();
                    Object.DestroyImmediate(targetRT);
                }
                if (gradientRT != null)
                {
                    gradientRT.Release();
                    Object.DestroyImmediate(gradientRT);
                }
            }
        }

        /// <summary>
        /// グラデーションからRenderTextureを作成
        /// </summary>
        private static RenderTexture CreateGradientRenderTexture(Gradient gradient, int resolution)
        {
            Texture2D gradientTex = new Texture2D(resolution, 1, TextureFormat.RGBA32, false);
            gradientTex.wrapMode = TextureWrapMode.Clamp;

            for (int i = 0; i < resolution; i++)
            {
                float t = i / (float)(resolution - 1);
                gradientTex.SetPixel(i, 0, gradient.Evaluate(t));
            }
            gradientTex.Apply();

            // UAVフラグ付きでRenderTextureを作成
            var desc = new RenderTextureDescriptor(resolution, 1, RenderTextureFormat.ARGB32, 0);
            desc.enableRandomWrite = true;
            RenderTexture rt = new RenderTexture(desc);
            rt.Create();
            Graphics.Blit(gradientTex, rt);

            Object.DestroyImmediate(gradientTex);

            return rt;
        }

        /// <summary>
        /// テクスチャの色変換を実行（CPU）
        /// </summary>
        public static Texture2D? ProcessTextureCPU(Texture2D sourceTexture, ColorTransformSettings settings)
        {
            if (sourceTexture == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source texture is null");
                return null;
            }

            // 読み取り可能なテクスチャを取得
            Texture2D readableTexture = GetReadableTexture(sourceTexture);

            // 出力テクスチャを作成
            Texture2D outputTexture = new Texture2D(
                readableTexture.width,
                readableTexture.height,
                TextureFormat.RGBA32,
                true
            );
            outputTexture.name = sourceTexture.name + "_ColorTransformed";

            // ピクセルデータを取得
            Color[] pixels = readableTexture.GetPixels();
            Color[] outputPixels = new Color[pixels.Length];

            // 色変換を実行
            for (int i = 0; i < pixels.Length; i++)
            {
                outputPixels[i] = TransformPixel(pixels[i], settings);
            }

            // 結果を適用
            outputTexture.SetPixels(outputPixels);
            outputTexture.Apply(true);

            // VRChat向けにミップストリーミングを有効化
            ShaderUtils.EnableMipStreaming(outputTexture);

            // 一時テクスチャをクリーンアップ
            if (readableTexture != sourceTexture)
            {
                Object.DestroyImmediate(readableTexture);
            }

            return outputTexture;
        }

        /// <summary>
        /// 基準テクスチャから色情報を抽出
        /// </summary>
        public static Color ExtractDominantColor(Texture2D texture)
        {
            if (texture == null) return Color.white;

            Texture2D readableTexture = GetReadableTexture(texture);
            Color[] pixels = readableTexture.GetPixels();

            // 中央付近のピクセルから支配的な色を取得（簡易実装）
            // より正確にはヒストグラム分析やk-means等を使用する
            Vector3 colorSum = Vector3.zero;
            int validCount = 0;

            int centerX = readableTexture.width / 2;
            int centerY = readableTexture.height / 2;
            int sampleRadius = Mathf.Min(readableTexture.width, readableTexture.height) / 4;

            for (int y = centerY - sampleRadius; y < centerY + sampleRadius; y++)
            {
                for (int x = centerX - sampleRadius; x < centerX + sampleRadius; x++)
                {
                    if (x < 0 || x >= readableTexture.width || y < 0 || y >= readableTexture.height)
                        continue;

                    Color pixel = pixels[y * readableTexture.width + x];

                    // 透明ピクセルは除外
                    if (pixel.a < 0.1f) continue;

                    // 彩度が低すぎるピクセルは除外（白/黒/グレー）
                    Color.RGBToHSV(pixel, out float h, out float s, out float v);
                    if (s < 0.1f) continue;

                    colorSum += new Vector3(pixel.r, pixel.g, pixel.b);
                    validCount++;
                }
            }

            if (validCount == 0) return Color.white;

            colorSum /= validCount;

            if (readableTexture != texture)
            {
                Object.DestroyImmediate(readableTexture);
            }

            return new Color(colorSum.x, colorSum.y, colorSum.z, 1f);
        }

        /// <summary>
        /// テクスチャから複数の代表色を抽出
        /// 色相でクラスタリングして代表的な色を返す
        /// </summary>
        /// <param name="texture">対象テクスチャ</param>
        /// <param name="colorCount">抽出する色数</param>
        /// <returns>代表色の配列</returns>
        public static Color[] ExtractDominantColors(Texture2D texture, int colorCount = 5)
        {
            if (texture == null) return new Color[] { Color.white };

            Texture2D readableTexture = GetReadableTexture(texture);

            // テクスチャをダウンサンプリングしてパフォーマンス向上
            int sampleSize = 64;
            var sampledColors = new List<Color>();

            float stepX = (float)readableTexture.width / sampleSize;
            float stepY = (float)readableTexture.height / sampleSize;

            for (int y = 0; y < sampleSize; y++)
            {
                for (int x = 0; x < sampleSize; x++)
                {
                    int px = Mathf.FloorToInt(x * stepX);
                    int py = Mathf.FloorToInt(y * stepY);
                    Color pixel = readableTexture.GetPixel(px, py);

                    // 透明ピクセルは除外
                    if (pixel.a < 0.1f) continue;

                    // 彩度が低すぎるピクセルは除外
                    Color.RGBToHSV(pixel, out float h, out float s, out float v);
                    if (s < 0.15f || v < 0.1f || v > 0.95f) continue;

                    sampledColors.Add(pixel);
                }
            }

            if (readableTexture != texture)
            {
                Object.DestroyImmediate(readableTexture);
            }

            if (sampledColors.Count == 0) return new Color[] { Color.white };

            // 色相でソートしてクラスタリング
            var sortedColors = sampledColors
                .Select(c => {
                    Color.RGBToHSV(c, out float h, out float s, out float v);
                    return new { Color = c, H = h, S = s, V = v };
                })
                .OrderBy(c => c.H)
                .ToList();

            // 等間隔でサンプリングして代表色を選択
            var result = new List<Color>();
            int step = Mathf.Max(1, sortedColors.Count / colorCount);

            for (int i = 0; i < colorCount && i * step < sortedColors.Count; i++)
            {
                // 各クラスタの中央付近から色を取得
                int idx = i * step + step / 2;
                idx = Mathf.Min(idx, sortedColors.Count - 1);
                result.Add(sortedColors[idx].Color);
            }

            // 重複を除去（近い色を除く）
            var filtered = new List<Color>();
            foreach (var color in result)
            {
                Color.RGBToHSV(color, out float h1, out float s1, out float v1);
                bool isDuplicate = false;

                foreach (var existing in filtered)
                {
                    Color.RGBToHSV(existing, out float h2, out float s2, out float v2);
                    float hueDiff = Mathf.Abs(h1 - h2);
                    if (hueDiff > 0.5f) hueDiff = 1f - hueDiff;

                    if (hueDiff < 0.05f && Mathf.Abs(s1 - s2) < 0.1f && Mathf.Abs(v1 - v2) < 0.1f)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    filtered.Add(color);
                }
            }

            return filtered.Count > 0 ? filtered.ToArray() : new Color[] { Color.white };
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 元テクスチャと同じ圧縮形式を適用する
        /// 非圧縮フォーマットの場合は何もしない
        /// </summary>
        private static void CompressToMatch(Texture2D texture, TextureFormat sourceFormat)
        {
            // 圧縮フォーマットへのマッピング
            // EditorUtility.CompressTextureがサポートする形式のみ対応
            TextureFormat? compressedFormat = sourceFormat switch
            {
                TextureFormat.DXT1 or TextureFormat.DXT1Crunched => TextureFormat.DXT1,
                TextureFormat.DXT5 or TextureFormat.DXT5Crunched => TextureFormat.DXT5,
                TextureFormat.BC5 => TextureFormat.BC5,
                TextureFormat.BC7 => TextureFormat.BC7,
                TextureFormat.BC4 => TextureFormat.BC4,
                TextureFormat.BC6H => TextureFormat.BC6H,
                _ => null
            };

            if (compressedFormat.HasValue)
            {
                EditorUtility.CompressTexture(texture, compressedFormat.Value, TextureCompressionQuality.Normal);
            }
        }

        /// <summary>
        /// 1ピクセルの色変換
        /// </summary>
        private static Color TransformPixel(Color pixel, ColorTransformSettings settings)
        {
            // 透明ピクセルはスキップ
            if (pixel.a < 0.01f) return pixel;

            switch (settings.Mode)
            {
                case ColorTransformMode.Gradient:
                    return TransformGradient(pixel, settings);

                case ColorTransformMode.HueShift:
                    return settings.HueShiftAlgorithm == HueShiftAlgorithm.Oklab
                        ? TransformHueShiftOklab(pixel, settings)
                        : TransformHueShift(pixel, settings);

                case ColorTransformMode.RGBDelta:
                    return TransformRGBDelta(pixel, settings);

                default:
                    return pixel;
            }
        }

        /// <summary>
        /// グラデーションモードの変換
        /// 輝度に基づいてグラデーションから色を取得
        /// </summary>
        private static Color TransformGradient(Color pixel, ColorTransformSettings settings)
        {
            // 輝度を計算（人間の視覚に基づく重み付け）
            float luminance = 0.299f * pixel.r + 0.587f * pixel.g + 0.114f * pixel.b;

            // グラデーションから色を取得
            Color gradientColor = settings.GradientCurve.Evaluate(luminance);

            // アルファを保持
            gradientColor.a = pixel.a;

            return gradientColor;
        }

        /// <summary>
        /// 色相シフトモードの変換
        /// HSV空間で色相をシフトし、彩度・明度は元の値を保持可能
        /// </summary>
        private static Color TransformHueShift(Color pixel, ColorTransformSettings settings)
        {
            // 元のピクセルをHSVに変換
            Color.RGBToHSV(pixel, out float pixelH, out float pixelS, out float pixelV);

            // ターゲット色をHSVに変換
            Color.RGBToHSV(settings.TargetColor, out float targetH, out float targetS, out float targetV);

            // ソース色をHSVに変換（基準色がある場合）
            Color.RGBToHSV(settings.SourceColor, out float sourceH, out float sourceS, out float sourceV);

            // 色相のシフト量を計算
            float hueShift = targetH - sourceH;

            // 新しい色相（0-1の範囲にラップ）
            float newH = (pixelH + hueShift) % 1f;
            if (newH < 0) newH += 1f;

            // 彩度の補間
            // saturationPreserve = 1.0 で元の彩度を完全保持
            // saturationPreserve = 0.0 でターゲットの彩度に合わせる
            float saturationRatio = targetS / Mathf.Max(sourceS, 0.001f);
            float newS = Mathf.Lerp(pixelS * saturationRatio, pixelS, settings.SaturationPreserve);
            newS = Mathf.Clamp01(newS);

            // 明度の補間
            // valuePreserve = 1.0 で元の明度を完全保持
            // valuePreserve = 0.0 でターゲットの明度に合わせる
            float valueRatio = targetV / Mathf.Max(sourceV, 0.001f);
            float newV = Mathf.Lerp(pixelV * valueRatio, pixelV, settings.ValuePreserve);
            newV = Mathf.Clamp01(newV);

            // 彩度が非常に低い場合（グレー系）は変換をスキップ
            if (pixelS < 0.05f)
            {
                // グレー系のピクセルは明度のみ調整
                newH = pixelH;
                newS = pixelS;
            }

            // HSVからRGBに戻す
            Color result = Color.HSVToRGB(newH, newS, newV);
            result.a = pixel.a;

            return result;
        }

        /// <summary>
        /// 色相シフトモードの変換（Oklab algorithm）
        /// Oklab の L（明度）・C（彩度）・h（色相）が独立している性質を利用して
        /// 色相を target.h に揃え、彩度・明度を滑らかに target に寄せる。
        /// L の target 寄せは「テクスチャ上位 5% L」を target.L に揃える加算オフセット + ソフトクリップ
        /// </summary>
        private static Color TransformHueShiftOklab(Color pixel, ColorTransformSettings settings)
        {
            // ピクセルを OKLCh に変換
            Vector3 pixLin = OklabConverter.SRGBToLinear(pixel);
            Vector3 pixLab = OklabConverter.LinearRGBToOklab(pixLin);
            Vector3 pixLch = OklabConverter.OklabToOklch(pixLab);

            // target を OKLCh に変換
            Vector3 targetLch = OklabConverter.SRGBToOklch(settings.TargetColor);

            // --- L の補正 ---
            // 線形リマップ: [source.L_p05, source.L_p95] → [target.L * darkEndRatio, target.L]
            // これにより source 側のテクスチャ毎の暗部-明部 range が target 側の
            // 同じ range に圧縮され、複数テクスチャの中間 V でも明度感が揃う。
            // oklabLToTarget で「どれだけリマップを適用するか」を線形補間する。
            float sourceRange = settings.OklabSourceLP95 - settings.OklabSourceLP05;
            float normalized = sourceRange > 1e-5f
                ? (pixLch.x - settings.OklabSourceLP05) / sourceRange
                : 0.5f;
            float targetDarkL = targetLch.x * settings.OklabLDarkEndRatio;
            float mappedL = Mathf.Lerp(targetDarkL, targetLch.x, normalized);
            float newL = Mathf.Lerp(pixLch.x, mappedL, settings.OklabLToTarget);
            // ソフトクリップ（範囲外を [0,1] に緩やかに収束）
            newL = OklabConverter.SoftClip01(newL, 0.05f);

            // --- C（彩度）の補正 ---
            // 元ピクセル彩度 → target.C へ直接 lerp
            float newC = Mathf.Lerp(pixLch.y, targetLch.y, settings.OklabSaturationToTarget);
            newC = Mathf.Max(0f, newC);

            // --- h（色相）の補正 ---
            // 全ピクセルで target.h に統一（hueRetain>0 なら元色相との相対差を一部保持）
            // グレー判定分岐は設けない。彩度が小さいピクセルに target.h を与えても、
            // C が小さければ色として見えないため破綻しない。
            // 以前はグレー判定に入ったピクセルが元色相を保持してしまい、暗い target のとき
            // 「元の色相の差」が浮き出る症状があったため、分岐を撤廃
            float deltaH = pixLch.z - settings.OklabSourceHDominant;
            deltaH = OklabConverter.WrapHueRadians(deltaH);
            float retained = deltaH * settings.OklabHueRetain;
            float newH = OklabConverter.WrapHueRadians(targetLch.z + retained);

            // OKLCh → sRGB に戻す
            Color result = OklchToSRGBWithAlpha(new Vector3(newL, newC, newH), pixel.a);
            return result;
        }

        /// <summary>
        /// RGBDelta モードの変換
        /// テクスチャの代表色（SourceColor）から target への差分を、全ピクセルに加算する。
        /// HSV を経由せず線形 RGB 空間で加算 + ソフトクリップするため、
        /// 彩度ドリフトが発生せず「相対差を保ったまま色味を target に寄せる」挙動になる
        /// </summary>
        private static Color TransformRGBDelta(Color pixel, ColorTransformSettings settings)
        {
            Vector3 pixLin = OklabConverter.SRGBToLinear(pixel);
            Vector3 baseLin = OklabConverter.SRGBToLinear(settings.SourceColor);
            Vector3 targetLin = OklabConverter.SRGBToLinear(settings.TargetColor);

            Vector3 diff = (targetLin - baseLin) * settings.RgbDeltaIntensity;
            Vector3 shifted = pixLin + diff;

            // 各チャンネルをソフトクリップ
            float softZone = settings.RgbDeltaSoftClipZone;
            shifted.x = OklabConverter.SoftClip01(shifted.x, softZone);
            shifted.y = OklabConverter.SoftClip01(shifted.y, softZone);
            shifted.z = OklabConverter.SoftClip01(shifted.z, softZone);

            return OklabConverter.LinearToSRGB(shifted, pixel.a);
        }

        /// <summary>
        /// OKLCh → sRGB（gamut外は彩度を段階的に下げて収める簡易ガミュートマッピング）
        /// </summary>
        private static Color OklchToSRGBWithAlpha(Vector3 lch, float alpha)
        {
            Vector3 lab = OklabConverter.OklchToOklab(lch);
            Vector3 lin = OklabConverter.OklabToLinearRGB(lab);

            // 線形 RGB が範囲外なら C を下げて再変換（最大 8 回）
            // わずかなはみ出し（float 誤差レベル）は許容して過剰な彩度縮小を避ける
            const int maxIter = 8;
            const float eps = 1e-3f;     // float 誤差許容量
            const float shrink = 0.9f;   // 縮小係数（0.75 → 0.9 に緩和）
            float c = lch.y;
            for (int i = 0; i < maxIter; i++)
            {
                if (lin.x >= -eps && lin.x <= 1f + eps &&
                    lin.y >= -eps && lin.y <= 1f + eps &&
                    lin.z >= -eps && lin.z <= 1f + eps)
                    break;
                c *= shrink;
                lab = OklabConverter.OklchToOklab(new Vector3(lch.x, c, lch.z));
                lin = OklabConverter.OklabToLinearRGB(lab);
            }

            lin.x = Mathf.Clamp01(lin.x);
            lin.y = Mathf.Clamp01(lin.y);
            lin.z = Mathf.Clamp01(lin.z);
            return OklabConverter.LinearToSRGB(lin, alpha);
        }

        /// <summary>
        /// 読み取り可能なテクスチャを取得
        /// </summary>
        public static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable) return source;

            // 読み取り不可の場合、RenderTextureを経由してコピー
            RenderTexture tmp = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            );

            Graphics.Blit(source, tmp);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return readable;
        }

        /// <summary>
        /// テクスチャの平均明度（V値）を計算
        /// </summary>
        public static float CalculateAverageBrightness(Texture2D texture)
        {
            if (texture == null) return 0f;

            var readable = GetReadableTexture(texture);
            var pixels = readable.GetPixels();

            float totalV = 0f;
            int count = 0;

            foreach (var pixel in pixels)
            {
                // アルファが0に近いピクセルは無視
                if (pixel.a < 0.01f) continue;

                Color.RGBToHSV(pixel, out _, out _, out float v);
                totalV += v;
                count++;
            }

            if (readable != texture)
            {
                Object.DestroyImmediate(readable);
            }

            return count > 0 ? totalV / count : 0f;
        }

        /// <summary>
        /// テクスチャに明度オフセットを適用
        /// </summary>
        /// <param name="texture">元テクスチャ</param>
        /// <param name="brightnessOffset">明度オフセット（-1.0 ~ 1.0）</param>
        /// <returns>明度調整後のテクスチャ</returns>
        public static Texture2D ApplyBrightnessOffset(Texture2D texture, float brightnessOffset)
        {
#pragma warning disable CS8603
            if (texture == null) return null;
#pragma warning restore CS8603

            // オフセットが0の場合はコピーを返す
            if (Mathf.Abs(brightnessOffset) < 0.001f)
            {
                return CopyTexture(texture);
            }

            var readable = GetReadableTexture(texture);
            var result = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, true);
            result.name = texture.name + "_BrightnessAdjusted";

            var pixels = readable.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];

                // アルファが0に近いピクセルはそのまま
                if (pixel.a < 0.01f)
                {
                    continue;
                }

                Color.RGBToHSV(pixel, out float h, out float s, out float v);

                // V値にオフセットを適用
                v = Mathf.Clamp01(v + brightnessOffset);

                var newColor = Color.HSVToRGB(h, s, v);
                newColor.a = pixel.a;
                pixels[i] = newColor;
            }

            result.SetPixels(pixels);
            result.Apply(true);

            CompressToMatch(result, texture.format);
            ShaderUtils.EnableMipStreaming(result);

            if (readable != texture)
            {
                Object.DestroyImmediate(readable);
            }

            return result;
        }

        /// <summary>
        /// テクスチャに UV 使用領域マスクを基準とした edge dilation（塗り足し）を適用する。
        /// マスク外のピクセルを近隣マスク内ピクセル色の平均で iterations 回拡張する。
        /// 目的: メッシュレンダリング時の bilinear / mipmap で UV 端の外側ピクセルが
        /// 滲み出すのを防ぐ（髪の端が黒くなる現象の対策）。
        ///
        /// alpha チャンネルは保持（透明領域でも RGB だけ拡張する）。
        /// </summary>
        /// <param name="source">入力テクスチャ</param>
        /// <param name="usageMask">UV 使用領域マスク（true=使用、false=未使用）。長さは source.width * source.height</param>
        /// <param name="iterations">拡張ピクセル数（1イテレーションで1ピクセル拡張）</param>
        /// <returns>dilation 適用後の新しい Texture2D</returns>
        public static Texture2D? DilateTexture(Texture2D source, bool[] usageMask, int iterations)
        {
            if (source == null) return null;
            if (usageMask == null) return CopyTexture(source);
            if (iterations <= 0) return CopyTexture(source);

            int width = source.width;
            int height = source.height;
            if (usageMask.Length != width * height) return CopyTexture(source);

            // GPU 版が使えれば優先
            if (ShaderUtils.IsGPUSupported() && ShaderUtils.GetDilateTextureShader() != null)
            {
                var gpuResult = DilateTextureGPU(source, usageMask, iterations, width, height);
                if (gpuResult != null) return gpuResult;
            }

            // CPU フォールバック（GPU 非対応環境）
            return DilateTextureCPU(source, usageMask, iterations, width, height);
        }

        /// <summary>
        /// Dilation の GPU 実装（ping-pong で iterations 回 Dispatch）
        /// </summary>
        private static Texture2D? DilateTextureGPU(Texture2D source, bool[] usageMask, int iterations, int width, int height)
        {
            var shader = ShaderUtils.GetDilateTextureShader();
            if (shader == null) return null;

            RenderTexture? rtA = null;
            RenderTexture? rtB = null;
            ComputeBuffer? maskA = null;
            ComputeBuffer? maskB = null;
            try
            {
                rtA = CreateUAV(width, height);
                rtB = CreateUAV(width, height);
                Graphics.Blit(source, rtA);

                int wordCount = (width * height + 31) / 32;
                var packed = new uint[wordCount];
                for (int i = 0; i < usageMask.Length; i++)
                    if (usageMask[i]) packed[i >> 5] |= 1u << (i & 31);

                maskA = new ComputeBuffer(wordCount, sizeof(uint));
                maskA.SetData(packed);
                maskB = new ComputeBuffer(wordCount, sizeof(uint));
                maskB.SetData(new uint[wordCount]); // 初期化

                int kernel = shader.FindKernel("CSDilate");
                shader.SetInt("_Width", width);
                shader.SetInt("_Height", height);
                int groupsX = Mathf.CeilToInt(width / 16f);
                int groupsY = Mathf.CeilToInt(height / 16f);

                var srcRT = rtA;
                var dstRT = rtB;
                var srcMask = maskA;
                var dstMask = maskB;

                for (int iter = 0; iter < iterations; iter++)
                {
                    // dstMask を 0 クリア（次パスの出力先）
                    dstMask.SetData(new uint[wordCount]);

                    shader.SetTexture(kernel, "_Source", srcRT);
                    shader.SetTexture(kernel, "_Target", dstRT);
                    shader.SetBuffer(kernel, "_Mask", srcMask);
                    shader.SetBuffer(kernel, "_NewMask", dstMask);
                    shader.Dispatch(kernel, groupsX, groupsY, 1);

                    // ping-pong
                    (srcRT, dstRT) = (dstRT, srcRT);
                    (srcMask, dstMask) = (dstMask, srcMask);
                }

                // 最終結果は srcRT（ループ末尾で swap された後）
                var output = new Texture2D(width, height, TextureFormat.RGBA32, true);
                output.name = source.name + "_Dilated";
                var prev = RenderTexture.active;
                RenderTexture.active = srcRT;
                output.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                output.Apply(true);
                RenderTexture.active = prev;

                CompressToMatch(output, source.format);
                ShaderUtils.EnableMipStreaming(output);
                return output;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChimeraHairMaster] Dilation GPU エラー（CPU フォールバック）: {ex}");
                return null;
            }
            finally
            {
                RenderTexture.active = null;
                if (rtA != null) { rtA.Release(); Object.DestroyImmediate(rtA); }
                if (rtB != null) { rtB.Release(); Object.DestroyImmediate(rtB); }
                maskA?.Dispose();
                maskB?.Dispose();
            }
        }

        private static RenderTexture CreateUAV(int w, int h)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                sRGB = true,
            };
            var rt = new RenderTexture(desc);
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Dilation の CPU 実装（GPU 非対応時のフォールバック）
        /// </summary>
        private static Texture2D? DilateTextureCPU(Texture2D source, bool[] usageMask, int iterations, int width, int height)
        {
            var readable = GetReadableTexture(source);
            Color[] pixels = readable.GetPixels();
            bool[] mask = (bool[])usageMask.Clone();

            Color[] nextPixels = new Color[pixels.Length];
            bool[] nextMask = new bool[mask.Length];

            for (int iter = 0; iter < iterations; iter++)
            {
                System.Array.Copy(pixels, nextPixels, pixels.Length);
                System.Array.Copy(mask, nextMask, mask.Length);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = y * width + x;
                        if (mask[idx]) continue;

                        float sumR = 0f, sumG = 0f, sumB = 0f;
                        int count = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= height) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                if (nx < 0 || nx >= width) continue;
                                int nidx = ny * width + nx;
                                if (!mask[nidx]) continue;
                                var p = pixels[nidx];
                                sumR += p.r; sumG += p.g; sumB += p.b;
                                count++;
                            }
                        }

                        if (count > 0)
                        {
                            float inv = 1f / count;
                            float origAlpha = pixels[idx].a;
                            nextPixels[idx] = new Color(sumR * inv, sumG * inv, sumB * inv, origAlpha);
                            nextMask[idx] = true;
                        }
                    }
                }

                var tp = pixels; pixels = nextPixels; nextPixels = tp;
                var tm = mask; mask = nextMask; nextMask = tm;
            }

            var output = new Texture2D(width, height, TextureFormat.RGBA32, true);
            output.name = source.name + "_Dilated";
            output.SetPixels(pixels);
            output.Apply(true);

            CompressToMatch(output, source.format);
            ShaderUtils.EnableMipStreaming(output);

            if (readable != source)
            {
                Object.DestroyImmediate(readable);
            }

            return output;
        }

        /// <summary>
        /// 色合わせ無視マスクを適用
        /// 元テクスチャと色変換済みテクスチャをマスクに基づいてブレンドする
        /// </summary>
        /// <param name="originalTexture">元テクスチャ（色変換前）</param>
        /// <param name="transformedTexture">色変換済みテクスチャ</param>
        /// <param name="mask">マスクテクスチャ（黒=元の色を維持、白=色変換を適用）</param>
        /// <returns>マスク適用後のテクスチャ</returns>
        public static Texture2D? ApplyColorMask(Texture2D originalTexture, Texture2D transformedTexture, Texture2D mask)
        {
            if (originalTexture == null || transformedTexture == null || mask == null)
                return null;

            var readableOriginal = GetReadableTexture(originalTexture);
            var readableTransformed = GetReadableTexture(transformedTexture);
            var readableMask = GetReadableTexture(mask);

            int width = readableTransformed.width;
            int height = readableTransformed.height;

            Color[] originalPixels = readableOriginal.GetPixels();
            Color[] transformedPixels = readableTransformed.GetPixels();
            Color[] maskPixels = readableMask.GetPixels();
            int maskWidth = readableMask.width;
            int maskHeight = readableMask.height;

            bool originalSizeMatch = (readableOriginal.width == width && readableOriginal.height == height);

            Color[] outputPixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    float u = (float)x / width;
                    float v = (float)y / height;

                    // マスクをサンプリング（テクスチャサイズが異なる場合はリサンプル）
                    int mx = Mathf.Clamp((int)(u * maskWidth), 0, maskWidth - 1);
                    int my = Mathf.Clamp((int)(v * maskHeight), 0, maskHeight - 1);
                    float maskValue = maskPixels[my * maskWidth + mx].grayscale;

                    // 元テクスチャをサンプリング（サイズが異なる場合はリサンプル）
                    Color origPixel;
                    if (originalSizeMatch)
                    {
                        origPixel = originalPixels[idx];
                    }
                    else
                    {
                        int ox = Mathf.Clamp((int)(u * readableOriginal.width), 0, readableOriginal.width - 1);
                        int oy = Mathf.Clamp((int)(v * readableOriginal.height), 0, readableOriginal.height - 1);
                        origPixel = originalPixels[oy * readableOriginal.width + ox];
                    }

                    // 黒(0) = 元の色を維持, 白(1) = 色変換を適用
                    outputPixels[idx] = Color.Lerp(origPixel, transformedPixels[idx], maskValue);
                }
            }

            Texture2D output = new Texture2D(width, height, TextureFormat.RGBA32, true);
            output.name = transformedTexture.name + "_Masked";
            output.SetPixels(outputPixels);
            output.Apply(true);

            CompressToMatch(output, originalTexture.format);
            ShaderUtils.EnableMipStreaming(output);

            // 一時テクスチャをクリーンアップ
            if (readableOriginal != originalTexture) Object.DestroyImmediate(readableOriginal);
            if (readableTransformed != transformedTexture) Object.DestroyImmediate(readableTransformed);
            if (readableMask != mask) Object.DestroyImmediate(readableMask);

            return output;
        }

        /// <summary>
        /// テクスチャをコピー
        /// </summary>
        private static Texture2D CopyTexture(Texture2D source)
        {
            var readable = GetReadableTexture(source);
            var copy = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, true);
            copy.SetPixels(readable.GetPixels());
            copy.Apply(true);

            CompressToMatch(copy, source.format);
            ShaderUtils.EnableMipStreaming(copy);

            if (readable != source)
            {
                Object.DestroyImmediate(readable);
            }

            return copy;
        }

        #endregion
    }

    /// <summary>
    /// 色変換設定
    /// </summary>
    public class ColorTransformSettings
    {
        /// <summary>
        /// 色変換モード
        /// </summary>
        public ColorTransformMode Mode { get; set; } = ColorTransformMode.HueShift;

        /// <summary>
        /// ターゲット色（変換先の色）
        /// </summary>
        public Color TargetColor { get; set; } = Color.white;

        /// <summary>
        /// ソース色（変換元の基準色）
        /// HueShift (HSV) と RGBDelta では「テクスチャの代表色」として使用
        /// </summary>
        public Color SourceColor { get; set; } = Color.white;

        /// <summary>
        /// グラデーションカーブ（Gradientモード用）
        /// </summary>
        public Gradient GradientCurve { get; set; } = new Gradient();

        /// <summary>
        /// 彩度の保持率（HueShift + HSV algorithm 用）
        /// </summary>
        public float SaturationPreserve { get; set; } = 0f;

        /// <summary>
        /// 明度の保持率（HueShift + HSV algorithm 用）
        /// </summary>
        public float ValuePreserve { get; set; } = 1.0f;

        /// <summary>
        /// 色相シフトモードのアルゴリズム選択
        /// </summary>
        public HueShiftAlgorithm HueShiftAlgorithm { get; set; } = HueShiftAlgorithm.HSV;

        // ----- Oklab algorithm パラメータ -----

        /// <summary>
        /// 色相保持率（Oklab algorithm 用）。0=target.h 完全固定、1=元の色相保持
        /// </summary>
        public float OklabHueRetain { get; set; } = 0f;

        /// <summary>
        /// 彩度を target.C に寄せる強さ（Oklab algorithm 用）
        /// </summary>
        public float OklabSaturationToTarget { get; set; } = 1f;

        /// <summary>
        /// L を target.L に寄せる強さ（Oklab algorithm 用）
        /// 0=補正なし、1=完全に線形リマップ適用
        /// </summary>
        public float OklabLToTarget { get; set; } = 1f;

        /// <summary>
        /// 線形リマップ時、暗端を target.L の何倍にマップするか（Oklab algorithm 用）
        /// 0=暗端を黒に / 1=暗端も target.L（明暗差消失）
        /// </summary>
        public float OklabLDarkEndRatio { get; set; } = 0.5f;

        /// <summary>
        /// テクスチャの上位 5% L 値（Oklab algorithm 用、事前計算）
        /// ColorTransformPass 等から事前にメッシュ UV 経由で算出して設定する
        /// </summary>
        public float OklabSourceLP95 { get; set; } = 0.5f;

        /// <summary>
        /// テクスチャの下位 5% L 値（Oklab algorithm 用、事前計算）
        /// 線形リマップの暗端基準点として使用する
        /// </summary>
        public float OklabSourceLP05 { get; set; } = 0f;

        /// <summary>
        /// テクスチャの代表 OKLCh の h 値ラジアン（Oklab algorithm 用、事前計算）
        /// </summary>
        public float OklabSourceHDominant { get; set; } = 0f;

        // ----- RGBDelta モードパラメータ -----

        /// <summary>
        /// RGB差分の加算強度（RGBDelta モード用）
        /// </summary>
        public float RgbDeltaIntensity { get; set; } = 1f;

        /// <summary>
        /// RGB差分のソフトクリップ領域幅（RGBDelta モード用、0-1 線形空間）
        /// </summary>
        public float RgbDeltaSoftClipZone { get; set; } = 0.05f;

        /// <summary>
        /// ChimeraHairMasterコンポーネントから設定を作成
        /// p95 や代表 OKLCh などの事前計算値はここでは設定されないため、
        /// 必要に応じて呼び出し側で SetOklabPrecomputed() を呼ぶこと
        /// </summary>
        public static ColorTransformSettings FromComponent(ChimeraHairMaster component, Color sourceColor)
        {
            return new ColorTransformSettings
            {
                Mode = component.colorTransformMode,
                TargetColor = component.targetColor,
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
        }

        /// <summary>
        /// Oklab algorithm 用の事前計算値（テクスチャ毎の代表値）を設定
        /// </summary>
        public void SetOklabPrecomputed(float lP95, float lP05, float hDominant)
        {
            OklabSourceLP95 = lP95;
            OklabSourceLP05 = lP05;
            OklabSourceHDominant = hDominant;
        }
    }
}
