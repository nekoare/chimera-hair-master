using ChimeraHairMaster.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// UV配置プレビューの拡大ビュー用 EditorWindow。
    /// 親 ChimeraHairMasterWindow の描画メソッドを参照し、ウィンドウ全体に大きく UV プレビューを表示する。
    /// マウス操作（ドラッグによるアイランド配置等）もそのまま動作する。
    /// </summary>
    public class UVPlacementPreviewWindow : EditorWindow
    {
        private ChimeraHairMasterWindow owner;

        internal static void Open(ChimeraHairMasterWindow owner)
        {
            if (owner == null) return;

            // 既に開いている場合はユーザーが調整したサイズを維持する
            bool isNewInstance = !HasOpenInstances<UVPlacementPreviewWindow>();

            var window = GetWindow<UVPlacementPreviewWindow>(false, CHMLocales.Tr("Window:UVPreview:Title"), true);
            window.owner = owner;
            window.minSize = new Vector2(400, 440);
            if (isNewInstance)
            {
                // 初期サイズ: プレビュー 600 + 上下左右 margin 40 + 下部ヘルプ領域
                window.position = new Rect(window.position.x, window.position.y, 680, 720);
            }
            window.Show();
        }

        private void OnEnable()
        {
            // 頻繁に再描画して、親ウィンドウの状態変化が見えるようにする
            EditorApplication.update += RepaintDelegate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintDelegate;
        }

        private void RepaintDelegate()
        {
            Repaint();
        }

        private void OnGUI()
        {
            if (owner == null)
            {
                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Window:UVPreview:OwnerClosed"),
                    MessageType.Warning);
                return;
            }

            // 余白を確保しつつ、残り全体を正方形で UV プレビューに割り当てる
            // 上下左右 40px の余白を確保（UV アイランドがはみ出したときにも見えるように）
            const float margin = 40f;
            float availableWidth = position.width - margin * 2;
            float availableHeight = position.height - margin * 2 - 30; // 下部のヘルプ用
            float previewSize = Mathf.Max(200, Mathf.Min(availableWidth, availableHeight));

            Rect previewRect = new Rect(
                (position.width - previewSize) * 0.5f,
                margin,
                previewSize,
                previewSize);

            owner.DrawUVPreview(previewRect);

            // 下部のヘルプ
            var helpRect = new Rect(margin, previewRect.yMax + 8, position.width - margin * 2, 20);
            EditorGUI.LabelField(helpRect, CHMLocales.Tr("Window:UVPreview:Hint"), EditorStyles.miniLabel);
        }
    }
}
