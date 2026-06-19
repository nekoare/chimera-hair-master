using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using ChimeraHairMaster.Editor.Processing;
using ChimeraHairMaster.Editor.Localization;
#if CHM_MASK_CREATION_TOOL
using NekoareMaskTool.Editor;
#endif

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// マスク作成支援ツールの起動ヘルパー
    /// 優先順位:
    /// 1. com.nekoare.mask-creation-tool (UPM版) がある場合は直接呼び出し
    /// 2. MaskToolRegistryにfallbackハンドラが登録されている場合はそれを使用
    /// 3. どちらもない場合は警告ダイアログを表示
    /// </summary>
    public static class MaskToolLauncher
    {
        /// <summary>
        /// マスクツールが編集中のRenderTexture（NDMFプレビューのOnFrameから参照される）
        /// </summary>
        public static RenderTexture ActiveMaskTexture { get; set; }

        /// <summary>
        /// ActiveMaskTextureを適用するマテリアルスロット名
        /// </summary>
        public static string ActiveMaskSlotName { get; set; }

        /// <summary>
        /// Renderer InstanceID → アイランド単位のマッピング情報（originalBounds, atlasPosition, atlasScale）
        /// プレビューではアトラスマスクを元UV空間に合成して表示するために使用
        /// </summary>
        public static Dictionary<int, List<(int submeshIndex, Rect originalBounds, Vector2 atlasPosition, Vector2 atlasScale)>> ActiveMaskIslandMappings { get; set; }

        public static bool IsAvailable
        {
            get
            {
                #if CHM_MASK_CREATION_TOOL
                return true;
                #else
                return MaskToolRegistry.HasHandler;
                #endif
            }
        }

        /// <summary>
        /// 指定されたスロットのマスクをマスクツールで編集する
        /// </summary>
        public static void OpenMaskTool(
            ChimeraHairMaster component,
            string maskSlotName)
        {
            #if CHM_MASK_CREATION_TOOL
            OpenMaskToolInternal(component, maskSlotName);
            #else
            if (MaskToolRegistry.HasHandler)
            {
                OpenMaskToolViaRegistry(component, maskSlotName);
            }
            else
            {
                // Assets版マスクツールの旧バージョン（CHMブリッジなし）が存在するか確認
                bool hasOldAssetsMaskTool = false;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "com.nekoare.mask-creation-tool.editor")
                    {
                        hasOldAssetsMaskTool = true;
                        break;
                    }
                }

                if (hasOldAssetsMaskTool)
                {
                    if (EditorUtility.DisplayDialog(
                        CHMLocales.Tr("MaskTool:Dialog:UpdateRequiredTitle"),
                        CHMLocales.Tr("MaskTool:Dialog:UpdateAssetsV1Body"),
                        CHMLocales.Tr("MaskTool:Dialog:OpenProductPage"),
                        CHMLocales.Tr("MaskTool:Dialog:Close")))
                    {
                        Application.OpenURL("https://neko-to-same.booth.pm/items/7951475");
                    }
                }
                else
                {
                    if (EditorUtility.DisplayDialog(
                        CHMLocales.Tr("MaskTool:Dialog:NotInstalledTitle"),
                        CHMLocales.Tr("MaskTool:Dialog:NotInstalledBody"),
                        CHMLocales.Tr("MaskTool:Dialog:OpenProductPage"),
                        CHMLocales.Tr("MaskTool:Dialog:Close")))
                    {
                        Application.OpenURL("https://neko-to-same.booth.pm/items/7951475");
                    }
                }
            }
            #endif
        }

        /// <summary>
        /// 単一RendererのSubMeshを対象にマスクツールを開く（非アトラスモード）
        /// マスクツール v1.1以上が必要
        /// </summary>
        public static void OpenMaskToolForSubmesh(
            ChimeraHairMaster component,
            SkinnedMeshRenderer renderer,
            int submeshIndex)
        {
            OpenMaskToolForSubmesh(component, renderer, submeshIndex, null);
        }

        /// <summary>
        /// 単一RendererのSubMeshを対象にマスクツールを開く（非アトラスモード）+ 保存後コールバック。
        /// マスクツール v1.3+ で onSaved が呼ばれる。それ未満では従来通り通知なし。
        /// </summary>
        public static void OpenMaskToolForSubmesh(
            ChimeraHairMaster component,
            SkinnedMeshRenderer renderer,
            int submeshIndex,
            System.Action<Texture2D> onSaved)
        {
            #if CHM_MASK_TOOL_SUBMESH
            // VPM版 v1.1+ → 直接呼び出し
            OpenMaskToolForSubmeshInternal(component, renderer, submeshIndex, onSaved);
            #elif CHM_MASK_CREATION_TOOL
            // VPM版はあるがv1.1未満 → 更新ダイアログ
            EditorUtility.DisplayDialog(
                CHMLocales.Tr("MaskTool:Dialog:UpdateRequiredTitle"),
                CHMLocales.Tr("MaskTool:Dialog:UpdateVpmV11Body"),
                "OK");
            #else
            if (MaskToolRegistry.HasSubmeshCallbackHandler)
            {
                // Assets版 v1.3+（callback ハンドラ登録済み）→ レジストリ経由
                MaskToolRegistry.OpenForSubmesh(renderer, submeshIndex, onSaved);
            }
            else if (MaskToolRegistry.HasSubmeshHandler)
            {
                // Assets版 v1.1+（submeshハンドラ登録済み、callback 非対応）→ レジストリ経由
                // onSaved は通知されない（古いマスクツールでは従来動作）
                MaskToolRegistry.OpenForSubmesh(renderer, submeshIndex);
            }
            else if (MaskToolRegistry.HasHandler)
            {
                // Assets版はあるがv1.1未満 → 更新ダイアログ
                if (EditorUtility.DisplayDialog(
                    CHMLocales.Tr("MaskTool:Dialog:UpdateRequiredTitle"),
                    CHMLocales.Tr("MaskTool:Dialog:UpdateAssetsV11Body"),
                    CHMLocales.Tr("MaskTool:Dialog:OpenProductPage"),
                    CHMLocales.Tr("MaskTool:Dialog:Close")))
                {
                    Application.OpenURL("https://neko-to-same.booth.pm/items/7951475");
                }
            }
            else
            {
                // マスクツール未インストール
                bool hasOldAssetsMaskTool = false;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "com.nekoare.mask-creation-tool.editor")
                    {
                        hasOldAssetsMaskTool = true;
                        break;
                    }
                }

                if (hasOldAssetsMaskTool)
                {
                    if (EditorUtility.DisplayDialog(
                        CHMLocales.Tr("MaskTool:Dialog:UpdateRequiredTitle"),
                        CHMLocales.Tr("MaskTool:Dialog:UpdateAssetsV11IncompatBody"),
                        CHMLocales.Tr("MaskTool:Dialog:OpenProductPage"),
                        CHMLocales.Tr("MaskTool:Dialog:Close")))
                    {
                        Application.OpenURL("https://neko-to-same.booth.pm/items/7951475");
                    }
                }
                else
                {
                    if (EditorUtility.DisplayDialog(
                        CHMLocales.Tr("MaskTool:Dialog:NotInstalledTitle"),
                        CHMLocales.Tr("MaskTool:Dialog:NotInstalledSubmeshBody"),
                        CHMLocales.Tr("MaskTool:Dialog:OpenProductPage"),
                        CHMLocales.Tr("MaskTool:Dialog:Close")))
                    {
                        Application.OpenURL("https://neko-to-same.booth.pm/items/7951475");
                    }
                }
            }
            #endif
        }

        #if CHM_MASK_TOOL_SUBMESH
        private static void OpenMaskToolForSubmeshInternal(
            ChimeraHairMaster component,
            SkinnedMeshRenderer renderer,
            int submeshIndex,
            System.Action<Texture2D> onSaved)
        {
            var context = new MaskToolExternalContext
            {
                targetRenderer = renderer,
                targetGameObject = renderer.gameObject,
                selectedMaterialIndex = submeshIndex
            };

            // 保存後コールバック: マスクツール v1.3+ から savedPath が通知される（旧版では呼ばれず）
            if (onSaved != null)
            {
                context.onMaskApplied = (savedPath, _) =>
                {
                    if (string.IsNullOrEmpty(savedPath)) return;
                    ConfigureTextureImporter(savedPath);
                    string assetPath = savedPath;
                    if (savedPath.StartsWith(Application.dataPath))
                    {
                        assetPath = "Assets" + savedPath.Substring(Application.dataPath.Length);
                    }
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (tex != null) onSaved(tex);
                };
            }

            MaskCreationToolWindow.OpenWithContext(context);
        }
        #endif

        #if CHM_MASK_CREATION_TOOL
        private static void OpenMaskToolInternal(
            ChimeraHairMaster component,
            string maskSlotName)
        {
            var islandPlacements = component.islandPlacements;
            if (islandPlacements == null || islandPlacements.Count == 0)
            {
                Debug.LogWarning("アイランド配置情報がありません。UV設定を先に行ってください。");
                return;
            }

            var resolution = component.GetResolutionValue();

            // IslandPlacement → SourceMaskEntry に変換
            var sourceMasks = new List<SourceMaskEntry>();
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count)
                    continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // 対象スロットからマスクテクスチャを取得
                Texture2D maskTex = GetMaskFromRenderer(
                    renderer, maskSlotName, island.submeshIndex);

                // メインカラーテクスチャを取得（アトラス背景の合成用）
                Texture2D mainTex = GetMainTextureFromRenderer(
                    renderer, island.submeshIndex);

                sourceMasks.Add(new SourceMaskEntry
                {
                    maskTexture = maskTex,
                    mainTexture = mainTex,
                    originalBounds = island.originalBounds,
                    atlasPosition = island.atlasPosition,
                    atlasScale = island.atlasScale,
                    sourceRendererIndex = island.rendererIndex,
                    sourceMaterialIndex = island.submeshIndex
                });
            }

            // ターゲットRendererの解決
            // 最初のtargetRendererを使用（フォールバック用）
            SkinnedMeshRenderer targetRenderer = null;
            if (component.targetRenderers.Count > 0)
                targetRenderer = component.targetRenderers[0];

            // アトラスUVメッシュを構築（UVワイヤーフレーム生成用）
            var atlasMesh = BuildAtlasMesh(component);

            // プレビュー用アイランドマッピングを事前計算（元UV空間への合成テクスチャ生成用）
            var islandMappings = BuildIslandMappings(component);

            var context = new MaskToolExternalContext
            {
                targetRenderer = targetRenderer,
                targetGameObject = component.gameObject,
                atlasMesh = atlasMesh,
                sourceMasks = sourceMasks,
                atlasResolution = resolution,
                sourceRenderers = new List<Renderer>(component.targetRenderers.ToArray()),
                currentMaskSlotName = maskSlotName,
                // マスクRenderTextureと関連データを静的フィールドに登録するコールバック
                onMaskTextureAvailable = (rt) =>
                {
                    ActiveMaskTexture = rt;
                    ActiveMaskSlotName = maskSlotName;
                    ActiveMaskIslandMappings = islandMappings;
                },
                onMaskToolClosed = () =>
                {
                    ActiveMaskTexture = null;
                    ActiveMaskSlotName = null;
                    ActiveMaskIslandMappings = null;

                    // CHM が生成した一時アトラスメッシュはツール側では破棄されないため、
                    // クローズ時にこちらで破棄する
                    if (atlasMesh != null)
                        Object.DestroyImmediate(atlasMesh);
                },
                onMaskApplied = (savedPath, slotName) =>
                {
                    // プレビュースロットを更新
                    ActiveMaskSlotName = slotName;
                    // TextureImporter設定
                    ConfigureTextureImporter(savedPath);
                    // 保存したテクスチャをプレビューマテリアルのスロットに設定
                    ApplyTextureToMaterial(component, savedPath, slotName);
                },
                onOutputSlotChanged = (slotName) =>
                {
                    // プレビュースロットを切り替え
                    ActiveMaskSlotName = slotName;
                }
            };

            MaskCreationToolWindow.OpenWithContext(context);
        }
        #endif

        /// <summary>
        /// MaskToolRegistryを経由してマスクツールを開く（Assets版マスクツール用）
        /// </summary>
        private static void OpenMaskToolViaRegistry(
            ChimeraHairMaster component,
            string maskSlotName)
        {
            var islandPlacements = component.islandPlacements;
            if (islandPlacements == null || islandPlacements.Count == 0)
            {
                Debug.LogWarning("アイランド配置情報がありません。UV設定を先に行ってください。");
                return;
            }

            var resolution = component.GetResolutionValue();

            // IslandPlacement → MaskToolSourceEntry に変換
            var sourceMasks = new List<MaskToolSourceEntry>();
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count)
                    continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                Texture2D maskTex = GetMaskFromRenderer(
                    renderer, maskSlotName, island.submeshIndex);
                Texture2D mainTex = GetMainTextureFromRenderer(
                    renderer, island.submeshIndex);

                sourceMasks.Add(new MaskToolSourceEntry
                {
                    maskTexture = maskTex,
                    mainTexture = mainTex,
                    originalBounds = island.originalBounds,
                    atlasPosition = island.atlasPosition,
                    atlasScale = island.atlasScale,
                    sourceRendererIndex = island.rendererIndex,
                    sourceMaterialIndex = island.submeshIndex
                });
            }

            SkinnedMeshRenderer targetRenderer = null;
            if (component.targetRenderers.Count > 0)
                targetRenderer = component.targetRenderers[0];

            var atlasMesh = BuildAtlasMesh(component);
            var islandMappings = BuildIslandMappings(component);

            var request = new MaskToolOpenRequest
            {
                targetRenderer = targetRenderer,
                targetGameObject = component.gameObject,
                atlasMesh = atlasMesh,
                sourceMasks = sourceMasks,
                atlasResolution = resolution,
                sourceRenderers = new List<Renderer>(component.targetRenderers.ToArray()),
                currentMaskSlotName = maskSlotName,
                onMaskTextureAvailable = (rt) =>
                {
                    ActiveMaskTexture = rt;
                    ActiveMaskSlotName = maskSlotName;
                    ActiveMaskIslandMappings = islandMappings;
                },
                onMaskToolClosed = () =>
                {
                    ActiveMaskTexture = null;
                    ActiveMaskSlotName = null;
                    ActiveMaskIslandMappings = null;

                    // CHM が生成した一時アトラスメッシュはツール側では破棄されないため、
                    // クローズ時にこちらで破棄する
                    if (atlasMesh != null)
                        Object.DestroyImmediate(atlasMesh);
                },
                onMaskApplied = (savedPath, slotName) =>
                {
                    ActiveMaskSlotName = slotName;
                    ConfigureTextureImporter(savedPath);
                    ApplyTextureToMaterial(component, savedPath, slotName);
                },
                onOutputSlotChanged = (slotName) =>
                {
                    ActiveMaskSlotName = slotName;
                }
            };

            MaskToolRegistry.Open(request);
        }

        /// <summary>
        /// プレビュー用アイランドマッピングを構築
        /// </summary>
        private static Dictionary<int, List<(int submeshIndex, Rect originalBounds, Vector2 atlasPosition, Vector2 atlasScale)>> BuildIslandMappings(
            ChimeraHairMaster component)
        {
            var islandMappings = new Dictionary<int, List<(int, Rect, Vector2, Vector2)>>();
            foreach (var island in component.islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count)
                    continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                int id = renderer.GetInstanceID();
                if (!islandMappings.ContainsKey(id))
                    islandMappings[id] = new List<(int, Rect, Vector2, Vector2)>();

                islandMappings[id].Add((island.submeshIndex, island.originalBounds, island.atlasPosition, island.atlasScale));
            }
            return islandMappings;
        }

        private static Texture2D GetMaskFromRenderer(
            SkinnedMeshRenderer renderer,
            string slotName,
            int submeshIndex)
        {
            var materials = renderer.sharedMaterials;
            if (submeshIndex < 0 || submeshIndex >= materials.Length)
                return null;

            var mat = materials[submeshIndex];
            if (mat == null || !mat.HasProperty(slotName))
                return null;

            return mat.GetTexture(slotName) as Texture2D;
        }

        /// <summary>
        /// 全RendererのメッシュをアトラスUV座標にリマップして1つのメッシュに結合
        /// </summary>
        private static Mesh BuildAtlasMesh(ChimeraHairMaster component)
        {
            var allVertices = new List<Vector3>();
            var allUVs = new List<Vector2>();
            var allTriangles = new List<int>();

            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // MeshUVRemapper でアトラスUVにリマップ
                var remapped = MeshUVRemapper.RemapUVsByIslands(
                    renderer.sharedMesh, component.islandPlacements, i);
                if (remapped == null) continue;

                int vertexOffset = allVertices.Count;

                allVertices.AddRange(remapped.vertices);
                var uvs = new List<Vector2>();
                remapped.GetUVs(0, uvs);
                allUVs.AddRange(uvs);

                // 全サブメッシュの三角形を結合（オフセット適用）
                for (int sub = 0; sub < remapped.subMeshCount; sub++)
                {
                    var triangles = remapped.GetTriangles(sub);
                    for (int t = 0; t < triangles.Length; t++)
                        allTriangles.Add(triangles[t] + vertexOffset);
                }

                Object.DestroyImmediate(remapped);
            }

            if (allVertices.Count == 0)
                return null;

            var mesh = new Mesh();
            // デフォルトの UInt16 では 65,535 頂点までしか扱えず、
            // 複数 Renderer の結合では容易に超過して壊れたメッシュになる
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.SetVertices(allVertices);
            mesh.SetUVs(0, allUVs);
            mesh.SetTriangles(allTriangles, 0);
            mesh.name = "CHM_AtlasUVMesh";
            return mesh;
        }

        private static Texture2D GetMainTextureFromRenderer(
            SkinnedMeshRenderer renderer,
            int submeshIndex)
        {
            var materials = renderer.sharedMaterials;
            if (submeshIndex < 0 || submeshIndex >= materials.Length)
                return null;

            var mat = materials[submeshIndex];
            if (mat == null)
                return null;

            // _MainTex（Unity標準のメインテクスチャプロパティ）
            if (mat.HasProperty("_MainTex"))
                return mat.GetTexture("_MainTex") as Texture2D;

            return mat.mainTexture as Texture2D;
        }

        /// <summary>
        /// 保存されたマスクテクスチャのTextureImporterを設定
        /// </summary>
        private static void ConfigureTextureImporter(string savedPath)
        {
            // プロジェクト相対パスに変換
            string assetPath = savedPath;
            if (savedPath.StartsWith(UnityEngine.Application.dataPath))
            {
                assetPath = "Assets" + savedPath.Substring(UnityEngine.Application.dataPath.Length);
            }

            AssetDatabase.ImportAsset(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.streamingMipmaps = true;
                importer.SaveAndReimport();
            }
        }

        /// <summary>
        /// 保存したテクスチャをプレビューマテリアルの指定スロットに設定
        /// </summary>
        private static void ApplyTextureToMaterial(
            ChimeraHairMaster component,
            string savedPath,
            string slotName)
        {
            string assetPath = savedPath;
            if (savedPath.StartsWith(UnityEngine.Application.dataPath))
            {
                assetPath = "Assets" + savedPath.Substring(UnityEngine.Application.dataPath.Length);
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null) return;

            var mat = component.previewMaterial;
            if (mat == null)
                mat = component.baseMaterial;
            if (mat == null) return;

            if (mat.HasProperty(slotName))
            {
                mat.SetTexture(slotName, tex);
                EditorUtility.SetDirty(mat);
            }
        }
    }
}
