#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// FakeShadowセットアップの中核ロジック。
    /// ステンシルを使った3点セットで、髪が顔に落とす影を非破壊に構成する:
    /// 1. 顔マテリアル（選択index）をステンシル書き込み（Ref/Always/Replace）のクローンに差し替え
    /// 2. 髪Rendererを複製した影レンダラーに専用シェーダーのマテリアル
    ///    （Ref/Equal/Zero=二重描画防止、乗算ブレンド、ZWrite off、queue=顔+1）を割り当て
    /// 3. 髪本体のqueueが影以下ならクローンで影queue+1に引き上げ（影の上に髪を描く）
    ///
    /// NDMFビルド（FakeShadowPass）とプレビュー（FakeShadowPreview）の両方から使う。
    /// マテリアルは transient に生成する（ビルドではNDMFが、プレビューではノードが破棄を管理）
    /// </summary>
    public static class FakeShadowSetup
    {
        /// <summary>影の描画に使うシェーダー名（lilToon同梱のオプションシェーダー）</summary>
        public const string ShadowShaderName = "_lil/[Optional] lilToonFakeShadow";

        /// <summary>顔のステンシル書き込みクローンの名前サフィックス（自前クローンの識別にも使う）</summary>
        public const string FaceCloneSuffix = "_CHMFakeShadowFace";

        /// <summary>
        /// 影レンダラーのGameObject名サフィックス。
        /// CHM生成物の識別マーカーを兼ねる（再実行時の作り直し・Prefab出力時の検出/除去が
        /// この名前で判定される）ため、ユーザー命名と衝突しにくい CHM 入りにしている
        /// </summary>
        public const string ShadowRendererSuffix = " (CHM FakeShadow)";

        /// <summary>髪Rendererに対する影レンダラーのGameObject名</summary>
        public static string GetShadowRendererName(SkinnedMeshRenderer source)
        {
            return source.gameObject.name + ShadowRendererSuffix;
        }

        /// <summary>Apply の生成物（呼び出し側が寿命を管理する）</summary>
        public class Result
        {
            /// <summary>生成した影レンダラー（各髪Rendererの子）</summary>
            public List<SkinnedMeshRenderer> ShadowRenderers = new List<SkinnedMeshRenderer>();

            /// <summary>生成した全マテリアル（影・顔クローン・queue引き上げクローン）</summary>
            public List<Material> GeneratedMaterials = new List<Material>();

            /// <summary>影マテリアル（全影レンダラーで共有）</summary>
            public Material? ShadowMaterial;

            /// <summary>実際に使用したステンシル値</summary>
            public int UsedStencilRef;

            /// <summary>顔マテリアルの 元→ステンシル書き込みクローン 対応表（アニメーション参照の書き換えに使う）</summary>
            public Dictionary<Material, Material> FaceCloneBySource = new Dictionary<Material, Material>();
        }

        /// <summary>
        /// 設定が実行可能かを検証する。false の場合 reason に理由（ユーザー向け・日本語はログ側でTr不要の内部文言）
        /// </summary>
        public static bool Validate(ChimeraHairMaster component, out string reason)
        {
            reason = string.Empty;

            if (component == null)
            {
                reason = "コンポーネントがありません";
                return false;
            }

            var face = component.fakeShadowFaceRenderer;
            if (face == null)
            {
                reason = "顔のRendererが設定されていません";
                return false;
            }

            if (face.sharedMesh == null)
            {
                reason = "顔のRendererにメッシュが設定されていません";
                return false;
            }

            var faceMaterials = face.sharedMaterials;
            if (faceMaterials == null || faceMaterials.Length == 0)
            {
                reason = "顔のRendererにマテリアルがありません";
                return false;
            }

            var indexes = component.fakeShadowFaceMaterialIndexes;
            if (indexes == null || indexes.Count == 0)
            {
                reason = "影を受ける顔マテリアルが選択されていません";
                return false;
            }

            bool anyStencilCapable = false;
            foreach (var index in indexes)
            {
                if (index < 0 || index >= faceMaterials.Length)
                {
                    reason = $"顔マテリアルの選択（index {index}）が範囲外です";
                    return false;
                }

                var material = faceMaterials[index];
                if (material != null && material.HasProperty("_StencilRef"))
                {
                    anyStencilCapable = true;
                }
            }

            if (!anyStencilCapable)
            {
                reason = "選択した顔マテリアルにステンシル設定（_StencilRef）がありません（lilToon系シェーダーのみ対応）";
                return false;
            }

            return true;
        }

        /// <summary>マテリアルがステンシル書き込み（Pass=Replace）を行う設定かどうか</summary>
        public static bool IsStencilWriter(Material material)
        {
            return material != null
                   && material.HasProperty("_StencilRef")
                   && material.HasProperty("_StencilPass")
                   && Mathf.Approximately(material.GetFloat("_StencilPass"), (float)StencilOp.Replace);
        }

        /// <summary>
        /// 使用するステンシル値を決める。選択した顔マテリアルに既存のステンシル書き込み
        /// （まつ毛の透け表現など）があればその値を尊重して顔を触らずに済ませ、
        /// なければコンポーネントの設定値を使う。
        /// 自前の顔クローン（FaceCloneSuffix）は「既存の書き込み」とは見なさない
        /// （再セットアップ時にコンポーネント設定値の変更を反映できるようにするため）
        /// </summary>
        public static int ResolveStencilRef(ChimeraHairMaster component)
        {
            if (component == null) return 1;
            if (!Validate(component, out _)) return component.fakeShadowStencilRef;

            var faceMaterials = component.fakeShadowFaceRenderer.sharedMaterials;
            foreach (var index in component.fakeShadowFaceMaterialIndexes)
            {
                var material = faceMaterials[index];
                if (material == null) continue;
                if (material.name.EndsWith(FaceCloneSuffix)) continue;
                if (IsStencilWriter(material))
                {
                    return Mathf.RoundToInt(material.GetFloat("_StencilRef"));
                }
            }

            return component.fakeShadowStencilRef;
        }

        /// <summary>
        /// 影マテリアルのrenderQueueを計算する（選択した顔マテリアルの最大queue+1。
        /// 顔→影の順で描画させるため）。設定が不正な場合は -1
        /// </summary>
        public static int ComputeShadowRenderQueue(ChimeraHairMaster component)
        {
            if (!Validate(component, out _)) return -1;

            var faceMaterials = component.fakeShadowFaceRenderer.sharedMaterials;
            int maxQueue = -1;
            foreach (var index in component.fakeShadowFaceMaterialIndexes)
            {
                var material = faceMaterials[index];
                if (material == null) continue;
                if (material.renderQueue > maxQueue) maxQueue = material.renderQueue;
            }

            return maxQueue < 0 ? -1 : maxQueue + 1;
        }

        /// <summary>
        /// 影マテリアルを生成する（プレビューとビルドの共通部）。
        /// シェーダーが見つからない場合は null。
        /// 乗算ブレンドはシェーダー既定値のまま（SrcBlend=DstColor, DstBlend=Zero）
        /// </summary>
        public static Material? CreateShadowMaterial(ChimeraHairMaster component, int shadowRenderQueue, int stencilRef)
        {
            var shader = Shader.Find(ShadowShaderName);
            if (shader == null) return null;

            var material = new Material(shader);
            material.name = "CHM_FakeShadow";
            material.SetFloat("_StencilRef", stencilRef);
            material.SetFloat("_StencilComp", (float)CompareFunction.Equal);
            // 描画した場所のステンシルを消す → 毛束の重なりで影が二重に濃くならない
            material.SetFloat("_StencilPass", (float)StencilOp.Zero);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_OffsetFactor", component.fakeShadowDepthBias.x);
            material.SetFloat("_OffsetUnits", component.fakeShadowDepthBias.y);
            material.SetColor("_Color", component.fakeShadowColor);
            material.SetVector("_FakeShadowVector", new Vector4(
                component.fakeShadowDirection.x,
                component.fakeShadowDirection.y,
                0f,
                component.fakeShadowOffset));
            material.renderQueue = shadowRenderQueue;
            return material;
        }

        /// <summary>
        /// 顔マテリアルのステンシル書き込みクローンを作る（Ref/Always/Replace）。
        /// renderQueueは変更しない。既にステンシル書き込みを使っている場合は上書きになるため警告する
        /// （プレビューはノード再生成のたびに呼ばれるため warnExistingStencilWrite=false で抑制する）
        /// </summary>
        public static Material CreateStencilWriterFaceMaterial(Material source, int stencilRef, bool warnExistingStencilWrite = true)
        {
            var material = new Material(source);
            material.name = source.name + FaceCloneSuffix;

            if (warnExistingStencilWrite &&
                material.HasProperty("_StencilPass") &&
                !Mathf.Approximately(material.GetFloat("_StencilPass"), (float)StencilOp.Keep))
            {
                Debug.LogWarning(
                    $"[ChimeraHairMaster] FakeShadow: 顔マテリアル '{source.name}' は既にステンシル操作を使用しています。" +
                    "FakeShadowの設定で上書きされるため、まつ毛の透け表現などが変わる場合はステンシル値を元の設定に合わせてください");
            }

            ApplyStencilWrite(material, stencilRef);
            return material;
        }

        /// <summary>
        /// マテリアルにステンシル書き込み設定（Ref/Always/Replace）を適用する。
        /// 透過系lilToonの深度プリパス（Pre系プロパティ）にも同じ設定を入れる（存在する場合のみ）
        /// </summary>
        public static void ApplyStencilWrite(Material material, int stencilRef)
        {
            SetStencilWrite(material, stencilRef, string.Empty);
            SetStencilWrite(material, stencilRef, "Pre");
        }

        private static void SetStencilWrite(Material material, int stencilRef, string prefix)
        {
            if (!material.HasProperty($"_{prefix}StencilRef")) return;
            material.SetFloat($"_{prefix}StencilRef", stencilRef);
            if (material.HasProperty($"_{prefix}StencilComp"))
                material.SetFloat($"_{prefix}StencilComp", (float)CompareFunction.Always);
            if (material.HasProperty($"_{prefix}StencilPass"))
                material.SetFloat($"_{prefix}StencilPass", (float)StencilOp.Replace);
        }

        /// <summary>
        /// renderQueueのみ引き上げたクローンを作る（髪本体を影の上に描かせるため）
        /// </summary>
        public static Material CreateQueueShiftedMaterial(Material source, int renderQueue)
        {
            var material = new Material(source);
            material.name = source.name + "_CHMQueueShifted";
            material.renderQueue = renderQueue;
            return material;
        }

        /// <summary>
        /// 髪queueの引き上げ先が透過域（2501以上）に入る場合に警告する。
        /// 透過の顔マテリアル（queue 3000等）を選択していると発生し、不透明の髪が
        /// 透過パスで描画されて他の透過オブジェクトとの前後関係が崩れる場合がある
        /// </summary>
        public static void WarnIfQueueBumpEntersTransparentRange(int shadowQueue, bool bumpedAnyHairMaterial)
        {
            if (!bumpedAnyHairMaterial || shadowQueue + 1 < 2501) return;
            Debug.LogWarning(
                $"[ChimeraHairMaster] FakeShadow: 髪マテリアルのrenderQueueを{shadowQueue + 1}（透過域）へ引き上げました。" +
                "透過の顔マテリアルを選択していると、髪と他の透過オブジェクトの描画順が崩れることがあります。" +
                "不透明の顔マテリアルのみを選択することを検討してください");
        }

        /// <summary>
        /// FakeShadowセットアップを適用する（ビルド用・NDMF非依存）。
        /// hairRenderers は最終状態の髪Renderer（統合ONなら統合Renderer＋残存、OFFなら対象Renderer）。
        /// 設定不備・シェーダー欠落時は警告を出して null を返す（例外は投げない）
        /// </summary>
        public static Result? Apply(ChimeraHairMaster component, IReadOnlyList<SkinnedMeshRenderer> hairRenderers)
        {
            if (!Validate(component, out var reason))
            {
                Debug.LogWarning($"[ChimeraHairMaster] FakeShadowをスキップ: {reason}");
                return null;
            }

            if (hairRenderers == null || hairRenderers.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] FakeShadowをスキップ: 対象の髪Rendererがありません");
                return null;
            }

            int shadowQueue = ComputeShadowRenderQueue(component);
            int stencilRef = ResolveStencilRef(component);
            var shadowMaterial = CreateShadowMaterial(component, shadowQueue, stencilRef);
            if (shadowMaterial == null)
            {
                Debug.LogWarning($"[ChimeraHairMaster] FakeShadowをスキップ: シェーダー '{ShadowShaderName}' が見つかりません（lilToonを確認してください）");
                return null;
            }

            var result = new Result { ShadowMaterial = shadowMaterial, UsedStencilRef = stencilRef };
            result.GeneratedMaterials.Add(shadowMaterial);

            // 髪本体のqueue引き上げ（同一マテリアルのクローンは共有する）
            var queueShiftedCache = new Dictionary<Material, Material>();
            foreach (var renderer in hairRenderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;

                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null || material.renderQueue > shadowQueue) continue;

                    if (!queueShiftedCache.TryGetValue(material, out var shifted))
                    {
                        shifted = CreateQueueShiftedMaterial(material, shadowQueue + 1);
                        queueShiftedCache[material] = shifted;
                        result.GeneratedMaterials.Add(shifted);
                    }

                    materials[i] = shifted;
                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;
            }
            WarnIfQueueBumpEntersTransparentRange(shadowQueue, queueShiftedCache.Count > 0);

            // 影レンダラー生成（各髪Rendererの子に必要な設定だけ持つSMRを新規作成）。
            // 除外指定された髪はスキップ（queue引き上げは他の髪の影との整合のため全髪に適用済み）
            foreach (var renderer in hairRenderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                if (component.IsFakeShadowExcluded(renderer)) continue;

                result.ShadowRenderers.Add(CreateShadowRenderer(renderer, shadowMaterial));
            }

            if (result.ShadowRenderers.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] FakeShadow: すべての髪が除外されているため影レンダラーを生成しませんでした");
            }

            // 顔マテリアルをステンシル書き込みクローンに差し替え（同一マテリアルのクローンは共有）。
            // 既にステンシル書き込み済みのマテリアルは触らない（そのRefを影側が使う）
            var faceRenderer = component.fakeShadowFaceRenderer;
            var faceMaterials = faceRenderer.sharedMaterials;
            var faceCloneCache = result.FaceCloneBySource;
            bool faceChanged = false;
            foreach (var index in component.fakeShadowFaceMaterialIndexes)
            {
                var source = faceMaterials[index];
                if (source == null) continue;
                if (!source.HasProperty("_StencilRef"))
                {
                    Debug.LogWarning($"[ChimeraHairMaster] FakeShadow: 顔マテリアル '{source.name}' にステンシル設定がないためスキップします");
                    continue;
                }
                if (IsStencilWriter(source))
                {
                    int existingRef = Mathf.RoundToInt(source.GetFloat("_StencilRef"));
                    if (existingRef != stencilRef)
                    {
                        Debug.LogWarning(
                            $"[ChimeraHairMaster] FakeShadow: 顔マテリアル '{source.name}' は別のステンシル値({existingRef})を書き込んでいます。" +
                            $"影はステンシル値{stencilRef}の領域にのみ表示されます");
                    }
                    continue;
                }

                if (!faceCloneCache.TryGetValue(source, out var clone))
                {
                    clone = CreateStencilWriterFaceMaterial(source, stencilRef);
                    faceCloneCache[source] = clone;
                    result.GeneratedMaterials.Add(clone);
                }

                faceMaterials[index] = clone;
                faceChanged = true;
            }
            if (faceChanged) faceRenderer.sharedMaterials = faceMaterials;

            return result;
        }

        /// <summary>
        /// 髪Rendererの子として影レンダラーを生成する（"(CHM FakeShadow)"名・影マテリアル割り当て・
        /// シャドウキャストOFF・MA BlendshapeSync付与）。
        /// NDMFビルド・シーン直接セットアップ・Prefab出力の共通部。
        /// GameObjectごと複製すると髪側に付いていたPhysBone・Constraint等まで持ち込まれて
        /// 二重動作するため、必要な描画設定だけを持つ新規GameObjectを作る。
        /// setupBlendShapeSync=false はPrefab出力用（Prefab配置後にMAのパス参照が解決できないため付けない）
        /// </summary>
        public static SkinnedMeshRenderer CreateShadowRenderer(SkinnedMeshRenderer renderer, Material shadowMaterial, bool setupBlendShapeSync = true)
        {
            var shadowObject = new GameObject(GetShadowRendererName(renderer));
            shadowObject.layer = renderer.gameObject.layer;
            shadowObject.transform.SetParent(renderer.transform, false);

            var shadowRenderer = shadowObject.AddComponent<SkinnedMeshRenderer>();
            var sourceMesh = renderer.sharedMesh;
            shadowRenderer.sharedMesh = sourceMesh;
            shadowRenderer.bones = renderer.bones;
            shadowRenderer.rootBone = renderer.rootBone;
            shadowRenderer.localBounds = renderer.localBounds;
            shadowRenderer.probeAnchor = renderer.probeAnchor;
            shadowRenderer.quality = renderer.quality;
            shadowRenderer.updateWhenOffscreen = renderer.updateWhenOffscreen;
            shadowRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // 現在のシェイプキー値を引き継ぐ（アニメ追従は MA BlendshapeSync が担う）
            if (sourceMesh != null)
            {
                for (int i = 0; i < sourceMesh.blendShapeCount; i++)
                {
                    shadowRenderer.SetBlendShapeWeight(i, renderer.GetBlendShapeWeight(i));
                }
            }

            // nullスロットはnullのまま（統合済みサブメッシュ等の二重描画防止）
            var sourceMaterials = renderer.sharedMaterials;
            var shadowMaterials = new Material[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                shadowMaterials[i] = sourceMaterials[i] != null ? shadowMaterial : null!;
            }
            shadowRenderer.sharedMaterials = shadowMaterials;

            if (setupBlendShapeSync)
            {
                SetupBlendShapeSync(renderer, shadowRenderer);
            }
            return shadowRenderer;
        }

        /// <summary>
        /// 影レンダラーにMA Blendshape Syncを付与する（ModularAvatar検出時のみ）。
        /// シェイプキーのアニメーションが影にも追従するようにする
        /// </summary>
        private static void SetupBlendShapeSync(SkinnedMeshRenderer source, SkinnedMeshRenderer shadowRenderer)
        {
#if CHM_MODULAR_AVATAR
            var mesh = source.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) return;

            var sync = shadowRenderer.gameObject.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync>();
            if (sync == null)
            {
                sync = shadowRenderer.gameObject.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync>();
            }
            sync.Bindings ??= new List<nadena.dev.modular_avatar.core.BlendshapeBinding>();

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var blendShapeName = mesh.GetBlendShapeName(i);
                var reference = new nadena.dev.modular_avatar.core.AvatarObjectReference();
                reference.Set(source.gameObject);
                sync.Bindings.Add(new nadena.dev.modular_avatar.core.BlendshapeBinding
                {
                    Blendshape = blendShapeName,
                    LocalBlendshape = blendShapeName,
                    ReferenceMesh = reference
                });
            }
#endif
        }

        /// <summary>
        /// プレビュー用: 対象サブメッシュのインデックスを連結したサブメッシュを末尾に追記した
        /// メッシュのクローンを作る。頂点は複製しないためスキニング・BlendShapeはそのまま追従する。
        /// 追記したサブメッシュに影マテリアルを割り当てることで、Renderer追加なしに影を描画できる
        /// </summary>
        public static Mesh AppendShadowSubmesh(Mesh source, IReadOnlyList<int> submeshIndices)
        {
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_CHMFakeShadow";

            var indices = new List<int>();
            foreach (var submeshIndex in submeshIndices)
            {
                if (submeshIndex < 0 || submeshIndex >= source.subMeshCount) continue;
                indices.AddRange(source.GetTriangles(submeshIndex));
            }

            int appendedIndex = mesh.subMeshCount;
            mesh.subMeshCount = appendedIndex + 1;
            mesh.SetTriangles(indices.ToArray(), appendedIndex, false);
            return mesh;
        }
    }
}
