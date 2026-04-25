using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    public class StrandPatternExtractorTests
    {
        // GPU 丸め誤差 + 小さな差分の丸め込みで許容幅はやや広め
        private const float FlatEpsilon = 0.02f;

        [Test]
        public void ExtractHighFrequency_FlatTexture_ReturnsNeutralDetail()
        {
            // 単色テクスチャ → 高周波成分はほぼ 0（エンコード上は 0.5）
            var source = MakeFlatTexture(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f));

            var result = StrandPatternExtractor.ExtractHighFrequency(source);
            try
            {
                Assert.That(result, Is.Not.Null, "GPU extraction should succeed");

                // 中央のピクセル（境界の影響を避ける）が中央値 0.5 付近であること
                var center = result.GetPixel(32, 32);
                Assert.That(center.r, Is.EqualTo(0.5f).Within(FlatEpsilon));
                Assert.That(center.g, Is.EqualTo(0.5f).Within(FlatEpsilon));
                Assert.That(center.b, Is.EqualTo(0.5f).Within(FlatEpsilon));
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ExtractHighFrequency_Checkerboard_ProducesVariation()
        {
            // 市松模様 → 高周波成分が明確に出る（標準偏差が大きい）
            var source = MakeCheckerboardTexture(64, 64);

            var result = StrandPatternExtractor.ExtractHighFrequency(source);
            try
            {
                Assert.That(result, Is.Not.Null);

                // 中央領域のピクセルの標準偏差を計算
                // 市松なら高周波が出るので std > 一定値
                var pixels = result.GetPixels(16, 16, 32, 32);
                float mean = 0f;
                foreach (var p in pixels) mean += p.r;
                mean /= pixels.Length;
                float variance = 0f;
                foreach (var p in pixels) variance += (p.r - mean) * (p.r - mean);
                variance /= pixels.Length;
                float std = Mathf.Sqrt(variance);

                Assert.That(std, Is.GreaterThan(0.05f),
                    $"Expected high-frequency variation, got std={std}");
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        private static Texture2D MakeFlatTexture(int w, int h, Color c)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        private static Texture2D MakeCheckerboardTexture(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = ((x / 2) + (y / 2)) % 2 == 0;
                    pixels[y * w + x] = on ? Color.white : Color.black;
                }
            }
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }
    }
}
