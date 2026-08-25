using ChimeraHairMaster.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 退行テスト (v1.5.12以前): メッシュ統合OFF + マットキャップ統一ONのとき、
    /// プレビューではマットキャップ画像が統一されて見えるのに、ビルド経路
    /// （TextureAtlasPass.ProcessPerRendererMaterials）では数値設定のみコピーされ
    /// 画像がコピーされず、アップロード後に反映されない不具合。
    /// _MatCapTex は lilToon 固有プロパティのため、lilToon が無い環境ではスキップする
    /// </summary>
    public class UnifyMatCapBuildTests
    {
        private GameObject _go;
        private Mesh _mesh;
        private Material _hairMaterial;
        private Material _previewMaterial;
        private Texture2D _hairMatCap;
        private Texture2D _previewMatCap;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                // ビルド経路が生成した複製マテリアル（_CHM_Settings）も破棄する
                var renderer = _go.GetComponent<SkinnedMeshRenderer>();
                if (renderer != null && renderer.sharedMaterials != null)
                {
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat != null && mat != _hairMaterial) Object.DestroyImmediate(mat);
                    }
                }
                Object.DestroyImmediate(_go);
            }
            if (_mesh != null) Object.DestroyImmediate(_mesh);
            if (_hairMaterial != null) Object.DestroyImmediate(_hairMaterial);
            if (_previewMaterial != null) Object.DestroyImmediate(_previewMaterial);
            if (_hairMatCap != null) Object.DestroyImmediate(_hairMatCap);
            if (_previewMatCap != null) Object.DestroyImmediate(_previewMatCap);
        }

        private ChimeraHairMaster SetUpComponent(bool unifyMatCap)
        {
            var lilToon = Shader.Find("lilToon");
            if (lilToon == null)
            {
                Assert.Ignore("lilToon が見つからないためスキップ（_MatCapTex を持つシェーダが必要）");
            }

            _go = new GameObject("CHMUnifyMatCapTest");
            var renderer = _go.AddComponent<SkinnedMeshRenderer>();

            // IsSubmeshIncluded が sharedMesh の subMeshCount を見るため実メッシュが必要
            _mesh = new Mesh();
            _mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            _mesh.triangles = new[] { 0, 1, 2 };
            renderer.sharedMesh = _mesh;

            _hairMatCap = new Texture2D(2, 2);
            _hairMaterial = new Material(lilToon);
            _hairMaterial.SetTexture("_MatCapTex", _hairMatCap);
            renderer.sharedMaterials = new[] { _hairMaterial };

            _previewMatCap = new Texture2D(2, 2);
            _previewMaterial = new Material(lilToon);
            _previewMaterial.SetTexture("_MatCapTex", _previewMatCap);

            var component = _go.AddComponent<ChimeraHairMaster>();
            component.targetRenderers.Add(renderer);
            component.previewMaterial = _previewMaterial;
            component.enableMeshMerge = false;
            component.unifyMatCap = unifyMatCap;
            return component;
        }

        private Material ResultMaterial(ChimeraHairMaster component)
        {
            return component.targetRenderers[0].sharedMaterials[0];
        }

        [Test]
        public void ProcessPerRendererMaterials_UnifyMatCapOn_CopiesMatCapImage()
        {
            var component = SetUpComponent(unifyMatCap: true);

            TextureAtlasPass.ProcessPerRendererMaterials(component);

            var result = ResultMaterial(component);
            Assert.That(result.name, Does.EndWith("_CHM_Settings"));
            Assert.That(result.GetTexture("_MatCapTex"), Is.EqualTo(_previewMatCap),
                "ビルド経路でもプレビュー同様にマットキャップ画像が基準マテリアルから統一されること");
        }

        [Test]
        public void ProcessPerRendererMaterials_UnifyMatCapOff_KeepsOriginalMatCapImage()
        {
            var component = SetUpComponent(unifyMatCap: false);

            TextureAtlasPass.ProcessPerRendererMaterials(component);

            var result = ResultMaterial(component);
            Assert.That(result.GetTexture("_MatCapTex"), Is.EqualTo(_hairMatCap),
                "統一OFFでは各素材のマットキャップ画像を維持すること");
        }

        [Test]
        public void ProcessPerRendererMaterials_UnifyMatCapOn_OverwritesWithNone()
        {
            var component = SetUpComponent(unifyMatCap: true);
            _previewMaterial.SetTexture("_MatCapTex", null);

            TextureAtlasPass.ProcessPerRendererMaterials(component);

            var result = ResultMaterial(component);
            Assert.That(result.GetTexture("_MatCapTex"), Is.Null,
                "基準マテリアルが None の場合も None で統一されること（プレビューの挙動と一致）");
        }
    }
}
