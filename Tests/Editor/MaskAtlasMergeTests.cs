using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 追加マスク（マットキャップ/エミッション）のアセット生成条件・ガード判定のテスト。
    /// lilToon に依存しないよう、テスト用スロット定義は Standard シェーダの
    /// _Metallic（float、既定0）を機能トグルとして流用する
    /// </summary>
    public class MaskAtlasMergeTests
    {
        private GameObject _go;
        private Material _islandMaterial;
        private Material _islandMaterial2;
        private Texture2D _tex;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_islandMaterial != null) Object.DestroyImmediate(_islandMaterial);
            if (_islandMaterial2 != null) Object.DestroyImmediate(_islandMaterial2);
            if (_tex != null) Object.DestroyImmediate(_tex);
        }

        private ChimeraHairMaster SetUpComponent()
        {
            _go = new GameObject("CHMMaskTest");
            var renderer = _go.AddComponent<SkinnedMeshRenderer>();
            _islandMaterial = new Material(Shader.Find("Standard"));
            renderer.sharedMaterials = new[] { _islandMaterial };

            var component = _go.AddComponent<ChimeraHairMaster>();
            component.targetRenderers.Add(renderer);
            return component;
        }

        private static List<IslandPlacement> SingleIsland()
        {
            return new List<IslandPlacement>
            {
                new IslandPlacement
                {
                    rendererIndex = 0,
                    submeshIndex = 0,
                    atlasPosition = Vector2.zero,
                    atlasScale = Vector2.one
                }
            };
        }

        /// <summary>
        /// 2つ目のサブメッシュ用マテリアルと、それを参照する2島構成を追加
        /// </summary>
        private List<IslandPlacement> TwoIslandsWithSecondMaterial(ChimeraHairMaster component)
        {
            _islandMaterial2 = new Material(Shader.Find("Standard"));
            var renderer = component.targetRenderers[0];
            renderer.sharedMaterials = new[] { _islandMaterial, _islandMaterial2 };

            var islands = SingleIsland();
            islands.Add(new IslandPlacement
            {
                rendererIndex = 0,
                submeshIndex = 1,
                atlasPosition = new Vector2(0.5f, 0f),
                atlasScale = new Vector2(0.5f, 0.5f)
            });
            return islands;
        }

        private static TextureAtlasBuilder.MaskSlotDefinition TestSlot()
        {
            // _Metallic を機能トグルに見立てたテスト用スロット
            return new TextureAtlasBuilder.MaskSlotDefinition
            {
                propertyName = "_BumpMap",
                enableProperty = "_Metallic",
                fallbackColor = _ => Color.white,
            };
        }

        // ---- ShouldGenerateAdditionalMask ----

        [Test]
        public void ShouldGenerate_EnabledIslandWithTexture_ReturnsTrue()
        {
            var component = SetUpComponent();
            _islandMaterial.SetFloat("_Metallic", 1f);
            _tex = new Texture2D(4, 4);
            _islandMaterial.SetTexture("_BumpMap", _tex);

            Assert.That(TextureAtlasBuilder.ShouldGenerateAdditionalMask(
                component, SingleIsland(), TestSlot()), Is.True);
        }

        [Test]
        public void ShouldGenerate_MixedIslandOnOff_NoTexture_ReturnsTrue()
        {
            var component = SetUpComponent();
            var islands = TwoIslandsWithSecondMaterial(component);
            _islandMaterial.SetFloat("_Metallic", 1f);
            _islandMaterial2.SetFloat("_Metallic", 0f);

            // テクスチャが無くても、ON/OFF混在なら塗り分けマスクとして意味がある
            Assert.That(TextureAtlasBuilder.ShouldGenerateAdditionalMask(
                component, islands, TestSlot()), Is.True);
        }

        [Test]
        public void ShouldGenerate_AllIslandsOff_ReturnsFalse()
        {
            var component = SetUpComponent();
            _islandMaterial.SetFloat("_Metallic", 0f);

            // 全島OFF = マスク不要（生成しても真っ黒/透明にしかならない）
            Assert.That(TextureAtlasBuilder.ShouldGenerateAdditionalMask(
                component, SingleIsland(), TestSlot()), Is.False);
        }

        [Test]
        public void ShouldGenerate_AllIslandsOn_NoTexture_ReturnsFalse()
        {
            var component = SetUpComponent();
            _islandMaterial.SetFloat("_Metallic", 1f);

            // 全島ONかつマスク無し = マスク無し（フル適用）で足りる
            Assert.That(TextureAtlasBuilder.ShouldGenerateAdditionalMask(
                component, SingleIsland(), TestSlot()), Is.False);
        }

        [Test]
        public void ShouldGenerate_TextureOnDisabledIslandOnly_ReturnsFalse()
        {
            var component = SetUpComponent();
            _islandMaterial.SetFloat("_Metallic", 0f);
            // 機能OFFの島のテクスチャは焼き込まれないため、生成トリガーにもしない
            _tex = new Texture2D(4, 4);
            _islandMaterial.SetTexture("_BumpMap", _tex);

            Assert.That(TextureAtlasBuilder.ShouldGenerateAdditionalMask(
                component, SingleIsland(), TestSlot()), Is.False);
        }

        // ---- IsUV0WithoutAnimation ----

        [Test]
        public void IsUV0WithoutAnimation_NullMaterial_ReturnsTrue()
        {
            var slot = TestSlot();
            slot.uvModeProperty = "_Metallic";

            Assert.That(TextureAtlasBuilder.IsUV0WithoutAnimation(null, slot), Is.True);
        }

        [Test]
        public void IsUV0WithoutAnimation_UVModeZero_ReturnsTrue()
        {
            var component = SetUpComponent();
            var slot = TestSlot();
            slot.uvModeProperty = "_Metallic";
            _islandMaterial.SetFloat("_Metallic", 0f);

            Assert.That(TextureAtlasBuilder.IsUV0WithoutAnimation(_islandMaterial, slot), Is.True);
        }

        [Test]
        public void IsUV0WithoutAnimation_UVModeNonZero_ReturnsFalse()
        {
            var component = SetUpComponent();
            var slot = TestSlot();
            slot.uvModeProperty = "_Metallic";
            _islandMaterial.SetFloat("_Metallic", 1f);

            Assert.That(TextureAtlasBuilder.IsUV0WithoutAnimation(_islandMaterial, slot), Is.False);
        }

        [Test]
        public void IsUV0WithoutAnimation_PropertyMissing_ReturnsTrue()
        {
            var component = SetUpComponent();
            var slot = TestSlot();
            slot.uvModeProperty = "_NoSuchProperty";
            slot.scrollRotateProperty = "_NoSuchVector";

            Assert.That(TextureAtlasBuilder.IsUV0WithoutAnimation(_islandMaterial, slot), Is.True);
        }

        // ---- CollectMatCapTextures ----

        [Test]
        public void CollectMatCapTextures_NoMatCap_ReturnsEmpty()
        {
            var component = SetUpComponent();
            component.islandPlacements = SingleIsland();

            Assert.That(TextureAtlasBuilder.CollectMatCapTextures(component), Is.Empty);
        }
    }
}
