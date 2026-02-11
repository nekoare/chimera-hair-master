# 色合わせ適用・テクスチャ出力機能 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** メッシュ統合無効・色合わせ有効時に、色変換済み_MainTexをPNG出力し、元マテリアルへのテクスチャ適用・設定統一を行う機能を追加する

**Architecture:** 新規 `ColorApplier` ユーティリティクラスに処理ロジックを集約し、既存の `ChimeraHairMasterEditor` のInspector UIからボタンで呼び出す。色変換は既存の `ColorProcessor.ProcessTexture()` を直接利用し、マテリアル設定統一は `TextureAtlasPass.ApplyShaderSettings()` を再利用する。

**Tech Stack:** Unity 2022.3.22f1, C#, lilToon shader, NDMF

---

## Task 1: ColorApplier.cs 新規作成

**Files:**
- Create: `Editor/Processing/ColorApplier.cs`

**概要:** 色合わせ適用のコアロジック。テクスチャ生成・保存、マテリアル適用、設定統一を一つの `Apply()` メソッドで提供する。

**Step 1: ファイル作成**

```csharp
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
            if (component.enableMeshMerge)
            {
                Debug.LogWarning("[CHM] メッシュ統合が有効な場合はこの機能を使用できません");
                return;
            }
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
            {
                Debug.LogWarning("[CHM] 対象Rendererが設定されていません");
                return;
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

            // 輝度統一（必要時）
            Dictionary<Texture2D, Texture2D>? brightnessAdjustedCache = null;
            if (settings.Mode == ColorTransformMode.HueShift &&
                settings.BrightnessUnifyMode != BrightnessUnifyMode.Off)
            {
                brightnessAdjustedCache = BuildBrightnessAdjustedCache(component, settings.BrightnessUnifyMode);
            }

            // 処理済みマテリアル → 生成テクスチャのマッピング（重複処理防止）
            var processedMaterials = new Dictionary<int, Texture2D>(); // materialInstanceID → savedTexture

            // 各Rendererを処理
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                float brightnessOffset = GetRendererBrightnessOffset(component, r);
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

                    // 色変換実行
                    Texture2D sourceTexture = mainTex;
                    if (brightnessAdjustedCache != null &&
                        brightnessAdjustedCache.TryGetValue(mainTex, out var adjusted))
                    {
                        sourceTexture = adjusted;
                    }

                    var processedTex = ColorProcessor.ProcessTexture(sourceTexture, settings);
                    if (processedTex == null) continue;

                    // 明度オフセット適用
                    if (Mathf.Abs(brightnessOffset) > 0.001f)
                    {
                        var offsetApplied = ColorProcessor.ApplyBrightnessOffset(processedTex, brightnessOffset);
                        if (offsetApplied != null)
                        {
                            Object.DestroyImmediate(processedTex);
                            processedTex = offsetApplied;
                        }
                    }

                    // PNG保存
                    var savedTexture = SaveTextureAsPng(mainTex, processedTex, component, r);
                    Object.DestroyImmediate(processedTex);

                    if (savedTexture != null)
                    {
                        processedMaterials[matId] = savedTexture;
                    }
                }
            }

            // brightnessAdjustedCacheの一時テクスチャを破棄
            if (brightnessAdjustedCache != null)
            {
                foreach (var kvp in brightnessAdjustedCache)
                {
                    if (kvp.Value != null) Object.DestroyImmediate(kvp.Value);
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
            EditorUtility.SetDirty(component);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CHM] 色合わせ適用完了: {processedMaterials.Count}個のテクスチャを出力しました");
        }

        /// <summary>
        /// 基準色（ソース色）を決定。最初のRendererの_MainTexから色を抽出
        /// </summary>
        private static Color DetermineSourceColor(ChimeraHairMaster component)
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
        private static float GetRendererBrightnessOffset(ChimeraHairMaster component, int rendererIndex)
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
        /// 輝度統一キャッシュを構築
        /// </summary>
        private static Dictionary<Texture2D, Texture2D> BuildBrightnessAdjustedCache(
            ChimeraHairMaster component, BrightnessUnifyMode mode)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            var brightnessMap = new Dictionary<Texture2D, float>();

            var includedSubmeshes = component.GetIncludedSubmeshes();

            // _MainTexのみ対象
            foreach (var (rendererIndex, submeshIndex) in includedSubmeshes)
            {
                if (rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                if (submeshIndex >= materials.Length) continue;
                var mat = materials[submeshIndex];
                if (mat == null || !mat.HasProperty("_MainTex")) continue;

                var tex = mat.GetTexture("_MainTex") as Texture2D;
                if (tex == null || brightnessMap.ContainsKey(tex)) continue;

                brightnessMap[tex] = ColorProcessor.CalculateAverageBrightness(tex);
            }

            if (brightnessMap.Count == 0) return result;

            float targetBrightness = mode == BrightnessUnifyMode.ToBrightest
                ? float.MinValue : float.MaxValue;

            foreach (var v in brightnessMap.Values)
            {
                if (mode == BrightnessUnifyMode.ToBrightest)
                    targetBrightness = Mathf.Max(targetBrightness, v);
                else
                    targetBrightness = Mathf.Min(targetBrightness, v);
            }

            foreach (var kvp in brightnessMap)
            {
                result[kvp.Key] = ColorProcessor.AdjustBrightness(kvp.Key, targetBrightness);
            }

            return result;
        }

        /// <summary>
        /// 色変換済みテクスチャをPNG保存し、Import設定を元テクスチャからコピー
        /// </summary>
        /// <param name="originalTex">元テクスチャ（パス・設定の参照元）</param>
        /// <param name="processedTex">色変換済みテクスチャ（メモリ上）</param>
        /// <param name="component">コンポーネント（同名テクスチャ判別用）</param>
        /// <param name="rendererIndex">レンダラーインデックス（同名テクスチャ判別用）</param>
        /// <returns>保存されImportされたTexture2Dアセット、失敗時null</returns>
        private static Texture2D? SaveTextureAsPng(
            Texture2D originalTex,
            Texture2D processedTex,
            ChimeraHairMaster component,
            int rendererIndex)
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
            string outputName = $"{textureName}_CHM.png";
            string outputPath = Path.Combine(directory, outputName);

            // 同名ファイルが既に存在し、別の元テクスチャから生成されたものでないかチェック
            // （同じ元テクスチャなら上書きOK）
            if (File.Exists(outputPath))
            {
                // 上書き（同一コンポーネントからの再実行を想定）
            }

            // 読み取り可能なテクスチャを取得してエンコード
            var readable = ColorProcessor.GetReadableTexture(processedTex);
            byte[] pngData = readable.EncodeToPNG();
            if (readable != processedTex) Object.DestroyImmediate(readable);

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
        private static void CopyTextureImportSettings(string sourcePath, string destPath)
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
        private static void CopyMatCapTextures(Material source, Material dest)
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
    }
}
```

