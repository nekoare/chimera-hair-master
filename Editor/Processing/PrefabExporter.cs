#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// CHM の対象 Renderer 群を、色変換適用済みの単独 Prefab として書き出すユーティリティ。
    /// 元マテリアル / 元テクスチャ / 元アバターには一切手を加えない（純粋に非破壊）。
    /// </summary>
    public static class PrefabExporter
    {
        private const string TextureSuffix = "_CHM";
        private const string MaterialSuffix = "_CHM";
        private const string DeformedMeshSuffix = "_Deformed";

        /// <summary>
        /// CHM コンポーネントの設定に従って Prefab を出力する。
        /// </summary>
        /// <param name="component">対象 CHM コンポーネント</param>
        /// <param name="applyDeformation">true なら rendererDeformations を適用した変形済みメッシュを Prefab 内 Renderer に設定</param>
        /// <param name="includeFakeShadow">true ならFakeShadowの影レンダラーを Prefab に同梱し、顔のステンシル設定をシーン上のアバターに適用する（元アバター非破壊の例外・呼び出し側で確認済みであること）</param>
        /// <returns>生成された Prefab アセット、失敗時 null</returns>
        public static GameObject? Export(ChimeraHairMaster component, bool applyDeformation, bool includeFakeShadow = false)
        {
            if (!Validate(component, out var avatarRoot)) return null;

            string? savePath = ChooseSavePath(avatarRoot!);
            if (string.IsNullOrEmpty(savePath)) return null;

            // 共有マテリアル × パーツ別設定の競合を警告（先勝ちで取りこぼす旨を通知）
            if (component.enableColorTransform)
            {
                ColorApplier.WarnSharedMaterialConflicts(component);
            }

            // 色変換テクスチャ生成（色変換オフ時は空 dict）
            var processedTextures = component.enableColorTransform
                ? GenerateColorTransformedTextures(component)
                : new Dictionary<int, Texture2D>();

            // clone マテリアル生成（色変換オフ かつ 統一オフの場合はスキップ → 元マテリアル参照のまま）
            Dictionary<int, Material> clonedMaterials;
            if (component.enableColorTransform || component.unifyMaterialSettings)
            {
                Material? sourceMaterial = component.previewMaterial != null ? component.previewMaterial : component.baseMaterial;
                clonedMaterials = CreateClonedMaterials(component, sourceMaterial!, processedTextures, component.unifyMaterialSettings);
            }
            else
            {
                clonedMaterials = new Dictionary<int, Material>();
            }

            // アバター root を Instantiate
            var tempRoot = Object.Instantiate(avatarRoot!);
            tempRoot.name = avatarRoot!.name;
            // Prefab ルートにアバターのワールド姿勢（回転・スケール）が焼き付かないよう正規化する。
            // 子のローカル値は不変のため、アバター直下にローカル原点で置けば元の髪と同じ位置になる
            tempRoot.transform.localPosition = Vector3.zero;
            tempRoot.transform.localRotation = Quaternion.identity;
            tempRoot.transform.localScale = Vector3.one;

            try
            {
                // 一時 root 内の対応 Renderer を取得
                var tempRenderers = GetCorrespondingRenderers(avatarRoot, tempRoot, component.targetRenderers);

                // メッシュ変形を適用
                if (applyDeformation)
                {
                    ApplyDeformations(component, tempRenderers);
                }

                // sharedMaterials を clone に差替（IsSubmeshIncluded=false は元マテのまま）
                ReplaceMaterials(component, tempRenderers, clonedMaterials);

                // 不要 GameObject を再帰削除
                var validTempRenderers = tempRenderers.OfType<SkinnedMeshRenderer>().ToList();
                HierarchyDependencyResolver.CleanUp(tempRoot, validTempRenderers);

                // FakeShadowを Prefab に同梱（影レンダラーのみ。顔はシーン側で後述）。
                // 除外判定は元Rendererで行うため、targetRenderers と index 並行の tempRenderers を渡す
                int? fakeShadowStencilRef = null;
                if (includeFakeShadow)
                {
                    fakeShadowStencilRef = SetupFakeShadowForExport(component, tempRenderers);
                }

                // ModularAvatar が入っていれば、各 Armature root に MA Merge Armature を自動付与
                AddMergeArmatureComponents(validTempRenderers, avatarRoot!);

                // Prefab 化
                var prefab = PrefabUtility.SaveAsPrefabAsset(tempRoot, savePath!);
                if (prefab == null)
                {
                    Debug.LogError("[CHM] Prefab 保存に失敗しました");
                    return null;
                }

                // FakeShadowの顔側セットアップ（Prefab には顔が入らないため、シーン上のアバターの
                // 顔マテリアルをステンシル書き込みクローンに差し替える。Undo可）
                if (fakeShadowStencilRef.HasValue)
                {
                    FakeShadowSceneSetup.ApplySceneFaceMaterials(component, fakeShadowStencilRef.Value);
                    Debug.Log($"[CHM] FakeShadowを同梱しました（ステンシル値 {fakeShadowStencilRef.Value}・顔設定はシーン側に適用）");
                }

                // CHM コンポーネント無効化（既存 Apply と同様）
                Undo.RegisterCompleteObjectUndo(component, "CHM Prefab 出力");
                component.isEnabled = false;
                EditorUtility.SetDirty(component);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                EditorGUIUtility.PingObject(prefab);
                EditorUtility.FocusProjectWindow();

                // アバターがシーン上にあれば、生成 Prefab Instance をアバター直下に自動配置する
                if (avatarRoot.scene.IsValid())
                {
                    var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (instance != null)
                    {
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, avatarRoot.scene);
                        instance.transform.SetParent(avatarRoot.transform, false);
                        // Prefab 内の子はアバターローカル値を保持しているため、
                        // ルートをローカル原点に合わせれば元の髪と同じ位置に一致する
                        instance.transform.localPosition = Vector3.zero;
                        instance.transform.localRotation = Quaternion.identity;
                        instance.transform.localScale = Vector3.one;
                        Undo.RegisterCreatedObjectUndo(instance, "CHM Prefab を配置");
                        EditorGUIUtility.PingObject(instance);
                        Selection.activeGameObject = instance;
                    }
                }
                else
                {
                    Selection.activeObject = prefab;
                }

                Debug.Log($"[CHM] Prefab 出力完了: {savePath} ({clonedMaterials.Count} 個のマテリアル, {processedTextures.Count} 個のテクスチャ)");
                return prefab;
            }
            finally
            {
                if (tempRoot != null) Object.DestroyImmediate(tempRoot);
            }
        }

        // ========== FakeShadowの同梱 ==========

        /// <summary>
        /// エクスポート用一時RendererにFakeShadowの影レンダラーを追加する。
        /// 影マテリアルはシーンセットアップと共通の固定フォルダにアセット保存される。
        /// tempRenderers は component.targetRenderers と index 並行であること（除外判定を元Rendererで行う）。
        /// 使用したステンシル値を返す（設定不備・シェーダー欠落時は警告してnull＝同梱スキップ）
        /// </summary>
        private static int? SetupFakeShadowForExport(ChimeraHairMaster component, List<SkinnedMeshRenderer?> tempRenderers)
        {
            if (!FakeShadowSetup.Validate(component, out var reason))
            {
                Debug.LogWarning($"[CHM] FakeShadowの同梱をスキップ: {reason}");
                return null;
            }

            int stencilRef = FakeShadowSetup.ResolveStencilRef(component);
            int shadowQueue = FakeShadowSetup.ComputeShadowRenderQueue(component);
            var shadowMaterial = FakeShadowSceneSetup.CreateOrUpdateShadowMaterialAsset(component, shadowQueue, stencilRef);
            if (shadowMaterial == null)
            {
                Debug.LogWarning($"[CHM] FakeShadowの同梱をスキップ: シェーダー '{FakeShadowSetup.ShadowShaderName}' が見つかりません（lilToonを確認してください）");
                return null;
            }

            var processedMaterials = new HashSet<Material>();
            bool bumpedAnyHairMaterial = false;
            int createdCount = 0;
            for (int i = 0; i < tempRenderers.Count; i++)
            {
                var renderer = tempRenderers[i];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // 元アバターに既存の "(CHM FakeShadow)" 子があるとアバター複製に含まれているため作り直す
                RemoveShadowChildren(renderer);

                // 髪本体を影の上に描くための queue 引き上げ（一時Rendererが参照する
                // マテリアルを直接調整。エクスポート用クローンならPrefab側のみに効く）。
                // 除外髪にも適用する（他の髪の影が除外髪の上に乗るのを防ぐため）
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material == shadowMaterial) continue;
                    if (!processedMaterials.Add(material)) continue;
                    if (material.renderQueue > shadowQueue) continue;
                    material.renderQueue = shadowQueue + 1;
                    EditorUtility.SetDirty(material);
                    bumpedAnyHairMaterial = true;
                }

                // 除外指定された髪には影レンダラーを作らない（判定は元Rendererで行う）
                var originalRenderer = i < component.targetRenderers.Count ? component.targetRenderers[i] : null;
                if (component.IsFakeShadowExcluded(originalRenderer)) continue;

                // MA BlendshapeSync は付けない（Prefab配置後にパス参照が解決できないため）
                FakeShadowSetup.CreateShadowRenderer(renderer, shadowMaterial, setupBlendShapeSync: false);
                createdCount++;
            }
            FakeShadowSetup.WarnIfQueueBumpEntersTransparentRange(shadowQueue, bumpedAnyHairMaterial);

            if (createdCount == 0)
            {
                Debug.LogWarning("[CHM] FakeShadow: すべての髪が除外されているため影レンダラーを同梱しませんでした");
            }

            return stencilRef;
        }

        /// <summary>既存の "(CHM FakeShadow)" 子レンダラーを削除する（一時オブジェクト用・Undo不要）</summary>
        private static void RemoveShadowChildren(SkinnedMeshRenderer renderer)
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
                Object.DestroyImmediate(go);
            }
        }

        // ========== バリデーション ==========

        private static bool Validate(ChimeraHairMaster component, out GameObject? avatarRoot)
        {
            avatarRoot = null;
            if (component == null) return false;

            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
            {
                Debug.LogWarning("[CHM] 対象 Renderer が設定されていません");
                return false;
            }

            // マテリアル clone が必要な場合のみ sourceMaterial が必須
            bool needsMaterialClone = component.enableColorTransform || component.unifyMaterialSettings;
            if (needsMaterialClone && component.previewMaterial == null && component.baseMaterial == null)
            {
                Debug.LogWarning("[CHM] previewMaterial / baseMaterial が設定されていません");
                return false;
            }

            var descriptor = component.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                Debug.LogError("[CHM] 親アバター（VRCAvatarDescriptor）が見つかりません");
                EditorUtility.DisplayDialog(
                    "Prefab として出力",
                    "親アバター（VRCAvatarDescriptor）が見つかりません。\nCHM コンポーネントをアバター内に配置してください。",
                    "OK");
                return false;
            }
            avatarRoot = descriptor.gameObject;
            return true;
        }

        // ========== 保存パス選択 ==========

        private static string? ChooseSavePath(GameObject avatarRoot)
        {
            string defaultFolder = "Assets";
            string avatarPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatarRoot);
            if (!string.IsNullOrEmpty(avatarPrefabPath))
            {
                var dir = Path.GetDirectoryName(avatarPrefabPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    defaultFolder = dir.Replace("\\", "/");
                }
            }

            string defaultName = $"{avatarRoot.name}_CHM";

            string savePath = EditorUtility.SaveFilePanelInProject(
                "キメラヘアを Prefab として出力",
                defaultName,
                "prefab",
                "保存先を選択してください",
                defaultFolder);

            if (string.IsNullOrEmpty(savePath)) return null;

            // 衝突時連番化（上書き禁止）
            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(savePath);
            if (uniquePath != savePath)
            {
                Debug.Log($"[CHM] 同名ファイルが存在したため連番化しました: {savePath} → {uniquePath}");
            }
            return uniquePath;
        }

        // ========== 色変換テクスチャ生成 ==========

        private static Dictionary<int, Texture2D> GenerateColorTransformedTextures(ChimeraHairMaster component)
        {
            var processed = new Dictionary<int, Texture2D>();

            Color sourceColor = ColorApplier.DetermineSourceColor(component);
            var settings = ColorTransformSettings.FromComponent(component, sourceColor);

            var pixelCache = new MeshUVSampler.PixelCache();
            // 塗り感統一: お手本 Renderer の参照データを事前計算
            StrandPatternApplier.RefData? strandRef = StrandPatternApplier.PrepareRefData(component, settings, pixelCache);
            try
            {
                // マテリアル単位でグループ化（走査順維持）。出力は clone マテリアルごとに 1 枚（連番PNG）
                var groupOrder = new List<Material>();
                var groups = new Dictionary<Material, List<(int rendererIndex, int submeshIndex, Material material)>>();
                for (int r = 0; r < component.targetRenderers.Count; r++)
                {
                    var renderer = component.targetRenderers[r];
                    if (renderer == null) continue;
                    var materials = renderer.sharedMaterials;

                    for (int s = 0; s < materials.Length; s++)
                    {
                        var mat = materials[s];
                        if (mat == null) continue;
                        if (!component.IsSubmeshIncluded(r, s)) continue;
                        if (!mat.HasProperty("_MainTex")) continue;
                        if (mat.GetTexture("_MainTex") as Texture2D == null) continue;
                        // _MainTex の色変更が明示的に OFF なら処理しない（NDMFビルドと一致させる）
                        if (component.IsColorChangeExplicitlyDisabled("_MainTex")) continue;

                        if (!groups.TryGetValue(mat, out var members))
                        {
                            members = new List<(int rendererIndex, int submeshIndex, Material material)>();
                            groups[mat] = members;
                            groupOrder.Add(mat);
                        }
                        members.Add((r, s, mat));
                    }
                }

                int sharedCount = 0;
                foreach (var mat in groupOrder)
                {
                    var mainTex = mat.GetTexture("_MainTex") as Texture2D;
                    if (mainTex == null) continue;

                    // 領域統計 → ブラー/シャープ → 色変換 → 明度 → dilation → マスク → 塗り感統一。
                    // UV 非重複なら領域ごと処理して 1 枚に合成、それ以外は従来どおり先頭 (r, s) のみ処理。
                    // PNG出力は各段を非圧縮で通し、圧縮は再インポート時にインポーターが1回だけ行う
                    var processedTex = ColorApplier.ProcessMainTexGroup(component, groups[mat], mainTex, settings, pixelCache, strandRef,
                        ColorApplier.MaskScope.SharedMaterial, out bool shared);
                    if (processedTex == null) continue;
                    if (shared) sharedCount++;

                    var savedTexture = SaveTextureAsPngUnique(mainTex, processedTex);
                    Object.DestroyImmediate(processedTex);

                    if (savedTexture != null)
                    {
                        processed[mat.GetInstanceID()] = savedTexture;
                    }
                }

                if (sharedCount > 0)
                {
                    Debug.Log($"[CHM] Prefab出力: UV非重複で 1 枚に合成した共有マテリアル {sharedCount} 件");
                }
            }
            finally
            {
                strandRef?.Dispose();
                pixelCache.Dispose();
            }

            return processed;
        }

        /// <summary>
        /// 色変換済みテクスチャを PNG 保存（衝突時連番化）。
        /// 既存 ColorApplier.SaveTextureAsPng と同等処理だが、上書きせず GenerateUniqueAssetPath で連番化する。
        /// </summary>
        private static Texture2D? SaveTextureAsPngUnique(Texture2D originalTex, Texture2D processedTex)
        {
            string originalPath = AssetDatabase.GetAssetPath(originalTex);
            if (string.IsNullOrEmpty(originalPath))
            {
                Debug.LogWarning($"[CHM] テクスチャ '{originalTex.name}' のアセットパスが取得できません");
                return null;
            }

            string directory = Path.GetDirectoryName(originalPath)!;
            string textureName = Path.GetFileNameWithoutExtension(originalPath);

            string baseOutputPath = $"{directory}/{textureName}{TextureSuffix}.png";
            string outputPath = AssetDatabase.GenerateUniqueAssetPath(baseOutputPath);

            var uncompressed = ColorApplier.DecompressTexture(processedTex);
            byte[] pngData = uncompressed.EncodeToPNG();
            Object.DestroyImmediate(uncompressed);

            if (pngData == null)
            {
                Debug.LogError($"[CHM] テクスチャ '{textureName}' の PNG エンコードに失敗しました");
                return null;
            }

            File.WriteAllBytes(outputPath, pngData);
            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
            ColorApplier.CopyTextureImportSettings(originalPath, outputPath);

            return AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        }

        // ========== clone マテリアル生成 ==========

        private static Dictionary<int, Material> CreateClonedMaterials(
            ChimeraHairMaster component,
            Material sourceMaterial,
            Dictionary<int, Texture2D> processedTextures,
            bool unifyMaterialSettings)
        {
            var cloned = new Dictionary<int, Material>();

            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;

                for (int s = 0; s < materials.Length; s++)
                {
                    var mat = materials[s];
                    if (mat == null) continue;
                    if (!component.IsSubmeshIncluded(r, s)) continue;

                    int matId = mat.GetInstanceID();
                    if (cloned.ContainsKey(matId)) continue;

                    processedTextures.TryGetValue(matId, out var newTexture);

                    // 統一オフ かつ テクスチャ差替も無い場合は clone する意味がない（元マテリアルを参照させる）
                    if (!unifyMaterialSettings && newTexture == null) continue;

                    var clone = CreateCloneMaterial(mat, sourceMaterial, newTexture, component.unifyMatCap, unifyMaterialSettings);
                    if (clone != null)
                    {
                        cloned[matId] = clone;
                    }
                }
            }

            return cloned;
        }

        /// <summary>
        /// 元マテリアルから clone マテリアルアセットを作成する。
        /// 数値設定は sourceMaterial（previewMaterial）からコピー（シェーダー本体は元マテリアルを維持）。
        /// _MainTex は newTexture に差し替え。
        /// </summary>
        private static Material? CreateCloneMaterial(Material original, Material sourceMaterial, Texture2D? newTexture, bool unifyMatCap, bool unifyMaterialSettings)
        {
            string originalPath = AssetDatabase.GetAssetPath(original);
            if (string.IsNullOrEmpty(originalPath))
            {
                Debug.LogWarning($"[CHM] マテリアル '{original.name}' は Project アセットではないため複製をスキップします");
                return null;
            }

            string directory = Path.GetDirectoryName(originalPath)!;
            string baseName = Path.GetFileNameWithoutExtension(originalPath);
            string baseOutputPath = $"{directory}/{baseName}{MaterialSuffix}.mat";
            string outputPath = AssetDatabase.GenerateUniqueAssetPath(baseOutputPath);

            // 元マテリアルを完全コピー
            var clone = Object.Instantiate(original);
            clone.name = Path.GetFileNameWithoutExtension(outputPath);

            // 数値設定を sourceMaterial から反映（統一オン時のみ、シェーダーは維持）
            if (unifyMaterialSettings)
            {
                // 輪郭線はシェーダ切替（Outline版の有無）で表現されるため、数値コピー前にシェーダを揃える
                NDMF.TextureAtlasPass.SyncOutlineVariant(sourceMaterial, clone);
                NDMF.TextureAtlasPass.ApplyShaderSettings(
                    sourceMaterial, clone,
                    excludeOverlayAndEmission: true,
                    excludeMatCap: !unifyMatCap);

                if (unifyMatCap)
                {
                    ColorApplier.CopyMatCapTextures(sourceMaterial, clone);
                }
            }

            // _MainTex を新規生成テクスチャに差し替え
            if (newTexture != null && clone.HasProperty("_MainTex"))
            {
                clone.SetTexture("_MainTex", newTexture);
            }

            AssetDatabase.CreateAsset(clone, outputPath);
            return AssetDatabase.LoadAssetAtPath<Material>(outputPath);
        }

        // ========== 一時 GameObject 内の対応 Renderer 取得 ==========

        /// <summary>
        /// 元アバター内の各 SkinnedMeshRenderer を、Instantiate された一時 root 内の同じパスの Renderer に対応付ける。
        /// </summary>
        private static List<SkinnedMeshRenderer?> GetCorrespondingRenderers(
            GameObject srcRoot,
            GameObject destRoot,
            List<SkinnedMeshRenderer> srcRenderers)
        {
            var result = new List<SkinnedMeshRenderer?>(srcRenderers.Count);
            foreach (var src in srcRenderers)
            {
                if (src == null)
                {
                    result.Add(null);
                    continue;
                }
                string? relPath = GetRelativePath(srcRoot.transform, src.transform);
                if (relPath == null)
                {
                    result.Add(null);
                    continue;
                }
                var destTransform = string.IsNullOrEmpty(relPath)
                    ? destRoot.transform
                    : destRoot.transform.Find(relPath);
                if (destTransform == null)
                {
                    Debug.LogWarning($"[CHM] 一時 root 内に対応 Renderer が見つかりません: {relPath}");
                    result.Add(null);
                    continue;
                }
                result.Add(destTransform.GetComponent<SkinnedMeshRenderer>());
            }
            return result;
        }

        private static string? GetRelativePath(Transform root, Transform target)
        {
            if (target == root) return string.Empty;
            var stack = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                stack.Push(current.name);
                current = current.parent;
            }
            if (current != root) return null;
            return string.Join("/", stack);
        }

        // ========== メッシュ変形適用 ==========

        private static void ApplyDeformations(ChimeraHairMaster component, List<SkinnedMeshRenderer?> tempRenderers)
        {
            if (component.rendererDeformations == null) return;

            foreach (var deformation in component.rendererDeformations)
            {
                if (deformation.deltas == null || deformation.deltas.Count == 0) continue;
                if (deformation.rendererIndex < 0 || deformation.rendererIndex >= tempRenderers.Count) continue;

                var tempRenderer = tempRenderers[deformation.rendererIndex];
                if (tempRenderer == null || tempRenderer.sharedMesh == null) continue;

                // Blendshape モードか焼き込みかで分岐
                bool asBlendshape = component.ExportAsBlendshape;
                string? actualBsName = null;
                Mesh deformedMesh = asBlendshape
                    ? MeshDeformer.ExportDeformedMeshAsBlendshape(
                        tempRenderer, deformation,
                        string.IsNullOrEmpty(component.BlendshapeName) ? "CHMDeform" : component.BlendshapeName,
                        out actualBsName)
                    : MeshDeformer.ExportDeformedMesh(tempRenderer, deformation);
                if (deformedMesh == null) continue;

                // 元メッシュ同フォルダに連番付きで保存
                string rendererName = tempRenderer.name;
                string originalPath = AssetDatabase.GetAssetPath(tempRenderer.sharedMesh);
                string assetName = string.IsNullOrEmpty(originalPath)
                    ? ""
                    : Path.GetFileNameWithoutExtension(originalPath);
                string meshName = string.IsNullOrEmpty(assetName)
                    ? $"{rendererName}{DeformedMeshSuffix}"
                    : $"{assetName}_{rendererName}{DeformedMeshSuffix}";
                deformedMesh.name = meshName;

                string folder = string.IsNullOrEmpty(originalPath)
                    ? "Assets"
                    : Path.GetDirectoryName(originalPath)!;
                string baseSavePath = $"{folder}/{meshName}.asset";
                string savePath = AssetDatabase.GenerateUniqueAssetPath(baseSavePath);

                AssetDatabase.CreateAsset(deformedMesh, savePath);
                tempRenderer.sharedMesh = deformedMesh;

                // Blendshape モードなら weight=100 を設定して見た目維持
                if (asBlendshape && actualBsName != null)
                {
                    int idx = deformedMesh.GetBlendShapeIndex(actualBsName);
                    if (idx >= 0)
                    {
                        tempRenderer.SetBlendShapeWeight(idx, 100f);
                    }
                }
            }
        }

        // ========== MA Merge Armature 自動付与（ModularAvatar 検出時のみ） ==========

        /// <summary>
        /// 各髪パーツの Armature root に MA Merge Armature を付与する。
        /// 各 Armature が独立している（パターンB）場合に、ビルド時にアバター本体の Armature にマージされる。
        /// ModularAvatar が未導入のプロジェクトでは何もしない（asmdef 条件コンパイル）。
        /// </summary>
        private static void AddMergeArmatureComponents(IEnumerable<SkinnedMeshRenderer> tempRenderers, GameObject avatarRootOriginal)
        {
#if CHM_MODULAR_AVATAR
            string? armatureRefPath = FindAvatarArmaturePath(avatarRootOriginal);
            if (armatureRefPath == null)
            {
                Debug.LogWarning("[CHM] アバター内に Armature root が見つかりません。MA Merge Armature の mergeTarget を Prefab で手動設定してください");
            }

            var processedRoots = new HashSet<Transform>();
            foreach (var renderer in tempRenderers)
            {
                if (renderer == null) continue;
                var armatureRoot = FindArmatureRoot(renderer);
                if (armatureRoot == null) continue;
                if (!processedRoots.Add(armatureRoot)) continue;

                // 既に MA Merge Armature が付いていればスキップ
                if (armatureRoot.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>() != null) continue;

                var ma = armatureRoot.gameObject.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>();
                if (armatureRefPath != null)
                {
                    ma.mergeTarget = new nadena.dev.modular_avatar.core.AvatarObjectReference
                    {
                        referencePath = armatureRefPath
                    };
                }
            }
#endif
        }

