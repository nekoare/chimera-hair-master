using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 退行テスト (v1.5.14以前): PNG出力（色合わせ適用）と Prefab 出力はマテリアル単位の
    /// 重複排除がマスク適用より先に効くため、同じマテリアルを共有する複数 (renderer, submesh)
    /// のうち走査順で最初でない (r, s) に設定した色合わせ無視マスクが無視される不具合。
    /// マテリアル単位でマスクを解決する TryApplyForSharedMaterial の挙動を固定する。
    /// </summary>
    public class ColorMaskSharedMaterialTests
    {
        private static readonly Color Original = new Color(1f, 0f, 0f, 1f);
        private static readonly Color Transformed = new Color(0f, 0f, 1f, 1f);

        private readonly List<Object> _cleanup = new List<Object>();
        private ChimeraHairMaster _component;
        private Material _sharedMaterial;
        private Texture2D _originalTex;
        private Texture2D _processedTex;

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        private Texture2D SolidTexture(Color color)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.SetPixels(new[] { color, color, color, color });
            tex.Apply();
            _cleanup.Add(tex);
            return tex;
        }

        /// <summary>
        /// 同一マテリアルを共有する Renderer を2つ持つコンポーネントを作る
        /// （全サブメッシュ統合対象、マスク未設定）
        /// </summary>
        private void SetUpTwoRenderersSharingMaterial()
        {
            _sharedMaterial = new Material(Shader.Find("Standard"));
            _cleanup.Add(_sharedMaterial);

            var holder = new GameObject("CHMColorMaskTest");
            _cleanup.Add(holder);
            _component = holder.AddComponent<ChimeraHairMaster>();

            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject($"CHMColorMaskTestRenderer{i}");
                _cleanup.Add(go);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();

                // IsSubmeshIncluded が sharedMesh の subMeshCount を見るため実メッシュが必要
                var mesh = new Mesh();
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.triangles = new[] { 0, 1, 2 };
                _cleanup.Add(mesh);
                renderer.sharedMesh = mesh;

                renderer.sharedMaterials = new[] { _sharedMaterial };
                _component.targetRenderers.Add(renderer);
            }

            _originalTex = SolidTexture(Original);
            _processedTex = SolidTexture(Transformed);
        }

        private void AddMask(int rendererIndex, int submeshIndex, Color maskColor)
        {
            var mask = SolidTexture(maskColor);
            _component.colorMasks.Add(
                new ColorMaskEntry(rendererIndex, submeshIndex) { mask = mask });
        }

        private Texture2D Apply()
        {
            var result = ColorMaskApplier.TryApplyForSharedMaterial(
                _component, _sharedMaterial, _originalTex, _processedTex, compressResult: false);
            if (result != null) _cleanup.Add(result);
            return result;
        }

        private static void AssertPixel(Texture2D tex, Color expected, string message)
        {
            var actual = tex.GetPixel(0, 0);
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f), message + " (R)");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f), message + " (G)");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f), message + " (B)");
        }

        [Test]
        public void TryApplyForSharedMaterial_MaskOnLaterRenderer_IsApplied()
        {
            SetUpTwoRenderersSharingMaterial();
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);

            var result = Apply();

            Assert.That(result, Is.Not.Null,
                "走査順で最初でない (r, s) のマスクもマテリアル単位で解決されること（v1.5.14以前は無視された）");
            AssertPixel(result, Original, "黒マスクで元の色が維持されること");
        }

        [Test]
        public void TryApplyForSharedMaterial_MaskOnFirstRenderer_IsApplied()
        {
            SetUpTwoRenderersSharingMaterial();
            AddMask(rendererIndex: 0, submeshIndex: 0, Color.black);

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            AssertPixel(result, Original, "先頭 (r, s) のマスクは従来通り適用されること");
        }

        [Test]
        public void TryApplyForSharedMaterial_MultipleMasks_DarkestWins()
        {
            SetUpTwoRenderersSharingMaterial();
            AddMask(rendererIndex: 0, submeshIndex: 0, Color.white);
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            AssertPixel(result, Original,
                "複数マスクは最小値（黒=維持）優先で合成されること。白マスクが黒マスクの保護領域を打ち消さない");
        }

        [Test]
        public void TryApplyForSharedMaterial_NoMask_ReturnsNull()
        {
            SetUpTwoRenderersSharingMaterial();

            var result = Apply();

            Assert.That(result, Is.Null,
                "マスク未設定なら null（呼び出し側は変換済みテクスチャをそのまま使う）");
        }

        [Test]
        public void TryApplyForSharedMaterial_MaskOnExcludedSubmesh_IsIgnored()
        {
            SetUpTwoRenderersSharingMaterial();
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);
            _component.materialSelections.Add(
                new MaterialSelectionEntry(1, 0, false));

            var result = Apply();

            Assert.That(result, Is.Null,
                "統合対象外のサブメッシュのマスクは収集しないこと");
        }
    }
}
