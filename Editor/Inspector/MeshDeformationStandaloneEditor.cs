using ChimeraHairMaster.Editor.Deformation;
using ChimeraHairMaster.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// MeshDeformationStandaloneコンポーネントのCustomEditor。
    /// MeshDeformationInspectorUI による編集UIを提供する。
    /// </summary>
    [CustomEditor(typeof(MeshDeformationStandalone))]
    public class MeshDeformationStandaloneEditor : UnityEditor.Editor
    {
        private MeshDeformationInspectorUI meshDeformationUI;

        protected override void OnHeaderGUI()
        {
            var rect = EditorGUILayout.GetControlRect(false, 22);

            // ヘッダー背景
            if (Event.current.type == EventType.Repaint)
            {
                new GUIStyle("IN Title").Draw(rect, false, false, false, false);
            }

            // アイコン
            var iconRect = new Rect(rect.x + 6, rect.y + 3, 16, 16);
            var icon = EditorGUIUtility.ObjectContent(null, typeof(MonoBehaviour)).image;
            if (icon != null) GUI.DrawTexture(iconRect, icon);

            // タイトル
            var titleRect = new Rect(rect.x + 28, rect.y + 2, rect.width - 60, 18);
            GUI.Label(titleRect, CHMLocales.Tr("MeshDeformStandalone:Header"), EditorStyles.boldLabel);

            // コンテキストメニュー（⋮ボタン）
            var menuRect = new Rect(rect.xMax - 20, rect.y + 3, 16, 16);
            if (GUI.Button(menuRect, EditorGUIUtility.IconContent("_Menu"), GUIStyle.none))
            {
                ShowContextMenu(menuRect);
            }

            // 右クリックコンテキストメニュー
            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                ShowContextMenu(new Rect(Event.current.mousePosition, Vector2.zero));
                Event.current.Use();
            }
        }

        private void ShowContextMenu(Rect position)
        {
            EditorUtility.DisplayPopupMenu(
                new Rect(position.x, position.yMax, 0, 0),
                "CONTEXT/Component", new MenuCommand(target));
        }

        private void OnEnable()
        {
            meshDeformationUI = new MeshDeformationInspectorUI();

            // SceneView のメッシュ変形ハンドラを登録
            SceneView.duringSceneGui -= OnMeshDeformSceneGUI;
            SceneView.duringSceneGui += OnMeshDeformSceneGUI;
        }

        private void OnDisable()
        {
            // 編集中でなければ Scene GUI コールバックを解除
            var activeEditor = MeshDeformationInspectorUI.ActiveSceneEditor;
            if (activeEditor == null || activeEditor.CurrentMode == MeshDeformationSceneEditor.EditMode.Off)
            {
                SceneView.duringSceneGui -= OnMeshDeformSceneGUI;
            }

            meshDeformationUI?.Cleanup();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var component = target as MeshDeformationStandalone;
            if (component == null) return;

            if (component.targetRenderer == null)
            {
                EditorGUILayout.HelpBox(CHMLocales.Tr("MeshDeformStandalone:NoRendererWarning"), MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(5);
            CHMLocales.DrawLanguagePicker();

            // メッシュ変形 UI（MeshDeformationInspectorUI を再利用）
            meshDeformationUI?.Draw(component);

            serializedObject.ApplyModifiedProperties();
        }

        private static void OnMeshDeformSceneGUI(SceneView sv)
        {
            var editor = MeshDeformationInspectorUI.ActiveSceneEditor;
            if (editor != null && editor.CurrentMode != MeshDeformationSceneEditor.EditMode.Off)
            {
                editor.OnSceneGUI(sv);
            }
        }
    }
}
