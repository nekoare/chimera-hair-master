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
    }
}
