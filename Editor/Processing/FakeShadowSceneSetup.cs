#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// FakeShadowをシーンのアバターへ直接セットアップするユーティリティ。
    /// NDMFを使わずに完結させたい場合（Prefab出力/PNG出力後の最終工程など）に使う。
    ///
    /// - 顔: 既存のステンシル書き込み（まつ毛の透け表現など）があればそのRefを尊重して顔を触らず、
    ///   なければステンシル書き込みクローンをアセット保存して差し替え（元マテリアルは不変）
    /// - 影: 各髪Rendererの子に影レンダラーを生成（影マテリアルはアセット保存・全影で共有）
    /// - 髪: renderQueueが影queue以下のマテリアルは直接引き上げ（共有アセットにも効く点に注意）
    ///
    /// 全操作を1つのUndoグループにまとめる（Ctrl+Zで一括で戻せる。生成済みアセットファイルは残る）。
    /// 適用後は enableFakeShadow を OFF にして NDMF/プレビューとの二重適用を防ぐ。
    /// 再実行は冪等: 既存の "(CHM FakeShadow)" 子は作り直し、アセットとクローンは同じものを更新する
    /// </summary>
    public static class FakeShadowSceneSetup
    {
        /// <summary>生成マテリアルの保存先（固定）</summary>
        public const string DefaultGeneratedFolder = "Assets/ChimeraHairMaster/Generated/FakeShadow";

        private const string UndoName = "CHM FakeShadowセットアップ";

        /// <summary>Apply の結果</summary>
        public class Result
        {
            /// <summary>生成した影レンダラー（各髪Rendererの子）</summary>
            public List<SkinnedMeshRenderer> ShadowRenderers = new List<SkinnedMeshRenderer>();

            /// <summary>アセット保存済みの影マテリアル（全影レンダラーで共有）</summary>
            public Material? ShadowMaterial;

            /// <summary>実際に使用したステンシル値</summary>
            public int UsedStencilRef;

            /// <summary>顔マテリアルの既存ステンシル書き込みのRefを再利用したかどうか</summary>
            public bool ReusedExistingStencilRef;
        }

        /// <summary>
        /// FakeShadowをシーンへ直接セットアップする。
        /// 設定不備・シェーダー欠落時は警告を出して null を返す（例外は投げない）
        /// </summary>
        /// <param name="component">対象コンポーネント</param>
        /// <param name="generatedFolder">生成マテリアルの保存先（省略時は DefaultGeneratedFolder。テスト用）</param>
        public static Result? Apply(ChimeraHairMaster component, string? generatedFolder = null)
        {
            if (!FakeShadowSetup.Validate(component, out var reason))
            {
                Debug.LogWarning($"[ChimeraHairMaster] FakeShadowセットアップを中止: {reason}");
                return null;
            }

            var hairRenderers = CollectHairRenderers(component);
            if (hairRenderers.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] FakeShadowセットアップを中止: 対象の髪Rendererがありません");
                return null;
            }

            string folder = string.IsNullOrEmpty(generatedFolder) ? DefaultGeneratedFolder : generatedFolder!;
            string avatarName = SanitizeFileName(GetAvatarRootName(component));
            bool reusedRef = HasExistingStencilWriter(component);
            int stencilRef = FakeShadowSetup.ResolveStencilRef(component);
            int shadowQueue = FakeShadowSetup.ComputeShadowRenderQueue(component);

            var configuredShadow = FakeShadowSetup.CreateShadowMaterial(component, shadowQueue, stencilRef);
            if (configuredShadow == null)
            {
                Debug.LogWarning($"[ChimeraHairMaster] FakeShadowセットアップを中止: シェーダー '{FakeShadowSetup.ShadowShaderName}' が見つかりません（lilToonを確認してください）");
                return null;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            int undoGroup = Undo.GetCurrentGroup();

            EnsureFolder(folder);

            // 影マテリアル（再実行時は同じアセットを更新し、既存の影レンダラーの参照を保つ）
            var shadowMaterial = SaveOrUpdateMaterialAsset(configuredShadow, $"{folder}/{avatarName}_FakeShadow.mat");

            // 顔マテリアル
            ApplyFaceMaterials(component, stencilRef, folder);

            // 髪queueの引き上げ（直接調整。同じマテリアルを使う他アバターにも影響する点に注意）
            var processedHairMaterials = new HashSet<Material>();
            bool bumpedAnyHairMaterial = false;
            foreach (var renderer in hairRenderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material == shadowMaterial) continue;
                    if (!processedHairMaterials.Add(material)) continue;
                    if (material.renderQueue > shadowQueue) continue;

                    Undo.RecordObject(material, UndoName);
                    material.renderQueue = shadowQueue + 1;
                    EditorUtility.SetDirty(material);
                    bumpedAnyHairMaterial = true;
                }
            }
            FakeShadowSetup.WarnIfQueueBumpEntersTransparentRange(shadowQueue, bumpedAnyHairMaterial);

            // 影レンダラー（再実行時は既存の "(CHM FakeShadow)" 子を作り直す）
            var result = new Result
            {
                ShadowMaterial = shadowMaterial,
                UsedStencilRef = stencilRef,
                ReusedExistingStencilRef = reusedRef,
            };
            foreach (var renderer in hairRenderers)
            {
                // 既存の影は除外髪も含めて消す（除外に変更して再実行した場合に古い影が残らないように）
                RemoveExistingShadowChildren(renderer);
                if (component.IsFakeShadowExcluded(renderer)) continue;

                var shadowRenderer = FakeShadowSetup.CreateShadowRenderer(renderer, shadowMaterial);
                Undo.RegisterCreatedObjectUndo(shadowRenderer.gameObject, UndoName);
                result.ShadowRenderers.Add(shadowRenderer);
            }

            if (result.ShadowRenderers.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] FakeShadow: すべての髪が除外されているため影レンダラーを生成しませんでした");
            }

            // 実物の影レンダラーができたので、NDMF/プレビューのFakeShadowは止める（二重適用防止）
            Undo.RecordObject(component, UndoName);
            component.enableFakeShadow = false;
            EditorUtility.SetDirty(component);

            if (component.isEnabled && component.enableMeshMerge)
            {
                Debug.LogWarning(
                    "[ChimeraHairMaster] FakeShadowセットアップ: メッシュ統合が有効なままNDMFビルドすると、" +
                    "統合で元のRendererと共に影レンダラーも削除されます。このアバターをビルドする場合は" +
                    "統合をOFFにするか、先に色合わせ適用/PNG出力で焼き込んでください");
            }

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[ChimeraHairMaster] FakeShadowセットアップ完了: 影レンダラー {result.ShadowRenderers.Count}個, " +
                $"ステンシル値 {stencilRef}{(reusedRef ? "（顔の既存設定を再利用）" : string.Empty)}");
            return result;
        }

        /// <summary>
        /// 影マテリアルを既定フォルダにアセットとして生成/更新する（Prefab出力との共用）。
        /// シーンセットアップと同じアセットパスを使うため、両経路で設定が一貫する。
        /// シェーダーが見つからない場合は null
        /// </summary>
        internal static Material? CreateOrUpdateShadowMaterialAsset(ChimeraHairMaster component, int shadowQueue, int stencilRef)
        {
            var configured = FakeShadowSetup.CreateShadowMaterial(component, shadowQueue, stencilRef);
            if (configured == null) return null;

            EnsureFolder(DefaultGeneratedFolder);
            string avatarName = SanitizeFileName(GetAvatarRootName(component));
            return SaveOrUpdateMaterialAsset(configured, $"{DefaultGeneratedFolder}/{avatarName}_FakeShadow.mat");
        }

        /// <summary>
        /// シーン上のアバターの顔マテリアルにステンシル書き込みを整える（Prefab出力との共用）。
        /// クローンは既定フォルダに保存。Undo対応
        /// </summary>
        internal static void ApplySceneFaceMaterials(ChimeraHairMaster component, int stencilRef)
        {
            EnsureFolder(DefaultGeneratedFolder);
            ApplyFaceMaterials(component, stencilRef, DefaultGeneratedFolder);
        }

        /// <summary>対象の髪Renderer（有効なもの）を収集する</summary>
        private static List<SkinnedMeshRenderer> CollectHairRenderers(ChimeraHairMaster component)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (component.targetRenderers == null) return result;

            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                if (!renderer.enabled || !renderer.gameObject.activeSelf) continue;
                result.Add(renderer);
            }

            return result;
        }

        /// <summary>
        /// 顔マテリアルにステンシル書き込みを整える。
        /// - 自前クローン（再実行）: 設定だけ更新
        /// - 既存の書き込みあり: 触らない（Refが異なる場合は警告のみ）
        /// - 書き込みなし: クローンを元マテリアルと同じフォルダにアセット保存して差し替え
        ///   （元がアセットでない場合のみ fallbackFolder に保存）
        /// </summary>
        private static void ApplyFaceMaterials(ChimeraHairMaster component, int stencilRef, string fallbackFolder)
        {
            var faceRenderer = component.fakeShadowFaceRenderer;
            var materials = faceRenderer.sharedMaterials;
            var cloneCache = new Dictionary<Material, Material>();
            bool changed = false;

            foreach (var index in component.fakeShadowFaceMaterialIndexes)
            {
                var source = materials[index];
                if (source == null) continue;
                if (!source.HasProperty("_StencilRef"))
                {
                    Debug.LogWarning($"[ChimeraHairMaster] FakeShadow: 顔マテリアル '{source.name}' にステンシル設定がないためスキップします");
                    continue;
                }

                // 再実行: 自前クローンは作り直さず設定だけ更新（設定値の変更を反映）
                if (source.name.EndsWith(FakeShadowSetup.FaceCloneSuffix))
                {
                    Undo.RecordObject(source, UndoName);
                    FakeShadowSetup.ApplyStencilWrite(source, stencilRef);
                    EditorUtility.SetDirty(source);
                    continue;
                }

                // 既存のステンシル書き込みは尊重（顔を触らない）
                if (FakeShadowSetup.IsStencilWriter(source))
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

                if (!cloneCache.TryGetValue(source, out var clone))
                {
                    var configured = FakeShadowSetup.CreateStencilWriterFaceMaterial(source, stencilRef, warnExistingStencilWrite: false);
                    clone = SaveOrUpdateMaterialAsset(configured, GetFaceClonePath(source, fallbackFolder));
                    cloneCache[source] = clone;
                }

                materials[index] = clone;
                changed = true;
            }

            if (changed)
            {
                Undo.RecordObject(faceRenderer, UndoName);
                faceRenderer.sharedMaterials = materials;
                EditorUtility.SetDirty(faceRenderer);
            }
        }

        /// <summary>
        /// 顔クローンの保存パスを決める。元マテリアルがアセットなら同じフォルダに
        /// 「{元名}_CHMFakeShadowFace.mat」で保存し、そうでなければ fallbackFolder に保存する
        /// </summary>
        private static string GetFaceClonePath(Material source, string fallbackFolder)
        {
            string fileName = $"{SanitizeFileName(source.name)}{FakeShadowSetup.FaceCloneSuffix}.mat";

            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (!string.IsNullOrEmpty(sourcePath))
            {
                string directory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    return $"{directory.Replace('\\', '/')}/{fileName}";
                }
            }

            return $"{fallbackFolder}/{fileName}";
        }

        /// <summary>選択した顔マテリアルに（自前クローン以外の）既存ステンシル書き込みがあるか</summary>
        private static bool HasExistingStencilWriter(ChimeraHairMaster component)
        {
            var materials = component.fakeShadowFaceRenderer.sharedMaterials;
            foreach (var index in component.fakeShadowFaceMaterialIndexes)
            {
                var material = materials[index];
                if (material == null) continue;
                if (material.name.EndsWith(FakeShadowSetup.FaceCloneSuffix)) continue;
                if (FakeShadowSetup.IsStencilWriter(material)) return true;
            }

            return false;
        }

        /// <summary>再実行時の重複を防ぐため、既存の "(CHM FakeShadow)" 子レンダラーを削除する</summary>
        private static void RemoveExistingShadowChildren(SkinnedMeshRenderer renderer)
        {
            string shadowName = FakeShadowSetup.GetShadowRendererName(renderer);
            var toRemove = new List<GameObject>();
            foreach (Transform child in renderer.transform)
            {
                if (child.name == shadowName && child.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    toRemove.Add(child.gameObject);
                }
            }

            foreach (var go in toRemove)
            {
                Undo.DestroyObjectImmediate(go);
            }
        }

        /// <summary>
        /// マテリアルをアセットとして保存する。既に同パスのアセットがあれば
        /// 新規作成せずプロパティを移して更新する（既存参照を保つ）
        /// </summary>
        private static Material SaveOrUpdateMaterialAsset(Material configured, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                Undo.RecordObject(existing, UndoName);
                existing.shader = configured.shader;
                existing.CopyPropertiesFromMaterial(configured);
                existing.renderQueue = configured.renderQueue;
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(configured);
                return existing;
            }

            configured.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(configured, path);
            return configured;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        private static string GetAvatarRootName(ChimeraHairMaster component)
        {
            var descriptor = component.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var root = descriptor != null ? descriptor.gameObject : component.transform.root.gameObject;
            return root.name;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Avatar";
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name;
        }
    }
}
