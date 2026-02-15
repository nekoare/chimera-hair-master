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

                // ターゲット色をHSVに変換
                Color.RGBToHSV(settings.TargetColor, out float targetH, out float targetS, out float targetV);
                shader.SetVector("_TargetColorHSV", new Vector3(targetH, targetS, targetV));

                shader.SetFloat("_SaturationPreserve", settings.SaturationPreserve);
                shader.SetFloat("_ValuePreserve", settings.ValuePreserve);

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
                    return TransformHueShift(pixel, settings);

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
        /// テクスチャの明度を調整
        /// </summary>
        /// <param name="texture">元テクスチャ</param>
        /// <param name="targetBrightness">目標平均明度</param>
        /// <returns>明度調整後のテクスチャ</returns>
        public static Texture2D AdjustBrightness(Texture2D texture, float targetBrightness)
        {
#pragma warning disable CS8603
            if (texture == null) return null;
#pragma warning restore CS8603

            var readable = GetReadableTexture(texture);
            var currentBrightness = CalculateAverageBrightness(readable);

            // 差がほとんどない場合はそのまま返す
            if (Mathf.Abs(currentBrightness - targetBrightness) < 0.01f)
            {
                if (readable != texture)
                {
                    return readable;
                }
                return CopyTexture(texture);
            }

            // 明度差を計算
            float brightnessDelta = targetBrightness - currentBrightness;

            var result = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, true);
            var pixels = readable.GetPixels();

            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                
                // アルファが0に近いピクセルはそのまま
                if (pixel.a < 0.01f)
                {
                    pixels[i] = pixel;
                    continue;
                }

                Color.RGBToHSV(pixel, out float h, out float s, out float v);
                
                // V値を調整
                v = Mathf.Clamp01(v + brightnessDelta);
                
                pixels[i] = Color.HSVToRGB(h, s, v) * new Color(1, 1, 1, pixel.a);
                pixels[i].a = pixel.a;
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
        /// </summary>
        public Color SourceColor { get; set; } = Color.white;

        /// <summary>
        /// グラデーションカーブ（Gradientモード用）
        /// </summary>
        public Gradient GradientCurve { get; set; } = new Gradient();

        /// <summary>
        /// 彩度の保持率（HueShiftモード用）
        /// </summary>
        public float SaturationPreserve { get; set; } = 0f;

        /// <summary>
        /// 明度の保持率（HueShiftモード用）
        /// </summary>
        public float ValuePreserve { get; set; } = 1.0f;

        /// <summary>
        /// 輝度統一モード（HueShiftモード用）
        /// </summary>
        public BrightnessUnifyMode BrightnessUnifyMode { get; set; } = BrightnessUnifyMode.Off;

        /// <summary>
        /// ChimeraHairMasterコンポーネントから設定を作成
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
                BrightnessUnifyMode = component.brightnessUnifyMode
            };
        }
    }
}
