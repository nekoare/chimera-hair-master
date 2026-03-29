using ChimeraHairMaster.Editor.Processing;
using ChimeraHairMaster.Editor.Deformation;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// メッシュ変形セクションのInspector UI
    /// ChimeraHairMasterEditor.OnInspectorGUI() から呼び出される
    /// </summary>
    public class MeshDeformationInspectorUI
    {
        /// <summary>
        /// SceneEditorはstaticインスタンスとして保持する。
        /// Inspectorの OnEnable/OnDisable でインスタンスが作り直されても
        /// 編集セッション（作業メッシュ・デルタ・選択状態）が維持される。
        /// </summary>
        private static MeshDeformationSceneEditor _sceneEditor;

        private int _selectedRendererIndex = 0;

        private bool _showDisplaySettings
        {
            get => SessionState.GetBool("CHM_MeshDeform_ShowDisplay", false);
            set => SessionState.SetBool("CHM_MeshDeform_ShowDisplay", value);
        }

        public MeshDeformationSceneEditor SceneEditor
        {
            get
            {
                if (_sceneEditor == null)
                    _sceneEditor = new MeshDeformationSceneEditor();
                return _sceneEditor;
            }
        }

        /// <summary>
        /// staticなSceneEditorへの外部アクセス用（Scene GUIコールバックなどから使用）
        /// </summary>
        public static MeshDeformationSceneEditor ActiveSceneEditor => _sceneEditor;

        /// <summary>
        /// メッシュ変形セクションを描画する
        /// </summary>
        public void Draw(IMeshDeformationTarget component)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.indentLevel++;

            // Renderer選択
            DrawRendererSelection(component);

            EditorGUILayout.Space(5);

            // 編集モードボタン
            DrawEditModeButtons(component);

            // 編集中の場合、設定・ステータスを表示
            if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off)
            {
                // 頂点モードのみブラシ設定を表示
                if (SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Vertex)
                {
                    EditorGUILayout.Space(5);
                    DrawBrushSettings();
                }
                // 表示設定は両モード共通
                EditorGUILayout.Space(3);
                DrawDisplaySettings();
                EditorGUILayout.Space(5);
                DrawEditStatus();
            }

            EditorGUILayout.Space(5);

            // エクスポートボタン
            DrawExportButtons(component);

            EditorGUILayout.Space(5);

            // リセットボタン
            DrawResetButton(component);

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        private void DrawRendererSelection(IMeshDeformationTarget component)
        {
            if (component.DeformTargetRenderers == null || component.DeformTargetRenderers.Count == 0)
            {
                EditorGUILayout.HelpBox("対象Rendererが設定されていません", MessageType.Info);
                return;
            }

            // Rendererが1つだけならセレクタを表示しない（スタンドアロン等）
            if (component.DeformTargetRenderers.Count == 1)
            {
                _selectedRendererIndex = 0;
                return;
            }

            var names = new string[component.DeformTargetRenderers.Count];
            for (int i = 0; i < component.DeformTargetRenderers.Count; i++)
            {
                var r = component.DeformTargetRenderers[i];
                names[i] = r != null ? r.name : "(missing)";
            }

            _selectedRendererIndex = Mathf.Clamp(_selectedRendererIndex, 0, names.Length - 1);

            EditorGUI.BeginChangeCheck();
            _selectedRendererIndex = EditorGUILayout.Popup("対象Renderer", _selectedRendererIndex, names);
            if (EditorGUI.EndChangeCheck())
            {
                // Renderer切替時、編集中なら新しいRendererで編集セッションを再開
                var currentMode = SceneEditor.CurrentMode;
                if (currentMode != MeshDeformationSceneEditor.EditMode.Off)
                {
                    SceneEditor.BeginEdit(component, _selectedRendererIndex, currentMode);
                    UnityEditor.SceneView.RepaintAll();
                }
            }
        }

        private void DrawEditModeButtons(IMeshDeformationTarget component)
        {
            var currentMode = SceneEditor.CurrentMode;
            bool isEditing = currentMode != MeshDeformationSceneEditor.EditMode.Off;

            EditorGUILayout.BeginHorizontal();

            // UVアイランドモード
            var islandActive = currentMode == MeshDeformationSceneEditor.EditMode.UVIsland;
            GUI.backgroundColor = islandActive ? new Color(0.4f, 0.8f, 1f) : Color.white;
            if (GUILayout.Button(islandActive ? "■ UVアイランド編集中" : "UVアイランド編集"))
            {
                if (islandActive)
                    SceneEditor.EndEdit();
                else
                    SceneEditor.BeginEdit(component, _selectedRendererIndex,
                        MeshDeformationSceneEditor.EditMode.UVIsland);
                UnityEditor.SceneView.RepaintAll();
            }

            // 頂点モード
            var vertexActive = currentMode == MeshDeformationSceneEditor.EditMode.Vertex;
            GUI.backgroundColor = vertexActive ? new Color(1f, 0.7f, 0.3f) : Color.white;
            if (GUILayout.Button(vertexActive ? "■ 頂点編集中" : "頂点編集"))
            {
                if (vertexActive)
                    SceneEditor.EndEdit();
                else
                    SceneEditor.BeginEdit(component, _selectedRendererIndex,
                        MeshDeformationSceneEditor.EditMode.Vertex);
                UnityEditor.SceneView.RepaintAll();
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBrushSettings()
        {
            EditorGUILayout.LabelField("編集設定", EditorStyles.boldLabel);
            SceneEditor.BrushRadius = EditorGUILayout.Slider(
                new GUIContent("影響範囲 (m)", "選択した頂点の周囲にどこまで変形の影響を広げるか。ドラッグ中にマウスホイールで変更可能"),
                SceneEditor.BrushRadius, 0.001f, 1.0f);
            SceneEditor.Falloff = (FalloffType)EditorGUILayout.EnumPopup(
                new GUIContent("距離減衰", "影響範囲の端に向かって変形量をどう弱めるか\n・均一: 範囲内すべて同じ強さ\n・直線: 距離に比例して弱まる\n・スムーズ: 自然な曲線で弱まる\n・中心優先: 中心付近に影響を集中"),
                SceneEditor.Falloff);
            SceneEditor.Metric = (DistanceMetric)EditorGUILayout.EnumPopup(
                new GUIContent("距離の扱い", "頂点間の距離をどう計算するか\n・空間上の距離: 直線距離で計算（高速）\n・つかんでいる表面の距離: メッシュの面に沿って計算（裏側に貫通しない）"),
                SceneEditor.Metric);
        }

        private void DrawDisplaySettings()
        {
            _showDisplaySettings = EditorGUILayout.Foldout(_showDisplaySettings, "表示設定", true);
            if (_showDisplaySettings)
            {
                EditorGUI.indentLevel++;
                SceneEditor.BackfaceCulling = EditorGUILayout.Toggle(
                    new GUIContent("背面カリング", "こちらを向いていない頂点を非表示にして選択対象から除外する"),
                    SceneEditor.BackfaceCulling);
                SceneEditor.ZTest = EditorGUILayout.Toggle(
                    new GUIContent("Z-test（遮蔽非表示）", "他のメッシュの裏に隠れている頂点を非表示にする"),
                    SceneEditor.ZTest);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawEditStatus()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"選択頂点数: {SceneEditor.SelectedVertexCount}");
            EditorGUILayout.LabelField($"変形頂点数: {SceneEditor.ActiveDeltaCount}");
            EditorGUILayout.EndVertical();

            // 選択中の頂点に対するツール
            if (SceneEditor.SelectedVertexCount > 0)
            {
                EditorGUILayout.Space(3);
                if (GUILayout.Button(new GUIContent("スムージング",
                    "選択頂点の位置を周囲の頂点に近づけて滑らかにする。複数回押すとより滑らかに。ガタガタになってしまった時に調整できます。")))
                {
                    SceneEditor.ApplySmooth(0.5f);
                }
            }
        }

        private void DrawExportButtons(IMeshDeformationTarget component)
        {
            if (component.RendererDeformations == null || component.RendererDeformations.Count == 0)
                return;

            var deformation = component.RendererDeformations.Find(
                d => d.rendererIndex == _selectedRendererIndex);
            if (deformation == null || deformation.deltas.Count == 0)
                return;

            EditorGUILayout.LabelField("エクスポート", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("メッシュを保存"))
            {
                ExportMesh(component, deformation, replaceOriginal: false);
            }

            if (GUILayout.Button("メッシュを保存して入れ替え"))
            {
                ExportMesh(component, deformation, replaceOriginal: true);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void ExportMesh(IMeshDeformationTarget component, RendererDeformation deformation, bool replaceOriginal)
        {
            if (_selectedRendererIndex < 0 || _selectedRendererIndex >= component.DeformTargetRenderers.Count)
                return;

            var renderer = component.DeformTargetRenderers[_selectedRendererIndex];
            if (renderer == null || renderer.sharedMesh == null) return;

            var exportedMesh = MeshDeformer.ExportDeformedMesh(renderer, deformation);
            if (exportedMesh == null) return;

            var path = EditorUtility.SaveFilePanelInProject(
                "変形済みメッシュを保存",
                renderer.sharedMesh.name + "_Deformed",
                "asset",
                "変形済みメッシュの保存先を選択してください");

            if (string.IsNullOrEmpty(path))
            {
                Object.DestroyImmediate(exportedMesh);
                return;
            }

            AssetDatabase.CreateAsset(exportedMesh, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ChimeraHairMaster] 変形済みメッシュを保存: {path}");

            if (replaceOriginal)
            {
                if (EditorUtility.DisplayDialog(
                    "メッシュの入れ替え確認",
                    $"{renderer.name} のメッシュを変形済みメッシュに入れ替えますか？\n" +
                    "変形データはメッシュに焼き込まれ、デルタはクリアされます。\n" +
                    "この操作はUndo可能です。",
                    "入れ替える", "キャンセル"))
                {
                    Undo.RegisterCompleteObjectUndo(component.UndoTarget, "Replace Mesh with Deformed");
                    Undo.RecordObject(renderer, "Replace Mesh with Deformed");
                    renderer.sharedMesh = exportedMesh;

                    // デルタをクリア（メッシュに焼き込み済みなので不要）
                    // クリアしないとMeshDeformPassがビルド時に二重適用する
                    deformation.deltas.Clear();

                    // 編集中なら終了
                    if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off
                        && SceneEditor.ActiveRendererIndex == _selectedRendererIndex)
                    {
                        SceneEditor.EndEdit();
                    }

                    Debug.Log($"[ChimeraHairMaster] メッシュを入れ替え（デルタクリア済み）: {renderer.name}");
                }
            }
        }

        private void DrawResetButton(IMeshDeformationTarget component)
        {
            var deformation = component.RendererDeformations?.Find(
                d => d.rendererIndex == _selectedRendererIndex);

            // デルタがある、またはドメインリロードで残った原本メッシュがある場合に表示
            bool hasDeltas = deformation != null && deformation.deltas.Count > 0;
            bool hasOrphanedMesh = component.DeformOriginalMesh != null;
            if (!hasDeltas && !hasOrphanedMesh) return;

            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("変形をリセット"))
            {
                if (EditorUtility.DisplayDialog(
                    "変形リセット確認",
                    "このRendererの変形データをすべてリセットしますか？",
                    "リセット", "キャンセル"))
                {
                    // 編集中ならScene Editorのリセット処理を使う
                    if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off
                        && SceneEditor.ActiveRendererIndex == _selectedRendererIndex)
                    {
                        SceneEditor.ResetDeltas();
                    }
                    else
                    {
                        Undo.RegisterCompleteObjectUndo(component.UndoTarget, "Reset Mesh Deformation");

                        // デルタをクリア
                        if (deformation != null)
                            deformation.deltas.Clear();

                        // ドメインリロードで残った原本メッシュがあれば復元
                        if (component.DeformOriginalMesh != null)
                        {
                            if (_selectedRendererIndex >= 0
                                && _selectedRendererIndex < component.DeformTargetRenderers.Count)
                            {
                                var renderer = component.DeformTargetRenderers[_selectedRendererIndex];
                                if (renderer != null)
                                {
                                    Undo.RecordObject(renderer, "Reset Mesh Deformation");
                                    renderer.sharedMesh = component.DeformOriginalMesh;
                                }
                            }
                            component.DeformOriginalMesh = null;
                        }

                        // DeformEditingRendererIndexも念のためクリア
                        component.DeformEditingRendererIndex = -1;
                    }
                }
            }
            GUI.backgroundColor = Color.white;
        }

        /// <summary>
        /// クリーンアップ（Editor OnDisable時に呼ぶ）
        /// SceneEditorはstaticなので編集セッションは終了しない。
        /// Inspectorが閉じても編集は維持され、再度開いた時に再接続できる。
        /// </summary>
        public void Cleanup()
        {
            // SceneEditorはstaticなのでEndEditは呼ばない。
            // 編集セッションはユーザーが明示的に終了ボタンを押すか、
            // コンポーネントが破棄された時にのみ終了する。
        }
    }
}
