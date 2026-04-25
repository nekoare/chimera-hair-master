using ChimeraHairMaster.Editor.Processing;
using ChimeraHairMaster.Editor.Deformation;
using UnityEditor;
using UnityEngine;
using ChimeraHairMaster.Editor.Localization;

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
                EditorGUILayout.HelpBox(CHMLocales.Tr("MeshDeformInspector:RendererNotSet"), MessageType.Info);
                return;
            }

            if (component.DeformTargetRenderers.Count == 1)
            {
                _selectedRendererIndex = 0;
                return;
            }

            int count = component.DeformTargetRenderers.Count;
            var names = new string[count];

            // Popup は GenericMenu ベースで同名項目を一意キー扱いするため、
            // 同名 GameObject が登録されていると 2 件目以降が選択不能になる。
            // 重複名のみ末尾に (2), (3) ... を付けて一意化する（非重複名は素のまま）。
            var nameCounts = new System.Collections.Generic.Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                var r = component.DeformTargetRenderers[i];
                var baseName = r != null ? r.name : "(missing)";
                names[i] = baseName;
                nameCounts[baseName] = nameCounts.TryGetValue(baseName, out int c) ? c + 1 : 1;
            }
            var running = new System.Collections.Generic.Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                var baseName = names[i];
                if (nameCounts[baseName] <= 1) continue;
                int occ = running.TryGetValue(baseName, out int c) ? c + 1 : 1;
                running[baseName] = occ;
                if (occ > 1) names[i] = $"{baseName} ({occ})";
            }

            _selectedRendererIndex = Mathf.Clamp(_selectedRendererIndex, 0, names.Length - 1);

            EditorGUI.BeginChangeCheck();
            _selectedRendererIndex = EditorGUILayout.Popup(CHMLocales.Tr("MeshDeformInspector:TargetRenderer"), _selectedRendererIndex, names);
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
            DrawModeButton(component, CHMLocales.Tr("MeshDeformInspector:Mode:UVIsland"), CHMLocales.Tr("MeshDeformInspector:Mode:UVIslandActive"),
                MeshDeformationSceneEditor.EditMode.UVIsland, currentMode,
                new Color(0.4f, 0.8f, 1f));

            // 頂点変形
            DrawModeButton(component, CHMLocales.Tr("MeshDeformInspector:Mode:Vertex"), CHMLocales.Tr("MeshDeformInspector:Mode:VertexActive"),
                MeshDeformationSceneEditor.EditMode.Vertex, currentMode,
                new Color(1f, 0.7f, 0.3f));

            // 全体の形を調整（ラティス変形）
            DrawModeButton(component, CHMLocales.Tr("MeshDeformInspector:Mode:Lattice"), CHMLocales.Tr("MeshDeformInspector:Mode:LatticeActive"),
                MeshDeformationSceneEditor.EditMode.Lattice, currentMode,
                new Color(0.5f, 1f, 0.5f));

            // ラティス編集中のみ「戻す」ボタンを直下に表示
            if (currentMode == MeshDeformationSceneEditor.EditMode.Lattice && SceneEditor.HasLattice)
            {
                EditorGUILayout.Space(5);
                GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
                if (GUILayout.Button(new GUIContent(CHMLocales.Tr("MeshDeformInspector:Lattice:Revert"),
                    CHMLocales.Tr("MeshDeformInspector:Lattice:RevertTooltip"))))
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
            EditorGUILayout.LabelField(CHMLocales.Tr("MeshDeformInspector:Lattice:SettingsHeader"), EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                CHMLocales.Tr("MeshDeformInspector:Lattice:HelpBox"),
                MessageType.Info);

            var div = SceneEditor.LatticeDivisions;
            EditorGUI.BeginChangeCheck();
            int newDivX = EditorGUILayout.IntSlider(CHMLocales.Tr("MeshDeformInspector:Lattice:DivX"), div.x, 1, 5);
            int newDivY = EditorGUILayout.IntSlider(CHMLocales.Tr("MeshDeformInspector:Lattice:DivY"), div.y, 1, 5);
            int newDivZ = EditorGUILayout.IntSlider(CHMLocales.Tr("MeshDeformInspector:Lattice:DivZ"), div.z, 1, 5);
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

            EditorGUILayout.LabelField(CHMLocales.Tr("MeshDeformInspector:Vertex:ToolsHeader"), EditorStyles.boldLabel);

            // 膨張/収縮スライダー
            int hotBefore = GUIUtility.hotControl;

            EditorGUI.BeginChangeCheck();
            float newInflate = EditorGUILayout.Slider(_inflateAmount, -0.02f, 0.02f);

            // スライダーの左右にラベルを表示
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(CHMLocales.Tr("MeshDeformInspector:Vertex:Shrink"), EditorStyles.label);
            GUILayout.FlexibleSpace();
            GUILayout.Label(CHMLocales.Tr("MeshDeformInspector:Vertex:Inflate"), EditorStyles.label);
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
                if (GUILayout.Button(CHMLocales.Tr("MeshDeformInspector:Vertex:ResetSlider")))
                {
                    Undo.IncrementCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(SceneEditor.TargetComponent.UndoTarget, "メッシュ変形: 膨張/収縮リセット");
                    SceneEditor.ApplyInflate(-_inflateAmount);
                    _inflateAmount = 0f;
                }
            }

            EditorGUILayout.Space(3);

            // スムージング
            if (GUILayout.Button(new GUIContent(CHMLocales.Tr("MeshDeformInspector:Vertex:Smooth"),
                CHMLocales.Tr("MeshDeformInspector:Vertex:SmoothTooltip"))))
            {
                SceneEditor.ApplySmooth(0.5f);
                _inflateAmount = 0f;
            }
        }

        #endregion

        #region 対称編集

        private void DrawSymmetrySettings()
        {
            EditorGUILayout.LabelField(CHMLocales.Tr("MeshDeformInspector:Symmetry:Header"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // ラティスモード時は分割数1の軸を無効化
            bool isLattice = SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Lattice;
            var div = SceneEditor.LatticeDivisions;

            bool canX = !isLattice || div.x >= 2;
            bool canY = !isLattice || div.y >= 2;
            bool canZ = !isLattice || div.z >= 2;

            using (new EditorGUI.DisabledScope(!canX))
            {
                bool newX = GUILayout.Toggle(SceneEditor.SymmetryX && canX, CHMLocales.Tr("MeshDeformInspector:Symmetry:X"), EditorStyles.miniButtonLeft);
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
                bool newY = GUILayout.Toggle(SceneEditor.SymmetryY && canY, CHMLocales.Tr("MeshDeformInspector:Symmetry:Y"), EditorStyles.miniButtonMid);
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
                bool newZ = GUILayout.Toggle(SceneEditor.SymmetryZ && canZ, CHMLocales.Tr("MeshDeformInspector:Symmetry:Z"), EditorStyles.miniButtonRight);
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
                SceneEditor.SymmetryOffsetX = EditorGUILayout.FloatField(CHMLocales.Tr("MeshDeformInspector:Symmetry:OffsetX"), SceneEditor.SymmetryOffsetX);
            if (SceneEditor.SymmetryY)
                SceneEditor.SymmetryOffsetY = EditorGUILayout.FloatField(CHMLocales.Tr("MeshDeformInspector:Symmetry:OffsetY"), SceneEditor.SymmetryOffsetY);
            if (SceneEditor.SymmetryZ)
                SceneEditor.SymmetryOffsetZ = EditorGUILayout.FloatField(CHMLocales.Tr("MeshDeformInspector:Symmetry:OffsetZ"), SceneEditor.SymmetryOffsetZ);
        }

        #endregion

        #region 詳細設定

        private void DrawAdvancedSettings()
        {
            _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, CHMLocales.Tr("MeshDeformInspector:Advanced:Header"), true);
            if (!_showAdvancedSettings) return;

            EditorGUI.indentLevel++;

            // ブラシ設定（頂点モードのみ）
            if (SceneEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Vertex)
            {
                SceneEditor.BrushRadius = EditorGUILayout.Slider(
                    new GUIContent(CHMLocales.Tr("MeshDeformInspector:Advanced:BrushRadius"), CHMLocales.Tr("MeshDeformInspector:Advanced:BrushRadiusTooltip")),
                    SceneEditor.BrushRadius, 0.001f, 1.0f);
                var falloffNames = new[]
                {
                    CHMLocales.Tr("FalloffType:Constant"),
                    CHMLocales.Tr("FalloffType:Linear"),
                    CHMLocales.Tr("FalloffType:Smooth"),
                    CHMLocales.Tr("FalloffType:Sphere"),
                };
                SceneEditor.Falloff = (FalloffType)EditorGUILayout.Popup(
                    new GUIContent(CHMLocales.Tr("MeshDeformInspector:Advanced:Falloff"), CHMLocales.Tr("MeshDeformInspector:Advanced:FalloffTooltip")),
                    (int)SceneEditor.Falloff, falloffNames);
                var metricNames = new[]
                {
                    CHMLocales.Tr("DistanceMetric:Euclidean"),
                    CHMLocales.Tr("DistanceMetric:Geodesic"),
                };
                SceneEditor.Metric = (DistanceMetric)EditorGUILayout.Popup(
                    new GUIContent(CHMLocales.Tr("MeshDeformInspector:Advanced:Metric"), CHMLocales.Tr("MeshDeformInspector:Advanced:MetricTooltip")),
                    (int)SceneEditor.Metric, metricNames);
            }

            // 表示設定
            SceneEditor.BackfaceCulling = EditorGUILayout.Toggle(
                new GUIContent(CHMLocales.Tr("MeshDeformInspector:Advanced:BackfaceCulling"), CHMLocales.Tr("MeshDeformInspector:Advanced:BackfaceCullingTooltip")),
                SceneEditor.BackfaceCulling);
            SceneEditor.ZTest = EditorGUILayout.Toggle(
                new GUIContent(CHMLocales.Tr("MeshDeformInspector:Advanced:ZTest"), CHMLocales.Tr("MeshDeformInspector:Advanced:ZTestTooltip")),
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

            // 出力設定: Blendshape として出力するかのトグル + 名前
            EditorGUI.BeginChangeCheck();
            bool newToggle = EditorGUILayout.Toggle(
                CHMLocales.Tr("MeshDeformInspector:Export:AsBlendshape"),
                component.ExportAsBlendshape);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(component.UndoTarget, "Toggle Export As Blendshape");
                component.ExportAsBlendshape = newToggle;
                EditorUtility.SetDirty(component.UndoTarget);
            }

            if (component.ExportAsBlendshape)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField(
                    CHMLocales.Tr("MeshDeformInspector:Export:BlendshapeName"),
                    component.BlendshapeName ?? "CHMDeform");
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(component.UndoTarget, "Edit Blendshape Name");
                    component.BlendshapeName = newName;
                    EditorUtility.SetDirty(component.UndoTarget);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(CHMLocales.Tr("MeshDeformInspector:Export:SaveMesh")))
            {
                ExportMesh(component, deformation, replaceOriginal: false);
            }

            if (GUILayout.Button(CHMLocales.Tr("MeshDeformInspector:Export:SaveAndReplace")))
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

            // Blendshape モード or 焼き込みモード
            bool asBlendshape = component.ExportAsBlendshape;
            string actualBsName = null;
            Mesh exportedMesh;
            if (asBlendshape)
            {
                exportedMesh = MeshDeformer.ExportDeformedMeshAsBlendshape(
                    renderer, deformation,
                    string.IsNullOrEmpty(component.BlendshapeName) ? "CHMDeform" : component.BlendshapeName,
                    out actualBsName);
            }
            else
            {
                exportedMesh = MeshDeformer.ExportDeformedMesh(renderer, deformation);
            }
            if (exportedMesh == null) return;

            string suffix = asBlendshape ? "_DeformedBS" : "_Deformed";

            // 元メッシュと同じフォルダを初期保存先にする
            string originalAssetPath = AssetDatabase.GetAssetPath(renderer.sharedMesh);
            string defaultFolder = string.IsNullOrEmpty(originalAssetPath)
                ? "Assets"
                : System.IO.Path.GetDirectoryName(originalAssetPath);

            var path = EditorUtility.SaveFilePanelInProject(
                CHMLocales.Tr("MeshDeformInspector:Export:SaveDialogTitle"),
                renderer.sharedMesh.name + suffix,
                "asset",
                CHMLocales.Tr("MeshDeformInspector:Export:SaveDialogMessage"),
                defaultFolder);

            if (string.IsNullOrEmpty(path))
            {
                Object.DestroyImmediate(exportedMesh);
                return;
            }

            AssetDatabase.CreateAsset(exportedMesh, path);
            AssetDatabase.SaveAssets();
            if (asBlendshape && actualBsName != null && actualBsName != component.BlendshapeName)
            {
                Debug.Log(string.Format(
                    CHMLocales.Tr("MeshDeformInspector:Export:BlendshapeRenamedLog"),
                    component.BlendshapeName, actualBsName));
            }
            Debug.Log($"[ChimeraHairMaster] 変形済みメッシュを保存: {path}");

            if (replaceOriginal)
            {
                if (EditorUtility.DisplayDialog(
                    CHMLocales.Tr("MeshDeformInspector:Export:ReplaceConfirmTitle"),
                    string.Format(CHMLocales.Tr("MeshDeformInspector:Export:ReplaceConfirmMessage"), renderer.name),
                    CHMLocales.Tr("MeshDeformInspector:Export:ReplaceConfirmOk"), CHMLocales.Tr("MeshDeformInspector:Export:Cancel")))
                {
                    Undo.RegisterCompleteObjectUndo(component.UndoTarget, "Replace Mesh with Deformed");
                    Undo.RecordObject(renderer, "Replace Mesh with Deformed");
                    renderer.sharedMesh = exportedMesh;

                    // Blendshape モードでは weight=100 を設定して見た目を維持
                    if (asBlendshape && actualBsName != null)
                    {
                        int idx = exportedMesh.GetBlendShapeIndex(actualBsName);
                        if (idx >= 0)
                        {
                            renderer.SetBlendShapeWeight(idx, 100f);
                        }
                    }

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
            if (GUILayout.Button(CHMLocales.Tr("MeshDeformInspector:Reset:Button")))
            {
                if (EditorUtility.DisplayDialog(
                    CHMLocales.Tr("MeshDeformInspector:Reset:ConfirmTitle"),
                    CHMLocales.Tr("MeshDeformInspector:Reset:ConfirmMessage"),
                    CHMLocales.Tr("MeshDeformInspector:Reset:ConfirmOk"), CHMLocales.Tr("MeshDeformInspector:Export:Cancel")))
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
