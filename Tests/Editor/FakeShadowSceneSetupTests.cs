using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// FakeShadowのシーン直接セットアップ（FakeShadowSceneSetup）の挙動を固定する。
    /// - 影レンダラーを髪の子に生成し、影マテリアルはアセット保存
    /// - 顔: ステンシル書き込みが無ければクローンをアセット保存して差し替え（元マテリアルは不変）、
    ///   既に書き込み済みならそのRefを尊重して顔を触らない
    /// - 髪queueは直接引き上げ
    /// - 再実行は冪等（影レンダラーが増えない・顔スロットが安定）
    /// - 適用後は enableFakeShadow が OFF になる
    /// </summary>
    public class FakeShadowSceneSetupTests
    {
        private const string TestFolder = "Assets/CHMFakeShadowSceneSetupTests";

        private readonly List<Object> _cleanup = new List<Object>();
        private GameObject _avatarRoot;
        private ChimeraHairMaster _component;
        private SkinnedMeshRenderer _faceRenderer;
        private Material _faceMaterial;
        private SkinnedMeshRenderer _hairRenderer;
        private Material _hairMaterial;

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();

            AssetDatabase.DeleteAsset(TestFolder);
        }

        private Mesh CreateTriMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);
            _cleanup.Add(mesh);
            return mesh;
        }

        private Material CreateLilToonMaterial(string name, int renderQueue)
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "テスト環境に lilToon が必要です");
            var mat = new Material(shader) { name = name };
            mat.renderQueue = renderQueue;
            _cleanup.Add(mat);
            return mat;
        }

        private SkinnedMeshRenderer CreateRenderer(string name, Transform parent, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateTriMesh();
            renderer.sharedMaterials = new[] { material };
            return renderer;
        }

        private void SetUpAvatar(int faceQueue = 2450, int hairQueue = 2450)
        {
            _avatarRoot = new GameObject("TestAvatar");
            _cleanup.Add(_avatarRoot);

            var holder = new GameObject("CHM");
            holder.transform.SetParent(_avatarRoot.transform);
            _component = holder.AddComponent<ChimeraHairMaster>();

            _faceMaterial = CreateLilToonMaterial("FaceSkin", faceQueue);
            _faceRenderer = CreateRenderer("Body", _avatarRoot.transform, _faceMaterial);

            _hairMaterial = CreateLilToonMaterial("HairMat", hairQueue);
            _hairRenderer = CreateRenderer("Hair", _avatarRoot.transform, _hairMaterial);

            _component.enableFakeShadow = true;
            _component.fakeShadowFaceRenderer = _faceRenderer;
            _component.fakeShadowFaceMaterialIndexes = new List<int> { 0 };
            _component.targetRenderers.Add(_hairRenderer);
        }

        private FakeShadowSceneSetup.Result Apply()
        {
            return FakeShadowSceneSetup.Apply(_component, TestFolder);
        }

        private static int CountShadowChildren(SkinnedMeshRenderer renderer)
        {
            int count = 0;
            foreach (Transform child in renderer.transform)
            {
                if (child.name.EndsWith(FakeShadowSetup.ShadowRendererSuffix)) count++;
            }
            return count;
        }

        [Test]
        public void Apply_CreatesShadowChild_WithSavedAssetMaterial()
        {
            SetUpAvatar();

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            Assert.That(CountShadowChildren(_hairRenderer), Is.EqualTo(1));
            var shadow = result.ShadowRenderers[0];
            Assert.That(shadow.transform.parent, Is.SameAs(_hairRenderer.transform));
            Assert.That(shadow.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));

            var mat = shadow.sharedMaterials[0];
            Assert.That(mat.shader.name, Is.EqualTo(FakeShadowSetup.ShadowShaderName));
            Assert.That(AssetDatabase.Contains(mat), Is.True, "影マテリアルはアセットとして保存されること（シーン保存でMissingにならない）");
            Assert.That(mat.GetFloat("_StencilRef"), Is.EqualTo(48f));
            Assert.That(mat.renderQueue, Is.EqualTo(2451), "影queueは顔queue(2450)+1");
        }

        [Test]
        public void Apply_FaceWithoutStencilWrite_CloneAssetAssigned_OriginalUntouched()
        {
            SetUpAvatar();
            float originalPass = _faceMaterial.GetFloat("_StencilPass");

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            var slot = _faceRenderer.sharedMaterials[0];
            Assert.That(slot, Is.Not.SameAs(_faceMaterial), "顔はクローンに差し替えること（元マテリアルは直接編集しない）");
            Assert.That(AssetDatabase.Contains(slot), Is.True, "顔クローンはアセットとして保存されること");
            Assert.That(slot.GetFloat("_StencilRef"), Is.EqualTo(48f));
            Assert.That(slot.GetFloat("_StencilComp"), Is.EqualTo((float)CompareFunction.Always));
            Assert.That(slot.GetFloat("_StencilPass"), Is.EqualTo((float)StencilOp.Replace));
            Assert.That(_faceMaterial.GetFloat("_StencilPass"), Is.EqualTo(originalPass), "元の顔マテリアルは不変であること");
        }

        [Test]
        public void Apply_FaceMaterialIsAsset_CloneSavedNextToOriginal()
        {
            SetUpAvatar();
            // 顔マテリアルをサブフォルダのアセットにして、クローンが同じフォルダに保存されることを確認
            string materialFolder = TestFolder + "/Mats";
            System.IO.Directory.CreateDirectory(materialFolder);
            AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(_faceMaterial, materialFolder + "/FaceSkin.mat");
            // アセット化したため DestroyImmediate の対象から外す（TearDown の DeleteAsset で消える）
            _cleanup.Remove(_faceMaterial);

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            var slot = _faceRenderer.sharedMaterials[0];
            Assert.That(AssetDatabase.GetAssetPath(slot), Is.EqualTo(materialFolder + "/FaceSkin_CHMFakeShadowFace.mat"),
                "顔クローンは元マテリアルと同じフォルダに保存されること");
        }

        [Test]
        public void Apply_FaceAlreadyWritingStencil_RefReused_FaceUntouched()
        {
            SetUpAvatar();
            // まつ毛の透け表現などで既にステンシル書き込みがある想定
            _faceMaterial.SetFloat("_StencilRef", 51f);
            _faceMaterial.SetFloat("_StencilPass", (float)StencilOp.Replace);

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            Assert.That(_faceRenderer.sharedMaterials[0], Is.SameAs(_faceMaterial), "書き込み済みの顔マテリアルは差し替えないこと");
            Assert.That(result.UsedStencilRef, Is.EqualTo(51), "既存の書き込みRefを影側が使うこと");
            Assert.That(result.ReusedExistingStencilRef, Is.True);
            Assert.That(result.ShadowMaterial.GetFloat("_StencilRef"), Is.EqualTo(51f));
        }

        [Test]
        public void Apply_HairQueueAtOrBelowShadowQueue_RaisedInPlace()
        {
            SetUpAvatar(faceQueue: 2450, hairQueue: 2450);

            var result = Apply();

            Assert.That(result, Is.Not.Null);
            Assert.That(_hairRenderer.sharedMaterials[0], Is.SameAs(_hairMaterial), "髪マテリアルはクローンせず直接調整すること");
            Assert.That(_hairMaterial.renderQueue, Is.EqualTo(2452), "髪queueは影queue(2451)+1に直接引き上げ");
        }

        [Test]
        public void Apply_Rerun_KeepsSingleShadowChild_AndFaceSlotStable()
        {
            SetUpAvatar();

            var first = Apply();
            string facePathAfterFirst = AssetDatabase.GetAssetPath(_faceRenderer.sharedMaterials[0]);
            var second = Apply();

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(CountShadowChildren(_hairRenderer), Is.EqualTo(1), "再実行で影レンダラーが増えないこと");

            // 注: SaveAssets/再インポートを跨ぐと、同じアセットでもマネージド参照の同一性は
            // 保証されない（別インスタンスが返りうる）ため、参照比較ではなくアセットパスと
            // 生成ファイル数で「クローンのクローンを作っていない」ことを検証する
            Assert.That(facePathAfterFirst, Is.Not.Empty, "顔クローンはアセットとして保存されていること");
            Assert.That(AssetDatabase.GetAssetPath(_faceRenderer.sharedMaterials[0]), Is.EqualTo(facePathAfterFirst),
                "再実行で顔クローンのクローンを作らず、同じアセットを更新すること");
            Assert.That(System.IO.Directory.GetFiles(TestFolder, "*.mat").Length, Is.EqualTo(2),
                "生成アセットは影マテリアル1つ＋顔クローン1つのままであること");
        }

        [Test]
        public void Apply_Rerun_ReflectsChangedStencilRef()
        {
            SetUpAvatar();

            Apply();
            _component.enableFakeShadow = true;
            _component.fakeShadowStencilRef = 60;
            var second = Apply();

            Assert.That(second, Is.Not.Null);
            Assert.That(second.UsedStencilRef, Is.EqualTo(60), "自前クローンは既存書き込み扱いせず、設定値の変更を反映すること");
            Assert.That(_faceRenderer.sharedMaterials[0].GetFloat("_StencilRef"), Is.EqualTo(60f));
            Assert.That(second.ShadowMaterial.GetFloat("_StencilRef"), Is.EqualTo(60f));
        }

        [Test]
        public void Apply_TurnsOffEnableFakeShadow()
        {
            SetUpAvatar();

            Apply();

            Assert.That(_component.enableFakeShadow, Is.False, "適用後はNDMF/プレビューとの二重適用を防ぐためOFFにすること");
        }

        [Test]
        public void Apply_InvalidConfig_ReturnsNullWithoutThrow()
        {
            SetUpAvatar();
            _component.fakeShadowFaceRenderer = null;

            Assert.That(Apply(), Is.Null, "顔Renderer未設定なら null（例外を出さない）");
        }
    }
}
