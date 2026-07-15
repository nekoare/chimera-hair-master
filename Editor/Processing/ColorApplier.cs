#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 色合わせ結果をテクスチャとして出力し、元マテリアルに適用するユーティリティ
    /// </summary>
    public static class ColorApplier
    {
        /// <summary>
        /// 色合わせ結果を適用する
        /// </summary>
        /// <param name="component">対象コンポーネント</param>
        /// <param name="applyTextureToMaterial">生成テクスチャを元マテリアルの_MainTexにセットするか</param>
        /// <param name="unifyMaterialSettings">previewMaterialの数値設定を元マテリアルに統一するか</param>
        public static void Apply(
            ChimeraHairMaster component,
            bool applyTextureToMaterial,
            bool unifyMaterialSettings)
        {
            // バリデーション
            if (component == null) return;
            if (!component.enableColorTransform)
            {
                Debug.LogWarning("[CHM] 色合わせが無効のため適用をスキップしました");
                return;
            }
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
            {
                Debug.LogWarning("[CHM] 対象Rendererが設定されていません");
                return;
            }

            // 共有マテリアル × パーツ別設定の競合を警告（先勝ちで取りこぼす旨を通知）
            WarnSharedMaterialConflicts(component);

            // メッシュ統合有効時はメッシュ統合設定を破棄してテクスチャ保存のみ行う
            bool wasMeshMergeEnabled = component.enableMeshMerge;
            if (wasMeshMergeEnabled)
            {
                Debug.Log("[CHM] メッシュ統合を破棄してテクスチャ保存のみ実行します");
            }

            // Undo一括登録
            var undoObjects = new List<Object>();
            undoObjects.Add(component);
            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null) undoObjects.Add(mat);
                }
            }
            Undo.RegisterCompleteObjectUndo(undoObjects.ToArray(), "CHM 色合わせを適用");

            // 基準色を決定（最初のRendererの_MainTexから）
            Color sourceColor = DetermineSourceColor(component);

            // 色変換設定を構築
            var settings = ColorTransformSettings.FromComponent(component, sourceColor);

            // 処理済みマテリアル → 生成テクスチャのマッピング（重複処理防止）
            var processedMaterials = new Dictionary<int, Texture2D>(); // materialInstanceID → savedTexture

            // Apply 全体で使うピクセル読み込みキャッシュ
            var pixelCache = new MeshUVSampler.PixelCache();
            // 塗り感統一: お手本 Renderer の参照データを事前計算（PNG 出力経路でも適用するため）
            StrandPatternApplier.RefData? strandRef = StrandPatternApplier.PrepareRefData(component, settings, pixelCache);
            try
            {

            // 各Rendererを処理
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                float brightnessOffset = GetRendererBrightnessOffset(component, r);
                float blurSharp = GetRendererBlurSharp(component, r);
                var materials = renderer.sharedMaterials;

                for (int s = 0; s < materials.Length; s++)
                {
                    var mat = materials[s];
                    if (mat == null || !component.IsSubmeshIncluded(r, s)) continue;

                    int matId = mat.GetInstanceID();
                    if (processedMaterials.ContainsKey(matId)) continue;

                    // _MainTexを取得
                    if (!mat.HasProperty("_MainTex")) continue;
                    var mainTex = mat.GetTexture("_MainTex") as Texture2D;
                    if (mainTex == null) continue;
                    // _MainTex の色変更が明示的に OFF なら処理しない（NDMFビルドと一致させる）
                    if (component.IsColorChangeExplicitlyDisabled("_MainTex")) continue;

                    // テクスチャ毎の Oklab/RGBDelta 事前計算（ブラー/シャープ前の元テクスチャから）
                    var perTextureSettings = MeshUVSampler.PrepareSettingsWithUVStats(
                        settings, renderer, new[] { s }, mainTex, pixelCache);

                    // UV マスク（前処理と dilation で共用）
                    bool[] uvMask = MeshUVRasterizer.Rasterize(
                        renderer, new[] { s }, mainTex.width, mainTex.height);

                    bool applyStrand = strandRef != null && strandRef.IsValid && r != strandRef.RefIndex;
                    // PNG出力は各段を非圧縮で通し、圧縮は再インポート時にインポーターが1回だけ行う
                    // （ペイントソフトで書き出したのと同等になり、多重圧縮のブロックノイズを避ける）

                    // 色変換の入力（ブラー/シャープ前処理）
                    Texture2D colorTransformInput = mainTex;
                    Texture2D? preprocessed = null;
                    if (Mathf.Abs(blurSharp) > 0.001f)
                    {
                        preprocessed = TextureBlurSharpener.Process(mainTex, blurSharp, uvMask);
                        if (preprocessed != null) colorTransformInput = preprocessed;
                    }

                    // 色変換実行
                    var processedTex = ColorProcessor.ProcessTexture(colorTransformInput, perTextureSettings, compressResult: false);

                    if (preprocessed != null) Object.DestroyImmediate(preprocessed);

                    if (processedTex == null) continue;

                    // 明度オフセット適用
                    if (Mathf.Abs(brightnessOffset) > 0.001f)
                    {
                        var offsetApplied = ColorProcessor.ApplyBrightnessOffset(processedTex, brightnessOffset, compressResult: false);
                        if (offsetApplied != null)
                        {
                            Object.DestroyImmediate(processedTex);
                            processedTex = offsetApplied;
                        }
                    }

                    // UV 使用領域外を edge dilation で塗り足し
                    {
                        var dilated = ColorProcessor.DilateTexture(processedTex, uvMask, 8, compressResult: false);
                        if (dilated != null)
                        {
                            Object.DestroyImmediate(processedTex);
                            processedTex = dilated;
                        }
                    }

                    // 色合わせ無視マスクを適用
                    {
                        var masked = ColorMaskApplier.TryApply(component, r, s, mainTex, processedTex, compressResult: false);
                        if (masked != null)
                        {
                            Object.DestroyImmediate(processedTex);
                            processedTex = masked;
                        }
                    }

                    // 塗り感統一: お手本 Renderer 以外の _MainTex に inline 適用
                    if (applyStrand)
                    {
                        // applyStrand が true の時点で strandRef は非null（applyStrand = strandRef != null && ...）
                        var composed = StrandPatternApplier.TryComposeStrand(processedTex, renderer, new[] { s }, strandRef!, compressResult: false);
                        if (composed != null)
                        {
                            Object.DestroyImmediate(processedTex);
                            processedTex = composed;
                        }
                    }

                    // PNG保存
                    var savedTexture = SaveTextureAsPng(mainTex, processedTex);
                    Object.DestroyImmediate(processedTex);

                    if (savedTexture != null)
                    {
                        processedMaterials[matId] = savedTexture;
                    }
                }
            }

            // テクスチャをマテリアルに適用
            if (applyTextureToMaterial)
            {
                ApplyTexturesToMaterials(component, processedMaterials);
            }

            // マテリアル設定統一
            if (unifyMaterialSettings)
            {
                UnifyMaterialSettings(component);
            }

            // コンポーネントを無効化
            component.isEnabled = false;

            // メッシュ統合設定を破棄（テクスチャ保存のみ実行した扱いにする）
            if (wasMeshMergeEnabled)
            {
                component.enableMeshMerge = false;
            }

            EditorUtility.SetDirty(component);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CHM] 色合わせ適用完了: {processedMaterials.Count}個のテクスチャを出力しました");
            }
            finally
            {
                strandRef?.Dispose();
                pixelCache.Dispose();
            }
        }

        /// <summary>
        /// 基準色（ソース色）を決定。最初のRendererの_MainTexから色を抽出
        /// </summary>
        internal static Color DetermineSourceColor(ChimeraHairMaster component)
        {
            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null && mat.HasProperty("_MainTex"))
                    {
                        var tex = mat.GetTexture("_MainTex") as Texture2D;
                        if (tex != null)
                        {
                            return ColorProcessor.ExtractDominantColor(tex);
                        }
                    }
                }
            }
            return Color.white;
        }

        /// <summary>
        /// Renderer単位の明度オフセットを取得
        /// </summary>
        internal static float GetRendererBrightnessOffset(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBrightnessAdjustments == null) return 0f;
            foreach (var adj in component.rendererBrightnessAdjustments)
            {
                if (adj.rendererIndex == rendererIndex)
                    return adj.brightnessOffset;
            }
            return 0f;
        }

        /// <summary>
        /// Renderer単位のブラー／シャープ強度を取得
        /// </summary>
        internal static float GetRendererBlurSharp(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBlurSharpAdjustments == null) return 0f;
            foreach (var adj in component.rendererBlurSharpAdjustments)
            {
                if (adj.rendererIndex == rendererIndex)
                    return adj.blurSharp;
            }
            return 0f;
        }

        /// <summary>
        /// 同一マテリアルを複数Rendererが共有し、かつパーツごとに異なる設定
        /// （明度／ブラー／マスク）が付いている競合を検知して警告する。
        /// 手動のApply/Prefab出力はマテリアルInstanceID単位で先勝ち処理するため、
        /// この状況では最初のパーツの設定だけが全パーツに適用されてしまう
        /// （NDMFビルドはスロット複製で個別に正しく適用されるため対象外）。
        /// </summary>
        internal static void WarnSharedMaterialConflicts(ChimeraHairMaster component)
        {
            if (component == null || component.targetRenderers == null) return;

            // マテリアル → そのマテリアルに対して現れた「パーツ別処理シグネチャ」の集合
            var signatures = new Dictionary<Material, HashSet<string>>();
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                float brightness = GetRendererBrightnessOffset(component, r);
                float blur = GetRendererBlurSharp(component, r);
                var materials = renderer.sharedMaterials;
                for (int s = 0; s < materials.Length; s++)
                {
                    var mat = materials[s];
                    if (mat == null || !component.IsSubmeshIncluded(r, s)) continue;

                    bool hasMask = component.GetColorMask(r, s) != null;
                    string sig = $"{brightness:F3}|{blur:F3}|{(hasMask ? 1 : 0)}";
                    if (!signatures.TryGetValue(mat, out var set))
                    {
                        set = new HashSet<string>();
                        signatures[mat] = set;
                    }
                    set.Add(sig);
                }
            }

            foreach (var kv in signatures)
            {
                if (kv.Value.Count > 1)
                {
                    Debug.LogWarning(
                        $"[CHM] マテリアル '{kv.Key.name}' が複数パーツで共有され、パーツごとに異なる設定" +
                        "（明度／ブラー／マスク）が付いています。手動のApply/Prefab出力では最初のパーツの" +
                        "設定だけが使われ、他パーツには反映されません（NDMFビルド時は個別に正しく適用されます）。" +
                        "パーツごとに別々の結果にしたい場合は、対象マテリアルを複製して分けてください。");
                }
            }
        }

        /// <summary>
        /// 色変換済みテクスチャをPNG保存し、Import設定を元テクスチャからコピー
        /// </summary>
        /// <param name="originalTex">元テクスチャ（パス・設定の参照元）</param>
        /// <param name="processedTex">色変換済みテクスチャ（メモリ上）</param>
        /// <returns>保存されImportされたTexture2Dアセット、失敗時null</returns>
        private static Texture2D? SaveTextureAsPng(Texture2D originalTex, Texture2D processedTex)
        {
            // 元テクスチャのアセットパスを取得
            string originalPath = AssetDatabase.GetAssetPath(originalTex);
            if (string.IsNullOrEmpty(originalPath))
            {
                Debug.LogWarning($"[CHM] テクスチャ '{originalTex.name}' のアセットパスが取得できません");
                return null;
            }

            string directory = Path.GetDirectoryName(originalPath)!;
            string textureName = Path.GetFileNameWithoutExtension(originalPath);

            // 出力ファイル名を決定
            string outputPath = Path.Combine(directory, $"{textureName}_CHM.png");

            // processedTex は既に非圧縮RGBA32（各段 compressResult:false）。
            // DecompressTexture は非圧縮ならパススルー。圧縮は保存後の再インポートでインポーターが1回だけ行う。
            var uncompressed = DecompressTexture(processedTex);
            byte[] pngData = uncompressed.EncodeToPNG();
            Object.DestroyImmediate(uncompressed);

            if (pngData == null)
            {
                Debug.LogError($"[CHM] テクスチャ '{textureName}' のPNGエンコードに失敗しました");
                return null;
            }

            // ファイル書き込み
            File.WriteAllBytes(outputPath, pngData);

            // Unityにアセットを認識させる
            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);

            // Import設定を元テクスチャからコピー
            CopyTextureImportSettings(originalPath, outputPath);

            // 保存されたテクスチャを読み込んで返す
            var savedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
            Debug.Log($"[CHM] テクスチャ出力: {outputPath}");
            return savedTexture;
        }

        /// <summary>
        /// TextureImporter設定を元テクスチャからコピー
        /// </summary>
        internal static void CopyTextureImportSettings(string sourcePath, string destPath)
        {
            var sourceImporter = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            var destImporter = AssetImporter.GetAtPath(destPath) as TextureImporter;

            if (sourceImporter == null || destImporter == null) return;

            // 主要なImport設定をコピー
            destImporter.textureType = sourceImporter.textureType;
            destImporter.sRGBTexture = sourceImporter.sRGBTexture;
            destImporter.alphaSource = sourceImporter.alphaSource;
            destImporter.alphaIsTransparency = sourceImporter.alphaIsTransparency;
            destImporter.npotScale = sourceImporter.npotScale;
            destImporter.isReadable = sourceImporter.isReadable;
            destImporter.streamingMipmaps = sourceImporter.streamingMipmaps;
            destImporter.mipmapEnabled = sourceImporter.mipmapEnabled;
            destImporter.wrapMode = sourceImporter.wrapMode;
            destImporter.filterMode = sourceImporter.filterMode;
            destImporter.maxTextureSize = sourceImporter.maxTextureSize;
            destImporter.textureCompression = sourceImporter.textureCompression;
            destImporter.crunchedCompression = sourceImporter.crunchedCompression;
            destImporter.compressionQuality = sourceImporter.compressionQuality;

            // プラットフォーム別設定をコピー
            foreach (var platform in new[] { "Standalone", "Android", "iOS" })
            {
                var sourceSettings = sourceImporter.GetPlatformTextureSettings(platform);
                if (sourceSettings.overridden)
                {
                    destImporter.SetPlatformTextureSettings(sourceSettings);
                }
            }

            destImporter.SaveAndReimport();
        }

        /// <summary>
        /// 生成テクスチャを元マテリアルの_MainTexにセット
        /// </summary>
        private static void ApplyTexturesToMaterials(
            ChimeraHairMaster component,
            Dictionary<int, Texture2D> processedMaterials)
        {
            foreach (var renderer in component.targetRenderers)
            {
                if (renderer == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    if (processedMaterials.TryGetValue(mat.GetInstanceID(), out var newTex))
                    {
                        mat.SetTexture("_MainTex", newTex);
                        EditorUtility.SetDirty(mat);
                    }
                }
            }
        }

        /// <summary>
        /// previewMaterialの数値設定を元マテリアルに統一
        /// </summary>
        private static void UnifyMaterialSettings(ChimeraHairMaster component)
        {
            // ソースマテリアルを決定
            Material? sourceMaterial = component.previewMaterial ?? component.baseMaterial;
            if (sourceMaterial == null)
            {
                Debug.LogWarning("[CHM] マテリアル設定統一: ソースマテリアルが見つかりません");
                return;
            }

            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;

                for (int s = 0; s < materials.Length; s++)
                {
                    var mat = materials[s];
                    if (mat == null || !component.IsSubmeshIncluded(r, s)) continue;

                    // 輪郭線はシェーダ切替（Outline版の有無）で表現されるため、数値コピー前にシェーダを揃える
                    NDMF.TextureAtlasPass.SyncOutlineVariant(sourceMaterial, mat);
                    // 数値パラメータを統一（2nd/3rd/発光は除外、MatCapはunifyMatCap設定に従う）
                    NDMF.TextureAtlasPass.ApplyShaderSettings(
                        sourceMaterial, mat,
                        excludeOverlayAndEmission: true,
                        excludeMatCap: !component.unifyMatCap);

                    // unifyMatCap=trueの場合、MatCapテクスチャもコピー
                    if (component.unifyMatCap)
                    {
                        CopyMatCapTextures(sourceMaterial, mat);
                    }

                    EditorUtility.SetDirty(mat);
                }
            }

            Debug.Log("[CHM] マテリアル設定統一完了");
        }

        /// <summary>
        /// MatCapテクスチャをコピー
        /// </summary>
        internal static void CopyMatCapTextures(Material source, Material dest)
        {
            string[] matCapProps = new[]
            {
                "_MatCapTex", "_MatCapBlendMask", "_MatCapBumpMap",
                "_MatCap2ndTex", "_MatCap2ndBlendMask", "_MatCap2ndBumpMap"
            };

            foreach (var prop in matCapProps)
            {
                if (source.HasProperty(prop) && dest.HasProperty(prop))
                {
                    dest.SetTexture(prop, source.GetTexture(prop));
                }
            }
        }

        /// <summary>
        /// 圧縮テクスチャをRenderTexture経由で非圧縮RGBA32に展開する
        /// EncodeToPNG()は圧縮フォーマット(DXT5, BC7等)をサポートしないため必要
        /// </summary>
        internal static Texture2D DecompressTexture(Texture2D source)
        {
            RenderTexture tmp = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);

            Graphics.Blit(source, tmp);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tmp;

            var result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            result.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return result;
        }
    }
}
