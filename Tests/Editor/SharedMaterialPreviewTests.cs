using System.Collections.Generic;
using ChimeraHairMaster.Editor.NDMF;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// プレビュー（統合OFF）の色変換キャッシュ生成で、同じテクスチャを使う (renderer, submesh) が
    /// UV 非重複なら領域ごとに自分の統計で変換して 1 枚に合成されることを固定する
    /// （以前は最初の (r, s) の領域統計でテクスチャ全体を変換していたため、共有マテリアルの 2 つ目以降が
    /// ビルドと違う明るさになっていた）。
    /// </summary>
    public class SharedMaterialPreviewTests
    {
        private const int W = 16;
        private const int H = 16;
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

        private Texture2D Track(Texture2D tex)
        {
            if (tex != null) _cleanup.Add(tex);
            return tex;
        }

        private static ColorTransformSettings Settings(ChimeraHairMaster c)
        {
            return ColorTransformSettings.FromComponent(c, ColorApplier.DetermineSourceColor(c));
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
        public void TryTransformSharedTexture_DisjointUV_EachRegionUsesOwnStats()
        {
            var tex = Gradient();
            var mat = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, mat);
            var b = MakeRenderer("B", 0.55f, 0.95f, mat);
            var c = MakeComponent("CHM", a, b);
            var settings = Settings(c);

            var result = Track(ChimeraHairMasterPreview.TryTransformSharedTexture(
                tex, settings, new[] { (a, 0, 0f), (b, 0, 0f) }));

            Assert.That(result, Is.Not.Null, "UV 非重複なら合成されること");

            var maskA = MeshUVRasterizer.Rasterize(a, new[] { 0 }, W, H);
            var maskB = MeshUVRasterizer.Rasterize(b, new[] { 0 }, W, H);
            Assert.That(CountTrue(maskA), Is.GreaterThan(0), "前提: A の UV 領域が空でない");
            Assert.That(CountTrue(maskB), Is.GreaterThan(0), "前提: B の UV 領域が空でない");

            var expectA = Track(ChimeraHairMasterPreview.TransformRegion(tex, settings, a, new[] { 0 }, 0f, null));
            var expectB = Track(ChimeraHairMasterPreview.TransformRegion(tex, settings, b, new[] { 0 }, 0f, null));

            AssertRegionEquals(result, expectA, maskA, "A の領域は A 自身の統計で変換した結果");
            AssertRegionEquals(result, expectB, maskB, "B の領域は B 自身の統計で変換した結果");
        }

        [Test]
        public void TryTransformSharedTexture_OverlappingUV_ReturnsNull()
        {
            var tex = Gradient();
            var mat = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.60f, mat);
            var b = MakeRenderer("B", 0.40f, 0.95f, mat);
            var c = MakeComponent("CHM", a, b);

            var result = Track(ChimeraHairMasterPreview.TryTransformSharedTexture(
                tex, Settings(c), new[] { (a, 0, 0f), (b, 0, 0f) }));

            Assert.That(result, Is.Null, "UV が重なる場合は合成せず従来処理に落とす");
        }

        [Test]
        public void CollectSlotTextureUsers_SharedMaterial_ListsBothRenderers()
        {
            var tex = Gradient();
            var mat = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, mat);
            var b = MakeRenderer("B", 0.55f, 0.95f, mat);
            var c = MakeComponent("CHM", a, b);

            var users = ChimeraHairMasterPreview.CollectSlotTextureUsers(c);

            Assert.That(users.ContainsKey(tex));
            Assert.That(users[tex].Count, Is.EqualTo(2));
            Assert.That(users[tex][0].renderer, Is.SameAs(a));
            Assert.That(users[tex][1].renderer, Is.SameAs(b));
        }

        [Test]
        public void CollectSlotTextureUsers_ExcludedSubmesh_IsNotListed()
        {
            var tex = Gradient();
            var mat = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, mat);
            var b = MakeRenderer("B", 0.55f, 0.95f, mat);
            var c = MakeComponent("CHM", a, b);
            c.materialSelections.Add(new MaterialSelectionEntry { rendererIndex = 1, submeshIndex = 0, isIncluded = false });

            var users = ChimeraHairMasterPreview.CollectSlotTextureUsers(c);

            Assert.That(users[tex].Count, Is.EqualTo(1), "統合対象外の (r, s) は使用者に数えない");
        }
    }
}
