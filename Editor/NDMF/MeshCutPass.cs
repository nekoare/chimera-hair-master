using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// マスクに基づくメッシュカット処理パス
    /// メッシュ統合前に実行し、不要な三角形を除去する
    /// </summary>
    public class MeshCutPass : Pass<MeshCutPass>
    {
        public override string DisplayName => "CHM: Mesh Cut";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableMeshMerge) continue;

                ProcessMeshCut(component);
            }
        }

        private void ProcessMeshCut(ChimeraHairMaster component)
        {
            // マスクが設定されているエントリを Renderer ごとにグループ化
            var masksByRenderer = new Dictionary<int, List<(int submeshIndex, Texture2D mask)>>();

            foreach (var entry in component.materialSelections)
            {
                if (!entry.isIncluded) continue;
                if (entry.meshCutMask == null) continue;

                if (!masksByRenderer.ContainsKey(entry.rendererIndex))
                    masksByRenderer[entry.rendererIndex] = new List<(int, Texture2D)>();
                masksByRenderer[entry.rendererIndex].Add((entry.submeshIndex, entry.meshCutMask));
            }

            if (masksByRenderer.Count == 0) return;

            Debug.Log($"[ChimeraHairMaster] メッシュカット処理開始: {component.gameObject.name}");

            foreach (var kvp in masksByRenderer)
            {
                int rendererIndex = kvp.Key;
                var submeshMasks = kvp.Value;

                if (rendererIndex < 0 || rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // メッシュを複製（元メッシュを壊さないため）
                var meshCopy = Object.Instantiate(renderer.sharedMesh);
                meshCopy.name = renderer.sharedMesh.name + "_Cut";
                int totalRemoved = 0;

                foreach (var (submeshIndex, mask) in submeshMasks)
                {
                    int removed = MeshCutter.CutMeshByMask(meshCopy, submeshIndex, mask);
                    totalRemoved += removed;
                    Debug.Log($"[ChimeraHairMaster] メッシュカット: {renderer.name} SubMesh {submeshIndex} から {removed} 三角形を除去");
                }

                renderer.sharedMesh = meshCopy;
                Debug.Log($"[ChimeraHairMaster] メッシュカット完了: {renderer.name} (合計 {totalRemoved} 三角形除去)");
            }
        }
    }
}