#if CHM_MODULAR_AVATAR
        /// <summary>
        /// 元アバターから本体 Armature の path を検出する。
        /// 1. avatarRoot 直下に "Armature" GameObject があればそのまま採用
        /// 2. 無ければ Animator humanoid Hips から遡って avatarRoot 直下の祖先を採用
        /// </summary>
        private static string? FindAvatarArmaturePath(GameObject avatarRoot)
        {
            var armature = avatarRoot.transform.Find("Armature");
            if (armature != null) return "Armature";

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                {
                    var t = hips;
                    while (t.parent != null && t.parent != avatarRoot.transform)
                    {
                        t = t.parent;
                    }
                    if (t.parent == avatarRoot.transform)
                    {
                        return t.name;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// SkinnedMeshRenderer の bone 階層を遡って、Renderer の祖先と分岐する直前の bone を Armature root とみなす。
        /// 例: HairTop/Armature/Root/Hips/.../HairBone の構造で、Renderer = HairTop/MeshRenderer なら Armature が返る。
        /// </summary>
        private static Transform? FindArmatureRoot(SkinnedMeshRenderer renderer)
        {
            var rendererAncestors = new HashSet<Transform>();
            var t = renderer.transform.parent;
            while (t != null)
            {
                rendererAncestors.Add(t);
                t = t.parent;
            }

            Transform? bone = renderer.rootBone;
            if (bone == null && renderer.bones != null && renderer.bones.Length > 0)
            {
                bone = renderer.bones[0];
            }
            if (bone == null) return null;

            Transform? last = null;
            while (bone != null && !rendererAncestors.Contains(bone))
            {
                last = bone;
                bone = bone.parent;
            }
            return last;
        }
#endif

        // ========== sharedMaterials 差替 ==========

        private static void ReplaceMaterials(
            ChimeraHairMaster component,
            List<SkinnedMeshRenderer?> tempRenderers,
            Dictionary<int, Material> clonedMaterials)
        {
            for (int r = 0; r < tempRenderers.Count; r++)
            {
                var temp = tempRenderers[r];
                if (temp == null) continue;
                var srcRenderer = component.targetRenderers[r];
                if (srcRenderer == null) continue;

                var srcMaterials = srcRenderer.sharedMaterials;
                var newMaterials = temp.sharedMaterials;
                if (newMaterials.Length != srcMaterials.Length)
                {
                    Debug.LogWarning($"[CHM] '{temp.name}' のマテリアル数が一致しません: src={srcMaterials.Length}, temp={newMaterials.Length}");
                    continue;
                }

                bool changed = false;
                for (int s = 0; s < srcMaterials.Length; s++)
                {
                    var srcMat = srcMaterials[s];
                    if (srcMat == null) continue;
                    if (!component.IsSubmeshIncluded(r, s)) continue;
                    if (clonedMaterials.TryGetValue(srcMat.GetInstanceID(), out var clone))
                    {
                        newMaterials[s] = clone;
                        changed = true;
                    }
                }
                if (changed)
                {
                    temp.sharedMaterials = newMaterials;
                }
            }
        }
    }
}
