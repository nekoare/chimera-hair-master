using System.Collections.Generic;
using ChimeraHairMaster.Editor.NDMF;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 統合OFFのビルド経路で、同じ元マテリアルを共有する Renderer の出力が
    /// UV 非重複なら 1 マテリアル・1 テクスチャに合成され、各領域の色は分割時と同一になることを固定する。
    /// あわせて後段の ProcessPerRendererMaterials が共有マテリアルを再分割しないことも確認する。
    /// </summary>
    public class SharedMaterialBuildTests
    {
        private const int W = 16;
        private const int H = 16;
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            ColorTransformPass.ProcessedTextureCache.Clear();
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

        // ビルド経路が生成したマテリアル・テクスチャを破棄対象に登録する
        private void TrackOutputs(ChimeraHairMaster c)
        {
            foreach (var r in c.targetRenderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || _cleanup.Contains(m)) continue;
                    _cleanup.Add(m);
                    var t = m.GetTexture("_MainTex");
                    if (t != null && !_cleanup.Contains(t)) _cleanup.Add(t);
                }
            }
            if (ColorTransformPass.ProcessedTextureCache.TryGetValue(c, out var cache))
            {
                foreach (var t in cache.Values)
                {
                    if (t != null && !_cleanup.Contains(t)) _cleanup.Add(t);
                }
            }
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
        public void ProcessComponent_SharedMaterial_DisjointUV_AssignsOneMaterialAndTexture()
        {
            var tex = Gradient();
            var shared = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, shared);
            var b = MakeRenderer("B", 0.55f, 0.95f, shared);
            var c = MakeComponent("CHM", a, b);

            ColorTransformPass.ProcessComponent(c);
            TrackOutputs(c);

            var ma = a.sharedMaterials[0];
            var mb = b.sharedMaterials[0];
            Assert.That(ma, Is.Not.SameAs(shared), "元マテリアルは直接変更しない");
            Assert.That(mb, Is.SameAs(ma), "UV 非重複の共有マテリアルは 1 つのマテリアルに合成されること");
            Assert.That(ma.GetTexture("_MainTex"), Is.Not.SameAs(tex), "テクスチャは処理済みのものに差し替わること");
        }

        [Test]
        public void ProcessComponent_SharedMaterial_OverlappingUV_SplitsAsBefore()
        {
            var tex = Gradient();
            var shared = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.60f, shared);
            var b = MakeRenderer("B", 0.40f, 0.95f, shared);
            var c = MakeComponent("CHM", a, b);

            ColorTransformPass.ProcessComponent(c);
            TrackOutputs(c);

            Assert.That(a.sharedMaterials[0], Is.Not.SameAs(shared));
            Assert.That(b.sharedMaterials[0], Is.Not.SameAs(a.sharedMaterials[0]),
                "UV が重なる場合は従来どおり Renderer ごとに分割されること");
        }

        [Test]
        public void ProcessComponent_SharedMaterial_MeshMergeOn_DoesNotShare()
        {
            var tex = Gradient();
            var shared = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, shared);
            var b = MakeRenderer("B", 0.55f, 0.95f, shared);
            var c = MakeComponent("CHM", a, b);
            c.enableMeshMerge = true;

            ColorTransformPass.ProcessComponent(c);
            TrackOutputs(c);

            Assert.That(b.sharedMaterials[0], Is.Not.SameAs(a.sharedMaterials[0]),
                "統合ONは後段でアトラス化されるため共有しない（従来どおり）");
        }

        [Test]
        public void ProcessComponent_DifferentMaterials_AreNotMerged()
        {
            var tex = Gradient();
            var a = MakeRenderer("A", 0.05f, 0.45f, SharedMaterial(tex));
            var b = MakeRenderer("B", 0.55f, 0.95f, SharedMaterial(tex));
            var c = MakeComponent("CHM", a, b);

            ColorTransformPass.ProcessComponent(c);
            TrackOutputs(c);

            Assert.That(b.sharedMaterials[0], Is.Not.SameAs(a.sharedMaterials[0]),
                "元マテリアルが別なら（テクスチャが同じでも）別マテリアルのまま");
        }

        [Test]
        public void ProcessComponent_SharedMaterial_CompositeMatchesPerRendererResult()
        {
            // 共有あり: A, B が同じコンポーネント。B にだけ明度オフセット
            var tex = Gradient();
            var shared = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, shared);
            var b = MakeRenderer("B", 0.55f, 0.95f, shared);
            var c = MakeComponent("CHM", a, b);
            c.rendererBrightnessAdjustments.Add(new RendererBrightnessAdjustment(1) { brightnessOffset = 0.3f });

            // 参照: 同じ設定で 1 Renderer ずつ別コンポーネント（＝分割時の結果）
            var refA = MakeRenderer("refA", 0.05f, 0.45f, SharedMaterial(tex));
            var refB = MakeRenderer("refB", 0.55f, 0.95f, SharedMaterial(tex));
            var cA = MakeComponent("refCHM_A", refA);
            var cB = MakeComponent("refCHM_B", refB);
            cB.rendererBrightnessAdjustments.Add(new RendererBrightnessAdjustment(0) { brightnessOffset = 0.3f });

            ColorTransformPass.ProcessComponent(c);
            ColorTransformPass.ProcessComponent(cA);
            ColorTransformPass.ProcessComponent(cB);
            TrackOutputs(c);
            TrackOutputs(cA);
            TrackOutputs(cB);

            Assert.That(b.sharedMaterials[0], Is.SameAs(a.sharedMaterials[0]), "前提: 共有されていること");

            var composite = (Texture2D)a.sharedMaterials[0].GetTexture("_MainTex");
            var expectA = (Texture2D)refA.sharedMaterials[0].GetTexture("_MainTex");
            var expectB = (Texture2D)refB.sharedMaterials[0].GetTexture("_MainTex");
            var maskA = MeshUVRasterizer.Rasterize(a, new[] { 0 }, W, H);
            var maskB = MeshUVRasterizer.Rasterize(b, new[] { 0 }, W, H);

            Assert.That(CountTrue(maskA), Is.GreaterThan(0), "前提: A の UV 領域が空でない");
            Assert.That(CountTrue(maskB), Is.GreaterThan(0), "前提: B の UV 領域が空でない");
            AssertRegionEquals(composite, expectA, maskA, "A の領域は A 単体の結果と一致");
            AssertRegionEquals(composite, expectB, maskB, "B の領域は B 単体の結果と一致（明度オフセット込み）");
        }

        [Test]
        public void ProcessPerRendererMaterials_SharedInputMaterial_ProducesOneSettingsMaterial()
        {
            var tex = Gradient();
            var shared = SharedMaterial(tex);
            var a = MakeRenderer("A", 0.05f, 0.45f, shared);
            var b = MakeRenderer("B", 0.55f, 0.95f, shared);
            var c = MakeComponent("CHM", a, b);
            c.previewMaterial = SharedMaterial(tex);

            TextureAtlasPass.ProcessPerRendererMaterials(c);
            TrackOutputs(c);

            Assert.That(a.sharedMaterials[0].name, Does.EndWith("_CHM_Settings"));
            Assert.That(b.sharedMaterials[0], Is.SameAs(a.sharedMaterials[0]),
                "同じ入力マテリアルからは 1 つの _CHM_Settings マテリアルを作り、再分割しないこと");
        }
    }
}
