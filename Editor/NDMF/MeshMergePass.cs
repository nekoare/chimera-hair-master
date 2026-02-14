using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// メッシュ統合処理パス
    /// </summary>
    public class MeshMergePass : Pass<MeshMergePass>
    {
        public override string DisplayName => "CHM: Mesh Merge";

        /// <summary>
        /// AAO等の後続プラグインの処理完了後にクリーンアップするGameObjectのリスト
        /// </summary>
        internal static readonly List<GameObject> GameObjectsToCleanup = new List<GameObject>();

        protected override void Execute(BuildContext context)
        {
            GameObjectsToCleanup.Clear();

            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableMeshMerge) continue;

                ProcessMeshMerge(context, component);
            }
        }

        private void ProcessMeshMerge(BuildContext context, ChimeraHairMaster component)
        {
            Debug.Log($"[ChimeraHairMaster] メッシュ統合処理開始: {component.gameObject.name}");

            ProcessIndependentMerge(context, component);

            Debug.Log($"[ChimeraHairMaster] メッシュ統合処理完了: {component.gameObject.name}");
        }

        private void ProcessIndependentMerge(BuildContext context, ChimeraHairMaster component)
        {
            Debug.Log("[ChimeraHairMaster] 独立モード: 自前でメッシュ統合を実行");

            if (component.targetRenderers.Count < 2)
            {
                Debug.Log("[ChimeraHairMaster] 対象Rendererが2つ未満のためスキップ");
                return;
            }

            // メッシュを統合
            var mergeResult = MeshMerger.Merge(
                component.targetRenderers,
                component.outputMaterial,
                context.AvatarRootObject.transform,
                component
            );

            if (mergeResult == null)
            {
                Debug.LogError("[ChimeraHairMaster] メッシュ統合に失敗しました");
                return;
            }

            // 統合したRendererを作成
            var mergedRenderer = MeshMerger.CreateMergedRenderer(
                mergeResult,
                component.outputMaterial,
                component.transform.parent,
                "CHM_MergedHair",
                component
            );

            Debug.Log($"[ChimeraHairMaster] 統合Renderer作成完了: 頂点数={mergeResult.MergedMesh.vertexCount}, ボーン数={mergeResult.Bones.Length}");

            // 元のRendererを処理：残すサブメッシュがあればそのメッシュを割り当て、無ければ削除
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                if (mergeResult.RemainingMeshes != null && mergeResult.RemainingMeshes.TryGetValue(renderer, out var remainingMesh) && remainingMesh != null && remainingMesh.vertexCount > 0)
                {
                    renderer.sharedMesh = remainingMesh;

                    // 除外されたサブメッシュに対応するマテリアルのみを残す
                    var originalMaterials = renderer.sharedMaterials;
                    var newMaterials = new List<Material>();

                    for (int s = 0; s < remainingMesh.subMeshCount; s++)
                    {
                        if (!component.IsSubmeshIncluded(r, s))
                        {
                            // 除外されたサブメッシュのマテリアルを残す
                            if (s < originalMaterials.Length)
                            {
                                newMaterials.Add(originalMaterials[s]);
                            }
                            else
                            {
                                newMaterials.Add(null);
                            }
                        }
                        else
                        {
                            // 統合されたサブメッシュは空にする
                            newMaterials.Add(null);
                        }
                    }

                    renderer.sharedMaterials = newMaterials.ToArray();
                    Debug.Log($"[ChimeraHairMaster] Renderer {renderer.name} を残しました（除外サブメッシュあり）");
                }
                else
                {
                    // AAOとの互換性のため、GameObjectは即座に破壊せず無効化して記録する。
                    // AAOがOptimizingフェーズで処理を終えた後、RendererCleanupPassで削除する。
                    string rendererName = renderer.name;
                    renderer.enabled = false;
                    renderer.sharedMesh = null;
                    renderer.sharedMaterials = new Material[0];
                    renderer.gameObject.SetActive(false);
                    GameObjectsToCleanup.Add(renderer.gameObject);
                    Debug.Log($"[ChimeraHairMaster] Renderer {rendererName} を無効化しました（クリーンアップ待ち）");
                }
            }

            Debug.Log("[ChimeraHairMaster] 元のRendererの処理完了");
        }
    }
}
