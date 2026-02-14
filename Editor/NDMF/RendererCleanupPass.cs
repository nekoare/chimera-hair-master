using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// メッシュ統合で不要になったRendererのGameObjectを削除するクリーンアップパス。
    /// AAO等の後続プラグインがOptimizingフェーズで処理を終えた後に実行する。
    /// </summary>
    public class RendererCleanupPass : Pass<RendererCleanupPass>
    {
        public override string DisplayName => "CHM: Renderer Cleanup";

        protected override void Execute(BuildContext context)
        {
            var gameObjects = MeshMergePass.GameObjectsToCleanup;
            if (gameObjects.Count == 0) return;

            foreach (var go in gameObjects)
            {
                if (go == null) continue;
                Debug.Log($"[ChimeraHairMaster] 不要なGameObjectを削除: {go.name}");
                Object.DestroyImmediate(go);
            }

            Debug.Log($"[ChimeraHairMaster] クリーンアップ完了: {gameObjects.Count}個のGameObjectを削除");
            gameObjects.Clear();
        }
    }
}
