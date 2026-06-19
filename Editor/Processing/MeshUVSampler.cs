#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// メッシュの UV 使用領域を三角形重心 + 面積重みでサンプリングするユーティリティ
    /// テクスチャの統計値（上位パーセンタイル輝度・代表色）を、UV で実際に使われている領域だけから算出する
    /// </summary>
    public static class MeshUVSampler
    {
        /// <summary>
        /// 1サンプル（テクスチャ色とその面積重み）
        /// </summary>
        public struct WeightedSample
        {
            public Color Color;
            public float Weight;
        }

        /// <summary>
        /// 指定した Renderer のサブメッシュ群について、UV 使用領域をサンプリングし
        /// 三角形重心位置でテクスチャから色を取得・三角形面積を重みに持つサンプル列を返す
        /// </summary>
        /// <param name="renderer">対象 SkinnedMeshRenderer</param>
        /// <param name="submeshIndices">対象サブメッシュ index 集合</param>
        /// <param name="texture">サンプル対象テクスチャ（UV0 でアクセス）</param>
        /// <returns>三角形ごとの (重心色, 面積) サンプル。アルファ < 0.01 のピクセルは除外</returns>
        /// <summary>
        /// テクスチャのピクセル配列キャッシュ（Pass 単位で使い回す用）
        /// key=Texture2D, value=(pixels, width, height)
        /// </summary>
        public sealed class PixelCache
        {
            private readonly Dictionary<Texture2D, (Color[] pixels, int w, int h, Texture2D? readable)> _cache = new();

            public (Color[] pixels, int width, int height) Get(Texture2D texture)
            {
                if (_cache.TryGetValue(texture, out var entry)) return (entry.pixels, entry.w, entry.h);
                var readable = ColorProcessor.GetReadableTexture(texture);
                var pixels = readable.GetPixels();
                _cache[texture] = (pixels, readable.width, readable.height, readable != texture ? readable : null);
                return (pixels, readable.width, readable.height);
            }

            public void Dispose()
            {
                foreach (var kvp in _cache)
                {
                    if (kvp.Value.readable != null) Object.DestroyImmediate(kvp.Value.readable);
                }
                _cache.Clear();
            }
        }

        public static List<WeightedSample> SampleByTriangleCentroid(
            SkinnedMeshRenderer renderer,
            IReadOnlyCollection<int> submeshIndices,
            Texture2D texture,
            PixelCache? cache = null)
        {
            var samples = new List<WeightedSample>();
            if (renderer == null || renderer.sharedMesh == null || texture == null) return samples;
            if (submeshIndices == null || submeshIndices.Count == 0) return samples;

            var mesh = renderer.sharedMesh;
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            if (uvs.Count == 0) return samples;

            // 読み取り可能なテクスチャを取得（キャッシュ経由 or 一時テクスチャ）
            Color[] pixels;
            int width, height;
            Texture2D? readable = null;
            if (cache != null)
            {
                (pixels, width, height) = cache.Get(texture);
            }
            else
            {
                readable = ColorProcessor.GetReadableTexture(texture);
                width = readable.width;
                height = readable.height;
                pixels = readable.GetPixels();
            }

            try
            {
                foreach (int submeshIndex in submeshIndices)
                {
                    if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) continue;

                    int[] triangles = mesh.GetTriangles(submeshIndex);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        int i0 = triangles[t];
                        int i1 = triangles[t + 1];
                        int i2 = triangles[t + 2];
                        if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;

                        Vector2 uv0 = uvs[i0];
                        Vector2 uv1 = uvs[i1];
                        Vector2 uv2 = uvs[i2];

                        Vector2 centroid = (uv0 + uv1 + uv2) / 3f;
                        float area = TriangleArea2D(uv0, uv1, uv2);
                        if (area <= 0f) continue;

                        Color px = SampleBilinear(pixels, width, height, centroid);
                        if (px.a < 0.01f) continue;

                        samples.Add(new WeightedSample { Color = px, Weight = area });
                    }
                }
            }
            finally
            {
                // キャッシュ使用時は PixelCache 側で解放されるので、ローカル readable のみ破棄
                if (readable != null && readable != texture)
                {
                    Object.DestroyImmediate(readable);
                }
            }

            return samples;
        }

        /// <summary>
        /// 重み付きサンプル列から指定パーセンタイルの Oklab L 値を取得
        /// </summary>
        /// <param name="samples">サンプル列</param>
        /// <param name="percentile">パーセンタイル（0-1）。0.95 で上位 5% 点</param>
        /// <returns>Oklab L 値。サンプル無しの場合は 0</returns>
        public static float CalculateOklabLPercentile(List<WeightedSample> samples, float percentile)
        {
            if (samples == null || samples.Count == 0) return 0f;

            // 各サンプルの Oklab L を計算
            var lws = new List<(float L, float W)>(samples.Count);
            float totalWeight = 0f;
            foreach (var s in samples)
            {
                Vector3 lin = OklabConverter.SRGBToLinear(s.Color);
                Vector3 lab = OklabConverter.LinearRGBToOklab(lin);
                lws.Add((lab.x, s.Weight));
                totalWeight += s.Weight;
            }

            if (totalWeight <= 0f) return 0f;

            // L 昇順ソート
            lws.Sort((a, b) => a.L.CompareTo(b.L));

            // 累積重みが totalWeight * percentile を超える位置の L を返す
            float threshold = totalWeight * percentile;
            float cum = 0f;
            foreach (var (l, w) in lws)
            {
                cum += w;
                if (cum >= threshold) return l;
            }
            return lws[lws.Count - 1].L;
        }

        /// <summary>
        /// 重み付きサンプル列から代表色（dominant color）を取得
        /// 面積重み平均で算出。彩度の低いサンプルは除外して色味のあるピクセルだけ平均化
        /// </summary>
        public static Color CalculateDominantColor(List<WeightedSample> samples)
        {
            if (samples == null || samples.Count == 0) return Color.white;

            Vector3 sum = Vector3.zero;
            float totalWeight = 0f;

            foreach (var s in samples)
            {
                Color.RGBToHSV(s.Color, out _, out float sat, out _);
                if (sat < 0.05f) continue; // 彩度の低いサンプル（白/黒/グレー）は除外

                sum += new Vector3(s.Color.r, s.Color.g, s.Color.b) * s.Weight;
                totalWeight += s.Weight;
            }

            if (totalWeight <= 0f)
            {
                // 彩度フィルタで全部除外された場合は、彩度フィルタ無しで再計算
                foreach (var s in samples)
                {
                    sum += new Vector3(s.Color.r, s.Color.g, s.Color.b) * s.Weight;
                    totalWeight += s.Weight;
                }
                if (totalWeight <= 0f) return Color.white;
            }

            sum /= totalWeight;
            return new Color(sum.x, sum.y, sum.z, 1f);
        }

        /// <summary>
        /// HueShift(Oklab) と RGBDelta モード用に、テクスチャ毎の UV 経由統計値を事前計算した
        /// ColorTransformSettings のコピーを返す
        /// （Gradient / HueShift HSV モードの場合は事前計算不要なのでそのままコピーを返す）
        /// </summary>
        public static ColorTransformSettings PrepareSettingsWithUVStats(
            ColorTransformSettings baseSettings,
            SkinnedMeshRenderer renderer,
            IReadOnlyCollection<int> submeshIndices,
            Texture2D texture,
            PixelCache? cache = null)
        {
            // 設定のコピーを作る
            var s = new ColorTransformSettings
            {
                Mode = baseSettings.Mode,
                TargetColor = baseSettings.TargetColor,
                SourceColor = baseSettings.SourceColor,
                GradientCurve = baseSettings.GradientCurve,
                SaturationPreserve = baseSettings.SaturationPreserve,
                ValuePreserve = baseSettings.ValuePreserve,
                HueShiftAlgorithm = baseSettings.HueShiftAlgorithm,
                OklabHueRetain = baseSettings.OklabHueRetain,
                OklabSaturationToTarget = baseSettings.OklabSaturationToTarget,
                OklabLToTarget = baseSettings.OklabLToTarget,
                OklabLDarkEndRatio = baseSettings.OklabLDarkEndRatio,
                RgbDeltaIntensity = baseSettings.RgbDeltaIntensity,
                RgbDeltaSoftClipZone = baseSettings.RgbDeltaSoftClipZone,
            };

            // 全モードで UV ベースの代表色を統一して使う（モード切替で基準色がブレるのを防ぐ）
            // Gradient モードは SourceColor を使わないので対象外
            bool needsUVStats = s.Mode == ColorTransformMode.HueShift
                                || s.Mode == ColorTransformMode.RGBDelta;

            if (!needsUVStats || renderer == null || texture == null) return s;

            var samples = SampleByTriangleCentroid(renderer, submeshIndices, texture, cache);
            if (samples.Count == 0) return s;

            Color dominant = CalculateDominantColor(samples);

            // 全モード（HueShift / RGBDelta）で UV ベースの dominant を SourceColor に使う
            s.SourceColor = dominant;

            if (s.Mode == ColorTransformMode.HueShift && s.HueShiftAlgorithm == HueShiftAlgorithm.Oklab)
            {
                float lP95 = CalculateOklabLPercentile(samples, 0.95f);
                float lP05 = CalculateOklabLPercentile(samples, 0.05f);
                Vector3 dominantLch = OklabConverter.SRGBToOklch(dominant);
                s.SetOklabPrecomputed(lP95, lP05, dominantLch.z);
            }

            return s;
        }

        #region Helpers

        /// <summary>
        /// 2D 三角形の符号無し面積
        /// </summary>
        private static float TriangleArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return 0.5f * Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
        }

        /// <summary>
        /// バイリニアサンプリング。UV 座標は [0, 1) で wrap、範囲外は wrap 後の位置を使う
        /// </summary>
        private static Color SampleBilinear(Color[] pixels, int width, int height, Vector2 uv)
        {
            // wrap UV
            float u = uv.x - Mathf.Floor(uv.x);
            float v = uv.y - Mathf.Floor(uv.y);

            float fx = u * width;
            float fy = v * height;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            float tx = fx - x0;
            float ty = fy - y0;

            x0 = ((x0 % width) + width) % width;
            y0 = ((y0 % height) + height) % height;
            x1 = ((x1 % width) + width) % width;
            y1 = ((y1 % height) + height) % height;

            Color c00 = pixels[y0 * width + x0];
            Color c10 = pixels[y0 * width + x1];
            Color c01 = pixels[y1 * width + x0];
            Color c11 = pixels[y1 * width + x1];

            Color cx0 = Color.Lerp(c00, c10, tx);
            Color cx1 = Color.Lerp(c01, c11, tx);
            return Color.Lerp(cx0, cx1, ty);
        }

        #endregion
    }
}
