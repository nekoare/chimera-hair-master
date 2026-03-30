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
        private static MeshDeformationSceneEditor _sceneEditor;

        private int _selectedRendererIndex = 0;
        private float _inflateAmount = 0f;
        private int _lastSelectionHash = 0;
        private int _lastOperationVersion = 0;
        private bool _inflateDragging = false;
        private int _inflateUndoGroup = -1;

        private bool _showAdvancedSettings
        {
            get => SessionState.GetBool("CHM_MeshDeform_ShowAdvanced", false);
            set => SessionState.SetBool("CHM_MeshDeform_ShowAdvanced", value);
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

        public static MeshDeformationSceneEditor ActiveSceneEditor => _sceneEditor;

        /// <summary>
        /// メッシュ変形セクションを描画する
        /// </summary>
        public void Draw(IMeshDeformationTarget component)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.indentLevel++;

            // Renderer選択（複数Rendererの場合のみ）
            DrawRendererSelection(component);

            EditorGUILayout.Space(5);

            // 編集モードボタン（縦並び、大→小の順）
            DrawEditModeButtons(component);

            // 編集中の場合、モード別UIを表示
            if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off)
            {
                EditorGUILayout.Space(5);

                if (SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Lattice)
                {
                    DrawLatticeSettings();
                }
                else
                {
                    // 頂点ツール（膨張/収縮・スムージング）
                    DrawVertexTools();
                }

                // 対称編集（頂点・ラティスモード共通、UVIslandでは非表示）
                if (SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Vertex
                    || SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Lattice)
                {
                    EditorGUILayout.Space(3);
                    DrawSymmetrySettings();
                }

                // 詳細設定（頂点モードとUVIslandモードのみ、ラティスでは非表示）
                if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Lattice)
                {
                    EditorGUILayout.Space(3);
                    DrawAdvancedSettings();
                }
            }

            EditorGUILayout.Space(5);

            // リセット → エクスポート の順
            DrawResetButton(component);
            DrawExportButtons(component);

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

            // パーツ変形（UVアイランド）
            DrawModeButton(component, "パーツ変形", "■ パーツ変形中",
                MeshDeformationSceneEditor.EditMode.UVIsland, currentMode,
                new Color(0.4f, 0.8f, 1f));

            // 頂点変形
            DrawModeButton(component, "頂点変形", "■ 頂点変形中",
                MeshDeformationSceneEditor.EditMode.Vertex, currentMode,
                new Color(1f, 0.7f, 0.3f));

            // 全体の形を調整（ラティス変形）
            DrawModeButton(component, "全体の形を調整 (ラティス変形)", "■ ラティス編集中",
                MeshDeformationSceneEditor.EditMode.Lattice, currentMode,
                new Color(0.5f, 1f, 0.5f));

            // ラティス編集中のみ「戻す」ボタンを直下に表示
            if (currentMode == MeshDeformationSceneEditor.EditMode.Lattice && SceneEditor.HasLattice)
            {
                EditorGUILayout.Space(5);
                GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
                if (GUILayout.Button(new GUIContent("ラティス変形前に戻す",
                    "ラティスで行ったすべての変形を取り消し、作成前の状態に戻します")))
                {
                    SceneEditor.CancelLattice();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        private void DrawModeButton(IMeshDeformationTarget component, string label, string activeLabel,
            MeshDeformationSceneEditor.EditMode mode, MeshDeformationSceneEditor.EditMode currentMode,
            Color activeColor)
        {
            bool isActive = currentMode == mode;
            GUI.backgroundColor = isActive ? activeColor : Color.white;
            if (GUILayout.Button(isActive ? activeLabel : label, GUILayout.Height(EditorGUIUtility.singleLineHeight * 1.5f)))
            {
                if (isActive)
                    SceneEditor.EndEdit();
                else
                    SceneEditor.BeginEdit(component, _selectedRendererIndex, mode);
                UnityEditor.SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
        }

        #region ラティス設定

        private void DrawLatticeSettings()
        {
            EditorGUILayout.LabelField("ラティス設定", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "制御点をドラッグで移動、クリックで選択するとXYZ軸ハンドルが表示されます。\n" +
                "変形はリアルタイムに反映されます。",
                MessageType.Info);

            var div = SceneEditor.LatticeDivisions;
            EditorGUI.BeginChangeCheck();
            int newDivX = EditorGUILayout.IntSlider("X分割", div.x, 1, 5);
            int newDivY = EditorGUILayout.IntSlider("Y分割", div.y, 1, 5);
            int newDivZ = EditorGUILayout.IntSlider("Z分割", div.z, 1, 5);
            if (EditorGUI.EndChangeCheck())
            {
                SceneEditor.RecreateLattice(newDivX, newDivY, newDivZ);
            }

        }

        #endregion

        #region 頂点ツール

        private void DrawVertexTools()
        {
            // 選択状態のリセット検知
            int currentHash = SceneEditor.SelectionHash;
            int currentVersion = SceneEditor.OperationVersion;
            if (currentHash != _lastSelectionHash || currentVersion != _lastOperationVersion)
            {
                _lastSelectionHash = currentHash;
                _lastOperationVersion = currentVersion;
                _inflateAmount = 0f;
            }

            if (SceneEditor.SelectedVertexCount == 0) return;

            EditorGUILayout.LabelField("選択中の頂点ツール", EditorStyles.boldLabel);

            // 膨張/収縮スライダー
            int hotBefore = GUIUtility.hotControl;

            EditorGUI.BeginChangeCheck();
            float newInflate = EditorGUILayout.Slider(_inflateAmount, -0.02f, 0.02f);

            // スライダーの左右にラベルを表示
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("収縮 ←", EditorStyles.label);
            GUILayout.FlexibleSpace();
            GUILayout.Label("→ 膨張", EditorStyles.label);
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                if (!_inflateDragging)
                {
                    _inflateDragging = true;
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(SceneEditor.TargetComponent.UndoTarget, "メッシュ変形: 膨張/収縮");
                    Undo.SetCurrentGroupName("メッシュ変形: 膨張/収縮");
                    _inflateUndoGroup = Undo.GetCurrentGroup();
                }

                float delta = newInflate - _inflateAmount;
                _inflateAmount = newInflate;
                if (Mathf.Abs(delta) > 0.00001f)
                {
                    SceneEditor.ApplyInflate(delta);
                }
            }

            if (_inflateDragging && GUIUtility.hotControl != hotBefore && GUIUtility.hotControl == 0)
            {
                if (_inflateUndoGroup >= 0)
                    Undo.CollapseUndoOperations(_inflateUndoGroup);
                _inflateDragging = false;
                _inflateUndoGroup = -1;
            }

            if (Mathf.Abs(_inflateAmount) > 0.0001f)
            {
                if (GUILayout.Button("スライダーを0にリセット"))
                {
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(SceneEditor.TargetComponent.UndoTarget, "メッシュ変形: 膨張/収縮リセット");
                    SceneEditor.ApplyInflate(-_inflateAmount);
                    _inflateAmount = 0f;
                }
            }

            EditorGUILayout.Space(3);

            // スムージング
            if (GUILayout.Button(new GUIContent("スムージング",
                "選択頂点の位置を周囲の頂点に近づけて滑らかにする。複数回押すとより滑らかに")))
            {
                SceneEditor.ApplySmooth(0.5f);
                _inflateAmount = 0f;
            }
        }

        #endregion

        #region 対称編集

        private void DrawSymmetrySettings()
        {
            EditorGUILayout.LabelField("対称編集", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // ラティスモード時は分割数1の軸を無効化
            bool isLattice = SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Lattice;
            var div = SceneEditor.LatticeDivisions;

            bool canX = !isLattice || div.x >= 2;
            bool canY = !isLattice || div.y >= 2;
            bool canZ = !isLattice || div.z >= 2;

            using (new EditorGUI.DisabledScope(!canX))
            {
                bool newX = GUILayout.Toggle(SceneEditor.SymmetryX && canX, "X (左右)", EditorStyles.miniButtonLeft);
                if (canX && newX && !SceneEditor.SymmetryX)
                {
                    SceneEditor.SymmetryY = false;
                    SceneEditor.SymmetryZ = false;
                    SceneEditor.SymmetryX = true;
                }
                else if (canX) SceneEditor.SymmetryX = newX;
            }
            using (new EditorGUI.DisabledScope(!canY))
            {
                bool newY = GUILayout.Toggle(SceneEditor.SymmetryY && canY, "Y (前後)", EditorStyles.miniButtonMid);
                if (canY && newY && !SceneEditor.SymmetryY)
                {
                    SceneEditor.SymmetryX = false;
                    SceneEditor.SymmetryZ = false;
                    SceneEditor.SymmetryY = true;
                }
                else if (canY) SceneEditor.SymmetryY = newY;
            }
            using (new EditorGUI.DisabledScope(!canZ))
            {
                bool newZ = GUILayout.Toggle(SceneEditor.SymmetryZ && canZ, "Z (上下)", EditorStyles.miniButtonRight);
                if (canZ && newZ && !SceneEditor.SymmetryZ)
                {
                    SceneEditor.SymmetryX = false;
                    SceneEditor.SymmetryY = false;
                    SceneEditor.SymmetryZ = true;
                }
                else if (canZ) SceneEditor.SymmetryZ = newZ;
            }

            EditorGUILayout.EndHorizontal();

            // 有効な軸のオフセット入力
            if (SceneEditor.SymmetryX)
                SceneEditor.SymmetryOffsetX = EditorGUILayout.FloatField("オフセット X", SceneEditor.SymmetryOffsetX);
            if (SceneEditor.SymmetryY)
                SceneEditor.SymmetryOffsetY = EditorGUILayout.FloatField("オフセット Y", SceneEditor.SymmetryOffsetY);
            if (SceneEditor.SymmetryZ)
                SceneEditor.SymmetryOffsetZ = EditorGUILayout.FloatField("オフセット Z", SceneEditor.SymmetryOffsetZ);
        }

        #endregion

        #region 詳細設定

        private void DrawAdvancedSettings()
        {
            _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "詳細設定", true);
            if (!_showAdvancedSettings) return;

            EditorGUI.indentLevel++;

            // ブラシ設定（頂点モードのみ）
            if (SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Vertex)
            {
                SceneEditor.BrushRadius = EditorGUILayout.Slider(
                    new GUIContent("影響範囲 (m)", "選択した頂点の周囲にどこまで変形の影響を広げるか。ドラッグ中にマウスホイールで変更可能"),
                    SceneEditor.BrushRadius, 0.001f, 1.0f);
                SceneEditor.Falloff = (FalloffType)EditorGUILayout.EnumPopup(
                    new GUIContent("距離減衰", "影響範囲の端に向かって変形量をどう弱めるか"),
                    SceneEditor.Falloff);
                SceneEditor.Metric = (DistanceMetric)EditorGUILayout.EnumPopup(
                    new GUIContent("距離の扱い", "頂点間の距離をどう計算するか"),
                    SceneEditor.Metric);
            }

            // 表示設定
            SceneEditor.BackfaceCulling = EditorGUILayout.Toggle(
                new GUIContent("背面カリング", "こちらを向いていない頂点を非表示にして選択対象から除外する"),
                SceneEditor.BackfaceCulling);
            SceneEditor.ZTest = EditorGUILayout.Toggle(
                new GUIContent("Z-test（遮蔽非表示）", "他のメッシュの裏に隠れている頂点を非表示にする"),
                SceneEditor.ZTest);

            EditorGUI.indentLevel--;
        }

        #endregion

        #region エクスポート・リセット

        private void DrawExportButtons(IMeshDeformationTarget component)
        {
            if (component.RendererDeformations == null || component.RendererDeformations.Count == 0)
                return;

            var deformation = component.RendererDeformations.Find(
                d => d.rendererIndex == _selectedRendererIndex);
            if (deformation == null || deformation.deltas.Count == 0)
                return;

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

                    deformation.deltas.Clear();

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
                    if (SceneEditor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off
                        && SceneEditor.ActiveRendererIndex == _selectedRendererIndex)
                    {
                        SceneEditor.ResetDeltas();
                    }
                    else
                    {
                        Undo.RegisterCompleteObjectUndo(component.UndoTarget, "Reset Mesh Deformation");

                        if (deformation != null)
                            deformation.deltas.Clear();

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

                        component.DeformEditingRendererIndex = -1;
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(3);
        }

        #endregion

        public void Cleanup()
        {
        }
    }
}
