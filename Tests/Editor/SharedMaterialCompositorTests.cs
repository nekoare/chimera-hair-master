using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 共有マテリアルの領域合成ヘルパーのテスト。
    /// 「非重複なら各領域が自分の結果になる」「重なれば合成しない」「サイズ不一致は合成しない」を固定する。
    /// </summary>
    public class SharedMaterialCompositorTests
    {
        private const int W = 8;
        private const int H = 8;
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _cleanup.Clear();
        }

        private Texture2D Solid(Color c, int w = W, int h = H)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            tex.SetPixels(px);
            tex.Apply();
            _cleanup.Add(tex);
            return tex;
        }

        // x が [x0, x1) の列だけ true のマスク
        private static bool[] Columns(int x0, int x1)
        {
            var m = new bool[W * H];
            for (int y = 0; y < H; y++)
                for (int x = x0; x < x1; x++)
                    m[y * W + x] = true;
            return m;
        }

        private static void AssertPixel(Texture2D tex, int x, int y, Color expected, string msg)
        {
            var a = tex.GetPixel(x, y);
            Assert.That(a.r, Is.EqualTo(expected.r).Within(0.02f), msg + " (R)");
            Assert.That(a.g, Is.EqualTo(expected.g).Within(0.02f), msg + " (G)");
            Assert.That(a.b, Is.EqualTo(expected.b).Within(0.02f), msg + " (B)");
        }

        [Test]
        public void HasOverlap_DisjointMasks_ReturnsFalse()
        {
            Assert.That(SharedMaterialCompositor.HasOverlap(new[] { Columns(0, 4), Columns(4, 8) }), Is.False);
        }

        [Test]
        public void HasOverlap_OnePixelSharedColumn_ReturnsTrue()
        {
            Assert.That(SharedMaterialCompositor.HasOverlap(new[] { Columns(0, 5), Columns(4, 8) }), Is.True,
                "1ピクセルでも重なれば重なり扱い（厳密判定）");
        }

        [Test]
        public void HasOverlap_SingleMask_ReturnsFalse()
        {
            Assert.That(SharedMaterialCompositor.HasOverlap(new[] { Columns(0, 8) }), Is.False);
        }

        [Test]
        public void HasOverlap_LengthMismatch_ReturnsTrue()
        {
            Assert.That(SharedMaterialCompositor.HasOverlap(new[] { Columns(0, 4), new bool[4] }), Is.True,
                "長さ不一致は合成不可として重なり扱い");
        }

        [Test]
        public void Composite_DisjointRegions_TakesEachRegionFromItsOwnSource()
        {
            var red = Solid(Color.red);
            var blue = Solid(Color.blue);
            var regions = new[]
            {
                new SharedMaterialCompositor.Region(Columns(0, 4), red),
                new SharedMaterialCompositor.Region(Columns(4, 8), blue),
            };

            var result = SharedMaterialCompositor.Composite(regions, dilationIterations: 0);
            if (result != null) _cleanup.Add(result);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.format, Is.EqualTo(TextureFormat.RGBA32), "合成結果は非圧縮で返すこと");
            AssertPixel(result, 1, 3, Color.red, "左領域は1つ目の結果");
            AssertPixel(result, 6, 3, Color.blue, "右領域は2つ目の結果");
            AssertPixel(red, 6, 3, Color.red, "入力テクスチャは変更しないこと");
            AssertPixel(blue, 1, 3, Color.blue, "入力テクスチャは変更しないこと");
        }

        [Test]
        public void Composite_OverlappingRegions_ReturnsNull()
        {
            var regions = new[]
            {
                new SharedMaterialCompositor.Region(Columns(0, 5), Solid(Color.red)),
                new SharedMaterialCompositor.Region(Columns(4, 8), Solid(Color.blue)),
            };

            Assert.That(SharedMaterialCompositor.Composite(regions), Is.Null,
                "重なりがある場合は合成せず null（呼び出し側が従来処理に落とす）");
        }

        [Test]
        public void Composite_SizeMismatch_ReturnsNull()
        {
            var regions = new[]
            {
                new SharedMaterialCompositor.Region(Columns(0, 4), Solid(Color.red)),
                new SharedMaterialCompositor.Region(new bool[4 * 4], Solid(Color.blue, 4, 4)),
            };

            Assert.That(SharedMaterialCompositor.Composite(regions), Is.Null);
        }

        [Test]
        public void Composite_SingleRegion_ReturnsNull()
        {
            var regions = new[]
            {
                new SharedMaterialCompositor.Region(Columns(0, 8), Solid(Color.red)),
            };

            Assert.That(SharedMaterialCompositor.Composite(regions), Is.Null,
                "1 件は合成対象外（呼び出し側が従来処理を使う）");
        }

        [Test]
        public void Composite_WithDilation_FillsGapBetweenRegionsFromNearestRegion()
        {
            // 左 [0,3) 赤、右 [5,8) 青、間の 2 列は空き → dilation で隣接色が塗り足される
            var regions = new[]
            {
                new SharedMaterialCompositor.Region(Columns(0, 3), Solid(Color.red)),
                new SharedMaterialCompositor.Region(Columns(5, 8), Solid(Color.blue)),
            };

            var result = SharedMaterialCompositor.Composite(regions, dilationIterations: 8);
            if (result != null) _cleanup.Add(result);

            Assert.That(result, Is.Not.Null);
            AssertPixel(result, 3, 3, Color.red, "左領域の縁は左の色で塗り足される");
            AssertPixel(result, 4, 3, Color.blue, "右領域の縁は右の色で塗り足される（土台の赤で埋まらない）");
        }
    }
}
