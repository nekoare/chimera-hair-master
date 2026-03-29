using System.Linq;
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

            var renderer = go.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null)
            {
                EditorUtility.DisplayDialog("メッシュ変形",
                    "選択したGameObjectにSkinnedMeshRendererがありません。", "OK");
                return;
            }

            var conflict = FindConflict(renderer);
            if (conflict != null)
            {
                EditorUtility.DisplayDialog("メッシュ変形", conflict, "OK");
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
            window.titleContent = new GUIContent("メッシュ変形");
            window.minSize = new Vector2(350, 200);
            window.Show();
        }

        private SkinnedMeshRenderer _targetRenderer;

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
            EditorGUILayout.LabelField("メッシュ変形 セットアップ", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "変形したいRendererを登録して「セットアップ」を押してください。\n" +
                "セットアップ後はInspectorから編集できます。",
                MessageType.Info);
        }

        private void DrawRendererField()
        {
            _targetRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "対象Renderer", _targetRenderer, typeof(SkinnedMeshRenderer), true);

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
                    if (obj is SkinnedMeshRenderer smr)
                    {
                        _targetRenderer = smr;
                        break;
                    }

                    if (obj is GameObject go)
                    {
                        var smrOnGo = go.GetComponent<SkinnedMeshRenderer>();
                        if (smrOnGo != null)
                        {
                            _targetRenderer = smrOnGo;
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
                EditorGUILayout.HelpBox("Rendererを登録してください。", MessageType.Warning);
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

            if (GUILayout.Button("セットアップ", GUILayout.Height(30)))
            {
                ExecuteSetup(_targetRenderer);
            }

            GUI.enabled = true;
        }

        /// <summary>
        /// 指定Rendererが既に他のコンポーネントで変形対象になっていないかチェック。
        /// 重複がある場合は競合元の名前を返す。なければnull。
        /// </summary>
        private static string FindConflict(SkinnedMeshRenderer renderer)
        {
            // CHMコンポーネントとの重複（CHMの有効/無効に関わらず、登録されていればブロック）
            var allCHM = Object.FindObjectsByType<ChimeraHairMaster>(FindObjectsSortMode.None);
            foreach (var chm in allCHM)
            {
                if (chm.targetRenderers != null && chm.targetRenderers.Contains(renderer))
                    return $"{renderer.name} にキメラヘアマスターがあるため変形はそこから行ってください。";
            }

            // 既存のスタンドアロンコンポーネントとの重複
            var allStandalone = Object.FindObjectsByType<MeshDeformationStandalone>(FindObjectsSortMode.None);
            foreach (var standalone in allStandalone)
            {
                if (standalone.targetRenderer == renderer)
                    return $"{renderer.name} には既にメッシュ変形コンポーネントがあります。";
            }

            return null;
        }

        private void ExecuteSetup(SkinnedMeshRenderer renderer)
        {
            var targetObject = renderer.gameObject;

            // 既存コンポーネントがあるか確認
            var existing = targetObject.GetComponent<MeshDeformationStandalone>();
            if (existing != null)
            {
                if (!EditorUtility.DisplayDialog(
                    "確認",
                    $"{targetObject.name} には既にメッシュ変形コンポーネントがあります。\n" +
                    "対象Rendererを上書きしますか？",
                    "上書き", "キャンセル"))
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
