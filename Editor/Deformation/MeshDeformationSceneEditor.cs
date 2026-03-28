using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Deformation
{
    /// <summary>
    /// メッシュ変形のScene Viewインタラクション管理
    /// UVアイランド選択・頂点選択・ギズモ操作・フォールオフ適用を担当する
    /// </summary>
    public class MeshDeformationSceneEditor
    {
        #region 編集モード

        public enum EditMode
        {
            Off,
            UVIsland,
            Vertex
        }

        #endregion

        #region 状態

        /// <summary>
        /// 現在の編集モード
        /// </summary>
        public EditMode CurrentMode { get; private set; } = EditMode.Off;

        /// <summary>
        /// 編集中のRendererインデックスのセット（静的）
        /// NDMFプレビューが二重デルタ適用を避けるために参照する
        /// </summary>
        public static readonly HashSet<int> ActiveEditingRendererIds = new HashSet<int>();

        /// <summary>
        /// 編集対象のCHMコンポーネント
        /// </summary>
        public ChimeraHairMaster TargetComponent { get; private set; }

        /// <summary>
        /// 編集対象のRendererインデックス
        /// </summary>
        public int ActiveRendererIndex { get; set; } = -1;

        /// <summary>
        /// ブラシ半径（ワールド空間）
        /// </summary>
        public float BrushRadius { get; set; } = 0.05f;

        /// <summary>
        /// フォールオフタイプ
        /// </summary>
        public FalloffType Falloff { get; set; } = FalloffType.Smooth;

        /// <summary>
        /// 距離メトリック
        /// </summary>
        public DistanceMetric Metric { get; set; } = DistanceMetric.Euclidean;

        /// <summary>
        /// 対称編集の有効軸
        /// </summary>
        public bool SymmetryX { get; set; } = false;
        public bool SymmetryY { get; set; } = false;
        public bool SymmetryZ { get; set; } = false;

        /// <summary>
        /// バックフェースカリング（裏向きの頂点を非表示・選択不可にする）
        /// </summary>
        public bool BackfaceCulling { get; set; } = true;

        /// <summary>
        /// Z-test（他のジオメトリに遮蔽された頂点を非表示にする）
        /// </summary>
        public bool ZTest { get; set; } = true;

        // 選択状態
        private HashSet<int> _selectedVertices = new HashSet<int>();
        private int _selectedIslandIndex = -1;
        private HashSet<int> _selectedIslandIndices = new HashSet<int>();
        private List<IslandInfo> _cachedIslands;

        // 作業用メッシュ
        private Mesh _workingMesh;
        private Mesh _originalMesh;
        private Vector3[] _baseVertices;

        // ワールド空間の頂点位置キャッシュ（表示・選択のヒットテストに使用）
        private Vector3[] _bakedVerticesWorld;

        // GL描画用マテリアル（ワイヤーフレーム・オーバーレイの一括描画に使用）
        private static Material _glMaterial;

        // 編集中のデルタ（高速アクセス用Dictionary）
        private Dictionary<int, Vector3> _activeDeltaMap = new Dictionary<int, Vector3>();

        // ギズモ操作
        private Vector3 _handlePosition;
        private Quaternion _handleRotation = Quaternion.identity;
        private Vector3 _handleScale = Vector3.one;
        private Vector3 _dragStartHandlePos;
        private Quaternion _dragStartHandleRot = Quaternion.identity;
        private Vector3 _dragStartHandleScale = Vector3.one;
        private bool _isDragging = false;

        // 矩形選択
        private bool _isBoxSelecting = false;
        private Vector2 _boxStart;
        private Vector2 _boxEnd;

        // フォールオフウェイトキャッシュ（ドラッグ中に使用）
        private Dictionary<int, float> _falloffWeights = new Dictionary<int, float>();

        // ドラッグ開始時の距離キャッシュ（半径変更時のウェイト再計算用）
        // EreMorphと同様、距離はドラッグ開始時に固定し、半径変更時はウェイトだけ再計算する
        private Dictionary<int, float> _dragStartDistances = new Dictionary<int, float>();

        // ドラッグ開始時のデルタスナップショット（絶対差分計算用）
        private Dictionary<int, Vector3> _dragStartDeltas = new Dictionary<int, Vector3>();

        // 対称マッピング
        private Dictionary<int, int> _symmetryMap;

        // 測地線距離計算
        private GeodesicDistanceCalculator _geodesicCalculator;

        // GPU instanced頂点描画
        private VertexDotRenderer _dotRenderer;

        // ドラッグ閾値（ピクセル）
        private const float DRAG_THRESHOLD = 6f;
        private Vector2 _mouseDownPos;
        private bool _mouseDownForSelection = false;


        #endregion

        #region 編集モードライフサイクル

        /// <summary>
        /// 編集モードを開始する
        /// </summary>
        public void BeginEdit(ChimeraHairMaster component, int rendererIndex, EditMode mode)
        {
            // 同じコンポーネント・同じRendererでモードだけ切り替える場合はメッシュを再作成しない
            if (CurrentMode != EditMode.Off
                && TargetComponent == component
                && ActiveRendererIndex == rendererIndex)
            {
                SwitchMode(mode);
                return;
            }

            EndEdit();

            TargetComponent = component;
            ActiveRendererIndex = rendererIndex;
            CurrentMode = mode;

            var renderer = GetActiveRenderer();
            if (renderer == null || renderer.sharedMesh == null)
            {
                CurrentMode = EditMode.Off;
                return;
            }

            // ドメインリロード対策: 前回の編集セッションが中断された場合、
            // renderer.sharedMesh がデルタ適用済みのまま残っている可能性がある。
            // コンポーネントに保存された原本メッシュがあればそちらを復元する。
            if (component.deformOriginalMesh != null)
            {
                renderer.sharedMesh = component.deformOriginalMesh;
            }

            // 元メッシュを保存し、作業用コピーを作成
            _originalMesh = renderer.sharedMesh;
            _workingMesh = Object.Instantiate(_originalMesh);
            _workingMesh.name = _originalMesh.name + "_Editing";
            _baseVertices = _originalMesh.vertices;

            // 既存のデルタをDictionaryに展開
            LoadDeltasFromComponent();

            // 作業用メッシュにデルタを適用
            ApplyDeltasToWorkingMesh();
            renderer.sharedMesh = _workingMesh;

            // NDMFプレビューの二重適用を防止:
            // 1. staticセットに登録（Instantiateでのデルタ二重適用防止）
            // 2. コンポーネントのフィールドを更新（GetTargetGroupsでのプロキシ除外トリガー）
            ActiveEditingRendererIds.Add(renderer.GetInstanceID());
            Undo.RegisterCompleteObjectUndo(TargetComponent, "Begin Mesh Deformation Edit");
            TargetComponent.deformEditingRendererIndex = rendererIndex;
            TargetComponent.deformOriginalMesh = _originalMesh;

            // UVアイランドモードならアイランドをキャッシュ
            if (mode == EditMode.UVIsland)
            {
                CacheIslands();
            }

            // 選択リセット
            _selectedVertices.Clear();
            _selectedIslandIndex = -1;
            _selectedIslandIndices.Clear();
            _symmetryMap = null;
            _geodesicCalculator = null;

            // GPU instanced頂点描画の初期化
            _dotRenderer = new VertexDotRenderer();
            _dotRenderer.Initialize();

            // Undo/Redoイベントを監視して作業メッシュを更新
            Undo.undoRedoPerformed += OnUndoRedo;

            // 編集セッションの自動終了イベントを登録
            RegisterAutoEndEditCallbacks();

            Tools.current = Tool.Move;
        }

        /// <summary>
        /// 同じRenderer上でモードを切り替える（メッシュ再作成なし）
        /// </summary>
        private void SwitchMode(EditMode newMode)
        {
            // ドラッグ中なら終了
            if (_isDragging) EndDrag();

            // デルタを保存
            SaveDeltasToComponent();

            CurrentMode = newMode;

            // 選択リセット
            _selectedVertices.Clear();
            _selectedIslandIndex = -1;
            _selectedIslandIndices.Clear();

            // UVアイランドモードならキャッシュ再構築
            if (newMode == EditMode.UVIsland)
            {
                CacheIslands();
            }
            else
            {
                _cachedIslands = null;
            }
        }

        /// <summary>
        /// 編集モードを終了する
        /// </summary>
        public void EndEdit()
        {
            if (CurrentMode == EditMode.Off) return;

            // Undo/Redo監視を解除
            Undo.undoRedoPerformed -= OnUndoRedo;

            // 自動終了イベントを解除
            UnregisterAutoEndEditCallbacks();

            // EndEditの全変更（デルタ保存 + エディタ専用フィールドクリア）を
            // Undoで追跡可能にするため、変更前の状態を即座にスナップショットする。
            // RecordObject（遅延diff）ではなくRegisterCompleteObjectUndo（即座キャプチャ）を使う。
            // これにより、セッション間を跨いだUndoチェーンが維持される。
            if (TargetComponent != null)
            {
                Undo.RegisterCompleteObjectUndo(TargetComponent, "End Mesh Deformation Edit");
            }

            // デルタをコンポーネントに保存（Undoスナップショット後なので追跡される）
            SaveDeltasToComponent();

            // NDMFプレビューの二重適用防止を解除
            var renderer = GetActiveRenderer();
            if (renderer != null)
            {
                ActiveEditingRendererIds.Remove(renderer.GetInstanceID());
            }

            // エディタ専用フィールドをクリア
            if (TargetComponent != null)
            {
                TargetComponent.deformEditingRendererIndex = -1;
                TargetComponent.deformOriginalMesh = null;
            }

            // 元のメッシュに戻す
            if (renderer != null && _originalMesh != null)
            {
                renderer.sharedMesh = _originalMesh;
            }

            // クリーンアップ
            if (_workingMesh != null)
            {
                Object.DestroyImmediate(_workingMesh);
                _workingMesh = null;
            }

            _originalMesh = null;
            _baseVertices = null;
            _activeDeltaMap.Clear();
            _bakedVerticesWorld = null;
            if (_bakeTempMesh != null) { Object.DestroyImmediate(_bakeTempMesh); _bakeTempMesh = null; }
            _selectedVertices.Clear();
            _cachedIslands = null;
            _selectedIslandIndex = -1;
            _selectedIslandIndices.Clear();
            _symmetryMap = null;
            _geodesicCalculator = null;

            // ドラッグ・選択の操作状態をリセット
            _isDragging = false;
            _undoOpen = false;
            _undoGroupId = -1;
            _isBoxSelecting = false;
            _mouseDownForSelection = false;
            _falloffWeights.Clear();
            _dragStartDeltas.Clear();
            _dragStartDistances.Clear();
            _visibleVertices = null;

            // GPU描画リソースの解放
            _dotRenderer?.Dispose();
            _dotRenderer = null;

            CurrentMode = EditMode.Off;
            ActiveRendererIndex = -1;
        }

        /// <summary>
        /// Undo/Redo実行時にコンポーネントからデルタを再読み込みし、作業メッシュを更新する
        /// </summary>
        private void OnUndoRedo()
        {
            if (CurrentMode == EditMode.Off || TargetComponent == null) return;

            // コンポーネントのシリアライズデータからデルタを再読み込み
            _activeDeltaMap.Clear();
            if (TargetComponent.rendererDeformations != null)
            {
                var deformation = TargetComponent.rendererDeformations.Find(
                    d => d.rendererIndex == ActiveRendererIndex);
                if (deformation?.deltas != null)
                {
                    foreach (var delta in deformation.deltas)
                        _activeDeltaMap[delta.vertexIndex] = delta.offset;
                }
            }

            // 作業メッシュを更新（_baseVertices + 新しいデルタ）
            ApplyDeltasToWorkingMesh();

            // ギズモ位置を選択頂点の現在の頂点位置に合わせる
            if (_selectedVertices.Count > 0 || _selectedIslandIndex >= 0)
            {
                var renderer = GetActiveRenderer();
                if (renderer != null && _workingMesh != null)
                {
                    var vertices = _workingMesh.vertices;
                    var center = Vector3.zero;
                    int count = 0;
                    foreach (int vi in _selectedVertices)
                    {
                        if (vi < vertices.Length)
                        {
                            center += vertices[vi];
                            count++;
                        }
                    }
                    if (count > 0)
                    {
                        center /= count;
                        _handlePosition = renderer.transform.TransformPoint(center);
                        _dragStartHandlePos = _handlePosition;
                    }
                }
            }

            // Scene Viewを再描画
            UnityEditor.SceneView.RepaintAll();
        }

        #endregion

        #region Scene View イベント処理

        /// <summary>
        /// OnSceneGUI から呼ばれるメインハンドラ
        ///
        /// 処理順序が重要:
        ///   1. ギズモ描画・操作（Handles APIがイベントを消費する可能性）
        ///   2. ギズモが消費しなかったイベントを選択処理に回す
        ///   3. 頂点可視化・矩形選択描画
        /// </summary>
        public void OnSceneGUI(UnityEditor.SceneView sceneView)
        {
            if (CurrentMode == EditMode.Off) return;
            if (TargetComponent == null || GetActiveRenderer() == null) return;

            var e = Event.current;

            // マウスホイールでブラシ半径を調整（頂点モード + ドラッグ中のみ）
            // ドラッグ中でなければ標準のScene Viewズームに任せる
            if (CurrentMode == EditMode.Vertex && _isDragging && e.type == EventType.ScrollWheel)
            {
                float scrollDelta = -e.delta.y * 0.005f; // 上スクロール=拡大
                BrushRadius = Mathf.Clamp(BrushRadius + scrollDelta * BrushRadius, 0.001f, 1.0f);

                // ドラッグ中ならキャッシュ済み距離から新半径でウェイトだけ再計算し、デルタを再適用
                if (_isDragging && _dragStartDistances.Count > 0)
                {
                    RecomputeWeightsFromCachedDistances();
                    ReapplyCurrentDrag();
                }

                e.Use();
                sceneView.Repaint();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            // ギズモを先に処理（ギズモがイベントを消費したら選択処理はスキップ）
            DrawGizmoHandles();

            // ギズモが消費しなかったイベントを選択処理に回す
            if (e.type != EventType.Used)
            {
                HandleInputEvents(e, sceneView);
            }

            DrawVertexVisualization();
            DrawBoxSelection();
            DrawOperationGuide(sceneView);

            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }
        }

        /// <summary>
        /// Scene View右下に操作ガイドを表示する
        /// </summary>
        private void DrawOperationGuide(UnityEditor.SceneView sceneView)
        {
            Handles.BeginGUI();

            var modeName = CurrentMode == EditMode.UVIsland ? "UVアイランド編集" : "頂点編集";

            string guide;
            if (CurrentMode == EditMode.UVIsland)
            {
                guide = $"--- {modeName} ---\n" +
                        "クリック: アイランド選択\n" +
                        "Ctrl+クリック: 追加/解除\n" +
                        "Shift+クリック: 追加\n" +
                        "W/E/R: 移動/回転/スケール";
            }
            else
            {
                guide = $"--- {modeName} ---\n" +
                        "クリック: 頂点選択\n" +
                        "ドラッグ: 矩形選択\n" +
                        "Ctrl+クリック: 追加/解除\n" +
                        "Shift+クリック: 追加\n" +
                        "W/E/R: 移動/回転/スケール\n" +
                        "ドラッグ中ホイール: 影響範囲変更";
            }

            var style = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 11,
                padding = new RectOffset(8, 8, 6, 6),
                richText = false,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 1f) }
            };

            var content = new GUIContent(guide);
            var size = style.CalcSize(content);
            size.x += 4;
            size.y += 2;

            float margin = 10f;
            var rect = new Rect(
                sceneView.position.width - size.x - margin,
                sceneView.position.height - size.y - margin - 20f, // ステータスバー分オフセット
                size.x,
                size.y);

            // 暗い背景を自前で描画
            EditorGUI.DrawRect(rect, new Color(0.05f, 0.05f, 0.05f, 0.92f));
            GUI.Label(rect, content, style);

            Handles.EndGUI();
        }

        #endregion

        #region 入力イベント処理

        private void HandleInputEvents(Event e, UnityEditor.SceneView sceneView)
        {
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                _mouseDownPos = e.mousePosition;
                _mouseDownForSelection = true;
                _boxStart = e.mousePosition;
                _isBoxSelecting = false;
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && !e.alt && !_isDragging)
            {
                // ドラッグ閾値を超えたら矩形選択開始
                if (_mouseDownForSelection &&
                    Vector2.Distance(e.mousePosition, _mouseDownPos) > DRAG_THRESHOLD)
                {
                    if (!IsHandleHovered())
                    {
                        _isBoxSelecting = true;
                        _boxEnd = e.mousePosition;
                        e.Use();
                        sceneView.Repaint();
                    }
                }
                else if (_isBoxSelecting)
                {
                    _boxEnd = e.mousePosition;
                    e.Use();
                    sceneView.Repaint();
                }
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && !e.alt)
            {
                if (_isBoxSelecting)
                {
                    _boxEnd = e.mousePosition;
                    PerformBoxSelection(e);
                    _isBoxSelecting = false;
                    e.Use();
                }
                else if (_mouseDownForSelection && !_isDragging)
                {
                    // ドラッグ閾値以内のクリック → 単一選択
                    // ギズモから離れた場所をクリックした場合、別のアイランド/頂点を選択可能
                    PerformClickSelection(e);
                }

                _mouseDownForSelection = false;
            }
        }

        /// <summary>
        /// ギズモハンドルがマウス直下にあるかを判定する
        /// ハンドル位置のスクリーン座標とマウス位置の距離で判定
        /// </summary>
        private bool IsHandleHovered()
        {
            if (_selectedVertices.Count == 0 && _selectedIslandIndex < 0) return false;

            var screenPos = HandleUtility.WorldToGUIPoint(_handlePosition);
            float dist = Vector2.Distance(screenPos, Event.current.mousePosition);
            // ギズモのおおよその画面上サイズ（ピクセル）
            return dist < 80f;
        }

        #endregion

        #region 選択

        private void PerformClickSelection(Event e)
        {
            if (CurrentMode == EditMode.UVIsland)
            {
                SelectIslandAtMouse(e);
            }
            else if (CurrentMode == EditMode.Vertex)
            {
                SelectVertexAtMouse(e);
            }
        }

        /// <summary>
        /// バックフェースカリングにより頂点が可視かどうかを判定する
        /// </summary>
        private bool IsVertexVisible(int vertexIndex)
        {
            if (!BackfaceCulling) return true;
            if (_visibleVertices != null && vertexIndex < _visibleVertices.Length)
                return _visibleVertices[vertexIndex];
            return true;
        }

        private void PerformBoxSelection(Event e)
        {
            if (CurrentMode != EditMode.Vertex) return;
            if (_bakedVerticesWorld == null) return;

            var rect = GetSelectionRect(_boxStart, _boxEnd);
            var newSelection = new HashSet<int>();

            for (int i = 0; i < _bakedVerticesWorld.Length; i++)
            {
                if (!IsVertexVisible(i)) continue;

                var screenPos = HandleUtility.WorldToGUIPoint(_bakedVerticesWorld[i]);
                if (rect.Contains(screenPos))
                {
                    newSelection.Add(i);
                }
            }

            ApplySelectionModifiers(e, newSelection);
            UpdateHandleFromSelection();
        }

        private void SelectVertexAtMouse(Event e)
        {
            if (_bakedVerticesWorld == null) return;

            float minDist = 20f;
            int closest = -1;

            for (int i = 0; i < _bakedVerticesWorld.Length; i++)
            {
                if (!IsVertexVisible(i)) continue;

                var screenPos = HandleUtility.WorldToGUIPoint(_bakedVerticesWorld[i]);
                float dist = Vector2.Distance(screenPos, e.mousePosition);

                if (dist < minDist)
                {
                    minDist = dist;
                    closest = i;
                }
            }

            if (closest >= 0)
            {
                var newSelection = new HashSet<int> { closest };
                ApplySelectionModifiers(e, newSelection);
                UpdateHandleFromSelection();
                e.Use();
            }
        }

        private void SelectIslandAtMouse(Event e)
        {
            if (_cachedIslands == null || _bakedVerticesWorld == null) return;

            const float centroidThreshold = 30f;
            const float vertexThreshold = 20f;

            // 1. まず重心点への距離で判定（重心点の近くなら確実にそのアイランドを選択）
            float minCentroidDist = centroidThreshold;
            int centroidHitIsland = -1;

            for (int islandIdx = 0; islandIdx < _cachedIslands.Count; islandIdx++)
            {
                var island = _cachedIslands[islandIdx];
                var worldCentroid = Vector3.zero;
                int count = 0;
                foreach (int vi in island.vertexIndices)
                {
                    if (vi < _bakedVerticesWorld.Length) { worldCentroid += _bakedVerticesWorld[vi]; count++; }
                }
                if (count == 0) continue;
                worldCentroid /= count;

                var screenPos = HandleUtility.WorldToGUIPoint(worldCentroid);
                float dist = Vector2.Distance(screenPos, e.mousePosition);
                if (dist < minCentroidDist)
                {
                    minCentroidDist = dist;
                    centroidHitIsland = islandIdx;
                }
            }

            // 2. 重心点にヒットしなかった場合、従来通り最寄り頂点で判定
            int resultIsland = centroidHitIsland;
            if (resultIsland < 0)
            {
                float minVertexDist = vertexThreshold;
                for (int islandIdx = 0; islandIdx < _cachedIslands.Count; islandIdx++)
                {
                    foreach (int vi in _cachedIslands[islandIdx].vertexIndices)
                    {
                        if (!IsVertexVisible(vi)) continue;
                        if (vi >= _bakedVerticesWorld.Length) continue;
                        var screenPos = HandleUtility.WorldToGUIPoint(_bakedVerticesWorld[vi]);
                        float dist = Vector2.Distance(screenPos, e.mousePosition);
                        if (dist < minVertexDist)
                        {
                            minVertexDist = dist;
                            resultIsland = islandIdx;
                        }
                    }
                }
            }

            if (resultIsland >= 0)
            {
                var islandVertices = new HashSet<int>(_cachedIslands[resultIsland].vertexIndices);

                if (e.control)
                {
                    // Ctrl: トグル（選択済みなら解除、未選択なら追加）
                    if (_selectedIslandIndices.Contains(resultIsland))
                    {
                        _selectedIslandIndices.Remove(resultIsland);
                        _selectedVertices.ExceptWith(islandVertices);
                    }
                    else
                    {
                        _selectedIslandIndices.Add(resultIsland);
                        _selectedVertices.UnionWith(islandVertices);
                    }
                }
                else if (e.shift)
                {
                    // Shift: 追加
                    _selectedIslandIndices.Add(resultIsland);
                    _selectedVertices.UnionWith(islandVertices);
                }
                else
                {
                    // 修飾キーなし: 置換
                    _selectedIslandIndices.Clear();
                    _selectedIslandIndices.Add(resultIsland);
                    _selectedVertices = islandVertices;
                }

                _selectedIslandIndex = resultIsland; // 最後に操作したアイランド（ギズモ位置用）
                UpdateHandleFromSelection();
                e.Use();
            }
        }

        private void ApplySelectionModifiers(Event e, HashSet<int> newSelection)
        {
            if (e.shift)
            {
                // Shift: 追加選択
                _selectedVertices.UnionWith(newSelection);
            }
            else if (e.control)
            {
                // Ctrl: トグル選択
                foreach (int v in newSelection)
                {
                    if (!_selectedVertices.Remove(v))
                        _selectedVertices.Add(v);
                }
            }
            else
            {
                // 修飾キーなし: 置換
                _selectedVertices = newSelection;
            }
        }

        private void UpdateHandleFromSelection()
        {
            // 選択変更前にドラッグ中なら確実に終了させる
            if (_isDragging)
            {
                EndDrag();
            }

            if (_selectedVertices.Count == 0) return;

            // ベイク済み頂点があればそれを使う（Bone変形を反映）
            if (_bakedVerticesWorld != null && _bakedVerticesWorld.Length > 0)
            {
                var center = Vector3.zero;
                int count = 0;
                foreach (int vi in _selectedVertices)
                {
                    if (vi < _bakedVerticesWorld.Length)
                    {
                        center += _bakedVerticesWorld[vi];
                        count++;
                    }
                }
                if (count == 0) return;
                _handlePosition = center / count;
            }
            else
            {
                var renderer = GetActiveRenderer();
                if (renderer == null) return;

                var vertices = _workingMesh.vertices;
                var center = Vector3.zero;
                foreach (int vi in _selectedVertices)
                {
                    if (vi < vertices.Length)
                        center += vertices[vi];
                }
                center /= _selectedVertices.Count;
                _handlePosition = renderer.transform.TransformPoint(center);
            }

            var rot = GetActiveRenderer();
            _handleRotation = rot != null ? rot.transform.rotation : Quaternion.identity;
            _handleScale = Vector3.one;

            // ドラッグ開始位置も新しいハンドル位置に同期
            // これにより次のドラッグ開始時に前回の位置との差分が発生しない
            _dragStartHandlePos = _handlePosition;
            _dragStartHandleRot = _handleRotation;
            _dragStartHandleScale = _handleScale;
        }

        private Rect GetSelectionRect(Vector2 start, Vector2 end)
        {
            float x = Mathf.Min(start.x, end.x);
            float y = Mathf.Min(start.y, end.y);
            float w = Mathf.Abs(start.x - end.x);
            float h = Mathf.Abs(start.y - end.y);
            return new Rect(x, y, w, h);
        }

        #endregion

        #region ギズモ操作

        /// <summary>
        /// ハンドルの返り値と渡した値を直接比較し、ユーザーが実際に操作したかを判定する。
        /// EditorGUI.EndChangeCheck() はプログラムによるposition変更（選択切替など）でも
        /// trueを返すため信頼できない。
        /// </summary>
        private static bool HasHandleMoved(Vector3 before, Vector3 after)
        {
            return (after - before).sqrMagnitude > 1e-10f;
        }

        private static bool HasHandleRotated(Quaternion before, Quaternion after)
        {
            return Quaternion.Angle(before, after) > 0.001f;
        }

        private static bool HasHandleScaled(Vector3 before, Vector3 after)
        {
            return (after - before).sqrMagnitude > 1e-10f;
        }

        private void DrawGizmoHandles()
        {
            if (_selectedVertices.Count == 0 && _selectedIslandIndex < 0) return;

            // Layout/Repaintイベントではギズモの変更検出を行わない
            // ユーザー操作イベント（MouseDrag等）でのみ検出する
            bool isInteractiveEvent = Event.current.type != EventType.Layout
                                   && Event.current.type != EventType.Repaint;

            // hotControlでハンドルのドラッグ状態を追跡する。
            // Handles.PositionHandle等はMouseUpを内部で消費するため、
            // Event.current.type == MouseUp では検出できない。
            // hotControlが非ゼロ→ゼロに変わった瞬間がドラッグ終了。
            int hotBefore = GUIUtility.hotControl;

            switch (Tools.current)
            {
                case Tool.Move:
                    var newPos = Handles.PositionHandle(_handlePosition, _handleRotation);
                    if (isInteractiveEvent && HasHandleMoved(_handlePosition, newPos))
                    {
                        if (!_isDragging) BeginDrag();
                        var totalDelta = newPos - _dragStartHandlePos;
                        _handlePosition = newPos;
                        ApplyMoveDelta(totalDelta);
                    }
                    break;

                case Tool.Rotate:
                    var newRot = Handles.RotationHandle(_handleRotation, _handlePosition);
                    if (HasHandleRotated(_handleRotation, newRot))
                    {
                        if (!_isDragging) BeginDrag();
                        _handleRotation = newRot;
                        var totalRotDelta = newRot * Quaternion.Inverse(_dragStartHandleRot);
                        ApplyRotateDelta(totalRotDelta);
                    }
                    break;

                case Tool.Scale:
                    var newScale = Handles.ScaleHandle(_handleScale, _handlePosition, _handleRotation);
                    if (HasHandleScaled(_handleScale, newScale))
                    {
                        if (!_isDragging) BeginDrag();
                        _handleScale = newScale;
                        var totalScaleDelta = new Vector3(
                            _dragStartHandleScale.x != 0 ? newScale.x / _dragStartHandleScale.x : 1,
                            _dragStartHandleScale.y != 0 ? newScale.y / _dragStartHandleScale.y : 1,
                            _dragStartHandleScale.z != 0 ? newScale.z / _dragStartHandleScale.z : 1);
                        ApplyScaleDelta(totalScaleDelta);
                    }
                    break;
            }

            // hotControlが非ゼロ→ゼロになった = ハンドルがリリースされた
            if (_isDragging && hotBefore != 0 && GUIUtility.hotControl == 0)
            {
                EndDrag();
            }
        }

        // Undoグループ管理（EreMorphと同じパターン）
        private int _undoGroupId = -1;
        private bool _undoOpen = false;

        private void BeginDrag()
        {
            _isDragging = true;
            _dragStartHandlePos = _handlePosition;
            _dragStartHandleRot = _handleRotation;
            _dragStartHandleScale = _handleScale;

            // ドラッグ開始時のデルタをスナップショットとして保存
            _dragStartDeltas.Clear();
            foreach (var kvp in _activeDeltaMap)
                _dragStartDeltas[kvp.Key] = kvp.Value;

            // フォールオフウェイトを事前計算
            ComputeFalloffWeights();

            // Undo: EreMorphと同じパターン
            // 1. 新しいUndoグループを開始
            // 2. ドラッグ開始前のコンポーネント状態を丸ごとスナップショット
            // 3. グループIDを記録し、EndDragでCollapseする
            // ドラッグ中はコンポーネントに直接書き込み、Collapseで1ステップにまとめる
            var toolName = Tools.current == Tool.Move ? "移動"
                         : Tools.current == Tool.Rotate ? "回転"
                         : Tools.current == Tool.Scale ? "スケール"
                         : "変形";
            var modeName = CurrentMode == EditMode.UVIsland ? "アイランド" : "頂点";
            var label = $"メッシュ変形: {modeName}{toolName}";

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(TargetComponent, label);
            Undo.SetCurrentGroupName(label);
            _undoGroupId = Undo.GetCurrentGroup();
            _undoOpen = true;

        }


        /// <summary>
        /// ドラッグ中にブラシ半径が変更された場合、現在のギズモ位置から
        /// 新しいフォールオフウェイトでデルタを再計算する
        /// </summary>
        private void ReapplyCurrentDrag()
        {
            if (!_isDragging) return;

            // まず全影響頂点をドラッグ開始時の状態にリセット
            foreach (var kvp in _dragStartDeltas)
                _activeDeltaMap[kvp.Key] = kvp.Value;

            // 新しいフォールオフウェイトで現在のデルタを再適用
            switch (Tools.current)
            {
                case Tool.Move:
                    var totalMoveDelta = _handlePosition - _dragStartHandlePos;
                    ApplyMoveDelta(totalMoveDelta);
                    break;
                case Tool.Rotate:
                    var totalRotDelta = _handleRotation * Quaternion.Inverse(_dragStartHandleRot);
                    ApplyRotateDelta(totalRotDelta);
                    break;
                case Tool.Scale:
                    var totalScaleDelta = new Vector3(
                        _dragStartHandleScale.x != 0 ? _handleScale.x / _dragStartHandleScale.x : 1,
                        _dragStartHandleScale.y != 0 ? _handleScale.y / _dragStartHandleScale.y : 1,
                        _dragStartHandleScale.z != 0 ? _handleScale.z / _dragStartHandleScale.z : 1);
                    ApplyScaleDelta(totalScaleDelta);
                    break;
            }
        }

        private void EndDrag()
        {
            _isDragging = false;
            _falloffWeights.Clear();
            _dragStartDeltas.Clear();
            _dragStartDistances.Clear();

            // ドラッグ終了: 最終状態をコンポーネントに書き出す
            SaveDeltasToComponent();

            // ドラッグ中の全操作を1つのUndoステップにまとめる
            if (_undoOpen)
            {
                Undo.CollapseUndoOperations(_undoGroupId);
                _undoOpen = false;
                _undoGroupId = -1;
            }
            else
            {
            }
        }

        #endregion

        #region 変形適用

        /// <summary>
        /// 変形対象の全頂点インデックスを返す
        /// フォールオフにより選択頂点の周囲にも影響が及ぶ
        /// </summary>
        private IEnumerable<int> GetAffectedVertices()
        {
            // フォールオフウェイトが計算済みならその全キー（選択頂点+周辺頂点）
            if (_falloffWeights.Count > 0)
                return _falloffWeights.Keys;
            // フォールバック: 選択頂点のみ
            return _selectedVertices;
        }

        /// <summary>
        /// 移動: ドラッグ開始位置からの絶対差分でデルタを上書きする
        /// </summary>
        private void ApplyMoveDelta(Vector3 totalWorldDelta)
        {
            var renderer = GetActiveRenderer();
            if (renderer == null) return;

            var localDelta = renderer.transform.InverseTransformVector(totalWorldDelta);

            foreach (int vi in GetAffectedVertices())
            {
                float weight = GetVertexWeight(vi);
                var startDelta = _dragStartDeltas.TryGetValue(vi, out var sd) ? sd : Vector3.zero;
                _activeDeltaMap[vi] = startDelta + localDelta * weight;
            }

            // 対称適用
            if (SymmetryX || SymmetryY || SymmetryZ)
            {
                ApplySymmetricMoveDelta(localDelta);
            }

            ApplyDeltasToWorkingMesh();
        }

        /// <summary>
        /// 回転: ドラッグ開始状態を基準に累積回転でデルタを上書きする
        /// </summary>
        private void ApplyRotateDelta(Quaternion totalRotDelta)
        {
            var renderer = GetActiveRenderer();
            if (renderer == null) return;

            var localPivot = renderer.transform.InverseTransformPoint(_dragStartHandlePos);

            foreach (int vi in GetAffectedVertices())
            {
                if (vi >= _baseVertices.Length) continue;

                float weight = GetVertexWeight(vi);
                var startDelta = _dragStartDeltas.TryGetValue(vi, out var sd) ? sd : Vector3.zero;

                var startPos = _baseVertices[vi] + startDelta;
                var relativePos = startPos - localPivot;

                var worldRelative = renderer.transform.TransformVector(relativePos);
                var rotated = totalRotDelta * worldRelative;
                var newLocal = renderer.transform.InverseTransformVector(rotated);

                var newDelta = startDelta + Vector3.Lerp(Vector3.zero, newLocal - relativePos, weight);
                _activeDeltaMap[vi] = newDelta;
            }

            ApplyDeltasToWorkingMesh();
        }

        /// <summary>
        /// スケール: ドラッグ開始状態を基準に累積スケールでデルタを上書きする
        /// </summary>
        private void ApplyScaleDelta(Vector3 totalScaleDelta)
        {
            var renderer = GetActiveRenderer();
            if (renderer == null) return;

            var localPivot = renderer.transform.InverseTransformPoint(_dragStartHandlePos);

            foreach (int vi in GetAffectedVertices())
            {
                if (vi >= _baseVertices.Length) continue;

                float weight = GetVertexWeight(vi);
                var startDelta = _dragStartDeltas.TryGetValue(vi, out var sd) ? sd : Vector3.zero;

                var startPos = _baseVertices[vi] + startDelta;
                var relativePos = startPos - localPivot;

                var rotInv = Quaternion.Inverse(renderer.transform.rotation * _dragStartHandleRot);
                var aligned = rotInv * renderer.transform.TransformVector(relativePos);
                aligned = Vector3.Scale(aligned, totalScaleDelta);
                var newLocal = renderer.transform.InverseTransformVector(
                    (renderer.transform.rotation * _dragStartHandleRot) * aligned);

                var newDelta = startDelta + Vector3.Lerp(Vector3.zero, newLocal - relativePos, weight);
                _activeDeltaMap[vi] = newDelta;
            }

            ApplyDeltasToWorkingMesh();
        }

        /// <summary>
        /// 対称移動: フォールオフ影響範囲の対称頂点にも適用
        /// </summary>
        private void ApplySymmetricMoveDelta(Vector3 localDelta)
        {
            EnsureSymmetryMap();
            if (_symmetryMap == null) return;

            var mirroredDelta = MirrorVector(localDelta);

            foreach (int vi in GetAffectedVertices())
            {
                if (!_symmetryMap.TryGetValue(vi, out int mirrorVi)) continue;
                if (_falloffWeights.ContainsKey(mirrorVi)) continue; // 既に影響範囲内ならスキップ

                float weight = GetVertexWeight(vi);
                var startDelta = _dragStartDeltas.TryGetValue(mirrorVi, out var sd) ? sd : Vector3.zero;
                _activeDeltaMap[mirrorVi] = startDelta + mirroredDelta * weight;
            }
        }

        private Vector3 MirrorVector(Vector3 v)
        {
            return new Vector3(
                SymmetryX ? -v.x : v.x,
                SymmetryY ? -v.y : v.y,
                SymmetryZ ? -v.z : v.z);
        }

        #endregion

        #region フォールオフ計算

        /// <summary>
        /// フォールオフウェイトを計算する。
        /// 距離を計算して _dragStartDistances にキャッシュし、ウェイトを _falloffWeights に設定する。
        /// </summary>
        private void ComputeFalloffWeights()
        {
            _falloffWeights.Clear();
            _dragStartDistances.Clear();

            if (CurrentMode == EditMode.UVIsland)
            {
                foreach (int vi in _selectedVertices)
                {
                    _falloffWeights[vi] = 1.0f;
                    _dragStartDistances[vi] = 0f;
                }
                return;
            }

            if (_selectedVertices.Count == 0) return;

            var renderer = GetActiveRenderer();
            if (renderer == null) return;

            var vertices = _workingMesh.vertices;

            // 選択中心を計算
            var center = Vector3.zero;
            foreach (int vi in _selectedVertices)
            {
                if (vi < vertices.Length)
                    center += vertices[vi];
            }
            center /= _selectedVertices.Count;

            if (Metric == DistanceMetric.Geodesic)
            {
                EnsureGeodesicCalculator();
                if (_geodesicCalculator != null)
                {
                    // マルチソースダイクストラ: 全選択頂点を起点にして
                    // 各頂点への「最寄りの選択頂点からの表面距離」を計算
                    var distances = _geodesicCalculator.ComputeDistances(_selectedVertices, BrushRadius * 2f);
                    foreach (var kvp in distances)
                    {
                        _dragStartDistances[kvp.Key] = kvp.Value;
                        float t = Mathf.Clamp01(kvp.Value / BrushRadius);
                        _falloffWeights[kvp.Key] = EvaluateFalloff(t);
                    }
                    return;
                }
            }

            // ユークリッド距離: 全頂点の距離をキャッシュ
            for (int i = 0; i < vertices.Length; i++)
            {
                float dist = Vector3.Distance(vertices[i], center);
                _dragStartDistances[i] = dist;
                if (dist <= BrushRadius)
                {
                    float t = Mathf.Clamp01(dist / BrushRadius);
                    _falloffWeights[i] = EvaluateFalloff(t);
                }
            }
        }

        /// <summary>
        /// キャッシュ済み距離から、新しいブラシ半径でウェイトだけ再計算する。
        /// EreMorphと同じパターン: 距離はドラッグ開始時に固定、半径だけ変更可能。
        /// </summary>
        private void RecomputeWeightsFromCachedDistances()
        {
            _falloffWeights.Clear();

            if (CurrentMode == EditMode.UVIsland)
            {
                foreach (int vi in _selectedVertices)
                    _falloffWeights[vi] = 1.0f;
                return;
            }

            foreach (var kvp in _dragStartDistances)
            {
                if (kvp.Value <= BrushRadius)
                {
                    float t = Mathf.Clamp01(kvp.Value / BrushRadius);
                    _falloffWeights[kvp.Key] = EvaluateFalloff(t);
                }
            }
        }

        private float GetVertexWeight(int vertexIndex)
        {
            if (_falloffWeights.TryGetValue(vertexIndex, out float w))
                return w;
            return _selectedVertices.Contains(vertexIndex) ? 1.0f : 0.0f;
        }

        /// <summary>
        /// フォールオフカーブを評価する
        /// </summary>
        public static float EvaluateFalloff(float t, FalloffType type)
        {
            t = Mathf.Clamp01(t);
            switch (type)
            {
                case FalloffType.Constant:
                    return 1.0f;
                case FalloffType.Linear:
                    return 1.0f - t;
                case FalloffType.Smooth:
                    return 1.0f - (3.0f * t * t - 2.0f * t * t * t);
                case FalloffType.Sphere:
                    return Mathf.Sqrt(1.0f - t * t);
                default:
                    return 1.0f;
            }
        }

        private float EvaluateFalloff(float t)
        {
            return EvaluateFalloff(t, Falloff);
        }

        #endregion

        #region Scene View 描画

        private void DrawVertexVisualization()
        {
            var renderer = GetActiveRenderer();
            if (renderer == null || _workingMesh == null) return;

            // Bone変形後の頂点位置を更新（表示・選択に使用）
            UpdateBakedVertices(renderer);

            // 頂点モードのみワイヤーフレームを描画
            if (CurrentMode == EditMode.Vertex)
                DrawMeshWireframe(renderer);

            _dotRenderer.EnableZTest = ZTest;

            if (_bakedVerticesWorld == null) return;

            var bakedNormals = _workingMesh != null ? _workingMesh.normals : null;
            var meshTransform = renderer.transform;
            var camera = UnityEditor.SceneView.currentDrawingSceneView?.camera;
            if (camera == null) return;

            var camForward = camera.transform.forward;
            int vertCount = _bakedVerticesWorld.Length;

            // 頂点の可視判定（バックフェースカリング）をキャッシュ
            if (_visibleVertices == null || _visibleVertices.Length != vertCount)
                _visibleVertices = new bool[vertCount];

            for (int i = 0; i < vertCount; i++)
            {
                if (BackfaceCulling && bakedNormals != null && i < bakedNormals.Length)
                {
                    // ベイク済み法線はローカル空間なのでTransformDirectionで変換
                    var worldNormal = meshTransform.TransformDirection(bakedNormals[i]);
                    _visibleVertices[i] = Vector3.Dot(worldNormal, camForward) < 0;
                }
                else
                {
                    _visibleVertices[i] = true;
                }
            }

            // 色の決定と描画データの収集
            var worldPositions = new List<Vector3>();
            var dotColors = new List<Color>();

            var selectedColor = new Color(1f, 0.4f, 0f, 1f);
            var unselectedColor = new Color(0f, 0.8f, 1f, 0.6f);
            var brushHighlightColor = new Color(0.2f, 1f, 0.4f, 0.8f);

            // UVアイランドモードの色パレット
            var islandPalette = new[]
            {
                new Color(0f, 0.8f, 1f, 0.4f),
                new Color(0.2f, 1f, 0.2f, 0.4f),
                new Color(1f, 0.8f, 0f, 0.4f),
                new Color(1f, 0.2f, 0.8f, 0.4f),
                new Color(0.6f, 0.4f, 1f, 0.4f),
            };

            if (CurrentMode == EditMode.UVIsland && _cachedIslands != null)
            {
                // ビビッドな色パレット（高彩度・高不透明度）
                var vividPalette = new[]
                {
                    new Color(0f, 0.7f, 1f, 1f),
                    new Color(0f, 1f, 0.3f, 1f),
                    new Color(1f, 1f, 0f, 1f),
                    new Color(1f, 0f, 0.7f, 1f),
                    new Color(0.5f, 0.3f, 1f, 1f),
                    new Color(1f, 0.5f, 0f, 1f),
                    new Color(0f, 1f, 0.8f, 1f),
                };

                for (int islandIdx = 0; islandIdx < _cachedIslands.Count; islandIdx++)
                {
                    var island = _cachedIslands[islandIdx];
                    bool isSelected = _selectedIslandIndices.Contains(islandIdx);

                    var worldCentroid = Vector3.zero;
                    int count = 0;
                    foreach (int vi in island.vertexIndices)
                    {
                        if (vi < _bakedVerticesWorld.Length) { worldCentroid += _bakedVerticesWorld[vi]; count++; }
                    }
                    if (count == 0) continue;
                    worldCentroid /= count;

                    float handleSize = HandleUtility.GetHandleSize(worldCentroid);
                    var camFwd = camera.transform.forward;

                    if (isSelected)
                    {
                        Handles.color = new Color(1f, 0.5f, 0f, 0.9f);
                        Handles.DrawWireDisc(worldCentroid, camFwd, handleSize * 0.12f);
                        Handles.color = Color.white;
                        Handles.DrawWireDisc(worldCentroid, camFwd, handleSize * 0.123f);
                    }
                    else
                    {
                        var vividColor = vividPalette[islandIdx % vividPalette.Length];
                        Handles.color = vividColor;
                        Handles.DrawWireDisc(worldCentroid, camFwd, handleSize * 0.09f);
                        Handles.color = new Color(0, 0, 0, 0.5f);
                        Handles.DrawWireDisc(worldCentroid, camFwd, handleSize * 0.093f);
                    }
                }
                Handles.color = Color.white;
            }
            else
            {
                // 頂点モード
                for (int i = 0; i < vertCount; i++)
                {
                    if (!_visibleVertices[i]) continue;

                    worldPositions.Add(_bakedVerticesWorld[i]);

                    if (_selectedVertices.Contains(i))
                        dotColors.Add(selectedColor);
                    else if (_isDragging && _falloffWeights.TryGetValue(i, out float fw) && fw > 0)
                        dotColors.Add(Color.Lerp(unselectedColor, brushHighlightColor, fw));
                    else
                        dotColors.Add(unselectedColor);
                }
            }

            if (_dotRenderer != null && worldPositions.Count > 0)
                _dotRenderer.Draw(worldPositions.ToArray(), dotColors.ToArray());

            // 選択頂点のワイヤーフレーム + 半透明オーバーレイを描画
            DrawSelectionOverlay();

            // 頂点モード + ドラッグ中: ブラシ半径のワイヤー球を表示
            if (CurrentMode == EditMode.Vertex && _isDragging && _selectedVertices.Count > 0)
            {
                DrawBrushRadiusSphere();
            }
        }

        /// <summary>
        /// 選択頂点の重心位置にブラシ半径のワイヤー球を表示する
        /// </summary>
        private void DrawBrushRadiusSphere()
        {
            Handles.color = new Color(0f, 1f, 0.5f, 0.4f);
            Handles.DrawWireDisc(_handlePosition, Camera.current.transform.forward, BrushRadius);
            Handles.DrawWireDisc(_handlePosition, Camera.current.transform.up, BrushRadius);
            Handles.DrawWireDisc(_handlePosition, Camera.current.transform.right, BrushRadius);
            Handles.color = Color.white;
        }

        /// <summary>
        /// 選択された頂点/アイランドに属する三角形を半透明オーバーレイ+ワイヤーフレームで描画する
        /// </summary>
        private void DrawSelectionOverlay()
        {
            if (_selectedVertices.Count == 0) return;
            if (_bakedVerticesWorld == null) return;

            var triangles = _workingMesh.triangles;
            var camForward = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;

            var mat = GetGLMaterial();
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetPass(0);

            GL.PushMatrix();
            GL.MultMatrix(Handles.matrix);

            // 半透明面
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(1f, 0.4f, 0f, 0.25f));

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];

                if (!_selectedVertices.Contains(i0) &&
                    !_selectedVertices.Contains(i1) &&
                    !_selectedVertices.Contains(i2))
                    continue;

                if (i0 >= _bakedVerticesWorld.Length || i1 >= _bakedVerticesWorld.Length || i2 >= _bakedVerticesWorld.Length)
                    continue;

                var p0 = _bakedVerticesWorld[i0];
                var p1 = _bakedVerticesWorld[i1];
                var p2 = _bakedVerticesWorld[i2];

                // 背面カリング
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (Vector3.Dot(normal, camForward) > 0) continue;

                GL.Vertex(p0); GL.Vertex(p1); GL.Vertex(p2);
            }
            GL.End();

            // エッジ
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 0.4f, 0f, 0.8f));

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];

                if (!_selectedVertices.Contains(i0) &&
                    !_selectedVertices.Contains(i1) &&
                    !_selectedVertices.Contains(i2))
                    continue;

                if (i0 >= _bakedVerticesWorld.Length || i1 >= _bakedVerticesWorld.Length || i2 >= _bakedVerticesWorld.Length)
                    continue;

                var p0 = _bakedVerticesWorld[i0];
                var p1 = _bakedVerticesWorld[i1];
                var p2 = _bakedVerticesWorld[i2];

                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (Vector3.Dot(normal, camForward) > 0) continue;

                GL.Vertex(p0); GL.Vertex(p1);
                GL.Vertex(p1); GL.Vertex(p2);
                GL.Vertex(p2); GL.Vertex(p0);
            }
            GL.End();

            GL.PopMatrix();
        }

        /// <summary>
        /// 編集対象メッシュのワイヤーフレームを重ねて描画する
        /// </summary>
        /// <summary>
        /// 作業メッシュの頂点をワールド空間に変換してキャッシュする。
        /// 表示・選択のヒットテストに使用する。
        /// </summary>
        private Mesh _bakeTempMesh;

        private void UpdateBakedVertices(SkinnedMeshRenderer renderer)
        {
            if (_bakeTempMesh == null)
                _bakeTempMesh = new Mesh();

            // BakeMesh: Bone変形を反映したローカル空間の頂点を取得
            // useScale=false → Rendererのスケールは含まない（TransformPointで適用）
            renderer.BakeMesh(_bakeTempMesh, false);
            var bakedLocal = _bakeTempMesh.vertices;

            if (_bakedVerticesWorld == null || _bakedVerticesWorld.Length != bakedLocal.Length)
                _bakedVerticesWorld = new Vector3[bakedLocal.Length];

            var t = renderer.transform;
            for (int i = 0; i < bakedLocal.Length; i++)
                _bakedVerticesWorld[i] = t.TransformPoint(bakedLocal[i]);
        }

        private static Material GetGLMaterial()
        {
            if (_glMaterial == null)
            {
                _glMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
                _glMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            return _glMaterial;
        }

        private void DrawMeshWireframe(SkinnedMeshRenderer renderer)
        {
            if (_bakedVerticesWorld == null) return;
            var triangles = _workingMesh.triangles;
            var camForward = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;

            var mat = GetGLMaterial();
            mat.SetInt("_ZTest", ZTest ? (int)UnityEngine.Rendering.CompareFunction.LessEqual
                                      : (int)UnityEngine.Rendering.CompareFunction.Always);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetPass(0);

            GL.PushMatrix();
            GL.MultMatrix(Handles.matrix);
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 0.6f, 0.2f, 0.35f));

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (i0 >= _bakedVerticesWorld.Length || i1 >= _bakedVerticesWorld.Length || i2 >= _bakedVerticesWorld.Length)
                    continue;

                var p0 = _bakedVerticesWorld[i0];
                var p1 = _bakedVerticesWorld[i1];
                var p2 = _bakedVerticesWorld[i2];

                // 背面カリング
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (Vector3.Dot(normal, camForward) > 0) continue;

                GL.Vertex(p0); GL.Vertex(p1);
                GL.Vertex(p1); GL.Vertex(p2);
                GL.Vertex(p2); GL.Vertex(p0);
            }

            GL.End();
            GL.PopMatrix();
        }

        // バックフェースカリング用キャッシュ
        private bool[] _visibleVertices;

        private void DrawBoxSelection()
        {
            if (!_isBoxSelecting) return;

            Handles.BeginGUI();
            var rect = GetSelectionRect(_boxStart, _boxEnd);
            var color = new Color(1f, 0.9f, 0.3f, 0.15f);
            EditorGUI.DrawRect(rect, color);

            // 枠線
            var borderColor = new Color(1f, 0.9f, 0.3f, 0.8f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), borderColor);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), borderColor);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), borderColor);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), borderColor);
            Handles.EndGUI();
        }

        #endregion

        #region メッシュ操作

        private void ApplyDeltasToWorkingMesh()
        {
            if (_workingMesh == null || _baseVertices == null) return;

            var vertices = (Vector3[])_baseVertices.Clone();

            foreach (var kvp in _activeDeltaMap)
            {
                if (kvp.Key >= 0 && kvp.Key < vertices.Length)
                    vertices[kvp.Key] += kvp.Value;
            }

            _workingMesh.vertices = vertices;
            _workingMesh.RecalculateNormals();
            _workingMesh.RecalculateBounds();
        }

        private void CacheIslands()
        {
            var renderer = GetActiveRenderer();
            if (renderer == null || _originalMesh == null) return;

            _cachedIslands = new List<IslandInfo>();
            for (int s = 0; s < _originalMesh.subMeshCount; s++)
            {
                var islands = UVIslandDetector.DetectIslandsWithVertices(_originalMesh, s);
                _cachedIslands.AddRange(islands);
            }
        }

        #endregion

        #region デルタ保存・復元

        /// <summary>
        /// コンポーネントのシリアライズデータからデルタをDictionaryに読み込む
        /// 編集モード開始時やUndo/Redo時に呼ばれる
        /// </summary>
        private void LoadDeltasFromComponent()
        {
            _activeDeltaMap.Clear();

            var deformation = TargetComponent.rendererDeformations?.Find(
                d => d.rendererIndex == ActiveRendererIndex);
            if (deformation == null) return;

            foreach (var delta in deformation.deltas)
                _activeDeltaMap[delta.vertexIndex] = delta.offset;
        }

        private void SaveDeltasToComponent()
        {
            if (TargetComponent == null || ActiveRendererIndex < 0) return;

            var deformation = GetOrCreateDeformation();
            if (deformation == null) return;

            // Dictionary → List<VertexDelta> 変換（ゼロベクトルは除外）
            deformation.deltas.Clear();
            foreach (var kvp in _activeDeltaMap)
            {
                if (kvp.Value.sqrMagnitude > 0.000001f) // ほぼゼロのデルタは保存しない
                {
                    deformation.deltas.Add(new VertexDelta(kvp.Key, kvp.Value));
                }
            }

            // vertexIndex順にソート（安定したシリアライズのため）
            deformation.deltas.Sort((a, b) => a.vertexIndex.CompareTo(b.vertexIndex));
        }

        private RendererDeformation GetOrCreateDeformation()
        {
            if (TargetComponent == null || ActiveRendererIndex < 0) return null;

            var renderer = GetActiveRenderer();
            if (renderer == null || renderer.sharedMesh == null) return null;

            var existing = TargetComponent.rendererDeformations.Find(
                d => d.rendererIndex == ActiveRendererIndex);

            if (existing != null) return existing;

            var newDeformation = new RendererDeformation(
                ActiveRendererIndex,
                _originalMesh != null ? _originalMesh.vertexCount : renderer.sharedMesh.vertexCount);

            TargetComponent.rendererDeformations.Add(newDeformation);
            return newDeformation;
        }

        #endregion

        #region 対称マッピング

        private void EnsureSymmetryMap()
        {
            if (_symmetryMap != null) return;
            if (!SymmetryX && !SymmetryY && !SymmetryZ) return;

            var vertices = _baseVertices;
            if (vertices == null) return;

            _symmetryMap = VertexSymmetryMapper.BuildSymmetryMap(
                vertices, SymmetryX, SymmetryY, SymmetryZ);
        }

        #endregion

        #region 測地線距離

        private void EnsureGeodesicCalculator()
        {
            if (_geodesicCalculator != null) return;
            if (_workingMesh == null) return;

            _geodesicCalculator = new GeodesicDistanceCalculator(_workingMesh);
        }

        #endregion

        #region ユーティリティ

        private SkinnedMeshRenderer GetActiveRenderer()
        {
            if (TargetComponent == null || ActiveRendererIndex < 0) return null;
            if (ActiveRendererIndex >= TargetComponent.targetRenderers.Count) return null;
            return TargetComponent.targetRenderers[ActiveRendererIndex];
        }

        /// <summary>
        /// 現在の選択頂点数を取得
        /// </summary>
        public int SelectedVertexCount => _selectedVertices.Count;

        /// <summary>
        /// 現在のアクティブなデルタ数を取得
        /// </summary>
        public int ActiveDeltaCount => _activeDeltaMap.Count(kvp => kvp.Value.sqrMagnitude > 0.000001f);

        /// <summary>
        /// デルタをリセットする
        /// </summary>
        /// <summary>
        /// 選択頂点の位置を隣接頂点の平均に近づけて滑らかにする（Laplacian Smooth）
        /// factor: 0=変化なし, 1=完全に隣接頂点の平均に移動
        /// </summary>
        public void ApplySmooth(float factor)
        {
            if (CurrentMode == EditMode.Off || _workingMesh == null) return;
            if (_selectedVertices.Count == 0) return;

            // Undo記録
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(TargetComponent, "メッシュ変形: スムーズ");

            var vertices = _workingMesh.vertices;
            var triangles = _workingMesh.triangles;

            // 隣接頂点マップを構築
            var adjacency = new Dictionary<int, HashSet<int>>();
            foreach (int vi in _selectedVertices)
                adjacency[vi] = new HashSet<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (adjacency.ContainsKey(i0)) { adjacency[i0].Add(i1); adjacency[i0].Add(i2); }
                if (adjacency.ContainsKey(i1)) { adjacency[i1].Add(i0); adjacency[i1].Add(i2); }
                if (adjacency.ContainsKey(i2)) { adjacency[i2].Add(i0); adjacency[i2].Add(i1); }
            }

            // 各頂点を隣接頂点の平均に近づける
            var newDeltas = new Dictionary<int, Vector3>();
            foreach (int vi in _selectedVertices)
            {
                if (vi >= vertices.Length) continue;
                if (!adjacency.TryGetValue(vi, out var neighbors) || neighbors.Count == 0)
                {
                    _activeDeltaMap.TryGetValue(vi, out var d);
                    newDeltas[vi] = d;
                    continue;
                }

                // 隣接頂点の平均位置を計算
                var avgPos = Vector3.zero;
                foreach (int ni in neighbors)
                {
                    if (ni < vertices.Length)
                        avgPos += vertices[ni];
                }
                avgPos /= neighbors.Count;

                // 現在位置から平均位置へfactor分だけ移動
                var currentPos = vertices[vi];
                var smoothedPos = Vector3.Lerp(currentPos, avgPos, factor);
                newDeltas[vi] = smoothedPos - _baseVertices[vi];
            }

            foreach (var kvp in newDeltas)
                _activeDeltaMap[kvp.Key] = kvp.Value;

            ApplyDeltasToWorkingMesh();
            SaveDeltasToComponent();
            UpdateHandleFromSelection();
            UnityEditor.SceneView.RepaintAll();
        }

        public void ResetDeltas()
        {
            // リセット前の状態を完全にUndoに記録
            SaveDeltasToComponent();
            Undo.RegisterCompleteObjectUndo(TargetComponent, "Reset Mesh Deformation");

            // デルタをクリアしてコンポーネントに保存
            _activeDeltaMap.Clear();
            ApplyDeltasToWorkingMesh();
            SaveDeltasToComponent();
            EditorUtility.SetDirty(TargetComponent);
        }

        #endregion

        #region 編集セッション自動終了

        private bool _autoEndCallbacksRegistered = false;

        private void RegisterAutoEndEditCallbacks()
        {
            if (_autoEndCallbacksRegistered) return;
            _autoEndCallbacksRegistered = true;

            // 別のGameObjectを選択した時
            Selection.selectionChanged += OnSelectionChanged;
            // プレイモードに入る時
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Unityが閉じられる時
            EditorApplication.quitting += OnEditorQuitting;
            // ドメインリロード（リコンパイル）前
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void UnregisterAutoEndEditCallbacks()
        {
            if (!_autoEndCallbacksRegistered) return;
            _autoEndCallbacksRegistered = false;

            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private void OnSelectionChanged()
        {
            if (CurrentMode == EditMode.Off) return;

            // CHMコンポーネントのあるGameObjectが選択されたままなら終了しない
            if (TargetComponent != null && Selection.activeGameObject == TargetComponent.gameObject) return;

            EndEdit();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                EndEdit();
            }
        }

        private void OnEditorQuitting()
        {
            EndEdit();
        }

        private void OnBeforeAssemblyReload()
        {
            EndEdit();
        }

        #endregion
    }

    /// <summary>
    /// ドメインリロード後にEditting状態が残っていた場合の復旧処理
    /// EndEditが呼ばれずにリロードされた場合、rendererのsharedMeshが
    /// workingMeshのまま残るため、deformOriginalMeshから復元する
    /// </summary>
    [InitializeOnLoad]
    internal static class MeshDeformationDomainReloadGuard
    {
        static MeshDeformationDomainReloadGuard()
        {
            // ドメインリロード直後に実行
            EditorApplication.delayCall += CleanupOrphanedEditingSessions;
        }

        private static void CleanupOrphanedEditingSessions()
        {
            var allComponents = Object.FindObjectsByType<ChimeraHairMaster>(FindObjectsSortMode.None);
            foreach (var component in allComponents)
            {
                if (component.deformOriginalMesh != null)
                {
                    // deformOriginalMeshが残っている = EndEditが呼ばれなかった
                    // rendererのsharedMeshを復元
                    if (component.deformEditingRendererIndex >= 0
                        && component.deformEditingRendererIndex < component.targetRenderers.Count)
                    {
                        var renderer = component.targetRenderers[component.deformEditingRendererIndex];
                        if (renderer != null)
                        {
                            renderer.sharedMesh = component.deformOriginalMesh;
                        }
                    }
                    component.deformOriginalMesh = null;
                    component.deformEditingRendererIndex = -1;
                    EditorUtility.SetDirty(component);
                }
            }

            // static状態もクリア
            MeshDeformationSceneEditor.ActiveEditingRendererIds.Clear();
        }
    }
}
