using System.Linq;
using ChimeraHairMaster.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// メッシュ変形スタンドアロンのセットアップウィンドウ。
    /// Rendererの登録とコンポーネント作成（セットアップ）を行う。
    /// セットアップ後の編集はInspectorのMeshDeformationInspectorUIで行う。
    /// </summary>
    public class MeshDeformationWindow : EditorWindow
    {
        // Hierarchy右クリックメニュー
        [MenuItem("GameObject/CHM - メッシュ変形を追加", false, 20)]
        private static void AddFromHierarchy()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            // SkinnedMeshRenderer 優先、なければ MeshFilter 付きの MeshRenderer
            Renderer renderer = go.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null)
            {
                var meshRenderer = go.GetComponent<MeshRenderer>();
                if (meshRenderer != null && RendererMeshAccess.IsSupported(meshRenderer))
                    renderer = meshRenderer;
            }
            if (renderer == null)
            {
                EditorUtility.DisplayDialog(CHMLocales.Tr("MeshDeformWindow:Title"),
                    CHMLocales.Tr("MeshDeformWindow:Dialog:NoSMR"), "OK");
                return;
            }

            var conflict = FindConflict(renderer);
            if (conflict != null)
            {
                EditorUtility.DisplayDialog(CHMLocales.Tr("MeshDeformWindow:Title"), conflict, "OK");
                return;
            }

            var component = Undo.AddComponent<MeshDeformationStandalone>(go);
            component.targetRenderer = renderer;
        }

        [MenuItem("GameObject/CHM - メッシュ変形を追加", true)]
        private static bool AddFromHierarchyValidate()
        {
            return Selection.activeGameObject != null;
        }

        [MenuItem("キメラヘアマスター/メッシュ変形")]
        public static void ShowWindow()
        {
            var window = GetWindow<MeshDeformationWindow>();
            window.titleContent = new GUIContent(CHMLocales.Tr("MeshDeformWindow:Title"));
            window.minSize = new Vector2(350, 200);
            window.Show();
        }

        private Renderer _targetRenderer;

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(10);
            DrawRendererField();
            EditorGUILayout.Space(10);
            DrawSetupButton();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(CHMLocales.Tr("MeshDeformWindow:Header:Title"), EditorStyles.boldLabel);
                CHMLocales.DrawLanguagePicker();
            }
            EditorGUILayout.HelpBox(
                CHMLocales.Tr("MeshDeformWindow:Header:Description"),
                MessageType.Info);
        }

        private void DrawRendererField()
        {
            _targetRenderer = (Renderer)EditorGUILayout.ObjectField(
                CHMLocales.Tr("MeshDeformWindow:TargetRenderer"), _targetRenderer, typeof(Renderer), true);

            // ドラッグ&ドロップ対応
            HandleDragAndDrop();
        }

        private void HandleDragAndDrop()
        {
            var dropArea = GUILayoutUtility.GetLastRect();

            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;
            if (!dropArea.Contains(evt.mousePosition))
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();

                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is Renderer r && RendererMeshAccess.IsSupported(r))
                    {
                        _targetRenderer = r;
                        break;
                    }

                    if (obj is GameObject go)
                    {
                        Renderer found = go.GetComponent<SkinnedMeshRenderer>();
                        if (found == null)
                        {
                            var mr = go.GetComponent<MeshRenderer>();
                            if (mr != null && RendererMeshAccess.IsSupported(mr))
                                found = mr;
                        }
                        if (found != null)
                        {
                            _targetRenderer = found;
                            break;
                        }
                    }
                }

                evt.Use();
            }
        }

        private void DrawSetupButton()
        {
            if (_targetRenderer == null)
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr("MeshDeformWindow:RegisterRendererHint"), MessageType.Warning);
                GUI.enabled = false;
            }
            else if (!RendererMeshAccess.IsSupported(_targetRenderer))
            {
                // SMR でも MeshFilter 付き MeshRenderer でもない（ParticleSystemRenderer 等）
                EditorGUILayout.HelpBox(CHMLocales.Tr("MeshDeformWindow:Dialog:NoSMR"), MessageType.Error);
                GUI.enabled = false;
            }
            else
            {
                // 既存の変形との重複チェック
                var conflict = FindConflict(_targetRenderer);
                if (conflict != null)
                {
                    EditorGUILayout.HelpBox(conflict, MessageType.Error);
                    GUI.enabled = false;
                }
            }

            if (GUILayout.Button(CHMLocales.Tr("MeshDeformWindow:Action:Setup"), GUILayout.Height(30)))
            {
                ExecuteSetup(_targetRenderer);
            }

            GUI.enabled = true;
        }

        /// <summary>
        /// 指定Rendererが既に他のコンポーネントで変形対象になっていないかチェック。
        /// 重複がある場合は競合元の名前を返す。なければnull。
        /// </summary>
        private static string FindConflict(Renderer renderer)
        {
            // CHMコンポーネントとの重複（CHMの有効/無効に関わらず、登録されていればブロック）
            // CHM 本体の対象は SkinnedMeshRenderer のみ
            var allCHM = Object.FindObjectsByType<ChimeraHairMaster>(FindObjectsSortMode.None);
            foreach (var chm in allCHM)
            {
                if (renderer is SkinnedMeshRenderer smr
                    && chm.targetRenderers != null && chm.targetRenderers.Contains(smr))
                    return string.Format(CHMLocales.Tr("MeshDeformWindow:Conflict:CHM"), renderer.name);
            }

            // 既存のスタンドアロンコンポーネントとの重複
            var allStandalone = Object.FindObjectsByType<MeshDeformationStandalone>(FindObjectsSortMode.None);
            foreach (var standalone in allStandalone)
            {
                if (standalone.targetRenderer == renderer)
                    return string.Format(CHMLocales.Tr("MeshDeformWindow:Conflict:Existing"), renderer.name);
            }

            return null;
        }

        private void ExecuteSetup(Renderer renderer)
        {
            var targetObject = renderer.gameObject;

            // 既存コンポーネントがあるか確認
            var existing = targetObject.GetComponent<MeshDeformationStandalone>();
            if (existing != null)
            {
                if (!EditorUtility.DisplayDialog(
                    CHMLocales.Tr("MeshDeformWindow:Dialog:ConfirmTitle"),
                    string.Format(CHMLocales.Tr("MeshDeformWindow:Dialog:OverwriteMessage"), targetObject.name),
                    CHMLocales.Tr("MeshDeformWindow:Dialog:Overwrite"), CHMLocales.Tr("MeshDeformWindow:Dialog:Cancel")))
                {
                    return;
                }

                Undo.RegisterCompleteObjectUndo(existing, "Update Mesh Deformation Standalone");
                existing.targetRenderer = renderer;
            }
            else
            {
                var component = Undo.AddComponent<MeshDeformationStandalone>(targetObject);
                component.targetRenderer = renderer;
            }

            // 作成したオブジェクトを選択してInspectorにフォーカス
            Selection.activeGameObject = targetObject;
            EditorGUIUtility.PingObject(targetObject);

            // ウィンドウを初期状態にリセット
            _targetRenderer = null;
        }
    }
}
