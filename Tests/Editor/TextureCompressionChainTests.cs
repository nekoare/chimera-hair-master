using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 多重ランタイム圧縮ノイズ修正の回帰テスト。
    /// 「中間段は compressResult:false で常に非圧縮(RGBA32)を返す」「CompressToMatch のフォーマットマッピング」を検証する。
    /// </summary>
    public class TextureCompressionChainTests
    {
        // DXT5 圧縮済みのテスト用テクスチャを作る（グラデーション）
        private static Texture2D MakeDxt5(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color((float)x / w, (float)y / h, 0.5f, 1f);
            tex.SetPixels(px);
            tex.Apply();
            EditorUtility.CompressTexture(tex, TextureFormat.DXT5, TextureCompressionQuality.Fast);
            Assert.That(tex.format, Is.EqualTo(TextureFormat.DXT5), "前提: 入力を DXT5 に圧縮できていること");
            return tex;
        }

        private static bool[] MakeMask(int w, int h)
        {
            var mask = new bool[w * h];
            // 中央付近だけ true（dilation が周囲へ塗り足す）
            for (int y = h / 4; y < h * 3 / 4; y++)
                for (int x = w / 4; x < w * 3 / 4; x++)
                    mask[y * w + x] = true;
            return mask;
        }

        [Test]
        public void ProcessTexture_CompressResultFalse_ReturnsUncompressed()
        {
            var src = MakeDxt5(64, 64);
            Texture2D result = null;
            try
            {
                var settings = new ColorTransformSettings(); // 既定: HueShift / white（実質恒等）
                result = ColorProcessor.ProcessTexture(src, settings, preferGPU: false, compressResult: false);
                Assert.That(result, Is.Not.Null, "ProcessTexture が結果を返すこと");
                Assert.That(result.format, Is.EqualTo(TextureFormat.RGBA32),
                    "compressResult:false のとき中間出力は非圧縮 RGBA32 であること");
            }
            finally
            {
                Object.DestroyImmediate(src);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void DilateTexture_CompressResultFalse_ReturnsUncompressed()
        {
            var src = MakeDxt5(64, 64);
            var mask = MakeMask(64, 64);
            Texture2D result = null;
            try
            {
                result = ColorProcessor.DilateTexture(src, mask, 4, compressResult: false);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.format, Is.EqualTo(TextureFormat.RGBA32),
                    "compressResult:false のとき DilateTexture 出力は非圧縮 RGBA32 であること");
            }
            finally
            {
                Object.DestroyImmediate(src);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ApplyBrightnessOffset_CompressResultFalse_ReturnsUncompressed()
        {
            var src = MakeDxt5(64, 64);
            Texture2D result = null;
            try
            {
                result = ColorProcessor.ApplyBrightnessOffset(src, 0.1f, compressResult: false);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.format, Is.EqualTo(TextureFormat.RGBA32),
                    "compressResult:false のとき ApplyBrightnessOffset 出力は非圧縮 RGBA32 であること");
            }
            finally
            {
                Object.DestroyImmediate(src);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void ApplyBrightnessOffset_CompressResultTrue_StillCompresses()
        {
            // 互換性: compressResult:true を明示的に渡した場合は従来どおり元フォーマットへ圧縮される
            var src = MakeDxt5(64, 64);
            Texture2D result = null;
            try
            {
                result = ColorProcessor.ApplyBrightnessOffset(src, 0.1f, compressResult: true);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.format, Is.EqualTo(TextureFormat.DXT5),
                    "compressResult:true のときは元フォーマット(DXT5)へ圧縮されること");
            }
            finally
            {
                Object.DestroyImmediate(src);
                if (result != null) Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void CompressToMatch_MapsCrunchedToPlain()
        {
            AssertCompressToFormat(TextureFormat.DXT5Crunched, TextureFormat.DXT5);
            AssertCompressToFormat(TextureFormat.DXT1Crunched, TextureFormat.DXT1);
            AssertCompressToFormat(TextureFormat.DXT5, TextureFormat.DXT5);
            AssertCompressToFormat(TextureFormat.DXT1, TextureFormat.DXT1);
            AssertCompressToFormat(TextureFormat.BC7, TextureFormat.BC7);
        }

        [Test]
        public void CompressToMatch_UnsupportedFormat_LeavesUnchanged()
        {
            // マッピングに無いフォーマット（非圧縮など）を渡した場合は圧縮せず RGBA32 のまま
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            tex.Apply();
            try
            {
                ColorProcessor.CompressToMatch(tex, TextureFormat.RGBA32);
                Assert.That(tex.format, Is.EqualTo(TextureFormat.RGBA32),
                    "非対応フォーマット指定時は圧縮されず元のまま（例: Android の ASTC 等はここで no-op）");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        private static void AssertCompressToFormat(TextureFormat sourceFormat, TextureFormat expected)
        {
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var px = new Color[64 * 64];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.4f, 0.6f, 0.8f, 1f);
            tex.SetPixels(px);
            tex.Apply();
            try
            {
                ColorProcessor.CompressToMatch(tex, sourceFormat);
                Assert.That(tex.format, Is.EqualTo(expected),
                    $"CompressToMatch({sourceFormat}) は {expected} へ圧縮すること");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }
    }
}