**Step 2: コンパイル確認**

Unityでコンパイルエラーがないことを確認する。

---

## Task 2: Inspector UI追加（ChimeraHairMasterEditor.cs）

**Files:**
- Modify: `Editor/Inspector/ChimeraHairMasterEditor.cs`

**概要:** `DrawActionButtons()` メソッドに、色合わせ適用UIを追加する。表示条件は `enableMeshMerge == false && enableColorTransform == true`。

**Step 1: EditorPrefs用フィールドをUI Stateリージョンに追加**

`#region UI State` 内（55行目付近）に以下を追加:

```csharp
// 色合わせ適用オプション
private const string PREF_APPLY_TEXTURE = "CHM_ApplyTexture";
private const string PREF_UNIFY_SETTINGS = "CHM_UnifySettings";
```

**Step 2: DrawActionButtons()メソッドにUI追加**

`DrawActionButtons()` メソッド内、プレビューボタンセクションの前（1156行目 `EditorGUILayout.Space(5);` の後）に色合わせ適用セクションを追加:

```csharp
// 色合わせ適用セクション（メッシュ統合無効 & 色合わせ有効の場合のみ）
if (!component.enableMeshMerge && component.enableColorTransform)
{
    DrawColorApplySection(component);
    EditorGUILayout.Space(5);
}
```

**Step 3: DrawColorApplySectionメソッド新規作成**

