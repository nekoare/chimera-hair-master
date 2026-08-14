using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// アトラスのメモリ最適化（空アトラススキップ・不透明判定→DXT1）のテスト
    /// </summary>
    public class AtlasMemoryOptimizationTests
    {
        private Texture2D _atlas;
        private GameObject _go;
        private Material _material;
        private Texture2D _bumpTex;

        [TearDown]
        public void TearDown()
        {
            if (_atlas != null) Object.DestroyImmediate(_atlas);
            if (_go != null) Object.DestroyImmediate(_go);
            if (_material != null) Object.DestroyImmediate(_material);
            if (_bumpTex != null) Object.DestroyImmediate(_bumpTex);
        }

        // ---- IsEffectivelyOpaqueInIslands ----

        /// <summary>
        /// 背景アルファ・アイランド内アルファを指定してアトラスを作成
        /// </summary>
        private Texture2D CreateAtlas(int resolution, byte backgroundAlpha, byte islandAlpha, RectInt islandRect)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            var pixels = new Color32[resolution * resolution];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, backgroundAlpha);
            }
            for (int y = islandRect.yMin; y < islandRect.yMax; y++)
            {
                for (int x = islandRect.xMin; x < islandRect.xMax; x++)
                {
                    pixels[y * resolution + x] = new Color32(255, 255, 255, islandAlpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static List<IslandPlacement> SingleIsland(Vector2 position, Vector2 scale)
        {
            return new List<IslandPlacement>
            {
                new IslandPlacement
                {
                    rendererIndex = 0,
                    submeshIndex = 0,
                    atlasPosition = position,
                    atlasScale = scale
                }
            };
        }

        [Test]
        public void IsEffectivelyOpaqueInIslands_OpaqueIsland_ReturnsTrue()
        {
            // アイランド（左下 1/4）は不透明、背景は透明
            _atlas = CreateAtlas(64, backgroundAlpha: 0, islandAlpha: 255, islandRect: new RectInt(0, 0, 32, 32));
            var islands = SingleIsland(Vector2.zero, new Vector2(0.5f, 0.5f));

            Assert.That(TextureAtlasBuilder.IsEffectivelyOpaqueInIslands(_atlas, islands), Is.True,
                "背景（サンプリングされない領域）の透明ピクセルは判定に影響しないこと");
        }

        [Test]
        public void IsEffectivelyOpaqueInIslands_TransparentPixelInIsland_ReturnsFalse()
        {
            // アイランド内に半透明ピクセル
            _atlas = CreateAtlas(64, backgroundAlpha: 255, islandAlpha: 128, islandRect: new RectInt(0, 0, 32, 32));
            var islands = SingleIsland(Vector2.zero, new Vector2(0.5f, 0.5f));

            Assert.That(TextureAtlasBuilder.IsEffectivelyOpaqueInIslands(_atlas, islands), Is.False);
        }

        [Test]
        public void IsEffectivelyOpaqueInIslands_DxtDegradedAlpha_ReturnsTrue()
        {
            // DXT5圧縮済みソース由来だと不透明でも254前後に化けるため、250以上は不透明扱い
            _atlas = CreateAtlas(64, backgroundAlpha: 0, islandAlpha: 252, islandRect: new RectInt(0, 0, 32, 32));
            var islands = SingleIsland(Vector2.zero, new Vector2(0.5f, 0.5f));

            Assert.That(TextureAtlasBuilder.IsEffectivelyOpaqueInIslands(_atlas, islands), Is.True);
        }

        [Test]
        public void IsEffectivelyOpaqueInIslands_BelowThresholdAlpha_ReturnsFalse()
        {
            _atlas = CreateAtlas(64, backgroundAlpha: 255, islandAlpha: 249, islandRect: new RectInt(0, 0, 32, 32));
            var islands = SingleIsland(Vector2.zero, new Vector2(0.5f, 0.5f));

            Assert.That(TextureAtlasBuilder.IsEffectivelyOpaqueInIslands(_atlas, islands), Is.False);
        }

        // ---- AnyIslandHasTexture ----

        private ChimeraHairMaster SetUpComponentWithRenderer(out SkinnedMeshRenderer renderer)
        {
            _go = new GameObject("CHMTest");
            renderer = _go.AddComponent<SkinnedMeshRenderer>();
            _material = new Material(Shader.Find("Standard"));
            renderer.sharedMaterials = new[] { _material };

            var component = _go.AddComponent<ChimeraHairMaster>();
            component.targetRenderers.Add(renderer);
            return component;
        }

        [Test]
        public void AnyIslandHasTexture_NoSourceTexture_ReturnsFalse()
        {
            var component = SetUpComponentWithRenderer(out _);
            var islands = SingleIsland(Vector2.zero, Vector2.one);

            Assert.That(TextureAtlasBuilder.AnyIslandHasTexture(component, islands, "_BumpMap"), Is.False,
                "ソースにノーマルマップが無ければ false（アトラス生成をスキップできる）");
        }

        [Test]
        public void AnyIslandHasTexture_WithSourceTexture_ReturnsTrue()
        {
            var component = SetUpComponentWithRenderer(out _);
            _bumpTex = new Texture2D(4, 4);
            _material.SetTexture("_BumpMap", _bumpTex);
            var islands = SingleIsland(Vector2.zero, Vector2.one);

            Assert.That(TextureAtlasBuilder.AnyIslandHasTexture(component, islands, "_BumpMap"), Is.True);
        }

        [Test]
        public void AnyIslandHasTexture_InvalidRendererIndex_ReturnsFalse()
        {
            var component = SetUpComponentWithRenderer(out _);
            var islands = new List<IslandPlacement>
            {
                new IslandPlacement { rendererIndex = 5, submeshIndex = 0 }
            };

            Assert.That(TextureAtlasBuilder.AnyIslandHasTexture(component, islands, "_BumpMap"), Is.False);
        }
    }
}
