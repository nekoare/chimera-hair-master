using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ChimeraHairMaster.Editor.Processing;
using UnityEditor;
using UnityEngine;
using ChimeraHairMaster.Editor.Localization;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// ChimeraHairMasterコンポーネントのカスタムインスペクター
    /// </summary>
    [CustomEditor(typeof(ChimeraHairMaster))]
    public class ChimeraHairMasterEditor : UnityEditor.Editor
    {
        #region Serialized Properties

        private SerializedProperty isEnabledProp;
        private SerializedProperty enableMeshMergeProp;
        private SerializedProperty targetRenderersProp;
        private SerializedProperty enableColorTransformProp;
        private SerializedProperty targetColorProp;
        private SerializedProperty colorTransformModeProp;
        private SerializedProperty gradientCurveProp;
        private SerializedProperty saturationPreserveProp;
        private SerializedProperty valuePreserveProp;
        private SerializedProperty hueShiftAlgorithmProp;
        private SerializedProperty oklabHueRetainProp;
        private SerializedProperty oklabLDarkEndRatioProp;
        private SerializedProperty rgbDeltaIntensityProp;
        private SerializedProperty rgbDeltaSoftClipZoneProp;
        private SerializedProperty textureResolutionProp;
        private SerializedProperty preserveAtlasAlphaProp;
        private SerializedProperty normalAtlasResolutionProp;
        private SerializedProperty aoAtlasResolutionProp;
        private SerializedProperty baseMaterialProp;
        private SerializedProperty previewMaterialProp;
        private SerializedProperty meshMergeModeProp;
        private SerializedProperty inheritProbeAnchorProp;
        private SerializedProperty probeAnchorProp;
        private SerializedProperty inheritBoundsProp;
        private SerializedProperty rootBoneProp;
        private SerializedProperty boundsProp;
        private SerializedProperty previewEnabledProp;
        private SerializedProperty previewResolutionProp;
        private SerializedProperty previewMaterialHashProp;
        private SerializedProperty rendererBrightnessAdjustmentsProp;
        private SerializedProperty unifyMatCapProp;

        #endregion

        #region UI State

        // ？ヘルプ: 行末の「？」ボタンで説明を個別に開閉する（同時に開くのは1つ）
        private string activeHelpKey;
        private bool showBasicSettings = false;
        private bool showColorSettings = true;
        private bool showTextureSettings = false;
        private bool showMeshCutSettings = false;
        private bool showMeshSettings = false;
        private bool showBrightnessAdjustment = false;
        private bool showBlurSharpAdjustment = false;
        private bool showStrandPattern = false;
        private bool showColorMaskSettings = false;
        private bool showPhysBoneList = false;
        private Dictionary<int, bool> physBoneFoldouts = new Dictionary<int, bool>();

        // 色合わせ適用オプション
        private const string PREF_APPLY_TEXTURE = "CHM_ApplyTexture";
        private const string PREF_UNIFY_SETTINGS = "CHM_UnifySettings";
        private const string PREF_APPLY_DEFORMATION = "CHM_ApplyDeformation";
        private const string PREF_PREFAB_APPLY_DEFORMATION = "CHM_PrefabApplyDeformation";

        // メッシュ変形UI
        private MeshDeformationInspectorUI meshDeformationUI;

        // Renderer click highlight state
        private static SkinnedMeshRenderer highlightRenderer;
        private static double highlightExpiry = 0;
        private const double HIGHLIGHT_DURATION = 2.0;

        // テクスチャ色ピッカー用
        private bool showTextureColorPicker = false;
        private int selectedColorPickerRendererIndex = 0;
        private Texture2D cachedTexturePreview;
        private SkinnedMeshRenderer cachedTextureRenderer;
        private Color[] extractedColors;

        // Gradient用テクスチャ色ピッカー
        private bool showGradientTextureColorPicker = false;
        private int selectedGradientColorPickerRendererIndex = 0;
        private Texture2D cachedGradientTexturePreview;
        private SkinnedMeshRenderer cachedGradientTextureRenderer;
        private Color[] extractedGradientColors;
        private float gradientColorKeyPosition = 0.5f; // 追加するキーの位置

        // マテリアルエディタ（Inspector内埋め込み用）
        private MaterialEditor materialEditor;
        private Material cachedPreviewMaterial;

        #endregion

        private void OnEnable()
        {
            if (target == null) return;

            isEnabledProp = serializedObject.FindProperty("isEnabled");
            enableMeshMergeProp = serializedObject.FindProperty("enableMeshMerge");
            targetRenderersProp = serializedObject.FindProperty("targetRenderers");
            enableColorTransformProp = serializedObject.FindProperty("enableColorTransform");
            targetColorProp = serializedObject.FindProperty("targetColor");
            colorTransformModeProp = serializedObject.FindProperty("colorTransformMode");
            gradientCurveProp = serializedObject.FindProperty("gradientCurve");
            saturationPreserveProp = serializedObject.FindProperty("saturationPreserve");
            valuePreserveProp = serializedObject.FindProperty("valuePreserve");
            hueShiftAlgorithmProp = serializedObject.FindProperty("hueShiftAlgorithm");
            oklabHueRetainProp = serializedObject.FindProperty("oklabHueRetain");
            oklabLDarkEndRatioProp = serializedObject.FindProperty("oklabLDarkEndRatio");
            rgbDeltaIntensityProp = serializedObject.FindProperty("rgbDeltaIntensity");
            rgbDeltaSoftClipZoneProp = serializedObject.FindProperty("rgbDeltaSoftClipZone");
            textureResolutionProp = serializedObject.FindProperty("textureResolution");
            preserveAtlasAlphaProp = serializedObject.FindProperty("preserveAtlasAlpha");
            normalAtlasResolutionProp = serializedObject.FindProperty("normalAtlasResolution");
            aoAtlasResolutionProp = serializedObject.FindProperty("aoAtlasResolution");
            baseMaterialProp = serializedObject.FindProperty("baseMaterial");
            previewMaterialProp = serializedObject.FindProperty("previewMaterial");
            meshMergeModeProp = serializedObject.FindProperty("meshMergeMode");
            inheritProbeAnchorProp = serializedObject.FindProperty("inheritProbeAnchor");
            probeAnchorProp = serializedObject.FindProperty("probeAnchor");
            inheritBoundsProp = serializedObject.FindProperty("inheritBounds");
            rootBoneProp = serializedObject.FindProperty("rootBone");
            boundsProp = serializedObject.FindProperty("bounds");
            previewEnabledProp = serializedObject.FindProperty("previewEnabled");
            previewResolutionProp = serializedObject.FindProperty("previewResolution");
            previewMaterialHashProp = serializedObject.FindProperty("previewMaterialHash");
            rendererBrightnessAdjustmentsProp = serializedObject.FindProperty("rendererBrightnessAdjustments");
            unifyMatCapProp = serializedObject.FindProperty("unifyMatCap");

            // メッシュ変形UI初期化
            meshDeformationUI = new MeshDeformationInspectorUI();

            // マテリアル変更監視を開始
            EditorApplication.update += OnEditorUpdate;
            // SceneViewでのハイライト描画を登録
            SceneView.duringSceneGui += OnSceneGUI;
            // 重複登録を防ぐため、先に解除してから登録
            SceneView.duringSceneGui -= OnMeshDeformSceneGUI;
            SceneView.duringSceneGui += OnMeshDeformSceneGUI;
            
            // ドメインリロード後に lastBaseMaterial が null にリセットされるため、
            // OnEnable で現在の baseMaterial を記録しておく。
            // これにより DrawBasicSettings での偽陽性な変更検知を防止する。
            var component = target as ChimeraHairMaster;
            lastBaseMaterial = component?.baseMaterial;

            // Renderer 構成スナップショットを初期化（index 再マップの差分起点）
            RendererIndexSynchronizer.InitializeSnapshot(component);

            if (component != null)
            {
                // 既存のインメモリ previewMaterial をアセットに変換（マイグレーション）
                // lilToon のシェーダー再インポート時にインメモリマテリアルはプロパティが失われるため
                if (component.previewMaterial != null && !AssetDatabase.Contains(component.previewMaterial))
                {
                    string path = GetPreviewMaterialAssetPath(component);
                    EnsureDirectoryExists(path);
                    path = AssetDatabase.GenerateUniqueAssetPath(path);
                    AssetDatabase.CreateAsset(component.previewMaterial, path);
                    EditorUtility.SetDirty(component);
                }

                // 初期化時にpreviewMaterialが未生成で、baseMaterialがある場合は自動生成
                if (component.previewMaterial == null && component.baseMaterial != null)
                {
                    CreatePreviewMaterial();
                    serializedObject.Update();
                }
            }
        }

        private void OnDisable()
        {
            // マテリアル変更監視を停止
            EditorApplication.update -= OnEditorUpdate;
            // SceneViewのハイライト描画を解除
            SceneView.duringSceneGui -= OnSceneGUI;

            // メッシュ変形のScene GUIは編集中なら解除しない
            // （Inspectorが閉じても編集セッションを維持するため）
            var activeEditor = MeshDeformationInspectorUI.ActiveSceneEditor;
            if (activeEditor == null || activeEditor.CurrentMode == Deformation.MeshDeformationSceneEditor.EditMode.Off)
            {
                SceneView.duringSceneGui -= OnMeshDeformSceneGUI;
            }

            // マテリアルエディタを破棄
            CleanupMaterialEditor();

            // メッシュ変形エディタをクリーンアップ
            meshDeformationUI?.Cleanup();
        }
        
        private void CleanupMaterialEditor()
        {
            if (materialEditor != null)
            {
                DestroyImmediate(materialEditor);
                materialEditor = null;
            }
            cachedPreviewMaterial = null;
        }

        // ハッシュ計算（マテリアル全プロパティ走査）を毎 tick 実行しないためのスロットリング
        private double _nextMaterialHashCheckTime;
        private const double MaterialHashCheckInterval = 0.3;

        /// <summary>
        /// エディタ更新時にマテリアルハッシュと色合わせ無視マスクの内容ハッシュをチェック
        /// </summary>
        private void OnEditorUpdate()
        {
            if (target == null) return;

            var component = target as ChimeraHairMaster;
            if (component == null) return;
            if (!component.previewEnabled) return;

            // ComputeHash は SerializedObject 生成 + 全プロパティ走査で重いためスロットリング
            if (EditorApplication.timeSinceStartup < _nextMaterialHashCheckTime) return;
            _nextMaterialHashCheckTime = EditorApplication.timeSinceStartup + MaterialHashCheckInterval;

            bool changed = false;

            // マテリアルのハッシュを計算
            if (component.previewMaterial != null)
            {
                int currentHash = MaterialHasher.ComputeHash(component.previewMaterial);
                if (component.previewMaterialHash != currentHash)
                {
                    component.previewMaterialHash = currentHash;
                    changed = true;
                }
            }

            // 色合わせ無視マスクの内容ハッシュを計算
            // （同じPNGアセットへの上書き保存はコンポーネントの変更イベントを発火しないため、
            //   ここで検知してNDMFプレビューの再評価を起こす）
            int maskHash = ColorMaskApplier.ComputeMaskContentsHash(component);
            if (component.colorMaskContentsHash != maskHash)
            {
                component.colorMaskContentsHash = maskHash;
                changed = true;
            }

            // ハッシュが変わっていたらコンポーネントを更新
            if (changed)
            {
                // ハッシュは派生キャッシュのため Undo 対象にしない
                // （Undo.RecordObject すると、マテリアル編集のたびにユーザーの Undo 履歴が
                // 「ハッシュ更新の取り消し」で埋まる。Undo でマテリアル側が戻れば
                // 次のチェックでハッシュも自動的に追従する）
                EditorUtility.SetDirty(component);

                // シーンビューを強制的に再描画
                UnityEditor.SceneView.RepaintAll();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // 変形編集中に他のパラメータが変更されたら編集を終了する
            EditorGUI.BeginChangeCheck();

            DrawComponentHeader();
            EditorGUILayout.Space(5);

            DrawBasicSettings();
            EditorGUILayout.Space(5);

            // メッシュ統合ON時のみ、テクスチャ設定を基本設定と同列に出す
            // （OFF時は中身が色変更対象のみのため、従来どおり基本設定内に表示）
            if (enableMeshMergeProp.boolValue)
            {
                DrawTextureSettings();
                EditorGUILayout.Space(5);
            }

            DrawColorSettings();
            EditorGUILayout.Space(10);

            if (enableMeshMergeProp.boolValue)
            {
                DrawMeshSettings();
                EditorGUILayout.Space(10);
            }

            if (EditorGUI.EndChangeCheck())
            {
                var editor = MeshDeformationInspectorUI.ActiveSceneEditor;
                if (editor != null && editor.CurrentMode != Deformation.MeshDeformationSceneEditor.EditMode.Off)
                {
                    editor.EndEdit();
                    UnityEditor.SceneView.RepaintAll();
                }
            }

            // メッシュ変形セクション
            DrawMeshDeformationSection();
            EditorGUILayout.Space(10);

            DrawActionButtons();
            EditorGUILayout.Space(10);

            DrawMaterialEditorSection();

            serializedObject.ApplyModifiedProperties();

            // Renderer リストの削除・並べ替えに rendererIndex ベースの各設定を追従させる
            // （変更が無ければ何もしない）
            RendererIndexSynchronizer.SyncAfterRendererListChange(target as ChimeraHairMaster);
        }

        private void DrawComponentHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:Title"), EditorStyles.boldLabel);
                CHMLocales.DrawLanguagePicker();
            }
            EditorGUILayout.EndVertical();

            // 有効/無効トグル
            EditorGUILayout.PropertyField(isEnabledProp, new GUIContent(CHMLocales.Tr("Inspector:Enabled")));

            if (!isEnabledProp.boolValue)
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:DisabledHelp"), MessageType.Info);
            }
        }

        // ----- ？ヘルプ（クリックで説明を開閉） -----
        // 以前は先頭の「ヘルプ」トグルで全 HelpBox を一括表示していたが、
        // 気づかれにくく開くと画面が埋まるため、各行末の「？」ボタンで個別に開閉する方式にした

        /// <summary>行内（Horizontal 内）に「？」ボタンを置く。クリックでそのキーの説明の開閉を切り替える。</summary>
        private void DrawHelpMark(string helpKey)
        {
            bool active = activeHelpKey == helpKey;
            if (GUILayout.Button(active ? "✕" : "?", EditorStyles.miniButton, GUILayout.Width(20f)))
            {
                activeHelpKey = active ? null : helpKey;
                GUI.FocusControl(null);
            }
        }

        /// <summary>「？」で開かれた説明を HelpBox で出す。対象行（Horizontal 終了）の直後に呼ぶ。</summary>
        private void DrawHelpBoxIfOpen(string helpKey, MessageType messageType = MessageType.Info)
        {
            if (activeHelpKey == helpKey)
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr(helpKey), messageType);
            }
        }

        /// <summary>
        /// テクスチャ設定（メッシュ統合ON時のみ呼ばれる、基本設定と同列のトップレベルセクション。
        /// 既定は閉じる）。統合OFF時は DrawBasicSettings 内に色変更対象のみ表示される
        /// </summary>
        private void DrawTextureSettings()
        {
            var component = target as ChimeraHairMaster;

            showTextureSettings = EditorGUILayout.Foldout(showTextureSettings, CHMLocales.Tr("Inspector:TextureSettings"), true, EditorStyles.foldoutHeader);
            if (!showTextureSettings) return;

            EditorGUI.indentLevel++;

            if (enableMeshMergeProp.boolValue)
            {
                // 解像度（メッシュ統合時のみ）
                EditorGUILayout.PropertyField(textureResolutionProp, new GUIContent(CHMLocales.Tr("Inspector:TextureResolution")));

                // ノーマル/AOアトラスの解像度（メインアトラス比）
                EditorGUILayout.PropertyField(normalAtlasResolutionProp, new GUIContent(CHMLocales.Tr("Inspector:NormalAtlasResolution")));
                EditorGUILayout.PropertyField(aoAtlasResolutionProp, new GUIContent(CHMLocales.Tr("Inspector:AOAtlasResolution")));

                // アルファチャンネル保持（メッシュ統合時のみ）
                EditorGUILayout.PropertyField(preserveAtlasAlphaProp, new GUIContent(
                    CHMLocales.Tr("Inspector:PreserveAtlasAlpha"),
                    CHMLocales.Tr("Inspector:PreserveAtlasAlphaHelp")));

                // マットキャップ/エミッションのマスク生成（自動適用はしない）
                DrawAdditionalMaskSection(component);
            }

            // 色変更対象（colorChangeTargets）はUI非表示。
            // _MainTex 以外のカラーテクスチャを色合わせ/アトラス化対象に追加する上級者向けの
            // 拡張口としてデータ・処理は維持しており、必要なら Inspector の Debug モードで編集できる

            EditorGUI.indentLevel--;
        }

        private void DrawBasicSettings()
        {
            var component = target as ChimeraHairMaster;

            // 配列プロパティを含むため通常のFoldoutを使用
            showBasicSettings = EditorGUILayout.Foldout(showBasicSettings, CHMLocales.Tr("Inspector:BasicSettings"), true, EditorStyles.foldoutHeader);
            if (showBasicSettings)
            {
                EditorGUI.indentLevel++;

                // 対象Renderer一覧（読み取り専用表示）
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(targetRenderersProp, new GUIContent(CHMLocales.Tr("Inspector:TargetRenderers")), true);
                EditorGUI.EndDisabledGroup();
                DrawHelpMark("Inspector:TargetRenderersHelp");
                EditorGUILayout.EndHorizontal();
                DrawHelpBoxIfOpen("Inspector:TargetRenderersHelp");

                // PhysBone一覧
                DrawPhysBoneList(component);

                // 共有Rendererの警告
                var sharedRendererWarning = GetSharedRendererWarning(component);
                if (sharedRendererWarning != null)
                {
                    EditorGUILayout.HelpBox(sharedRendererWarning, MessageType.Warning);
                }

                // メッシュカット設定（メッシュ統合有効時のみ）
                if (enableMeshMergeProp.boolValue)
                {
                    EditorGUILayout.Space(10);
                    DrawMeshCutSettings(component);
                }

                EditorGUILayout.Space(10);

                // 基準マテリアル
                EditorGUILayout.PropertyField(baseMaterialProp, new GUIContent(CHMLocales.Tr("Inspector:BaseMaterial")));

                var currentBaseMaterial = baseMaterialProp.objectReferenceValue as Material;

                // baseMaterialが変更された場合はpreviewMaterialを再生成
                if (currentBaseMaterial != lastBaseMaterial)
                {
                    // PropertyField の変更はまだ serializedObject の保留バッファにしかなく、
                    // CreatePreviewMaterial は component.baseMaterial（実値）を読む。
                    // 先に Apply しないと旧マテリアル基準でプレビューが生成され、
                    // さらに CreatePreviewMaterial 末尾の serializedObject.Update() が
                    // 保留中の baseMaterial 変更を破棄してしまう（A→Bの変更が巻き戻る）
                    serializedObject.ApplyModifiedProperties();

                    if (currentBaseMaterial != null)
                    {
                        CreatePreviewMaterial();
                    }
                    lastBaseMaterial = currentBaseMaterial;
                }
                // baseMaterialが設定されてpreviewMaterialがない場合も自動生成
                else if (currentBaseMaterial != null && previewMaterialProp.objectReferenceValue == null)
                {
                    // CreatePreviewMaterial 内の serializedObject.Update() が
                    // 同フレームの保留中プロパティ変更を破棄しないよう先に確定する
                    serializedObject.ApplyModifiedProperties();
                    CreatePreviewMaterial();
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawMeshCutSettings(ChimeraHairMaster component)
        {
            EditorGUILayout.BeginHorizontal();
            showMeshCutSettings = EditorGUILayout.Foldout(showMeshCutSettings, CHMLocales.Tr("Inspector:MeshCutSettings"), true);
            DrawHelpMark("Inspector:MeshCutHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:MeshCutHelp");
            if (!showMeshCutSettings) return;

            EditorGUI.indentLevel++;

            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null || renderer.sharedMesh == null) continue;

                EditorGUILayout.LabelField(renderer.name, EditorStyles.boldLabel);

                EditorGUI.indentLevel++;

                int submeshCount = renderer.sharedMesh.subMeshCount;
                var materials = renderer.sharedMaterials;

                for (int s = 0; s < submeshCount; s++)
                {
                    if (!component.IsSubmeshIncluded(r, s)) continue;

                    string matName = s < materials.Length && materials[s] != null
                        ? materials[s].name
                        : string.Format(CHMLocales.Tr("Inspector:MaterialFallback"), s);

                    // 現在のマスクを取得
                    var entry = component.materialSelections.Find(
                        e => e.rendererIndex == r && e.submeshIndex == s);
                    var currentMask = entry?.meshCutMask;

                    EditorGUILayout.BeginHorizontal();

                    EditorGUI.BeginChangeCheck();
                    var newMask = (Texture2D)EditorGUILayout.ObjectField(
                        string.Format(CHMLocales.Tr("Inspector:SubmeshLabelFormat"), s, matName), currentMask, typeof(Texture2D), false);

                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(component, "Change Mesh Cut Mask");
                        if (entry != null)
                        {
                            entry.meshCutMask = newMask;
                        }
                        else
                        {
                            var newEntry = new MaterialSelectionEntry(r, s, true);
                            newEntry.meshCutMask = newMask;
                            component.materialSelections.Add(newEntry);
                        }
                        EditorUtility.SetDirty(component);
                    }

                    if (GUILayout.Button(CHMLocales.Tr("Inspector:CreateMask"), GUILayout.Width(80)))
                    {
                        int capturedR = r;
                        int capturedS = s;
                        OpenMaskToolForSubmesh(component, renderer, s, savedTex =>
                        {
                            if (savedTex == null) return;
                            Undo.RecordObject(component, "CHM Assign Mesh Cut Mask");
                            var existing = component.materialSelections.Find(
                                e => e.rendererIndex == capturedR && e.submeshIndex == capturedS);
                            if (existing != null)
                            {
                                existing.meshCutMask = savedTex;
                            }
                            else
                            {
                                var newEntry = new MaterialSelectionEntry(capturedR, capturedS, true);
                                newEntry.meshCutMask = savedTex;
                                component.materialSelections.Add(newEntry);
                            }
                            EditorUtility.SetDirty(component);
                        });
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(3);
            }

            EditorGUI.indentLevel--;
        }

        private void DrawColorSettings()
        {
            showColorSettings = EditorGUILayout.Foldout(showColorSettings, CHMLocales.Tr("Inspector:ColorSettings"), true, EditorStyles.foldoutHeader);
            if (showColorSettings)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUI.indentLevel++;

                // 色合わせの有効/無効トグル
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(enableColorTransformProp, new GUIContent(CHMLocales.Tr("Inspector:EnableColorTransform")));
                DrawHelpMark("Inspector:EnableColorTransformHelp");
                EditorGUILayout.EndHorizontal();
                DrawHelpBoxIfOpen("Inspector:EnableColorTransformHelp");

                // 色合わせが無効な場合は他の設定を表示しない
                if (!enableColorTransformProp.boolValue)
                {
                    EditorGUI.indentLevel--;
                    EditorGUILayout.EndVertical();
                    return;
                }

                EditorGUILayout.Space(5);

                // 色変換モード（表示順: Mode1=HueShift, Mode2=Gradient, Mode3=RGBDelta）
                // enum 値 (0=Gradient, 1=HueShift, 2=RGBDelta) はシリアライズ互換のため維持
                {
                    var modeNames = new[]
                    {
                        CHMLocales.Tr("ColorTransformMode:HueShift"),        // display 0 (enum 1)
                        CHMLocales.Tr("ColorTransformMode:Gradient"),        // display 1 (enum 0)
                        CHMLocales.Tr("Inspector:ColorTransformMode:RGBDelta"), // display 2 (enum 2)
                    };
                    int[] displayToEnum = { 1, 0, 2 };
                    int[] enumToDisplay = { 1, 0, 2 };
                    int currentDisplay = enumToDisplay[colorTransformModeProp.enumValueIndex];
                    EditorGUILayout.BeginHorizontal();
                    int newDisplay = EditorGUILayout.Popup(
                        CHMLocales.Tr("Inspector:ColorTransformMode"),
                        currentDisplay,
                        modeNames);
                    colorTransformModeProp.enumValueIndex = displayToEnum[newDisplay];

                    // 選択中モードの説明（？で開閉）
                    var currentMode = (ColorTransformMode)colorTransformModeProp.enumValueIndex;
                    string modeHelpKey =
                        currentMode == ColorTransformMode.Gradient ? "Inspector:GradientHelp" :
                        currentMode == ColorTransformMode.RGBDelta ? "Inspector:RgbDeltaHelp" :
                        "Inspector:HueShiftHelp";
                    DrawHelpMark(modeHelpKey);
                    EditorGUILayout.EndHorizontal();
                    DrawHelpBoxIfOpen(modeHelpKey);
                }

                ColorTransformMode transformMode = (ColorTransformMode)colorTransformModeProp.enumValueIndex;
                switch (transformMode)
                {
                    case ColorTransformMode.Gradient:
                        EditorGUILayout.PropertyField(gradientCurveProp, new GUIContent(CHMLocales.Tr("Inspector:GradientCurve")));

                        // テクスチャ色ピッカー（Gradient用）
                        DrawGradientTextureColorPicker();
                        break;

                    case ColorTransformMode.HueShift:
                        EditorGUILayout.Space(5);
                        EditorGUILayout.PropertyField(targetColorProp, new GUIContent(CHMLocales.Tr("Inspector:TargetColor")));

                        // テクスチャ色ピッカー
                        DrawTextureColorPicker();

                        EditorGUILayout.Space(5);

                        // algorithm サブオプション（HSV / Oklab）
                        {
                            var algoNames = new[]
                            {
                                CHMLocales.Tr("Inspector:HueShiftAlgorithm:HSV"),
                                CHMLocales.Tr("Inspector:HueShiftAlgorithm:Oklab")
                            };
                            EditorGUILayout.BeginHorizontal();
                            hueShiftAlgorithmProp.enumValueIndex = EditorGUILayout.Popup(
                                CHMLocales.Tr("Inspector:HueShiftAlgorithm"),
                                hueShiftAlgorithmProp.enumValueIndex,
                                algoNames);
                            DrawHelpMark("Inspector:OklabHelp");
                            EditorGUILayout.EndHorizontal();
                            DrawHelpBoxIfOpen("Inspector:OklabHelp");
                        }

                        var algorithm = (HueShiftAlgorithm)hueShiftAlgorithmProp.enumValueIndex;
                        if (algorithm == HueShiftAlgorithm.HSV)
                        {
                            EditorGUILayout.PropertyField(saturationPreserveProp, new GUIContent(CHMLocales.Tr("Inspector:SaturationPreserve")));

                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.PropertyField(valuePreserveProp, new GUIContent(CHMLocales.Tr("Inspector:ValuePreserve")));
                            if (GUILayout.Button(CHMLocales.Tr("Inspector:AutoAdjust"), GUILayout.Width(70)))
                            {
                                var component = (ChimeraHairMaster)target;
                                Undo.RecordObject(component, "Auto Adjust Value Preserve");
                                component.UpdateValuePreserveFromTargetColor();
                                serializedObject.Update();
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                        else // Oklab
                        {
                            EditorGUILayout.PropertyField(oklabHueRetainProp,
                                new GUIContent(
                                    CHMLocales.Tr("Inspector:OklabHueRetain"),
                                    CHMLocales.Tr("Inspector:OklabHueRetainTooltip")));

                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.PropertyField(oklabLDarkEndRatioProp,
                                new GUIContent(
                                    CHMLocales.Tr("Inspector:OklabLDarkEnd"),
                                    CHMLocales.Tr("Inspector:OklabLDarkEndTooltip")));
                            if (GUILayout.Button(CHMLocales.Tr("Inspector:AutoAdjust"), GUILayout.Width(70)))
                            {
                                var component = (ChimeraHairMaster)target;
                                Undo.RecordObject(component, "Auto Adjust Oklab Dark End");
                                component.UpdateOklabLDarkEndRatioFromTargetColor();
                                serializedObject.Update();
                            }
                            DrawHelpMark("Inspector:OklabLDarkEndHelp");
                            EditorGUILayout.EndHorizontal();
                            DrawHelpBoxIfOpen("Inspector:OklabLDarkEndHelp");
                        }

                        break;

                    case ColorTransformMode.RGBDelta:
                        EditorGUILayout.Space(5);
                        EditorGUILayout.PropertyField(targetColorProp, new GUIContent(CHMLocales.Tr("Inspector:TargetColor")));

                        // テクスチャ色ピッカー（HueShift と共有）
                        DrawTextureColorPicker();

                        EditorGUILayout.Space(5);
                        EditorGUILayout.PropertyField(rgbDeltaIntensityProp,
                            new GUIContent(
                                CHMLocales.Tr("Inspector:RgbDeltaIntensity"),
                                CHMLocales.Tr("Inspector:RgbDeltaIntensityTooltip")));
                        EditorGUILayout.PropertyField(rgbDeltaSoftClipZoneProp,
                            new GUIContent(
                                CHMLocales.Tr("Inspector:RgbDeltaSoftClipZone"),
                                CHMLocales.Tr("Inspector:RgbDeltaSoftClipZoneTooltip")));

                        break;
                }

                // 個別の明るさの調整（色合わせが有効な場合のみ表示）
                EditorGUILayout.Space(10);
                DrawBrightnessAdjustmentUI();

                // 塗りの細かさを調整
                EditorGUILayout.Space(10);
                DrawBlurSharpAdjustmentUI();

                // 毛束パターン統一
                EditorGUILayout.Space(10);
                DrawStrandPatternUI();

                // 色合わせ無視マスク
                EditorGUILayout.Space(10);
                DrawColorMaskUI();

                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }
        }

        /// <summary>
        /// 個別の明るさの調整UI
        /// </summary>
        private void DrawBrightnessAdjustmentUI()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            showBrightnessAdjustment = EditorGUILayout.Foldout(showBrightnessAdjustment, CHMLocales.Tr("Inspector:BrightnessAdjustment"), true);
            DrawHelpMark("Inspector:BrightnessAdjustmentHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:BrightnessAdjustmentHelp");

            if (!showBrightnessAdjustment)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.indentLevel++;

            // rendererBrightnessAdjustments リストを targetRenderers に同期
            SyncBrightnessAdjustments(component);

            // 各Rendererに対してスライダーを表示
            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null) continue;

                // 対応する調整値を取得
                float currentOffset = 0f;
                int adjustmentIndex = -1;
                for (int j = 0; j < component.rendererBrightnessAdjustments.Count; j++)
                {
                    if (component.rendererBrightnessAdjustments[j].rendererIndex == i)
                    {
                        currentOffset = component.rendererBrightnessAdjustments[j].brightnessOffset;
                        adjustmentIndex = j;
                        break;
                    }
                }

                // スライダー表示
                EditorGUILayout.BeginHorizontal();

                // Renderer名（短縮表示）
                string displayName = renderer.name;
                if (displayName.Length > 15) displayName = displayName.Substring(0, 15) + "...";

                if (GUILayout.Button(displayName, EditorStyles.label, GUILayout.Width(120)))
                {
                    HighlightRenderer(renderer);
                }

                // 明度オフセットスライダー
                EditorGUI.BeginChangeCheck();
                float newOffset = EditorGUILayout.Slider(currentOffset, -1f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Adjust Renderer Brightness");

                    if (adjustmentIndex >= 0)
                    {
                        component.rendererBrightnessAdjustments[adjustmentIndex].brightnessOffset = newOffset;
                    }
                    else
                    {
                        // 新しいエントリを追加
                        component.rendererBrightnessAdjustments.Add(new RendererBrightnessAdjustment(i) { brightnessOffset = newOffset });
                    }

                    EditorUtility.SetDirty(component);
                }

                // リセットボタン
                if (GUILayout.Button(CHMLocales.Tr("Inspector:Reset"), GUILayout.Width(50)))
                {
                    Undo.RecordObject(component, "Reset Renderer Brightness");
                    if (adjustmentIndex >= 0)
                    {
                        component.rendererBrightnessAdjustments[adjustmentIndex].brightnessOffset = 0f;
                    }
                    EditorUtility.SetDirty(component);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 塗りの細かさを調整UI
        /// 色変換の直前にテクスチャの塗り感を統一するための前処理（ブラー／シャープ）
        /// </summary>
        private void DrawBlurSharpAdjustmentUI()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            showBlurSharpAdjustment = EditorGUILayout.Foldout(showBlurSharpAdjustment, CHMLocales.Tr("Inspector:BlurSharpAdjustment"), true);
            DrawHelpMark("Inspector:BlurSharpAdjustmentHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:BlurSharpAdjustmentHelp");

            if (!showBlurSharpAdjustment)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.indentLevel++;

            SyncBlurSharpAdjustments(component);

            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null) continue;

                float currentValue = 0f;
                int adjustmentIndex = -1;
                for (int j = 0; j < component.rendererBlurSharpAdjustments.Count; j++)
                {
                    if (component.rendererBlurSharpAdjustments[j].rendererIndex == i)
                    {
                        currentValue = component.rendererBlurSharpAdjustments[j].blurSharp;
                        adjustmentIndex = j;
                        break;
                    }
                }

                EditorGUILayout.BeginHorizontal();

                string displayName = renderer.name;
                if (displayName.Length > 15) displayName = displayName.Substring(0, 15) + "...";

                if (GUILayout.Button(displayName, EditorStyles.label, GUILayout.Width(120)))
                {
                    HighlightRenderer(renderer);
                }

                EditorGUI.BeginChangeCheck();
                float newValue = EditorGUILayout.Slider(currentValue, -1f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Adjust Renderer BlurSharp");

                    if (adjustmentIndex >= 0)
                    {
                        component.rendererBlurSharpAdjustments[adjustmentIndex].blurSharp = newValue;
                    }
                    else
                    {
                        component.rendererBlurSharpAdjustments.Add(new RendererBlurSharpAdjustment(i) { blurSharp = newValue });
                    }

                    EditorUtility.SetDirty(component);
                }

                if (GUILayout.Button(CHMLocales.Tr("Inspector:Reset"), GUILayout.Width(50)))
                {
                    Undo.RecordObject(component, "Reset Renderer BlurSharp");
                    if (adjustmentIndex >= 0)
                    {
                        component.rendererBlurSharpAdjustments[adjustmentIndex].blurSharp = 0f;
                    }
                    EditorUtility.SetDirty(component);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// rendererBlurSharpAdjustments を targetRenderers と同期
        /// </summary>
        private void SyncBlurSharpAdjustments(ChimeraHairMaster component)
        {
            for (int i = component.rendererBlurSharpAdjustments.Count - 1; i >= 0; i--)
            {
                int rendererIndex = component.rendererBlurSharpAdjustments[i].rendererIndex;
                if (rendererIndex < 0 || rendererIndex >= component.targetRenderers.Count)
                {
                    component.rendererBlurSharpAdjustments.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 毛束パターン統一 UI
        /// お手本 Renderer の毛束模様（高周波）を抽出して他 Renderer に転送する
        /// </summary>
        private void DrawStrandPatternUI()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            var settings = component.strandPatternSettings;
            if (settings == null)
            {
                // null 防御（古いデータ互換）
                component.strandPatternSettings = new StrandPatternSettings();
                settings = component.strandPatternSettings;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            showStrandPattern = EditorGUILayout.Foldout(showStrandPattern, CHMLocales.Tr("Inspector:StrandPattern"), true);
            DrawHelpMark("Inspector:StrandPatternHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:StrandPatternHelp");

            if (!showStrandPattern)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();
            bool newEnabled = EditorGUILayout.Toggle(CHMLocales.Tr("Inspector:StrandPatternEnable"), settings.enabled);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(component, "Toggle Strand Pattern");
                settings.enabled = newEnabled;
                EditorUtility.SetDirty(component);
            }

            using (new EditorGUI.DisabledScope(!settings.enabled))
            {
                // お手本 Renderer ドロップダウン
                var rendererNames = new string[component.targetRenderers.Count];
                for (int i = 0; i < component.targetRenderers.Count; i++)
                {
                    var r = component.targetRenderers[i];
                    rendererNames[i] = r != null ? r.name : $"(null #{i})";
                }

                int currentRefIndex = settings.referenceRendererIndex;
                if (currentRefIndex < 0 || currentRefIndex >= component.targetRenderers.Count)
                    currentRefIndex = 0;

                EditorGUI.BeginChangeCheck();
                int newRefIndex = EditorGUILayout.Popup(
                    CHMLocales.Tr("Inspector:StrandPatternReference"),
                    currentRefIndex, rendererNames);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Change Strand Pattern Reference");
                    settings.referenceRendererIndex = newRefIndex;
                    EditorUtility.SetDirty(component);
                }

                // 線の細さの強度（B_high バンド rescale）
                EditorGUI.BeginChangeCheck();
                float newStrengthFine = EditorGUILayout.Slider(
                    CHMLocales.Tr("Inspector:StrandPatternStrengthFine"),
                    settings.strengthFine, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Adjust Strand Pattern Strength Fine");
                    settings.strengthFine = newStrengthFine;
                    EditorUtility.SetDirty(component);
                }

                // 塗りの濃淡の強度（B_mid バンド rescale）
                EditorGUI.BeginChangeCheck();
                float newStrengthShade = EditorGUILayout.Slider(
                    CHMLocales.Tr("Inspector:StrandPatternStrengthShade"),
                    settings.strengthShade, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Adjust Strand Pattern Strength Shade");
                    settings.strengthShade = newStrengthShade;
                    EditorUtility.SetDirty(component);
                }

                // 対象スケール (Gaussian sigma) - バンド分解の境界
                EditorGUI.BeginChangeCheck();
                float newSigma = EditorGUILayout.Slider(
                    CHMLocales.Tr("Inspector:StrandPatternSigma"),
                    settings.sigma, 1f, 15f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component, "Adjust Strand Pattern Sigma");
                    settings.sigma = newSigma;
                    EditorUtility.SetDirty(component);
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 色合わせ無視マスクUI
        /// </summary>
        private void DrawColorMaskUI()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            showColorMaskSettings = EditorGUILayout.Foldout(showColorMaskSettings, CHMLocales.Tr("Inspector:ColorMaskSettings"), true);
            DrawHelpMark("Inspector:ColorMaskHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:ColorMaskHelp");

            if (!showColorMaskSettings)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.indentLevel++;

            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null || renderer.sharedMesh == null) continue;

                EditorGUILayout.LabelField(renderer.name, EditorStyles.boldLabel);

                EditorGUI.indentLevel++;

                int submeshCount = renderer.sharedMesh.subMeshCount;
                var materials = renderer.sharedMaterials;

                for (int s = 0; s < submeshCount; s++)
                {
                    // 統合対象外のサブメッシュはスキップ
                    if (!component.IsSubmeshIncluded(r, s)) continue;

                    string matName = s < materials.Length && materials[s] != null
                        ? materials[s].name
                        : string.Format(CHMLocales.Tr("Inspector:MaterialFallback"), s);

                    // 現在のマスクを取得
                    var currentMask = component.GetColorMask(r, s);

                    EditorGUILayout.BeginHorizontal();

                    EditorGUI.BeginChangeCheck();
                    var newMask = (Texture2D)EditorGUILayout.ObjectField(
                        string.Format(CHMLocales.Tr("Inspector:SubmeshLabelFormat"), s, matName), currentMask, typeof(Texture2D), false);

                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(component, "Change Color Mask");

                        var entry = component.colorMasks.Find(e => e.rendererIndex == r && e.submeshIndex == s);
                        if (newMask != null)
                        {
                            if (entry != null)
                            {
                                entry.mask = newMask;
                            }
                            else
                            {
                                component.colorMasks.Add(new ColorMaskEntry(r, s) { mask = newMask });
                            }
                        }
                        else
                        {
                            // マスクをクリア：エントリを削除
                            if (entry != null)
                            {
                                component.colorMasks.Remove(entry);
                            }
                        }

                        EditorUtility.SetDirty(component);
                    }

                    if (GUILayout.Button(CHMLocales.Tr("Inspector:CreateMask"), GUILayout.Width(80)))
                    {
                        int capturedR = r;
                        int capturedS = s;
                        OpenMaskToolForSubmesh(component, renderer, s, savedTex =>
                        {
                            if (savedTex == null) return;
                            Undo.RecordObject(component, "CHM Assign Color Mask");
                            var existing = component.colorMasks.Find(
                                e => e.rendererIndex == capturedR && e.submeshIndex == capturedS);
                            if (existing != null)
                            {
                                existing.mask = savedTex;
                            }
                            else
                            {
                                component.colorMasks.Add(new ColorMaskEntry(capturedR, capturedS) { mask = savedTex });
                            }
                            EditorUtility.SetDirty(component);
                        });
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(3);
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 各Rendererに影響するPhysBone一覧を表示
        /// </summary>
        private void DrawPhysBoneList(ChimeraHairMaster component)
        {
#if CHM_VRCSDK3_AVATARS
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.BeginHorizontal();
            showPhysBoneList = EditorGUILayout.Foldout(showPhysBoneList, CHMLocales.Tr("Inspector:PhysBoneList"), true);
            DrawHelpMark("Inspector:PhysBoneListHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:PhysBoneListHelp");
            if (!showPhysBoneList) return;

            EditorGUI.indentLevel++;

            // アバタールートを探す（VRC_AvatarDescriptorを持つ親）
            var avatarRoot = component.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (avatarRoot == null)
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:AvatarDescriptorNotFound"), MessageType.Warning);
                EditorGUI.indentLevel--;
                return;
            }

            // アバター内の全PhysBoneを取得
            var allPhysBones = avatarRoot.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true);

            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // RendererごとのFoldout
                if (!physBoneFoldouts.ContainsKey(r)) physBoneFoldouts[r] = false;
                physBoneFoldouts[r] = EditorGUILayout.Foldout(physBoneFoldouts[r], renderer.name, true);
                if (!physBoneFoldouts[r]) continue;

                EditorGUI.indentLevel++;

                // ウェイトが塗られているBoneを抽出
                var weightedBones = GetWeightedBones(renderer);

                // 各PhysBoneがこのRendererに影響しているか判定
                var affectingPhysBones = new List<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>();
                foreach (var pb in allPhysBones)
                {
                    var pbRoot = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                    if (IsAncestorOfAny(pbRoot, weightedBones))
                    {
                        affectingPhysBones.Add(pb);
                    }
                }

                if (affectingPhysBones.Count == 0)
                {
                    EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:NoPhysBone"));
                    EditorGUI.indentLevel--;
                    continue;
                }

                foreach (var pb in affectingPhysBones)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField(pb, typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone), true);
                    EditorGUI.EndDisabledGroup();
                }

                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
#endif
        }

        /// <summary>
        /// SkinnedMeshRendererからウェイトが塗られているBoneのセットを取得
        /// </summary>
        private static HashSet<Transform> GetWeightedBones(SkinnedMeshRenderer renderer)
        {
            var weightedBones = new HashSet<Transform>();
            var bones = renderer.bones;
            var boneWeights = renderer.sharedMesh.boneWeights;
            if (bones == null || boneWeights == null) return weightedBones;

            foreach (var bw in boneWeights)
            {
                if (bw.weight0 > 0 && bw.boneIndex0 < bones.Length && bones[bw.boneIndex0] != null)
                    weightedBones.Add(bones[bw.boneIndex0]);
                if (bw.weight1 > 0 && bw.boneIndex1 < bones.Length && bones[bw.boneIndex1] != null)
                    weightedBones.Add(bones[bw.boneIndex1]);
                if (bw.weight2 > 0 && bw.boneIndex2 < bones.Length && bones[bw.boneIndex2] != null)
                    weightedBones.Add(bones[bw.boneIndex2]);
                if (bw.weight3 > 0 && bw.boneIndex3 < bones.Length && bones[bw.boneIndex3] != null)
                    weightedBones.Add(bones[bw.boneIndex3]);
            }
            return weightedBones;
        }

        /// <summary>
        /// rootがweightedBonesのいずれかの祖先であるか判定
        /// </summary>
        private static bool IsAncestorOfAny(Transform root, HashSet<Transform> weightedBones)
        {
            foreach (var bone in weightedBones)
            {
                var current = bone;
                while (current != null)
                {
                    if (current == root) return true;
                    current = current.parent;
                }
            }
            return false;
        }

        /// <summary>
        /// プレビューを停止してマスクツールを開く
        /// </summary>
        private void OpenMaskToolForSubmesh(ChimeraHairMaster component, SkinnedMeshRenderer renderer, int submeshIndex)
        {
            OpenMaskToolForSubmesh(component, renderer, submeshIndex, null);
        }

        /// <summary>
        /// プレビューを停止してマスクツールを開く + 保存後コールバック
        /// </summary>
        private void OpenMaskToolForSubmesh(ChimeraHairMaster component, SkinnedMeshRenderer renderer, int submeshIndex, System.Action<Texture2D> onSaved)
        {
            // プレビューを停止
            if (component.previewEnabled)
            {
                component.previewEnabled = false;
                EditorUtility.SetDirty(component);
            }

            MaskToolLauncher.OpenMaskToolForSubmesh(component, renderer, submeshIndex, onSaved);
        }

        /// <summary>
        /// rendererBrightnessAdjustments を targetRenderers と同期
        /// </summary>
        private void SyncBrightnessAdjustments(ChimeraHairMaster component)
        {
            // 不要なエントリを削除（存在しないrendererIndexを参照しているもの）
            for (int i = component.rendererBrightnessAdjustments.Count - 1; i >= 0; i--)
            {
                int rendererIndex = component.rendererBrightnessAdjustments[i].rendererIndex;
                if (rendererIndex < 0 || rendererIndex >= component.targetRenderers.Count)
                {
                    component.rendererBrightnessAdjustments.RemoveAt(i);
                }
            }
        }

        // ----------------------- Highlight helpers -----------------------
        private static void HighlightRenderer(SkinnedMeshRenderer renderer, double duration = HIGHLIGHT_DURATION)
        {
            if (renderer == null) return;
            highlightRenderer = renderer;
            highlightExpiry = EditorApplication.timeSinceStartup + duration;

            // Mimic Hierarchy single-click: ping only (no selection to avoid Inspector switch)
            EditorGUIUtility.PingObject(renderer.gameObject);

            SceneView.RepaintAll();
        }

        // メッシュ変形のScene Viewハンドラ
        // SceneEditorはstaticインスタンスなのでstaticメソッドで直接呼べる
        private static void OnMeshDeformSceneGUI(SceneView sv)
        {
            // staticなSceneEditorが編集中なら描画を続行
            // Inspectorが閉じていても編集セッションは維持される
            var editor = MeshDeformationInspectorUI.ActiveSceneEditor;
            if (editor != null && editor.CurrentMode != Deformation.MeshDeformationSceneEditor.EditMode.Off)
            {
                editor.OnSceneGUI(sv);
            }
        }

        private static void OnSceneGUI(SceneView sv)
        {
            if (highlightRenderer == null) return;
            if (EditorApplication.timeSinceStartup > highlightExpiry)
            {
                highlightRenderer = null;
                SceneView.RepaintAll();
                return;
            }

            var bounds = highlightRenderer.bounds;

            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            Color prev = Handles.color;
            Handles.color = new Color(0f, 0.7f, 1f, 0.95f);

            var corners = GetBoundsCorners(bounds);
            DrawBoundsWire(corners);

            Handles.color = prev;
        }

        private static Vector3[] GetBoundsCorners(Bounds b)
        {
            var c = b.center;
            var e = b.extents;
            return new Vector3[]
            {
                c + new Vector3(-e.x, -e.y, -e.z),
                c + new Vector3(e.x, -e.y, -e.z),
                c + new Vector3(e.x, -e.y, e.z),
                c + new Vector3(-e.x, -e.y, e.z),
                c + new Vector3(-e.x, e.y, -e.z),
                c + new Vector3(e.x, e.y, -e.z),
                c + new Vector3(e.x, e.y, e.z),
                c + new Vector3(-e.x, e.y, e.z)
            };
        }

        private static void DrawBoundsWire(Vector3[] c)
        {
            if (c == null || c.Length < 8) return;
            // bottom
            Handles.DrawAAPolyLine(4f, c[0], c[1], c[2], c[3], c[0]);
            // top
            Handles.DrawAAPolyLine(4f, c[4], c[5], c[6], c[7], c[4]);
            // vertical edges
            for (int i = 0; i < 4; i++)
            {
                Handles.DrawAAPolyLine(4f, c[i], c[i + 4]);
            }
        }

        /// <summary>
        /// テクスチャから色を選択するUI
        /// </summary>
        private void DrawTextureColorPicker()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.Space(5);
            EditorGUI.indentLevel++;
            showTextureColorPicker = EditorGUILayout.Foldout(showTextureColorPicker, CHMLocales.Tr("Inspector:TextureColorPicker"), true);
            EditorGUI.indentLevel--;

            if (!showTextureColorPicker) return;

            EditorGUI.indentLevel++;

            // Renderer選択ボタン
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null) continue;

                string name = renderer.name;
                if (name.Length > 10) name = name.Substring(0, 10) + "...";

                bool isSelected = (i == selectedColorPickerRendererIndex);
                GUI.backgroundColor = isSelected ? Color.cyan : Color.white;

                if (GUILayout.Button(name, GUILayout.MaxWidth(80)))
                {
                    selectedColorPickerRendererIndex = i;
                    cachedTexturePreview = null;
                    extractedColors = null;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 選択中のRendererからテクスチャを取得
            if (selectedColorPickerRendererIndex >= 0 && selectedColorPickerRendererIndex < component.targetRenderers.Count)
            {
                var selectedRenderer = component.targetRenderers[selectedColorPickerRendererIndex];
                if (selectedRenderer != null)
                {
                    // テクスチャプレビューをキャッシュ
                    if (cachedTexturePreview == null || cachedTextureRenderer != selectedRenderer)
                    {
                        cachedTextureRenderer = selectedRenderer;
                        var materials = selectedRenderer.sharedMaterials;
                        foreach (var mat in materials)
                        {
                            if (mat != null && mat.HasProperty("_MainTex"))
                            {
                                cachedTexturePreview = mat.GetTexture("_MainTex") as Texture2D;
                                if (cachedTexturePreview != null)
                                {
                                    extractedColors = Processing.ColorProcessor.ExtractDominantColors(cachedTexturePreview, 5);
                                    break;
                                }
                            }
                        }
                    }

                    // テクスチャプレビュー表示
                    if (cachedTexturePreview != null)
                    {
                        EditorGUILayout.Space(5);

                        // プレビュー領域
                        float previewSize = EditorGUIUtility.currentViewWidth - 60;
                        previewSize = Mathf.Min(previewSize, 200);

                        Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                        previewRect.x += 15; // インデント調整
                        previewRect.width = previewSize;
                        previewRect.height = previewSize;

                        // テクスチャを描画
                        GUI.DrawTexture(previewRect, cachedTexturePreview, ScaleMode.ScaleToFit);

                        // クリック検出
                        Event e = Event.current;
                        if (e.type == EventType.MouseDown && previewRect.Contains(e.mousePosition))
                        {
                            // クリック位置からテクスチャ座標を計算
                            float aspectRatio = (float)cachedTexturePreview.width / cachedTexturePreview.height;
                            Rect actualRect;

                            if (aspectRatio > 1)
                            {
                                float h = previewRect.width / aspectRatio;
                                actualRect = new Rect(previewRect.x, previewRect.y + (previewRect.height - h) / 2, previewRect.width, h);
                            }
                            else
                            {
                                float w = previewRect.height * aspectRatio;
                                actualRect = new Rect(previewRect.x + (previewRect.width - w) / 2, previewRect.y, w, previewRect.height);
                            }

                            if (actualRect.Contains(e.mousePosition))
                            {
                                float u = (e.mousePosition.x - actualRect.x) / actualRect.width;
                                float v = 1f - (e.mousePosition.y - actualRect.y) / actualRect.height;

                                // 読み取り可能なテクスチャを取得
                                var readableTex = Processing.ColorProcessor.GetReadableTexture(cachedTexturePreview);
                                int px = Mathf.FloorToInt(u * readableTex.width);
                                int py = Mathf.FloorToInt(v * readableTex.height);
                                px = Mathf.Clamp(px, 0, readableTex.width - 1);
                                py = Mathf.Clamp(py, 0, readableTex.height - 1);

                                Color pickedColor = readableTex.GetPixel(px, py);

                                if (readableTex != cachedTexturePreview)
                                {
                                    Object.DestroyImmediate(readableTex);
                                }

                                // ターゲット色に設定
                                targetColorProp.colorValue = pickedColor;
                                serializedObject.ApplyModifiedProperties();
                                
                                // プレビューを強制更新
                                EditorUtility.SetDirty(component);
                                UnityEditor.SceneView.RepaintAll();
                                
                                e.Use();
                            }
                        }

                        EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:ClickToPickColor"), MessageType.None);

                        // 代表色パレット
                        if (extractedColors != null && extractedColors.Length > 0)
                        {
                            EditorGUILayout.Space(5);
                            EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:CandidateColors"));
                            EditorGUILayout.BeginHorizontal();

                            foreach (var color in extractedColors)
                            {
                                Rect colorRect = GUILayoutUtility.GetRect(30, 30);
                                EditorGUI.DrawRect(colorRect, color);

                                // 枠線
                                Handles.color = Color.black;
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.y), new Vector3(colorRect.xMax, colorRect.y));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.y), new Vector3(colorRect.xMax, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.yMax), new Vector3(colorRect.x, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.yMax), new Vector3(colorRect.x, colorRect.y));

                                // クリック検出
                                if (Event.current.type == EventType.MouseDown && colorRect.Contains(Event.current.mousePosition))
                                {
                                    targetColorProp.colorValue = color;
                                    serializedObject.ApplyModifiedProperties();
                                    
                                    // プレビューを強制更新
                                    EditorUtility.SetDirty(component);
                                    UnityEditor.SceneView.RepaintAll();
                                    
                                    Event.current.Use();
                                }
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:TextureNotFound"), MessageType.Warning);
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Gradient用：テクスチャから色を選択するUI
        /// </summary>
        private void DrawGradientTextureColorPicker()
        {
            var component = (ChimeraHairMaster)target;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return;

            EditorGUILayout.Space(5);
            EditorGUI.indentLevel++;
            showGradientTextureColorPicker = EditorGUILayout.Foldout(showGradientTextureColorPicker, CHMLocales.Tr("Inspector:GradientTextureColorPicker"), true);
            EditorGUI.indentLevel--;

            if (!showGradientTextureColorPicker) return;

            EditorGUI.indentLevel++;

            // Renderer選択ボタン
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null) continue;

                string name = renderer.name;
                if (name.Length > 10) name = name.Substring(0, 10) + "...";

                bool isSelected = (i == selectedGradientColorPickerRendererIndex);
                GUI.backgroundColor = isSelected ? Color.cyan : Color.white;

                if (GUILayout.Button(name, GUILayout.MaxWidth(80)))
                {
                    selectedGradientColorPickerRendererIndex = i;
                    cachedGradientTexturePreview = null;
                    extractedGradientColors = null;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 選択中のRendererからテクスチャを取得
            if (selectedGradientColorPickerRendererIndex >= 0 && selectedGradientColorPickerRendererIndex < component.targetRenderers.Count)
            {
                var selectedRenderer = component.targetRenderers[selectedGradientColorPickerRendererIndex];
                if (selectedRenderer != null)
                {
                    // テクスチャプレビューをキャッシュ
                    if (cachedGradientTexturePreview == null || cachedGradientTextureRenderer != selectedRenderer)
                    {
                        cachedGradientTextureRenderer = selectedRenderer;
                        var materials = selectedRenderer.sharedMaterials;
                        foreach (var mat in materials)
                        {
                            if (mat != null && mat.HasProperty("_MainTex"))
                            {
                                cachedGradientTexturePreview = mat.GetTexture("_MainTex") as Texture2D;
                                if (cachedGradientTexturePreview != null)
                                {
                                    extractedGradientColors = Processing.ColorProcessor.ExtractDominantColors(cachedGradientTexturePreview, 5);
                                    break;
                                }
                            }
                        }
                    }

                    // テクスチャプレビュー表示
                    if (cachedGradientTexturePreview != null)
                    {
                        EditorGUILayout.Space(5);

                        // キー位置スライダー
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:DarkColor"), GUILayout.Width(80));
                        gradientColorKeyPosition = EditorGUILayout.Slider(gradientColorKeyPosition, 0f, 1f);
                        EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:LightColor"), GUILayout.Width(80));
                        EditorGUILayout.EndHorizontal();

                        EditorGUILayout.Space(5);

                        // プレビュー領域
                        float previewSize = EditorGUIUtility.currentViewWidth - 60;
                        previewSize = Mathf.Min(previewSize, 200);

                        Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                        previewRect.x += 15; // インデント調整
                        previewRect.width = previewSize;
                        previewRect.height = previewSize;

                        // テクスチャを描画
                        GUI.DrawTexture(previewRect, cachedGradientTexturePreview, ScaleMode.ScaleToFit);

                        // クリック検出
                        Event e = Event.current;
                        if (e.type == EventType.MouseDown && previewRect.Contains(e.mousePosition))
                        {
                            // クリック位置からテクスチャ座標を計算
                            float aspectRatio = (float)cachedGradientTexturePreview.width / cachedGradientTexturePreview.height;
                            Rect actualRect;

                            if (aspectRatio > 1)
                            {
                                float h = previewRect.width / aspectRatio;
                                actualRect = new Rect(previewRect.x, previewRect.y + (previewRect.height - h) / 2, previewRect.width, h);
                            }
                            else
                            {
                                float w = previewRect.height * aspectRatio;
                                actualRect = new Rect(previewRect.x + (previewRect.width - w) / 2, previewRect.y, w, previewRect.height);
                            }

                            if (actualRect.Contains(e.mousePosition))
                            {
                                float u = (e.mousePosition.x - actualRect.x) / actualRect.width;
                                float v = 1f - (e.mousePosition.y - actualRect.y) / actualRect.height;

                                // 読み取り可能なテクスチャを取得
                                var readableTex = Processing.ColorProcessor.GetReadableTexture(cachedGradientTexturePreview);
                                int px = Mathf.FloorToInt(u * readableTex.width);
                                int py = Mathf.FloorToInt(v * readableTex.height);
                                px = Mathf.Clamp(px, 0, readableTex.width - 1);
                                py = Mathf.Clamp(py, 0, readableTex.height - 1);

                                Color pickedColor = readableTex.GetPixel(px, py);

                                if (readableTex != cachedGradientTexturePreview)
                                {
                                    Object.DestroyImmediate(readableTex);
                                }

                                // Gradientにキーを追加
                                AddColorKeyToGradient(component.gradientCurve, pickedColor, gradientColorKeyPosition);
                                gradientCurveProp.gradientValue = component.gradientCurve;
                                
                                // プレビューを強制更新（ハッシュをリセットして次のUpdateで更新をトリガー）
                                component.previewMaterialHash = 0;
                                EditorUtility.SetDirty(component);
                                serializedObject.ApplyModifiedProperties();
                                UnityEditor.SceneView.RepaintAll();
                                Repaint();
                                
                                e.Use();
                            }
                        }

                        EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:ClickToAddColorToGradient"), MessageType.None);

                        // 代表色パレット
                        if (extractedGradientColors != null && extractedGradientColors.Length > 0)
                        {
                            EditorGUILayout.Space(5);
                            EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:CandidateColors"));
                            EditorGUILayout.BeginHorizontal();

                            foreach (var color in extractedGradientColors)
                            {
                                Rect colorRect = GUILayoutUtility.GetRect(30, 30);
                                EditorGUI.DrawRect(colorRect, color);

                                // 枠線
                                Handles.color = Color.black;
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.y), new Vector3(colorRect.xMax, colorRect.y));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.y), new Vector3(colorRect.xMax, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.yMax), new Vector3(colorRect.x, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.yMax), new Vector3(colorRect.x, colorRect.y));

                                // クリック検出
                                if (Event.current.type == EventType.MouseDown && colorRect.Contains(Event.current.mousePosition))
                                {
                                    AddColorKeyToGradient(component.gradientCurve, color, gradientColorKeyPosition);
                                    gradientCurveProp.gradientValue = component.gradientCurve;
                                    
                                    // プレビューを強制更新（ハッシュをリセットして次のUpdateで更新をトリガー）
                                    component.previewMaterialHash = 0;
                                    EditorUtility.SetDirty(component);
                                    serializedObject.ApplyModifiedProperties();
                                    UnityEditor.SceneView.RepaintAll();
                                    Repaint();
                                    
                                    Event.current.Use();
                                }
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:TextureNotFound"), MessageType.Warning);
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Gradientにカラーキーを追加
        /// </summary>
        private void AddColorKeyToGradient(Gradient gradient, Color color, float position)
        {
            var colorKeys = new System.Collections.Generic.List<GradientColorKey>(gradient.colorKeys);
            var alphaKeys = new System.Collections.Generic.List<GradientAlphaKey>(gradient.alphaKeys);

            // 同じ位置にキーがあれば置き換え、なければ追加
            bool found = false;
            for (int i = 0; i < colorKeys.Count; i++)
            {
                if (Mathf.Abs(colorKeys[i].time - position) < 0.01f)
                {
                    colorKeys[i] = new GradientColorKey(color, position);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // 最大8キーまで
                if (colorKeys.Count < 8)
                {
                    colorKeys.Add(new GradientColorKey(color, position));
                }
                else
                {
                    // 最も近い位置のキーを置き換え
                    float minDist = float.MaxValue;
                    int minIndex = 0;
                    for (int i = 0; i < colorKeys.Count; i++)
                    {
                        float dist = Mathf.Abs(colorKeys[i].time - position);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            minIndex = i;
                        }
                    }
                    colorKeys[minIndex] = new GradientColorKey(color, position);
                }
            }

            // 時間順にソート
            colorKeys.Sort((a, b) => a.time.CompareTo(b.time));

            gradient.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
        }

        private Material lastBaseMaterial;
        
        /// <summary>
        /// マテリアル設定（lilToon Inspector埋め込み）を描画
        /// </summary>
        private void DrawMaterialEditorSection()
        {
            var component = target as ChimeraHairMaster;
            
            // previewMaterialがないがbaseMaterialがある場合、自動生成を促す
            if (previewMaterialProp.objectReferenceValue == null && baseMaterialProp.objectReferenceValue != null)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:PreviewGenerateHelp"),
                    MessageType.Info
                );
                
                // 手動生成ボタン
                if (GUILayout.Button(CHMLocales.Tr("Inspector:GeneratePreviewMaterial"), GUILayout.Height(25)))
                {
                    CreatePreviewMaterial();
                    serializedObject.Update();
                    Repaint();
                }
            }
            // previewMaterialがある場合はマテリアルインスペクターを埋め込み表示
            else if (previewMaterialProp.objectReferenceValue != null)
            {
                EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:MaterialSettings"), EditorStyles.boldLabel);
                DrawEmbeddedMaterialEditor();
            }
            else if (baseMaterialProp.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:BaseMaterialRequiredHelp"),
                    MessageType.Info
                );
            }
        }

        private void DrawMeshSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            showMeshSettings = EditorGUILayout.Foldout(showMeshSettings, CHMLocales.Tr("Inspector:MeshSettings"), true);
            DrawHelpMark("Inspector:MeshSettingsHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:MeshSettingsHelp");
            if (showMeshSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.Space(5);
                
                // Probe Anchor設定
                EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:ProbeAnchorHeader"), EditorStyles.boldLabel);
                {
                    var inheritNames = new[]
                    {
                        CHMLocales.Tr("MeshSettingsInheritMode:Inherit"),
                        CHMLocales.Tr("MeshSettingsInheritMode:Set"),
                        CHMLocales.Tr("MeshSettingsInheritMode:DontSet"),
                        CHMLocales.Tr("MeshSettingsInheritMode:SetOrInherit"),
                    };
                    inheritProbeAnchorProp.enumValueIndex = EditorGUILayout.Popup(
                        CHMLocales.Tr("Inspector:SettingMode"),
                        inheritProbeAnchorProp.enumValueIndex, inheritNames);
                }
                
                var inheritProbeAnchorMode = (MeshSettingsInheritMode)inheritProbeAnchorProp.enumValueIndex;
                if (inheritProbeAnchorMode == MeshSettingsInheritMode.Set || 
                    inheritProbeAnchorMode == MeshSettingsInheritMode.SetOrInherit)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PropertyField(probeAnchorProp, new GUIContent(CHMLocales.Tr("Inspector:ProbeAnchor")));
                    DrawHelpMark("Inspector:ProbeAnchorHelp");
                    EditorGUILayout.EndHorizontal();
                    DrawHelpBoxIfOpen("Inspector:ProbeAnchorHelp", MessageType.None);
                }

                EditorGUILayout.Space(10);

                // Bounds設定
                EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:BoundsHeader"), EditorStyles.boldLabel);
                {
                    var inheritNames = new[]
                    {
                        CHMLocales.Tr("MeshSettingsInheritMode:Inherit"),
                        CHMLocales.Tr("MeshSettingsInheritMode:Set"),
                        CHMLocales.Tr("MeshSettingsInheritMode:DontSet"),
                        CHMLocales.Tr("MeshSettingsInheritMode:SetOrInherit"),
                    };
                    inheritBoundsProp.enumValueIndex = EditorGUILayout.Popup(
                        CHMLocales.Tr("Inspector:SettingMode"),
                        inheritBoundsProp.enumValueIndex, inheritNames);
                }
                
                var inheritBoundsMode = (MeshSettingsInheritMode)inheritBoundsProp.enumValueIndex;
                if (inheritBoundsMode == MeshSettingsInheritMode.Set || 
                    inheritBoundsMode == MeshSettingsInheritMode.SetOrInherit)
                {
                    EditorGUILayout.PropertyField(rootBoneProp, new GUIContent(CHMLocales.Tr("Inspector:RootBone")));
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PropertyField(boundsProp, new GUIContent(CHMLocales.Tr("Inspector:Bounds")));
                    DrawHelpMark("Inspector:BoundsHelp");
                    EditorGUILayout.EndHorizontal();
                    DrawHelpBoxIfOpen("Inspector:BoundsHelp", MessageType.None);

                    // Bounds可視化用のGizmo描画ボタン
                    var component = target as ChimeraHairMaster;
                    if (component != null && component.rootBone != null)
                    {
                        EditorGUILayout.Space(5);
                        if (GUILayout.Button(CHMLocales.Tr("Inspector:FrameBoundsInScene")))
                        {
                            Selection.activeGameObject = component.gameObject;
                            UnityEditor.SceneView.lastActiveSceneView.FrameSelected();
                        }
                    }
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        private bool showMeshDeformSection
        {
            get => SessionState.GetBool("CHM_MeshDeform_ShowSection", false);
            set => SessionState.SetBool("CHM_MeshDeform_ShowSection", value);
        }

        private bool showExportSection
        {
            get => SessionState.GetBool("CHM_Export_ShowSection", false);
            set => SessionState.SetBool("CHM_Export_ShowSection", value);
        }

        private void DrawMeshDeformationSection()
        {
            var component = target as ChimeraHairMaster;
            if (component == null) return;

            showMeshDeformSection = EditorGUILayout.Foldout(showMeshDeformSection, CHMLocales.Tr("Inspector:MeshDeformSection"), true, EditorStyles.foldoutHeader);
            if (!showMeshDeformSection) return;

            // スタンドアロンとの重複チェック（重複時は機能を表示せず警告のみ）
            var conflict = FindStandaloneConflict(component);
            if (conflict != null)
            {
                EditorGUILayout.HelpBox(
                    string.Format(CHMLocales.Tr("Inspector:MeshDeformConflictMessage"), conflict),
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(5);
            meshDeformationUI?.Draw(component);
        }

        /// <summary>
        /// CHMの対象RendererがスタンドアロンのRendererと重複していないかチェック。
        /// 重複がある場合は競合元の説明を返す。なければnull。
        /// </summary>
        private static string FindStandaloneConflict(ChimeraHairMaster component)
        {
            if (component.targetRenderers == null || component.targetRenderers.Count == 0)
                return null;

            var allStandalone = Object.FindObjectsByType<MeshDeformationStandalone>(FindObjectsSortMode.None);
            foreach (var standalone in allStandalone)
            {
                if (standalone.targetRenderer != null
                    && component.targetRenderers.Contains(standalone.targetRenderer))
                {
                    return string.Format(CHMLocales.Tr("Inspector:StandaloneConflictFormat"), standalone.gameObject.name, standalone.targetRenderer.name);
                }
            }

            return null;
        }

        private void DrawActionButtons()
        {
            var component = target as ChimeraHairMaster;
            if (component == null) return;

            EditorGUILayout.Space(5);

            // エクスポート枠（テクスチャ出力 + Prefab出力 を 1 つの Foldout で囲む）
            // 色変換オフ時も Prefab 出力は意味がある（不要bone整理・マテリアル統一・メッシュ変形反映）ため常時表示
            if (component.targetRenderers != null && component.targetRenderers.Count > 0)
            {
                showExportSection = EditorGUILayout.Foldout(
                    showExportSection,
                    CHMLocales.Tr("Inspector:ExportSection"),
                    true,
                    EditorStyles.foldoutHeader);

                if (showExportSection)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUI.indentLevel++;

                    EditorGUILayout.HelpBox(
                        CHMLocales.Tr("Inspector:ExportSectionHint"),
                        MessageType.Info);

                    // 色変換オフ時はテクスチャ書き出し（DrawColorApplySection）は意味がないので非表示
                    if (component.enableColorTransform)
                    {
                        DrawColorApplySection(component);
                        EditorGUILayout.Space(3);
                    }
                    DrawPrefabExportSection(component);

                    EditorGUI.indentLevel--;
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.Space(5);
            }

            // プレビュートグルボタン
            EditorGUILayout.BeginHorizontal();

            bool canPreview = CanGeneratePreview(component);
            GUI.enabled = canPreview;

            string previewButtonText = component.previewEnabled ? CHMLocales.Tr("Inspector:PreviewStop") : CHMLocales.Tr("Inspector:PreviewStart");
            GUIStyle previewButtonStyle = component.previewEnabled ? 
                new GUIStyle(GUI.skin.button) { normal = { textColor = Color.green } } : 
                GUI.skin.button;

            if (GUILayout.Button(previewButtonText, previewButtonStyle, GUILayout.Height(25)))
            {
                // プレビュー開始時にpreviewMaterialがなければ生成
                if (!component.previewEnabled && component.previewMaterial == null && component.baseMaterial != null)
                {
                    CreatePreviewMaterial();
                    serializedObject.Update();
                }

                component.previewEnabled = !component.previewEnabled;
                EditorUtility.SetDirty(component);
            }

            GUI.enabled = true;

            if (component.enableMeshMerge)
            {
                if (GUILayout.Button(CHMLocales.Tr("Inspector:EditMask"), GUILayout.Height(25)))
                {
                    MaskToolLauncher.OpenMaskTool(component, "_Main2ndBlendMask");
                }
            }

            EditorGUILayout.EndHorizontal();

            // プレビュー状態の表示
            if (component.previewEnabled)
            {
                if (component.enableMeshMerge)
                {
                    EditorGUILayout.PropertyField(previewResolutionProp, new GUIContent(CHMLocales.Tr("Inspector:PreviewResolution")));
                }
                if (!component.enableMeshMerge)
                {
                    EditorGUILayout.HelpBox(
                        CHMLocales.Tr("Inspector:PreviewHelpNoMerge"),
                        MessageType.Info
                    );
                    EditorGUILayout.PropertyField(unifyMatCapProp, new GUIContent(CHMLocales.Tr("Inspector:UnifyMatCap"),
                        CHMLocales.Tr("Inspector:UnifyMatCapTooltip")));
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        CHMLocales.Tr("Inspector:PreviewHelpMerge"),
                        MessageType.Info
                    );
                }
            }
            else if (!canPreview)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:PreviewRequiresSetup"),
                    MessageType.Info
                );
            }
        }

        /// <summary>
        /// 色合わせ適用セクションを描画
        /// </summary>
        private void DrawColorApplySection(ChimeraHairMaster component)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:SaveAsTextureSection"), EditorStyles.boldLabel);
            DrawHelpMark("Inspector:ApplyHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:ApplyHelp");

            // メッシュ統合有効時の注意書き
            if (component.enableMeshMerge)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:MeshMergeDiscardWarning"),
                    MessageType.Warning);
            }

            // チェックボックス
            bool applyTexture = EditorPrefs.GetBool(PREF_APPLY_TEXTURE, true);
            bool unifySettings = EditorPrefs.GetBool(PREF_UNIFY_SETTINGS, true);
            bool applyDeformation = EditorPrefs.GetBool(PREF_APPLY_DEFORMATION, false);

            EditorGUI.BeginChangeCheck();
            applyTexture = EditorGUILayout.ToggleLeft(CHMLocales.Tr("Inspector:ApplyTexture"), applyTexture);
            unifySettings = EditorGUILayout.ToggleLeft(CHMLocales.Tr("Inspector:UnifySettings"), unifySettings);

            // メッシュ変形データがある場合のみ表示
            bool hasDeformation = component.enableMeshDeformation
                && component.rendererDeformations != null
                && component.rendererDeformations.Exists(d => d.deltas != null && d.deltas.Count > 0);
            if (hasDeformation)
            {
                applyDeformation = EditorGUILayout.ToggleLeft(
                    new GUIContent(CHMLocales.Tr("Inspector:ApplyDeformation"),
                        CHMLocales.Tr("Inspector:ApplyDeformationTooltip")),
                    applyDeformation);
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(PREF_APPLY_TEXTURE, applyTexture);
                EditorPrefs.SetBool(PREF_UNIFY_SETTINGS, unifySettings);
                EditorPrefs.SetBool(PREF_APPLY_DEFORMATION, applyDeformation);
            }

            // 適用ボタン
            bool canApply = CanGeneratePreview(component);
            GUI.enabled = canApply;

            if (GUILayout.Button(CHMLocales.Tr("Inspector:ApplyButton"), GUILayout.Height(28)))
            {
                // メッシュ統合有効時は確認ダイアログを出す（設定破棄の明示）
                bool proceed = true;
                if (component.enableMeshMerge)
                {
                    proceed = EditorUtility.DisplayDialog(
                        CHMLocales.Tr("Inspector:SaveAsTextureSection"),
                        CHMLocales.Tr("Inspector:MeshMergeDiscardConfirm"),
                        CHMLocales.Tr("Inspector:Confirm:Apply"),
                        CHMLocales.Tr("Inspector:Confirm:Cancel"));
                }

                if (proceed)
                {
                    Processing.ColorApplier.Apply(component, applyTexture, unifySettings);

                    // メッシュ変形の適用
                    if (applyDeformation && hasDeformation)
                    {
                        ApplyDeformationToRenderers(component);
                    }

                    serializedObject.Update();
                }
            }

            GUI.enabled = true;

            if (!canApply)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:ApplyRequiresSetup"),
                    MessageType.Warning
                );
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Prefab出力セクションを描画。色変換適用済みの単独Prefabを書き出す（非破壊）。
        /// </summary>
        private void DrawPrefabExportSection(ChimeraHairMaster component)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:PrefabExportSection"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                CHMLocales.Tr(component.enableColorTransform
                    ? "Inspector:PrefabExportInfo"
                    : "Inspector:PrefabExportInfoNoColor"),
                MessageType.Info);

            if (component.enableMeshMerge)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:PrefabExportMeshMergeIgnored"),
                    MessageType.Warning);
            }

            // マテリアル設定統一トグル
            EditorGUI.BeginChangeCheck();
            bool unifyMaterialSettings = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    CHMLocales.Tr("Inspector:PrefabUnifyMaterialSettings"),
                    CHMLocales.Tr("Inspector:PrefabUnifyMaterialSettingsTooltip")),
                component.unifyMaterialSettings);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(component, "CHM Toggle Unify Material Settings");
                component.unifyMaterialSettings = unifyMaterialSettings;
                EditorUtility.SetDirty(component);
            }

            bool prefabApplyDeformation = EditorPrefs.GetBool(PREF_PREFAB_APPLY_DEFORMATION, true);
            bool hasDeformation = component.enableMeshDeformation
                && component.rendererDeformations != null
                && component.rendererDeformations.Exists(d => d.deltas != null && d.deltas.Count > 0);

            if (hasDeformation)
            {
                EditorGUI.BeginChangeCheck();
                prefabApplyDeformation = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        CHMLocales.Tr("Inspector:ApplyDeformation"),
                        CHMLocales.Tr("Inspector:ApplyDeformationTooltip")),
                    prefabApplyDeformation);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetBool(PREF_PREFAB_APPLY_DEFORMATION, prefabApplyDeformation);
                }
            }

            bool canExport = CanGeneratePreview(component);
            GUI.enabled = canExport;
            bool exportClicked = GUILayout.Button(CHMLocales.Tr("Inspector:PrefabExportButton"), GUILayout.Height(28));
            GUI.enabled = true;

            if (!canExport)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Inspector:ApplyRequiresSetup"),
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();

            // Modal ダイアログ（SaveFilePanel）は GUI レイアウト確定後に呼ぶ。
            // OnInspectorGUI 内で開くと EndVertical 等の Layout イベントが崩れるため、
            // EndVertical 完了後に呼び出して GUIUtility.ExitGUI() で抜ける。
            if (exportClicked)
            {
                bool deformation = prefabApplyDeformation && hasDeformation;
                Processing.PrefabExporter.Export(component, deformation);
                serializedObject.Update();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// メッシュ変形データを適用した変形済みメッシュを保存し、Rendererに設定する
        /// </summary>
        private void ApplyDeformationToRenderers(ChimeraHairMaster component)
        {
            // 変形編集セッション中は Renderer のメッシュに編集中のデルタが乗っているため、
            // 先に EndEdit で元メッシュへ戻してから焼き込む。これを怠ると
            // デルタが二重適用され、元メッシュも失われる。ExportMesh と同じ前処理。
            var editor = MeshDeformationInspectorUI.ActiveSceneEditor;
            if (editor != null && editor.CurrentMode != Deformation.MeshDeformationSceneEditor.EditMode.Off)
            {
                editor.EndEdit();
                UnityEditor.SceneView.RepaintAll();
            }

            foreach (var deformation in component.rendererDeformations)
            {
                if (deformation.deltas == null || deformation.deltas.Count == 0) continue;
                if (deformation.rendererIndex < 0 || deformation.rendererIndex >= component.targetRenderers.Count) continue;

                var renderer = component.targetRenderers[deformation.rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // 変形済みメッシュを生成（Blendshape モードか焼き込みかで分岐）
                bool asBlendshape = component.ExportAsBlendshape;
                Mesh deformedMesh;
                string actualBsName = null;
                if (asBlendshape)
                {
                    deformedMesh = Processing.MeshDeformer.ExportDeformedMeshAsBlendshape(
                        renderer, deformation,
                        string.IsNullOrEmpty(component.BlendshapeName) ? "CHMDeform" : component.BlendshapeName,
                        out actualBsName);
                }
                else
                {
                    deformedMesh = Processing.MeshDeformer.ExportDeformedMesh(renderer, deformation);
                }
                if (deformedMesh == null) continue;

                // Renderer名 + 元メッシュのアセット名（fbx名など）で命名
                var rendererName = renderer.name;
                var originalPath = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                var assetName = string.IsNullOrEmpty(originalPath)
                    ? ""
                    : System.IO.Path.GetFileNameWithoutExtension(originalPath);
                string suffix = asBlendshape ? "_DeformedBS" : "_Deformed";
                var meshName = string.IsNullOrEmpty(assetName)
                    ? $"{rendererName}{suffix}"
                    : $"{assetName}_{rendererName}{suffix}";
                deformedMesh.name = meshName;

                // 元メッシュと同じフォルダに保存
                string folder = string.IsNullOrEmpty(originalPath)
                    ? "Assets"
                    : System.IO.Path.GetDirectoryName(originalPath);
                var savePath = $"{folder}/{meshName}.asset";

                // 同名ファイルが既にあればユニーク名にする
                savePath = AssetDatabase.GenerateUniqueAssetPath(savePath);

                AssetDatabase.CreateAsset(deformedMesh, savePath);
                Debug.Log($"[ChimeraHairMaster] 変形済みメッシュを保存: {savePath}");

                // Rendererに設定
                Undo.RecordObject(renderer, "Apply Deformed Mesh");
                renderer.sharedMesh = deformedMesh;

                // Blendshape モードなら weight=100 で見た目維持
                if (asBlendshape && actualBsName != null)
                {
                    int idx = deformedMesh.GetBlendShapeIndex(actualBsName);
                    if (idx >= 0)
                    {
                        renderer.SetBlendShapeWeight(idx, 100f);
                    }
                }

                // 変形データをクリア（メッシュに反映済みなので不要）
                deformation.deltas.Clear();
            }

            // 変形機能を無効化
            component.enableMeshDeformation = false;
            component.deformOriginalMesh = null;
            component.deformEditingRendererIndex = -1;

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(component);
        }

        private bool CanGeneratePreview(ChimeraHairMaster component)
        {
            if (component == null) return false;
            if (component.targetRenderers == null || component.targetRenderers.Count == 0) return false;
            if (component.baseMaterial == null) return false;
            return true;
        }

        /// <summary>
        /// 他のCHMコンポーネントと共有されているRendererがあれば警告メッセージを返す
        /// </summary>
        private static string GetSharedRendererWarning(ChimeraHairMaster component)
        {
            if (component == null || component.targetRenderers == null || component.targetRenderers.Count == 0)
                return null;

            // Prefab アセット編集など、シーンに属さない文脈では GetRootGameObjects() が
            // 例外を投げるため、有効なシーンのときだけ他コンポーネントを走査する。
            var scene = component.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            var otherComponents = scene
                .GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<ChimeraHairMaster>(true))
                .Where(c => c != component && c.isEnabled && c.targetRenderers != null);

            var sharedNames = new List<string>();
            foreach (var other in otherComponents)
            {
                foreach (var renderer in component.targetRenderers)
                {
                    if (renderer != null && other.targetRenderers.Contains(renderer))
                    {
                        string name = string.Format(CHMLocales.Tr("Inspector:SharedRendererNameFormat"), renderer.name, other.gameObject.name);
                        if (!sharedNames.Contains(name))
                            sharedNames.Add(name);
                    }
                }
            }

            if (sharedNames.Count == 0)
                return null;

            return string.Format(CHMLocales.Tr("Inspector:SharedRendererWarningFormat"), string.Join("\n", sharedNames.Select(n => string.Format(CHMLocales.Tr("Inspector:SharedRendererItemFormat"), n))));
        }

        /// <summary>
        /// プレビュー用マテリアルのアセット保存先パスを生成
        /// シーンのパスベースでフォルダを決定し、オブジェクト名ベースのファイル名を生成
        /// </summary>
        private static string GetPreviewMaterialAssetPath(ChimeraHairMaster component)
        {
            var scene = component.gameObject.scene;
            string baseDir;
            if (!string.IsNullOrEmpty(scene.path))
            {
                string sceneDir = Path.GetDirectoryName(scene.path);
                string sceneName = Path.GetFileNameWithoutExtension(scene.path);
                baseDir = $"{sceneDir}/{sceneName}_CHM_Generated";
            }
            else
            {
                baseDir = "Assets/CHM_Generated";
            }

            string safeName = SanitizeFileName(component.gameObject.name);
            return $"{baseDir}/{safeName}_Preview.mat";
        }

        /// <summary>
        /// 生成マスクPNGの保存先フォルダ（プレビューマテリアルと同じ CHM_Generated 配下）
        /// </summary>
        private static string GetGeneratedMasksFolder(ChimeraHairMaster component)
        {
            string baseDir = Path.GetDirectoryName(GetPreviewMaterialAssetPath(component))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(baseDir)) baseDir = "Assets/CHM_Generated";
            string safeName = SanitizeFileName(component.gameObject.name);
            return $"{baseDir}/{safeName}_Masks";
        }

        /// <summary>
        /// マットキャップ/エミッションのマスク生成セクション。
        /// 素材のマスクを統合後UVに再配置したPNGを生成し、ユーザーが「割り当て」で
        /// previewMaterial に適用する。ビルドでの自動適用はしない
        /// </summary>
        private void DrawAdditionalMaskSection(ChimeraHairMaster component)
        {
            if (component == null) return;

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:AdditionalMaskSection"), EditorStyles.boldLabel);
            DrawHelpMark("Inspector:AdditionalMaskSectionHelp");
            EditorGUILayout.EndHorizontal();
            DrawHelpBoxIfOpen("Inspector:AdditionalMaskSectionHelp");

            if (GUILayout.Button(CHMLocales.Tr("Inspector:GenerateAdditionalMasks")))
            {
                GenerateAdditionalMaskTextures(component);
            }

            bool hasPreviewMaterial = component.previewMaterial != null;

            if (component.generatedMasks.Count > 0)
            {
                // 生成後にUV配置や素材が変わっていたら再生成を促す
                if (component.generatedMasksInputHash != TextureAtlasBuilder.ComputeAdditionalMaskInputHash(component))
                {
                    EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:GeneratedMasksStale"), MessageType.Warning);
                }

                foreach (var entry in component.generatedMasks)
                {
                    if (entry == null || entry.texture == null) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField(GetMaskDisplayName(entry.propertyName), entry.texture, typeof(Texture2D), false);
                    EditorGUI.EndDisabledGroup();

                    // 解像度（インポータの maxTextureSize を直接編集。PNG自体はフル解像度のまま
                    // なので再生成不要で切り替えられる）
                    DrawGeneratedMaskSizePopup(entry.texture);

                    EditorGUI.BeginDisabledGroup(!hasPreviewMaterial);
                    if (GUILayout.Button(CHMLocales.Tr("Inspector:AssignToMaterial"), GUILayout.Width(60)))
                    {
                        AssignGeneratedMask(component, entry);
                    }
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                }
            }

            // 元の髪で使われているマットキャップ画像（視線ベースのため再配置不要。選んで割り当てる）
            var matCaps = TextureAtlasBuilder.CollectMatCapTextures(component);
            if (matCaps.Count > 0)
            {
                EditorGUILayout.LabelField(CHMLocales.Tr("Inspector:SourceMatCaps"), EditorStyles.miniBoldLabel);
                foreach (var (propertyName, texture) in matCaps)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField(GetMaskDisplayName(propertyName), texture, typeof(Texture2D), false);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.BeginDisabledGroup(!hasPreviewMaterial);
                    if (GUILayout.Button(CHMLocales.Tr("Inspector:AssignToMaterial"), GUILayout.Width(60)))
                    {
                        AssignMatCapTexture(component, propertyName, texture);
                    }
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (!hasPreviewMaterial && (component.generatedMasks.Count > 0 || matCaps.Count > 0))
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr("Inspector:AssignRequiresPreviewMaterial"), MessageType.Info);
            }
        }

        /// <summary>生成マスクの解像度ポップアップの選択肢</summary>
        private static readonly int[] MaskSizeValues = { 512, 1024, 2048, 4096 };
        private static readonly string[] MaskSizeLabels = { "512", "1024", "2048", "4096" };

        /// <summary>
        /// マスクプロパティの表示名（lilToonのマテリアル設定UIの「セクション名/欄名」に揃える。
        /// 例: _MatCapBlendMask → マットキャップ/マスク）
        /// </summary>
        private static string GetMaskDisplayName(string propertyName)
        {
            return CHMLocales.Tr("Inspector:MaskName:" + propertyName);
        }

        /// <summary>
        /// 生成マスクの解像度ポップアップ（インポータの maxTextureSize を直接読み書き）。
        /// 再生成してもインポータ設定は維持されるため、ユーザーの選択が失われない
        /// </summary>
        private static void DrawGeneratedMaskSizePopup(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return;
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;

            int newSize = EditorGUILayout.IntPopup(importer.maxTextureSize, MaskSizeLabels, MaskSizeValues, GUILayout.Width(60));
            if (newSize != importer.maxTextureSize)
            {
                importer.maxTextureSize = newSize;
                importer.SaveAndReimport();
            }
        }

        /// <summary>
        /// 追加マスク（マットキャップ/エミッション）を統合後UVに再配置し、PNGアセットとして保存
        /// </summary>
        private void GenerateAdditionalMaskTextures(ChimeraHairMaster component)
        {
            int resolution = (int)component.textureResolution;
            var generated = TextureAtlasBuilder.BuildAdditionalMaskTextures(component, resolution);

            Undo.RecordObject(component, "Generate Mask Textures");
            component.generatedMasks.Clear();
            component.generatedMasksInputHash = TextureAtlasBuilder.ComputeAdditionalMaskInputHash(component);

            if (generated.Count == 0)
            {
                EditorUtility.SetDirty(component);
                serializedObject.Update();
                Debug.Log("[ChimeraHairMaster] 生成対象のマットキャップ/エミッションマスクがありませんでした" +
                    "（素材側でマスク未使用、または機能ON/OFFの塗り分けが不要）");
                return;
            }

            string folder = GetGeneratedMasksFolder(component);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }

            foreach (var (def, texture) in generated)
            {
                // 同名パスへ上書きすることでGUIDを維持し、割り当て済み参照を壊さない
                string path = $"{folder}/Atlas{def.propertyName}.png";
                bool isNewAsset = !File.Exists(path);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);

                AssetDatabase.ImportAsset(path);
                if (AssetImporter.GetAtPath(path) is TextureImporter importer)
                {
                    // 初回生成時のみ既定解像度を設定（再生成でユーザーの選択を上書きしない）。
                    // マスクにフル解像度は通常不要なため 512〜2048 にクランプ
                    if (isNewAsset)
                    {
                        importer.maxTextureSize = Mathf.Clamp(resolution, 512, 2048);
                    }
                    // エミッション系のアルファは発光強度データであり透過表現ではない
                    importer.alphaIsTransparency = false;
                    // シェーダがアルファを読まないスロットはアルファを捨てて DXT1 に固定
                    // （ソース由来のアルファ混入で DXT5 = メモリ2倍に化けるのを防ぐ）
                    importer.alphaSource = def.alphaUnused
                        ? TextureImporterAlphaSource.None
                        : TextureImporterAlphaSource.FromInput;
                    // ビルド生成アトラスと同基準でミップストリーミングを有効化
                    importer.streamingMipmaps = true;
                    importer.SaveAndReimport();
                }

                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                component.generatedMasks.Add(new GeneratedMaskEntry
                {
                    propertyName = def.propertyName,
                    texture = asset,
                });
            }

            EditorUtility.SetDirty(component);
            serializedObject.Update();
            Debug.Log($"[ChimeraHairMaster] マスクテクスチャを {component.generatedMasks.Count} 件生成しました: {folder}");
        }

        /// <summary>
        /// 生成済みマスクを previewMaterial に割り当て、対応する機能トグルもONにする
        /// （割り当てたのに機能が無効で見えない事故の防止）
        /// </summary>
        private static void AssignGeneratedMask(ChimeraHairMaster component, GeneratedMaskEntry entry)
        {
            var previewMat = component.previewMaterial;
            if (previewMat == null || entry.texture == null) return;

            Undo.RecordObject(previewMat, "Assign Generated Mask");
            if (previewMat.HasProperty(entry.propertyName))
            {
                previewMat.SetTexture(entry.propertyName, entry.texture);
            }
            foreach (var def in TextureAtlasBuilder.AdditionalMaskSlots)
            {
                if (def.propertyName == entry.propertyName && previewMat.HasProperty(def.enableProperty))
                {
                    previewMat.SetFloat(def.enableProperty, 1f);
                }
            }
            EditorUtility.SetDirty(previewMat);
        }

        /// <summary>
        /// 元素材のマットキャップ画像を previewMaterial に割り当て、機能トグルもONにする
        /// </summary>
        private static void AssignMatCapTexture(ChimeraHairMaster component, string propertyName, Texture2D texture)
        {
            var previewMat = component.previewMaterial;
            if (previewMat == null || texture == null) return;

            Undo.RecordObject(previewMat, "Assign MatCap Texture");
            if (previewMat.HasProperty(propertyName))
            {
                previewMat.SetTexture(propertyName, texture);
            }
            string enableProperty = propertyName == "_MatCap2ndTex" ? "_UseMatCap2nd" : "_UseMatCap";
            if (previewMat.HasProperty(enableProperty))
            {
                previewMat.SetFloat(enableProperty, 1f);
            }
            EditorUtility.SetDirty(previewMat);
        }

        /// <summary>
        /// パス中のディレクトリが存在しなければ作成
        /// </summary>
        private static void EnsureDirectoryExists(string assetPath)
        {
            string dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// ファイル名として使用できない文字を除去
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            return Regex.Replace(name, @"[<>:""/\\|?*]", "_");
        }

        /// <summary>
        /// プレビュー用マテリアルを生成
        /// baseMaterialから数値パラメータとトグルのみをコピーし、テクスチャはMatCapのみコピー
        /// </summary>
        private void CreatePreviewMaterial()
        {
            var component = target as ChimeraHairMaster;
            if (component == null) return;
            if (component.baseMaterial == null) return;

            // テクスチャなしで新規マテリアル作成
            var previewMat = CreateMaterialWithoutTextures(component.baseMaterial);
            previewMat.name = component.baseMaterial.name + "_CHM_Preview";

            // MatCapテクスチャのみコピー
            CopyMatCapTextures(component.baseMaterial, previewMat);

            // メッシュ統合なしの場合、2nd/3rd/発光セクションを無効化
            if (!component.enableMeshMerge)
            {
                StripOverlayAndEmission(previewMat);
            }

            // アセットとして保存（シェーダー再インポート耐性）
            string path = GetPreviewMaterialAssetPath(component);
            EnsureDirectoryExists(path);
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            // 既存の previewMaterial がアセットなら削除
            // ※ CHM が生成したプレビューマテリアル（CHM_Generated フォルダ配下）のみ対象にする。
            //    ユーザーが割り当てた/フォルダ外へ移動した Asset を誤削除しないための所有権チェック。
            if (component.previewMaterial != null && AssetDatabase.Contains(component.previewMaterial))
            {
                string oldPath = AssetDatabase.GetAssetPath(component.previewMaterial);
                if (!string.IsNullOrEmpty(oldPath) && oldPath.Contains("CHM_Generated"))
                {
                    AssetDatabase.DeleteAsset(oldPath);
                }
                else if (!string.IsNullOrEmpty(oldPath))
                {
                    Debug.LogWarning($"[ChimeraHairMaster] previewMaterial '{oldPath}' は CHM 生成物ではないため削除しません（参照のみ差し替え）。");
                }
            }

            AssetDatabase.CreateAsset(previewMat, path);

            // コンポーネントに設定
            Undo.RecordObject(component, "Create Preview Material");
            component.previewMaterial = previewMat;
            EditorUtility.SetDirty(component);

            // SerializedObjectを更新
            serializedObject.Update();
        }

        /// <summary>
        /// テクスチャを除いて数値パラメータとトグルのみをコピーした新規マテリアルを作成
        /// すべてのテクスチャスロットを明示的にnullに設定
        /// </summary>
        private static Material CreateMaterialWithoutTextures(Material source)
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
        /// マットキャップテクスチャのみをコピー
        /// </summary>
        private static void CopyMatCapTextures(Material source, Material dest)
        {
            // マットキャップ画像のみコピーする。
            // マスク（_MatCapBlendMask等）とカスタムノーマル（_MatCapBumpMap等）はUV0参照のため、
            // 統合後UVとずれたままコピーされてしまう（顧客報告のズレの直接原因）。
            // 再配置済みマスクは「マスクテクスチャを生成」で作成し、ユーザーが割り当てる
            string[] matCapProps = new string[]
            {
                "_MatCapTex",
                "_MatCap2ndTex"
            };

            foreach (var prop in matCapProps)
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
        
        /// <summary>
        /// StripOverlayAndEmission で無効化する使用トグル一覧
        /// </summary>
        internal static readonly string[] OverlayAndEmissionToggleProps =
        {
            "_UseMain2ndTex",
            "_UseMain3rdTex",
            "_UseEmission",
            "_UseEmission2nd"
        };

        /// <summary>
        /// lilToonのメインカラー2nd/3rd・発光設定の使用トグルを0に設定
        /// これによりlilToonのカスタムGUIがこれらのセクションを非表示にする
        /// </summary>
        internal static void StripOverlayAndEmission(Material mat)
        {
            foreach (var prop in OverlayAndEmissionToggleProps)
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetFloat(prop, 0f);
                }
            }
        }

        /// <summary>
        /// StripOverlayAndEmission で無効化した使用トグルを baseMaterial の値に復元する。
        /// previewMaterial はメッシュ統合OFF時の生成でこれらが0固定されるため、
        /// 後からメッシュ統合ONに切り替えた場合、そのままだと出力マテリアル
        /// （previewMaterial の完全コピー）でも発光等が無効のままになる
        /// </summary>
        internal static void RestoreOverlayAndEmissionFromBase(Material baseMaterial, Material previewMaterial)
        {
            foreach (var prop in OverlayAndEmissionToggleProps)
            {
                if (baseMaterial.HasProperty(prop) && previewMaterial.HasProperty(prop))
                {
                    previewMaterial.SetFloat(prop, baseMaterial.GetFloat(prop));
                }
            }
        }

        /// <summary>
        /// マテリアルエディタをInspector内に埋め込み描画
        /// </summary>
        private void DrawEmbeddedMaterialEditor()
        {
            var component = target as ChimeraHairMaster;
            if (component == null || component.previewMaterial == null) return;
            
            // マテリアルが変わったらエディタを再作成
            if (cachedPreviewMaterial != component.previewMaterial)
            {
                CleanupMaterialEditor();
                cachedPreviewMaterial = component.previewMaterial;
            }
            
            // MaterialEditorを作成
            if (materialEditor == null)
            {
                materialEditor = (MaterialEditor)UnityEditor.Editor.CreateEditor(
                    component.previewMaterial, 
                    typeof(MaterialEditor));
            }
            
            if (materialEditor != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                
                // ヘッダー（プレビュー表示）
                materialEditor.DrawHeader();
                
                // シェーダーのインスペクター（lilToonのカスタムGUI）
                if (materialEditor.customShaderGUI != null)
                {
                    materialEditor.customShaderGUI.OnGUI(
                        materialEditor, 
                        MaterialEditor.GetMaterialProperties(new Object[] { component.previewMaterial }));
                }
                else
                {
                    // カスタムGUIがない場合はデフォルトのインスペクター
                    materialEditor.OnInspectorGUI();
                }
                
                EditorGUILayout.EndVertical();
            }
        }

        /// <summary>
        /// Boundsをシーンビューに描画
        /// </summary>
        [DrawGizmo(GizmoType.Selected)]
        private static void DrawBoundsGizmo(ChimeraHairMaster component, GizmoType gizmoType)
        {
            if (component == null || component.rootBone == null) return;

            Matrix4x4 oldMatrix = Gizmos.matrix;

            try
            {
                Gizmos.matrix = component.rootBone.localToWorldMatrix;
                Gizmos.color = new Color(0, 1, 1, 0.3f);
                Gizmos.DrawWireCube(component.bounds.center, component.bounds.size);
            }
            finally
            {
                Gizmos.matrix = oldMatrix;
            }
        }
    }
}
