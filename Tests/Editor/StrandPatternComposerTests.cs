using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    public class StrandPatternComposerTests
    {
        private const float PixelEps = 0.01f;

        [Test]
        public void ComposeBands_RatiosOne_ReturnsUnchanged()
        {
            // 両 ratio=1 → 変更なし
            var target = MakeFlat(64, 64, new Color(0.5f, 0.3f, 0.2f, 1f), linear: false);
            var bandHigh = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var hp8 = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var uvMask = MakeFullMask(64, 64);

            var result = StrandPatternComposer.ComposeBands(target, bandHigh, hp8, uvMask, ratioHigh: 1f, ratioMid: 1f, strengthFine: 1f, strengthShade: 1f);
            try
            {
                Assert.That(result, Is.Not.Null);
                var p = result.GetPixel(32, 32);
                var t = target.GetPixel(32, 32);
                Assert.That(p.r, Is.EqualTo(t.r).Within(PixelEps));
                Assert.That(p.g, Is.EqualTo(t.g).Within(PixelEps));
                Assert.That(p.b, Is.EqualTo(t.b).Within(PixelEps));
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(bandHigh);
                Object.DestroyImmediate(hp8);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ComposeBands_StrengthZero_ReturnsUnchanged()
        {
            var target = MakeFlat(64, 64, new Color(0.5f, 0.3f, 0.2f, 1f), linear: false);
            var bandHigh = MakeFlat(64, 64, new Color(0.8f, 0.2f, 0.5f, 1f), linear: true);
            var hp8 = MakeFlat(64, 64, new Color(0.6f, 0.4f, 0.3f, 1f), linear: true);
            var uvMask = MakeFullMask(64, 64);

            var result = StrandPatternComposer.ComposeBands(target, bandHigh, hp8, uvMask, ratioHigh: 5f, ratioMid: 5f, strengthFine: 0f, strengthShade: 0f);
            try
            {
                Assert.That(result, Is.Not.Null);
                var p = result.GetPixel(32, 32);
                var t = target.GetPixel(32, 32);
                Assert.That(p.r, Is.EqualTo(t.r).Within(PixelEps));
                Assert.That(p.g, Is.EqualTo(t.g).Within(PixelEps));
                Assert.That(p.b, Is.EqualTo(t.b).Within(PixelEps));
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(bandHigh);
                Object.DestroyImmediate(hp8);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ComposeBands_AlphaPreserved()
        {
            var target = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 0.73f), linear: false);
            var bandHigh = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 0.1f), linear: true);
            var hp8 = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 0.1f), linear: true);
            var uvMask = MakeFullMask(64, 64);

            var result = StrandPatternComposer.ComposeBands(target, bandHigh, hp8, uvMask, ratioHigh: 2f, ratioMid: 2f, strengthFine: 1f, strengthShade: 1f);
            try
            {
                Assert.That(result, Is.Not.Null);
                var p = result.GetPixel(32, 32);
                var t = target.GetPixel(32, 32);
                Assert.That(p.r, Is.EqualTo(t.r).Within(PixelEps));
                Assert.That(p.g, Is.EqualTo(t.g).Within(PixelEps));
                Assert.That(p.b, Is.EqualTo(t.b).Within(PixelEps));
                Assert.That(p.a, Is.EqualTo(0.73f).Within(PixelEps));
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(bandHigh);
                Object.DestroyImmediate(hp8);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ComposeBands_CompressesResultToRequestedFormat()
        {
            var target = MakeFlat(64, 64, new Color(0.5f, 0.3f, 0.2f, 1f), linear: false);
            var bandHigh = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var hp8 = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var compressionProbe = MakeFlat(64, 64, Color.white, linear: false);
            var uvMask = MakeFullMask(64, 64);
            Texture2D result = null;
            try
            {
                EditorUtility.CompressTexture(compressionProbe, TextureFormat.DXT5, TextureCompressionQuality.Normal);
                Assume.That(compressionProbe.format, Is.EqualTo(TextureFormat.DXT5), "DXT5 compression is not available in this editor environment.");

                result = StrandPatternComposer.ComposeBands(target, bandHigh, hp8, uvMask, ratioHigh: 2f, ratioMid: 1f, strengthFine: 1f, strengthShade: 0f, compressionFormat: TextureFormat.DXT5);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.format, Is.EqualTo(TextureFormat.DXT5));
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(bandHigh);
                Object.DestroyImmediate(hp8);
                Object.DestroyImmediate(compressionProbe);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ComputeBandStds_NeutralBands_ReturnsZero()
        {
            var bandHigh = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var hp8 = MakeFlat(64, 64, new Color(0.5f, 0.5f, 0.5f, 1f), linear: true);
            var mask = MakeFullMask(64, 64);
            try
            {
                var (sh, sm) = StrandPatternComposer.ComputeBandStds(bandHigh, hp8, mask);
                Assert.That(sh, Is.EqualTo(0f).Within(0.001f));
                Assert.That(sm, Is.EqualTo(0f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(bandHigh);
                Object.DestroyImmediate(hp8);
            }
        }

        [Test]
        public void ComputeBandStds_DifferentBands_StdsDiffer()
        {
            // bandHigh: checkerboard（高周波）
            // hp8: 同じ checkerboard（→ B_mid = hp8 - bandHigh = 0、stdMid = 0）
            var bandHigh = MakeCheckerboard(64, 64);
            var hp8 = MakeCheckerboard(64, 64);
            var mask = MakeFullMask(64, 64);
            var (sh, sm) = StrandPatternComposer.ComputeBandStds(bandHigh, hp8, mask);
            Assert.That(sh, Is.GreaterThan(0.1f));
            Assert.That(sm, Is.EqualTo(0f).Within(0.001f));
            Object.DestroyImmediate(bandHigh);
            Object.DestroyImmediate(hp8);
        }

        private static Texture2D MakeFlat(int w, int h, Color c, bool linear)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        private static Texture2D MakeCheckerboard(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = (x + y) % 2 == 0;
                    var v = on ? 0.75f : 0.25f;
                    pixels[y * w + x] = new Color(v, v, v, 1f);
                }
            }
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        private static bool[] MakeFullMask(int w, int h)
        {
            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = true;
            return mask;
        }

        // --- バンドゲインのノイズ床ゲート（v1.5.10）---
        // 以下は GPU 不要（純粋な数値計算）

        [Test]
        public void ComputeBandRatios_TargetAtNoiseFloor_ReturnsOne()
        {
            // 模様がほぼ無いテクスチャでは std の実体が 8bit 量子化ノイズなので、
            // 比を上げてもノイズが増幅されるだけ。効果を掛けないこと。
            // 修正前はここで 80 倍のゲインが掛かっていた。
            var (ratioHigh, ratioMid) = StrandPatternComposer.ComputeBandRatios(
                refStdHigh: 0.020f, refStdMid: 0.020f,
                targetStdHigh: 0.00025f, targetStdMid: 0.00035f);

            Assert.That(ratioHigh, Is.EqualTo(1f), "ノイズ床の高バンドにゲインが掛かった");
            Assert.That(ratioMid, Is.EqualTo(1f), "ノイズ床の中バンドにゲインが掛かった");
        }

        [Test]
        public void ComputeBandRatios_TargetZero_ReturnsOneWithoutDivideByZero()
        {
            // 完全な単色。ゼロ除算で Infinity / NaN が漏れないこと
            var (ratioHigh, ratioMid) = StrandPatternComposer.ComputeBandRatios(
                refStdHigh: 0.020f, refStdMid: 0.020f,
                targetStdHigh: 0f, targetStdMid: 0f);

            Assert.That(ratioHigh, Is.EqualTo(1f));
            Assert.That(ratioMid, Is.EqualTo(1f));
            Assert.That(float.IsNaN(ratioHigh) || float.IsInfinity(ratioHigh), Is.False,
                "高バンドが NaN / Infinity になった");
            Assert.That(float.IsNaN(ratioMid) || float.IsInfinity(ratioMid), Is.False,
                "中バンドが NaN / Infinity になった");
        }

        [TestCase(0.02023f)]   // 普通の毛束
        [TestCase(0.05793f)]   // 濃い毛束
        public void ComputeBandRatios_WellAboveNoiseFloor_ReturnsRawRatio(float targetStd)
        {
            // 実在するディテールがある場合は従来の値と完全一致すること。
            // 既存ユーザーの見た目を変えないための契約。
            const float refStd = 0.020f;
            var (ratioHigh, _) = StrandPatternComposer.ComputeBandRatios(
                refStd, refStd, targetStd, targetStd);

            Assert.That(ratioHigh, Is.EqualTo(refStd / targetStd),
                "ノイズ床から十分離れているのに従来と違う比になった");
        }

        [Test]
        public void ComputeBandRatios_InGateBand_BlendsMonotonically()
        {
            // ゲート帯では 1 と生の比の間を単調に繋ぐこと。
            // 硬い閾値だと隣接メッシュの片方だけ質感が跳ぶ。
            const float refStd = 0.020f;
            float prev = 1f;
            for (int i = 0; i <= 20; i++)
            {
                float targetStd = 0.0050f + (0.0100f - 0.0050f) * i / 20f;
                var (ratio, _) = StrandPatternComposer.ComputeBandRatios(
                    refStd, refStd, targetStd, targetStd);

                Assert.That(ratio, Is.GreaterThanOrEqualTo(1f), $"targetStd={targetStd} で 1 を下回った");
                Assert.That(ratio, Is.LessThanOrEqualTo(refStd / targetStd + 1e-4f),
                    $"targetStd={targetStd} で生の比を超えた");
                if (i > 0)
                {
                    Assert.That(ratio, Is.GreaterThanOrEqualTo(prev - 1e-4f),
                        $"targetStd={targetStd} でブレンドが単調でない");
                }
                prev = ratio;
            }
        }

        [Test]
        public void ComputeBandRatios_NoiseFloorMatchesQuantizationDerivation()
        {
            // ノイズ床は「バンドが 8bit 格納され *2.0 で復号される」ことから導いている。
            // ここで固定するのは床の定数と導出式の対応関係だけで、格納フォーマット
            // そのものは検証できない（GPU が必要なため）。バンドを高精度フォーマットに
            // 変えるときは、このテストが通っていても床の定数を必ず見直すこと。
            // 見直さないとゲートが常時発火して機能が丸ごと止まる。
            const float decodedQuantStep = 2f / 255f;
            float theoretical = decodedQuantStep / Mathf.Sqrt(12f);

            // 高バンドは理論値どおり
            var (ratioAtFloor, _) = StrandPatternComposer.ComputeBandRatios(
                0.020f, 0.020f, theoretical * 2f - 1e-6f, 1f);
            Assert.That(ratioAtFloor, Is.EqualTo(1f),
                $"理論ノイズ床 {theoretical:F5} の 2 倍以下がゲートされていない");

            // 中バンドは独立に量子化された 2 枚の差なので √2 倍
            var (_, ratioMidAtFloor) = StrandPatternComposer.ComputeBandRatios(
                0.020f, 0.020f, 1f, theoretical * Mathf.Sqrt(2f) * 2f - 1e-6f);
            Assert.That(ratioMidAtFloor, Is.EqualTo(1f),
                "中バンドのノイズ床が √2 倍になっていない");
        }
    }
}
