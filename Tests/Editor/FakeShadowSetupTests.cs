using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// FakeShadowセットアップの中核ロジック FakeShadowSetup の挙動を固定する。
    /// - 顔マテリアル: ステンシル書き込み（Ref/Always/Replace）のクローンに差し替え、非選択は不変
    /// - 影レンダラー: 髪の複製（子なし）に専用シェーダーのマテリアル（Ref/Equal/Zero、ZWrite off、
    ///   renderQueue = 顔queue最大+1）を割り当て
    /// - 髪queue: 影queue以下なら クローンで影queue+1 に引き上げ（同一マテリアルはクローン共有）
    /// </summary>
    public class FakeShadowSetupTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private ChimeraHairMaster _component;
        private SkinnedMeshRenderer _faceRenderer;
        private Material _faceMaterial;

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        private Mesh CreateTriMesh(int submeshCount = 1)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            mesh.subMeshCount = submeshCount;
            for (int s = 0; s < submeshCount; s++)
            {
                mesh.SetTriangles(new[] { 0, 1, 2 }, s, false);
            }
            _cleanup.Add(mesh);
            return mesh;
        }

        private SkinnedMeshRenderer CreateRenderer(string name, Material[] materials, int submeshCount = 1)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateTriMesh(submeshCount);
            renderer.sharedMaterials = materials;
            return renderer;
        }

        private Material CreateLilToonMaterial(string name, int renderQueue = -1)
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "テスト環境に lilToon が必要です");
            var mat = new Material(shader) { name = name };
            if (renderQueue >= 0) mat.renderQueue = renderQueue;
            _cleanup.Add(mat);
            return mat;
        }

        /// <summary>
        /// 顔Renderer（マテリアル2枚・index 0 のみ選択）と設定済みコンポーネントを作る
        /// </summary>
        private void SetUpComponentWithFace(int faceQueue = 2000)
        {
            var holder = new GameObject("CHMFakeShadowTest");
            _cleanup.Add(holder);
            _component = holder.AddComponent<ChimeraHairMaster>();

            _faceMaterial = CreateLilToonMaterial("FaceSkin", faceQueue);
            var otherFaceMaterial = CreateLilToonMaterial("FaceEye", faceQueue);
            _faceRenderer = CreateRenderer("Body", new[] { _faceMaterial, otherFaceMaterial }, submeshCount: 2);

            _component.enableFakeShadow = true;
            _component.fakeShadowFaceRenderer = _faceRenderer;
            _component.fakeShadowFaceMaterialIndexes = new List<int> { 0 };
        }

        private FakeShadowSetup.Result Apply(params SkinnedMeshRenderer[] hairRenderers)
        {
            var result = FakeShadowSetup.Apply(_component, hairRenderers);
            if (result != null)
            {
                foreach (var mat in result.GeneratedMaterials)
                {
                    if (mat != null) _cleanup.Add(mat);
                }
                foreach (var renderer in result.ShadowRenderers)
                {
                    if (renderer != null) _cleanup.Add(renderer.gameObject);
                }
            }
            return result;
        }

        [Test]
        public void Apply_SelectedFaceMaterial_GetsStencilWrite_UnselectedUnchanged()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });
            var originalUnselected = _faceRenderer.sharedMaterials[1];

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            var faceMats = _faceRenderer.sharedMaterials;
            Assert.That(faceMats[0], Is.Not.SameAs(_faceMaterial), "選択した顔マテリアルはクローンに差し替えられること");
            Assert.That(faceMats[0].GetFloat("_StencilRef"), Is.EqualTo(48f), "ステンシル値は既定48");
            Assert.That(faceMats[0].GetFloat("_StencilComp"), Is.EqualTo((float)CompareFunction.Always));
            Assert.That(faceMats[0].GetFloat("_StencilPass"), Is.EqualTo((float)StencilOp.Replace));
            Assert.That(faceMats[0].renderQueue, Is.EqualTo(_faceMaterial.renderQueue), "顔のrenderQueueは変更しない");
            Assert.That(faceMats[1], Is.SameAs(originalUnselected), "非選択の顔マテリアルは不変であること");
        }

        [Test]
        public void Apply_FaceAlreadyWritingStencil_RefReused_FaceUntouched()
        {
            SetUpComponentWithFace();
            // まつ毛の透け表現などで既にステンシル書き込みがある想定
            _faceMaterial.SetFloat("_StencilRef", 51f);
            _faceMaterial.SetFloat("_StencilPass", (float)StencilOp.Replace);
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            Assert.That(_faceRenderer.sharedMaterials[0], Is.SameAs(_faceMaterial),
                "書き込み済みの顔マテリアルは差し替えないこと（既存の透け表現などを壊さない）");
            Assert.That(result.ShadowMaterial.GetFloat("_StencilRef"), Is.EqualTo(51f),
                "影マテリアルは既存の書き込みRefを使うこと");
        }

        [Test]
        public void Apply_CreatesShadowRenderer_WithShadowMaterialSettings()
        {
            SetUpComponentWithFace(faceQueue: 2450);
            _component.fakeShadowColor = new Color(0.9f, 0.8f, 0.7f, 1f);
            _component.fakeShadowDirection = new Vector2(0.3f, -0.1f);
            _component.fakeShadowOffset = 0.01f;
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2465) });

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ShadowRenderers.Count, Is.EqualTo(1));
            var shadow = result.ShadowRenderers[0];
            Assert.That(shadow.transform.parent, Is.SameAs(hair.transform), "影レンダラーは髪の子であること");
            Assert.That(shadow.gameObject.name, Does.EndWith(FakeShadowSetup.ShadowRendererSuffix),
                "影レンダラー名はCHM識別サフィックス付きであること");

            var mat = shadow.sharedMaterials[0];
            Assert.That(mat.shader.name, Is.EqualTo(FakeShadowSetup.ShadowShaderName));
            Assert.That(mat.GetFloat("_StencilRef"), Is.EqualTo(48f));
            Assert.That(mat.GetFloat("_StencilComp"), Is.EqualTo((float)CompareFunction.Equal), "顔が書いたステンシルと一致した場所のみ描画");
            Assert.That(mat.GetFloat("_StencilPass"), Is.EqualTo((float)StencilOp.Zero), "描画済み領域のステンシルを消して影の二重掛けを防ぐ");
            Assert.That(mat.GetFloat("_ZWrite"), Is.EqualTo(0f));
            Assert.That(mat.renderQueue, Is.EqualTo(2451), "影のqueueは選択顔マテリアルの最大queue+1");
            Assert.That(mat.GetColor("_Color"), Is.EqualTo(_component.fakeShadowColor));
            var v = mat.GetVector("_FakeShadowVector");
            Assert.That(v.x, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(v.y, Is.EqualTo(-0.1f).Within(1e-5f));
            Assert.That(v.w, Is.EqualTo(0.01f).Within(1e-5f));
        }

        [Test]
        public void Apply_HairQueueAtOrBelowShadowQueue_IsRaised_CloneShared()
        {
            SetUpComponentWithFace(faceQueue: 2450);
            var sharedHairMat = CreateLilToonMaterial("HairMat", 2450);
            var hair1 = CreateRenderer("Hair1", new[] { sharedHairMat });
            var hair2 = CreateRenderer("Hair2", new[] { sharedHairMat });

            var result = Apply(hair1, hair2);

            Assert.That(result, Is.Not.Null);
            var bumped1 = hair1.sharedMaterials[0];
            var bumped2 = hair2.sharedMaterials[0];
            Assert.That(bumped1, Is.Not.SameAs(sharedHairMat), "元マテリアルは直接変更せずクローンすること");
            Assert.That(sharedHairMat.renderQueue, Is.EqualTo(2450), "元マテリアルのqueueは不変であること");
            Assert.That(bumped1.renderQueue, Is.EqualTo(2452), "髪queueは影queue(2451)+1に引き上げ");
            Assert.That(bumped2, Is.SameAs(bumped1), "同一マテリアルのクローンは共有すること");
        }

        [Test]
        public void Apply_HairQueueAboveShadowQueue_IsUntouched()
        {
            SetUpComponentWithFace(faceQueue: 2450);
            var hairMat = CreateLilToonMaterial("HairMat", 2465);
            var hair = CreateRenderer("Hair", new[] { hairMat });

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            Assert.That(hair.sharedMaterials[0], Is.SameAs(hairMat), "影queueより上の髪マテリアルは差し替えないこと");
        }

        [Test]
        public void Apply_NullMaterialSlot_StaysNullInShadowRenderer()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460), null }, submeshCount: 2);

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            var shadowMats = result.ShadowRenderers[0].sharedMaterials;
            Assert.That(shadowMats.Length, Is.EqualTo(2));
            Assert.That(shadowMats[0], Is.Not.Null);
            Assert.That(shadowMats[1], Is.Null, "nullスロットは影レンダラーでもnullのまま（統合済みサブメッシュの二重描画防止）");
        }

        [Test]
        public void Apply_ChildTransforms_AreNotDuplicated()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });
            var child = new GameObject("NestedHairPart");
            _cleanup.Add(child);
            child.transform.SetParent(hair.transform);

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            var shadow = result.ShadowRenderers[0];
            Assert.That(shadow.transform.childCount, Is.EqualTo(0), "影レンダラーに子Transformが複製されないこと");
            Assert.That(child.transform.parent, Is.SameAs(hair.transform), "退避した子が髪に戻されること");
        }

        [Test]
        public void Apply_ExcludedRenderer_NoShadowButQueueStillRaised()
        {
            SetUpComponentWithFace(faceQueue: 2450);
            var includedHair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2465) });
            var excludedMat = CreateLilToonMaterial("ExcludedHairMat", 2450);
            var excludedHair = CreateRenderer("ExcludedHair", new[] { excludedMat });
            _component.fakeShadowExcludedRenderers.Add(excludedHair);

            var result = Apply(includedHair, excludedHair);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ShadowRenderers.Count, Is.EqualTo(1), "除外した髪には影レンダラーを作らないこと");
            Assert.That(result.ShadowRenderers[0].transform.parent, Is.SameAs(includedHair.transform));
            Assert.That(excludedHair.transform.childCount, Is.EqualTo(0), "除外髪の子に影が生成されないこと");
            Assert.That(excludedHair.sharedMaterials[0].renderQueue, Is.EqualTo(2452),
                "queue引き上げは除外髪にも適用されること（他の髪の影が上に乗るのを防ぐ）");
        }

        [Test]
        public void Apply_ExtraComponentsOnHair_NotCopiedToShadowRenderer()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });
            hair.gameObject.AddComponent<BoxCollider>();

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ShadowRenderers[0].GetComponent<BoxCollider>(), Is.Null,
                "髪GameObject上の他コンポーネント（PhysBone等）を影レンダラーに持ち込まないこと");
        }

        [Test]
        public void Apply_ShadowRenderer_ShadowCastingOff()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });

            var result = Apply(hair);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ShadowRenderers[0].shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
        }

        [Test]
        public void Apply_InvalidConfig_ReturnsNullWithoutThrow()
        {
            SetUpComponentWithFace();
            var hair = CreateRenderer("Hair", new[] { CreateLilToonMaterial("HairMat", 2460) });

            _component.fakeShadowFaceRenderer = null;
            Assert.That(Apply(hair), Is.Null, "顔Renderer未設定なら null（例外を出さない）");

            _component.fakeShadowFaceRenderer = _faceRenderer;
            _component.fakeShadowFaceMaterialIndexes = new List<int> { 5 };
            Assert.That(Apply(hair), Is.Null, "顔マテリアルindexが範囲外なら null");

            _component.fakeShadowFaceMaterialIndexes = new List<int>();
            Assert.That(Apply(hair), Is.Null, "顔マテリアル未選択なら null");
        }

        [Test]
        public void AppendShadowSubmesh_AddsIndexConcatSubmesh_KeepsVertices()
        {
            var mesh = CreateTriMesh(submeshCount: 2);
            mesh.SetTriangles(new[] { 0, 2, 1 }, 1, false);

            var appended = FakeShadowSetup.AppendShadowSubmesh(mesh, new[] { 0, 1 });
            _cleanup.Add(appended);

            Assert.That(appended.vertexCount, Is.EqualTo(mesh.vertexCount), "頂点は複製しないこと");
            Assert.That(appended.subMeshCount, Is.EqualTo(3), "末尾にサブメッシュが1つ追加されること");
            Assert.That(appended.GetTriangles(2), Is.EqualTo(new[] { 0, 1, 2, 0, 2, 1 }),
                "追加サブメッシュは対象サブメッシュのインデックス連結であること");
            Assert.That(appended.GetTriangles(0), Is.EqualTo(mesh.GetTriangles(0)), "既存サブメッシュは不変であること");
        }
    }
}
