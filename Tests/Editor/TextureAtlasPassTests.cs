using ChimeraHairMaster.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    public class TextureAtlasPassTests
    {
        private Material _from;
        private Material _to;

        [SetUp]
        public void SetUp()
        {
            _from = new Material(Shader.Find("Standard"));
            _to = null;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_from);
            if (_to != null) Object.DestroyImmediate(_to);
        }

        // 退行テスト (v1.4.0〜v1.5.5): 基準マテのシェーダーを対象に強制適用すると、
        // 描画モードの異なる対象（例: 不透明の耳 × 半透明の基準マテ）が不可視になる
        [Test]
        public void ApplyShaderSettings_KeepsTargetShader()
        {
            _to = new Material(Shader.Find("Unlit/Color"));

            TextureAtlasPass.ApplyShaderSettings(_from, _to);

            Assert.That(_to.shader.name, Is.EqualTo("Unlit/Color"));
        }

        [Test]
        public void ApplyShaderSettings_CopiesSharedProperties()
        {
            // _Color は Standard / Unlit/Color の両方に存在する
            _to = new Material(Shader.Find("Unlit/Color"));
            _from.SetColor("_Color", Color.red);
            _to.SetColor("_Color", Color.blue);
            _from.renderQueue = 3000;

            TextureAtlasPass.ApplyShaderSettings(_from, _to);

            Assert.That(_to.GetColor("_Color"), Is.EqualTo(Color.red));
            Assert.That(_to.renderQueue, Is.EqualTo(3000));
        }

        [Test]
        public void ApplyShaderSettings_DoesNotCopyTextures()
        {
            // _MainTex は Standard / Unlit/Texture の両方に存在する
            _to = new Material(Shader.Find("Unlit/Texture"));
            var tex = new Texture2D(2, 2);
            try
            {
                _from.SetTexture("_MainTex", tex);

                TextureAtlasPass.ApplyShaderSettings(_from, _to);

                Assert.That(_to.GetTexture("_MainTex"), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }
    }
}
