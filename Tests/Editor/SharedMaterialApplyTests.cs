using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 手動適用／Prefab出力の共通ヘルパー ProcessMainTexGroup のテスト。
    /// 非重複なら領域ごとの結果が 1 枚に合成され、重なれば従来の先頭 (r, s) 処理に落ちることを固定する。
    /// </summary>
    public class SharedMaterialApplyTests
    {
        private const int W = 16;
        private const int H = 16;
        private readonly List<Object> _cleanup = new List<Object>();
        private MeshUVSampler.PixelCache _pixelCache;

        [SetUp]
        public void SetUp()
        {
            _pixelCache = new MeshUVSampler.PixelCache();
        }

        [TearDown]
        public void TearDown()
        {
            _pixelCache.Dispose();
            foreach (var o in _cleanup)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _cleanup.Clear();
        }

        private Texture2D Gradient()
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = new Color(0.2f + 0.6f * x / (W - 1), 0.3f, 0.8f - 0.5f * y / (H - 1), 1f);
            tex.SetPixels(px);
            tex.Apply();
            _cleanup.Add(tex);
            return tex;
        }

        // 三角形 1 枚。UV を u∈[uMin,uMax] の帯に置く
        private SkinnedMeshRenderer MakeRenderer(string name, float uMin, float uMax, Material mat)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            mesh.uv = new[] { new Vector2(uMin, 0.05f), new Vector2(uMin, 0.95f), new Vector2(uMax, 0.05f) };
            mesh.triangles = new[] { 0, 1, 2 };
            _cleanup.Add(mesh);
            r.sharedMesh = mesh;
            r.sharedMaterials = new[] { mat };
            return r;
        }

        private ChimeraHairMaster MakeComponent(string name, params SkinnedMeshRenderer[] renderers)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            var c = go.AddComponent<ChimeraHairMaster>();
            c.enableMeshMerge = false;
            c.enableColorTransform = true;
            c.colorChangeTargets = new List<TextureSlot> { new TextureSlot("_MainTex", "Main", true) };
            foreach (var r in renderers) c.targetRenderers.Add(r);
            return c;
        }

        private Material SharedMaterial(Texture2D mainTex)
        {
            var mat = new Material(Shader.Find("Unlit/Texture"));
            mat.SetTexture("_MainTex", mainTex);
            _cleanup.Add(mat);
            return mat;
        }

        private (ChimeraHairMaster c, SkinnedMeshRenderer a, SkinnedMeshRenderer b, Material mat, Texture2D tex) SetUpPair(bool overlap)
        {
            var tex = Gradient();
            var mat = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, overlap ? 0.60f : 0.45f, mat);
            var b = MakeRenderer("B", overlap ? 0.40f : 0.55f, 0.95f, mat);
            var c = MakeComponent("CHM", a, b);
            // B にだけ明度オフセット（領域ごとの結果が異なることを保証する）
            c.rendererBrightnessAdjustments.Add(new RendererBrightnessAdjustment(1) { brightnessOffset = 0.3f });
            return (c, a, b, mat, tex);
        }

        private static ColorTransformSettings Settings(ChimeraHairMaster c)
        {
            return ColorTransformSettings.FromComponent(c, ColorApplier.DetermineSourceColor(c));
        }

        private Texture2D Track(Texture2D tex)
        {
            if (tex != null) _cleanup.Add(tex);
            return tex;
        }

        private static int CountTrue(bool[] m)
        {
            int n = 0;
            foreach (var b in m) if (b) n++;
            return n;
        }

        private static void AssertRegionEquals(Texture2D actual, Texture2D expected, bool[] mask, string msg)
        {
            var ap = ColorProcessor.GetReadableTexture(actual).GetPixels();
            var ep = ColorProcessor.GetReadableTexture(expected).GetPixels();
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                Assert.That(ap[i].r, Is.EqualTo(ep[i].r).Within(0.02f), $"{msg} px={i} R");
                Assert.That(ap[i].g, Is.EqualTo(ep[i].g).Within(0.02f), $"{msg} px={i} G");
                Assert.That(ap[i].b, Is.EqualTo(ep[i].b).Within(0.02f), $"{msg} px={i} B");
            }
        }

        [Test]
        public void ProcessMainTexGroup_DisjointUV_CompositesEachRegion()
        {
            var (c, a, b, mat, tex) = SetUpPair(overlap: false);
            var members = new[] { (0, 0, mat), (1, 0, mat) };
            var settings = Settings(c);

            var result = Track(ColorApplier.ProcessMainTexGroup(c, members, tex, settings, _pixelCache, null,
                ColorApplier.MaskScope.SharedMaterial, out bool shared));

            Assert.That(shared, Is.True, "UV 非重複なら合成されること");
            Assert.That(result, Is.Not.Null);

            var maskA = MeshUVRasterizer.Rasterize(a, new[] { 0 }, W, H);
            var maskB = MeshUVRasterizer.Rasterize(b, new[] { 0 }, W, H);
            Assert.That(CountTrue(maskA), Is.GreaterThan(0), "前提: A の UV 領域が空でない");
            Assert.That(CountTrue(maskB), Is.GreaterThan(0), "前提: B の UV 領域が空でない");

            var expectA = Track(ColorApplier.ProcessMainTexRegion(c, 0, 0, mat, tex, maskA, settings, _pixelCache, null, ColorApplier.MaskScope.PerRegion));
            var expectB = Track(ColorApplier.ProcessMainTexRegion(c, 1, 0, mat, tex, maskB, settings, _pixelCache, null, ColorApplier.MaskScope.PerRegion));

            AssertRegionEquals(result, expectA, maskA, "A の領域は A 単体の結果");
            AssertRegionEquals(result, expectB, maskB, "B の領域は B 単体の結果（明度オフセット込み）");
        }

        [Test]
        public void ProcessMainTexGroup_OverlappingUV_FallsBackToFirstMember()
        {
            var (c, a, b, mat, tex) = SetUpPair(overlap: true);
            var members = new[] { (0, 0, mat), (1, 0, mat) };
            var settings = Settings(c);

            var result = Track(ColorApplier.ProcessMainTexGroup(c, members, tex, settings, _pixelCache, null,
                ColorApplier.MaskScope.SharedMaterial, out bool shared));

            Assert.That(shared, Is.False, "重なる場合は合成しない");
            Assert.That(result, Is.Not.Null);

            var maskA = MeshUVRasterizer.Rasterize(a, new[] { 0 }, W, H);
            var expect = Track(ColorApplier.ProcessMainTexRegion(c, 0, 0, mat, tex, maskA, settings, _pixelCache, null, ColorApplier.MaskScope.SharedMaterial));
            AssertRegionEquals(result, expect, maskA, "従来どおり先頭 (r, s) の結果");
        }

        [Test]
        public void ProcessMainTexGroup_SingleMember_DoesNotShare()
        {
            var (c, a, b, mat, tex) = SetUpPair(overlap: false);
            var settings = Settings(c);

            var result = Track(ColorApplier.ProcessMainTexGroup(c, new[] { (0, 0, mat) }, tex, settings, _pixelCache, null,
                ColorApplier.MaskScope.SharedMaterial, out bool shared));

            Assert.That(shared, Is.False);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void ProcessMainTexGroup_MaskOnSecondMember_OnlyProtectsItsOwnRegion()
        {
            var (c, a, b, mat, tex) = SetUpPair(overlap: false);
            var black = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            black.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
            black.Apply();
            _cleanup.Add(black);
            c.colorMasks.Add(new ColorMaskEntry(1, 0) { mask = black });
            var settings = Settings(c);

            var result = Track(ColorApplier.ProcessMainTexGroup(c, new[] { (0, 0, mat), (1, 0, mat) }, tex, settings, _pixelCache, null,
                ColorApplier.MaskScope.SharedMaterial, out bool shared));

            Assert.That(shared, Is.True);
            Assert.That(result, Is.Not.Null);

            var maskA = MeshUVRasterizer.Rasterize(a, new[] { 0 }, W, H);
            var maskB = MeshUVRasterizer.Rasterize(b, new[] { 0 }, W, H);
            AssertRegionEquals(result, tex, maskB, "黒マスクを付けた B の領域は元の色が維持される");

            var expectA = Track(ColorApplier.ProcessMainTexRegion(c, 0, 0, mat, tex, maskA, settings, _pixelCache, null, ColorApplier.MaskScope.PerRegion));
            AssertRegionEquals(result, expectA, maskA, "マスクの無い A の領域は通常どおり変換される");
        }
    }
}
