using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// テクスチャアトラス処理パス
    /// </summary>
    public class TextureAtlasPass : Pass<TextureAtlasPass>
    {
        public override string DisplayName => "CHM: Texture Atlas";

        /// <summary>
        /// アトラス結果のキャッシュ（コンポーネントごと）
        /// </summary>
        internal static Dictionary<ChimeraHairMaster, TextureAtlasBuilder.AtlasResult> AtlasResultCache
            = new Dictionary<ChimeraHairMaster, TextureAtlasBuilder.AtlasResult>();

        protected override void Execute(BuildContext context)
        {
            // キャッシュをクリア
            AtlasResultCache.Clear();

            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;

                ProcessTextureAtlas(context, component);
            }
        }

        private void ProcessTextureAtlas(BuildContext context, ChimeraHairMaster component)
        {
            // メッシュ統合が無効の場合、per-rendererマテリアル作成のみ行う
            if (!component.enableMeshMerge)
            {
                ProcessPerRendererMaterials(component);
                return;
            }

            Debug.Log($"[ChimeraHairMaster] テクスチャアトラス処理開始: {component.gameObject.name}");

            int resolution = component.GetResolutionValue();
            Debug.Log($"[ChimeraHairMaster] 出力解像度: {resolution}x{resolution}");

            // ColorTransformPassで処理済みのテクスチャキャッシュを取得
            Dictionary<Texture2D, Texture2D> processedTextureCache = null;
            if (ColorTransformPass.ProcessedTextureCache.TryGetValue(component, out var cache))
            {
                processedTextureCache = cache;
            }

            // アトラステクスチャをビルド
            var atlasResult = TextureAtlasBuilder.Build(component, resolution, processedTextureCache);

            if (atlasResult.AtlasTextures.Count == 0)
            {
                Debug.LogWarning($"[ChimeraHairMaster] アトラステクスチャが生成されませんでした: {component.gameObject.name}");
                return;
            }

            // キャッシュに保存
            AtlasResultCache[component] = atlasResult;

            // メッシュのUV座標を再マッピング（アイランド単位）
            RemapMeshUVsByIslands(component, atlasResult);

            // 統合マテリアルを生成
            CreateOutputMaterial(component, atlasResult);

            Debug.Log($"[ChimeraHairMaster] テクスチャアトラス処理完了: {component.gameObject.name}, " +
                      $"アトラス数: {atlasResult.AtlasTextures.Count}");
        }

        /// <summary>
        /// メッシュのUVを再マッピング（アイランド単位）
        /// </summary>
        private void RemapMeshUVsByIslands(ChimeraHairMaster component, TextureAtlasBuilder.AtlasResult atlasResult)
        {
            if (atlasResult.IslandPlacements == null || atlasResult.IslandPlacements.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] アイランド配置情報がありません。UV変換をスキップします。");
                return;
            }

            // Rendererごとにアイランド配置をグループ化
            var rendererIslands = new Dictionary<int, List<IslandPlacement>>();
            foreach (var island in atlasResult.IslandPlacements)
            {
                if (!rendererIslands.TryGetValue(island.rendererIndex, out var list))
                {
                    list = new List<IslandPlacement>();
                    rendererIslands[island.rendererIndex] = list;
                }
                list.Add(island);
            }

            // 各Rendererのメッシュを処理
            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null) continue;

                var sourceMesh = renderer.sharedMesh;
                if (sourceMesh == null) continue;

                if (!rendererIslands.TryGetValue(i, out var islands))
                {
                    Debug.LogWarning($"[ChimeraHairMaster] Renderer {renderer.name} のアイランド情報がありません");
                    continue;
                }

                // アイランド単位でUVを変換（サブメッシュ対応）
                var newMesh = MeshUVRemapper.RemapUVsByIslands(sourceMesh, islands, i);
                if (newMesh != null)
                {
                    renderer.sharedMesh = newMesh;
                    Debug.Log($"[ChimeraHairMaster] UV再マッピング完了: {renderer.name}, アイランド数: {islands.Count}");
                }
            }
        }

        /// <summary>
        /// メッシュのUVを再マッピング（旧、後方互換性のため残す）
        /// </summary>
        private void RemapMeshUVs(ChimeraHairMaster component, TextureAtlasBuilder.AtlasResult atlasResult)
        {
            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;
                if (!atlasResult.UVTransforms.TryGetValue(renderer, out var transform)) continue;

                var sourceMesh = renderer.sharedMesh;
                if (sourceMesh == null) continue;

                var newMesh = MeshUVRemapper.RemapUVsNormalized(sourceMesh, transform);
                if (newMesh != null)
                {
                    renderer.sharedMesh = newMesh;
                    Debug.Log($"[ChimeraHairMaster] UV再マッピング完了: {renderer.name}");
                }
            }
        }

        /// <summary>
        /// 出力マテリアルを生成
        /// previewMaterialがあればその設定を継承、なければbaseMaterialを使用
        /// テクスチャは複製せず、数値パラメータとトグルのみコピー
        /// </summary>
        private void CreateOutputMaterial(ChimeraHairMaster component, TextureAtlasBuilder.AtlasResult atlasResult)
        {
            // ソースマテリアルを決定（優先順位: previewMaterial > baseMaterial > 最初のRenderer）
            Material sourceMaterial = component.previewMaterial;
            
            if (sourceMaterial == null)
            {
                sourceMaterial = component.baseMaterial;
            }
            
            // 基準マテリアルが設定されていない場合、最初のRendererのマテリアルを使用
            if (sourceMaterial == null)
            {
                foreach (var renderer in component.targetRenderers)
                {
                    if (renderer != null && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null)
                    {
                        sourceMaterial = renderer.sharedMaterials[0];
                        Debug.Log($"[ChimeraHairMaster] 基準マテリアルが未設定のため、{renderer.name}のマテリアルを使用します");
                        break;
                    }
                }
            }

            if (sourceMaterial == null)
            {
                Debug.LogError($"[ChimeraHairMaster] 使用可能なマテリアルが見つかりません: {component.gameObject.name}");
                return;
            }

            // ソースマテリアルの完全コピー（テクスチャ含む）
            // アトラステクスチャは後続のSetTextureで上書きされる
            var outputMaterial = new Material(sourceMaterial);
            outputMaterial.name = (component.previewMaterial != null ? "Preview" : sourceMaterial.name) + "_CHM_Atlas";

            // アトラステクスチャを設定
            foreach (var kvp in atlasResult.AtlasTextures)
            {
                string propertyName = kvp.Key;
                Texture2D atlasTexture = kvp.Value;

                if (outputMaterial.HasProperty(propertyName))
                {
                    outputMaterial.SetTexture(propertyName, atlasTexture);
                    Debug.Log($"[ChimeraHairMaster] テクスチャ設定: {propertyName}");
                }
            }

            // 出力マテリアルを保存
            component.outputMaterial = outputMaterial;

            // 各Rendererにマテリアルを適用
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;
                if (renderer.sharedMaterials == null) continue;

                var oldMaterials = renderer.sharedMaterials;
                var newMaterials = new Material[oldMaterials.Length];

                for (int i = 0; i < oldMaterials.Length; i++)
                {
                    // 統合対象かどうかをチェックして、除外なら元のマテリアルを保持
                    if (component.IsSubmeshIncluded(r, i))
                    {
                        newMaterials[i] = outputMaterial;
                    }
                    else
                    {
                        newMaterials[i] = oldMaterials[i];
                    }
                }

                renderer.sharedMaterials = newMaterials;
            }

            Debug.Log($"[ChimeraHairMaster] 出力マテリアル生成完了: {outputMaterial.name}" +
                      (component.previewMaterial != null ? " (previewMaterial設定を継承)" : ""));
        }

        /// <summary>
        /// テクスチャを除いて数値パラメータとトグルのみをコピーした新規マテリアルを作成
        /// すべてのテクスチャスロットを明示的にnullに設定
        /// </summary>
        private Material CreateMaterialWithoutTextures(Material source)
        {
            // 同じシェーダーで新規マテリアルを作成
            var newMat = new Material(source.shader);
            
            // レンダーキューをコピー
            newMat.renderQueue = source.renderQueue;
            
            // シェーダーキーワードをコピー
            newMat.shaderKeywords = source.shaderKeywords;
            
            // プロパティをコピー（テクスチャ以外）
            var shader = source.shader;
            int propertyCount = shader.GetPropertyCount();
            
            for (int i = 0; i < propertyCount; i++)
            {
                var propertyType = shader.GetPropertyType(i);
                var propertyName = shader.GetPropertyName(i);
                
                switch (propertyType)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        newMat.SetColor(propertyName, source.GetColor(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        newMat.SetVector(propertyName, source.GetVector(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        newMat.SetFloat(propertyName, source.GetFloat(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        newMat.SetInt(propertyName, source.GetInt(propertyName));
                        break;
                    // Textureは明示的にnullに設定
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        newMat.SetTexture(propertyName, null);
                        // テクスチャのScale/Offsetはコピー
                        newMat.SetTextureScale(propertyName, source.GetTextureScale(propertyName));
                        newMat.SetTextureOffset(propertyName, source.GetTextureOffset(propertyName));
                        break;
                }
            }
            
            return newMat;
        }

        /// <summary>
        /// メッシュ統合なしモード: 各RendererのマテリアルにbaseMaterialの数値設定のみを適用
        /// テクスチャは各Rendererの元マテリアル（色変換済み含む）をそのまま保持
        /// </summary>
        private void ProcessPerRendererMaterials(ChimeraHairMaster component)
        {
            Debug.Log($"[ChimeraHairMaster] per-rendererマテリアル処理開始（メッシュ統合なし）: {component.gameObject.name}");

            // ソースマテリアルを決定（優先順位: previewMaterial > baseMaterial > 最初のRenderer）
            Material sourceMaterial = component.previewMaterial;

            if (sourceMaterial == null)
            {
                sourceMaterial = component.baseMaterial;
            }

            if (sourceMaterial == null)
            {
                foreach (var renderer in component.targetRenderers)
                {
                    if (renderer != null && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null)
                    {
                        sourceMaterial = renderer.sharedMaterials[0];
                        Debug.Log($"[ChimeraHairMaster] 基準マテリアルが未設定のため、{renderer.name}のマテリアルを使用します");
                        break;
                    }
                }
            }

            if (sourceMaterial == null)
            {
                Debug.LogError($"[ChimeraHairMaster] 使用可能なマテリアルが見つかりません: {component.gameObject.name}");
                return;
            }

            // 各Rendererのマテリアルを処理
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;
                if (renderer.sharedMaterials == null) continue;

                var oldMaterials = renderer.sharedMaterials;
                var newMaterials = new Material[oldMaterials.Length];

                for (int s = 0; s < oldMaterials.Length; s++)
                {
                    var mat = oldMaterials[s];
                    if (mat == null || !component.IsSubmeshIncluded(r, s))
                    {
                        newMaterials[s] = mat;
                        continue;
                    }

                    // 元マテリアルをコピー（テクスチャ全保持）
                    var newMat = new Material(mat);
                    newMat.name = mat.name + "_CHM_Settings";

                    // 数値プロパティのみbaseMaterialから上書き（2nd/3rd/発光は除外、MatCapは設定に応じて除外）
                    ApplyShaderSettings(sourceMaterial, newMat,
                        excludeOverlayAndEmission: true,
                        excludeMatCap: !component.unifyMatCap);

                    newMaterials[s] = newMat;
                }

                renderer.sharedMaterials = newMaterials;
            }

            Debug.Log($"[ChimeraHairMaster] per-rendererマテリアル処理完了: {component.gameObject.name}" +
                      (component.previewMaterial != null ? " (previewMaterial設定を継承)" : ""));
        }

        /// <summary>
        /// テクスチャ以外のプロパティ（数値、色、ベクトル、キーワード、レンダーキュー）をコピー
        /// テクスチャスロットはスキップし、元のマテリアルのテクスチャをそのまま保持する
        /// excludeOverlayAndEmission が true の場合、メインカラー2nd/3rd・発光設定もスキップする
        ///
        /// シェーダー本体は差し替えない。lilToon は描画モード（不透明/カットアウト/半透明）が
        /// 別シェーダーになっており、from に合わせると描画モードの異なる対象（例: 不透明の耳に
        /// 半透明の基準マテ）がアルファ評価で不可視になるため、対象側のシェーダーを維持する。
        /// </summary>
        internal static void ApplyShaderSettings(Material from, Material to, bool excludeOverlayAndEmission = false, bool excludeMatCap = false)
        {
            to.renderQueue = from.renderQueue;
            to.shaderKeywords = from.shaderKeywords;

            var shader = from.shader;
            int propertyCount = shader.GetPropertyCount();

            for (int i = 0; i < propertyCount; i++)
            {
                var propertyType = shader.GetPropertyType(i);
                var propertyName = shader.GetPropertyName(i);

                // テクスチャはスキップ
                if (propertyType == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    continue;

                // メインカラー2nd/3rd・発光設定をスキップ
                if (excludeOverlayAndEmission && IsOverlayOrEmissionProperty(propertyName))
                    continue;

                // MatCap設定をスキップ
                if (excludeMatCap && IsMatCapProperty(propertyName))
                    continue;

                // コピー先にプロパティが存在する場合のみコピー
                if (!to.HasProperty(propertyName))
                    continue;

                switch (propertyType)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        to.SetColor(propertyName, from.GetColor(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        to.SetVector(propertyName, from.GetVector(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        to.SetFloat(propertyName, from.GetFloat(propertyName));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        to.SetInt(propertyName, from.GetInt(propertyName));
                        break;
                }
            }
        }

        /// <summary>
        /// lilToonのメインカラー2nd/3rd・発光設定に該当するプロパティか判定
        /// </summary>
        private static bool IsOverlayOrEmissionProperty(string propertyName)
        {
            return propertyName.StartsWith("_Main2nd") ||
                   propertyName.StartsWith("_Color2nd") ||
                   propertyName == "_UseMain2ndTex" ||
                   propertyName.StartsWith("_Main3rd") ||
                   propertyName.StartsWith("_Color3rd") ||
                   propertyName == "_UseMain3rdTex" ||
                   propertyName.StartsWith("_Emission") ||
                   propertyName.StartsWith("_UseEmission");
        }

        /// <summary>
        /// lilToonのMatCap 1st/2ndに該当するプロパティか判定
        /// </summary>
        private static bool IsMatCapProperty(string propertyName)
        {
            return propertyName.StartsWith("_MatCap") ||
                   propertyName == "_UseMatCap" ||
                   propertyName == "_UseMatCap2nd";
        }

        /// <summary>
        /// マットキャップテクスチャのみをコピー
        /// </summary>
        private void CopyMatCapTextures(Material source, Material dest)
        {
            // マットキャップ1st
            string[] matCap1stProps = new string[]
            {
                "_MatCapTex",
                "_MatCapBlendMask",
                "_MatCapBumpMap"
            };

            foreach (var prop in matCap1stProps)
            {
                if (source.HasProperty(prop) && dest.HasProperty(prop))
                {
                    var tex = source.GetTexture(prop);
                    if (tex != null)
                    {
                        dest.SetTexture(prop, tex);
                    }
                }
            }

            // マットキャップ2nd
            string[] matCap2ndProps = new string[]
            {
                "_MatCap2ndTex",
                "_MatCap2ndBlendMask",
                "_MatCap2ndBumpMap"
            };

            foreach (var prop in matCap2ndProps)
            {
                if (source.HasProperty(prop) && dest.HasProperty(prop))
                {
                    var tex = source.GetTexture(prop);
                    if (tex != null)
                    {
                        dest.SetTexture(prop, tex);
                    }
                }
            }
        }
    }
}