`DrawActionButtons()` メソッドの後に追加:

```csharp
/// <summary>
/// 色合わせ適用セクションを描画
/// </summary>
private void DrawColorApplySection(ChimeraHairMaster component)
{
    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

    EditorGUILayout.LabelField("色合わせの適用", EditorStyles.boldLabel);

    // チェックボックス
    bool applyTexture = EditorPrefs.GetBool(PREF_APPLY_TEXTURE, true);
    bool unifySettings = EditorPrefs.GetBool(PREF_UNIFY_SETTINGS, true);

    EditorGUI.BeginChangeCheck();
    applyTexture = EditorGUILayout.ToggleLeft("テクスチャをマテリアルに適用", applyTexture);
    unifySettings = EditorGUILayout.ToggleLeft("マテリアル設定を統一", unifySettings);
    if (EditorGUI.EndChangeCheck())
    {
        EditorPrefs.SetBool(PREF_APPLY_TEXTURE, applyTexture);
        EditorPrefs.SetBool(PREF_UNIFY_SETTINGS, unifySettings);
    }

    if (showHelp)
    {
        EditorGUILayout.HelpBox(
            "色合わせしたテクスチャをPNGとして出力します。\n" +
            "「テクスチャをマテリアルに適用」: 生成テクスチャを元マテリアルの_MainTexにセットします。\n" +
            "「マテリアル設定を統一」: プレビューマテリアルの数値設定を元マテリアルに上書きします。\n" +
            "適用後、コンポーネントは自動的に無効化されます。",
            MessageType.Info
        );
    }

    // 適用ボタン
    bool canApply = CanGeneratePreview(component);
    GUI.enabled = canApply;

    if (GUILayout.Button("色合わせを適用", GUILayout.Height(28)))
    {
        Processing.ColorApplier.Apply(component, applyTexture, unifySettings);
        serializedObject.Update();
    }

    GUI.enabled = true;

    if (!canApply)
    {
        EditorGUILayout.HelpBox(
            "適用するには、対象Rendererと基準マテリアルを設定してください。",
            MessageType.Warning
        );
    }

    EditorGUILayout.EndVertical();
}
```

**Step 4: コンパイル確認**

Unityでコンパイルエラーがないことを確認する。

---

## Task 3: 動作確認

**確認項目:**

1. **表示条件**
   - `enableMeshMerge=false` & `enableColorTransform=true` → ボタン表示される
   - `enableMeshMerge=true` → ボタン表示されない
   - `enableColorTransform=false` → ボタン表示されない

2. **テクスチャ出力**
   - ボタン押下で `{元テクスチャ名}_CHM.png` が元テクスチャと同じフォルダに生成される
   - Import設定（maxTextureSize, compression等）が元テクスチャと一致する

3. **テクスチャ適用チェックボックス**
   - ON: 元マテリアルの_MainTexが生成テクスチャに差し替わる
   - OFF: 元マテリアルの_MainTexは変更されない

4. **マテリアル設定統一チェックボックス**
   - ON: 元マテリアルの数値パラメータがpreviewMaterialの値に統一される
   - unifyMatCap=true時: MatCapテクスチャも統一される
   - OFF: 元マテリアルの設定は変更されない

5. **コンポーネント無効化**
   - 適用後に `isEnabled` が `false` になる

6. **Undo**
   - Ctrl+Zでマテリアルの変更とコンポーネント状態が元に戻る

---

## 破綻シナリオ

この設計が破綻するケース:
- **_MainTex以外のスロットも出力したい場合**: `SaveTextureAsPng` のループを `colorChangeTargets` 全体に広げる必要がある。現状は _MainTex ハードコードなので影響範囲は `Apply()` メソッド内に閉じる。
- **ビルド時に自動適用したい場合**: 現在はEditor操作（ボタン押下）でのみ動作。NDMFパスとしての統合が必要になる。
- **同一テクスチャ・異なるbrightness offsetで複数Rendererが使用している場合**: 現在は同一マテリアルを共有している場合のみ重複スキップ。異なるマテリアルが同じテクスチャを参照しbrightness offsetが異なる場合、後から処理されたものが上書きする。ただし実運用上この状況は稀。
