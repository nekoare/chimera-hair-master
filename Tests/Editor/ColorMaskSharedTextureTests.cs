using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 退行テスト: PNG出力（色合わせ適用）は出力パスが元テクスチャ名由来
    /// （{名前}_CHM.png・上書き保存）のため、**別マテリアル**が同一 _MainTex を共有すると
    /// 後に処理されたマテリアルの結果が勝ち、マスク無しマテリアルが後だとマスク版が潰される。
    /// テクスチャ単位でマスクを解決する TryApplyForSharedMainTex の挙動を固定する
    /// （どの順で書かれても同じマスクが乗り、上書きが無害になる）。
    /// </summary>
    public class ColorMaskSharedTextureTests
    {
        private static readonly Color Original = new Color(1f, 0f, 0f, 1f);
        private static readonly Color Transformed = new Color(0f, 0f, 1f, 1f);

        private readonly List<Object> _cleanup = new List<Object>();
        private ChimeraHairMaster _component;
        private Material[] _materials;
        private Texture2D _sharedMainTex;
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
        /// 別々のマテリアルを持つ Renderer を2つ作り、両方の _MainTex に同じテクスチャを設定する
        /// （全サブメッシュ統合対象、マスク未設定）
        /// </summary>
        private void SetUpTwoMaterialsSharingMainTex()
        {
            _sharedMainTex = SolidTexture(Original);
            _processedTex = SolidTexture(Transformed);

            var holder = new GameObject("CHMSharedTexTest");
            _cleanup.Add(holder);
            _component = holder.AddComponent<ChimeraHairMaster>();

            _materials = new Material[2];
            for (int i = 0; i < 2; i++)
            {
                var material = new Material(Shader.Find("Standard"));
                material.SetTexture("_MainTex", _sharedMainTex);
                _cleanup.Add(material);
                _materials[i] = material;

                var go = new GameObject($"CHMSharedTexTestRenderer{i}");
                _cleanup.Add(go);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();

                // IsSubmeshIncluded が sharedMesh の subMeshCount を見るため実メッシュが必要
                var mesh = new Mesh();
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.triangles = new[] { 0, 1, 2 };
                _cleanup.Add(mesh);
                renderer.sharedMesh = mesh;

                renderer.sharedMaterials = new[] { material };
                _component.targetRenderers.Add(renderer);
            }
        }

        private void AddMask(int rendererIndex, int submeshIndex, Color maskColor)
        {
            var mask = SolidTexture(maskColor);
            _component.colorMasks.Add(
                new ColorMaskEntry(rendererIndex, submeshIndex) { mask = mask });
        }

        private Texture2D Apply()
        {
            var result = ColorMaskApplier.TryApplyForSharedMainTex(
                _component, _sharedMainTex, _processedTex, compressResult: false);
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
        public void TryApplyForSharedMainTex_MaskViaOtherMaterial_IsApplied()
        {
            SetUpTwoMaterialsSharingMainTex();
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);

            var result = Apply();

            Assert.That(result, Is.Not.Null,
                "別マテリアルでも同一 _MainTex を共有する (r, s) のマスクはテクスチャ単位で収集されること" +
                "（マテリアル単位の解決では脱落し、PNG上書きの順序次第でマスクが消えていた）");
            AssertPixel(result, Original, "黒マスクで元の色が維持されること");
        }

        [Test]
        public void TryApplyForSharedMainTex_NoMask_ReturnsNull()
        {
            SetUpTwoMaterialsSharingMainTex();

            var result = Apply();

            Assert.That(result, Is.Null,
                "マスク未設定なら null（呼び出し側は変換済みテクスチャをそのまま使う）");
        }

        [Test]
        public void TryApplyForSharedMainTex_MaskOnDifferentTexture_IsIgnored()
        {
            SetUpTwoMaterialsSharingMainTex();
            // 2つ目のマテリアルは別テクスチャを使う → そこに付いたマスクは対象外
            var otherTex = SolidTexture(Color.green);
            _materials[1].SetTexture("_MainTex", otherTex);
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);

            var result = Apply();

            Assert.That(result, Is.Null,
                "_MainTex が別テクスチャの (r, s) のマスクは収集しないこと");
        }

        [Test]
        public void TryApplyForSharedMainTex_MaskOnExcludedSubmesh_IsIgnored()
        {
            SetUpTwoMaterialsSharingMainTex();
            AddMask(rendererIndex: 1, submeshIndex: 0, Color.black);
            _component.materialSelections.Add(
                new MaterialSelectionEntry(1, 0, false));

            var result = Apply();

            Assert.That(result, Is.Null,
                "統合対象外のサブメッシュのマスクは収集しないこと");
        }
    }
}
