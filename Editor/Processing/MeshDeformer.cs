using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// メッシュにデルタ（頂点オフセット）を適用する処理クラス
    /// </summary>
    public static class MeshDeformer
    {
        /// <summary>
        /// CHMコンポーネントの変形データをもとに、対象Rendererのメッシュを変形する
        /// NDMFパスから呼ばれる。メッシュは複製してから変形する（元メッシュを壊さない）
        /// </summary>
        public static void ApplyDeformation(ChimeraHairMaster component)
        {
            if (component.rendererDeformations == null || component.rendererDeformations.Count == 0)
                return;

            foreach (var deformation in component.rendererDeformations)
            {
                if (deformation.deltas == null || deformation.deltas.Count == 0)
                    continue;

                int rendererIndex = deformation.rendererIndex;
                if (rendererIndex < 0 || rendererIndex >= component.targetRenderers.Count)
                    continue;

                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null || renderer.sharedMesh == null)
                    continue;

                // 頂点数の検証
                if (renderer.sharedMesh.vertexCount != deformation.expectedVertexCount)
                {
                    Debug.LogWarning(
                        $"[ChimeraHairMaster] メッシュ変形スキップ: {renderer.name} の頂点数が変更されています " +
                        $"(期待: {deformation.expectedVertexCount}, 実際: {renderer.sharedMesh.vertexCount})");
                    continue;
                }

                // メッシュを複製してデルタを適用
                var meshCopy = Object.Instantiate(renderer.sharedMesh);
                meshCopy.name = renderer.sharedMesh.name + "_Deformed";

                ApplyDeltas(meshCopy, deformation.deltas);
                renderer.sharedMesh = meshCopy;
            }
        }

        /// <summary>
        /// 変形済みメッシュを新規アセットとしてエクスポートする
        /// </summary>
        public static Mesh ExportDeformedMesh(SkinnedMeshRenderer renderer, RendererDeformation deformation)
        {
            if (renderer == null || renderer.sharedMesh == null || deformation == null)
                return null;

            var mesh = Object.Instantiate(renderer.sharedMesh);
            mesh.name = renderer.sharedMesh.name + "_Deformed";

            if (deformation.deltas != null && deformation.deltas.Count > 0)
            {
                ApplyDeltas(mesh, deformation.deltas);
            }

            return mesh;
        }

        /// <summary>
        /// デルタ一覧をメッシュの頂点に適用する
        /// </summary>
        private static void ApplyDeltas(Mesh mesh, List<VertexDelta> deltas)
        {
            var vertices = mesh.vertices;

            foreach (var delta in deltas)
            {
                if (delta.vertexIndex < 0 || delta.vertexIndex >= vertices.Length)
                    continue;

                vertices[delta.vertexIndex] += delta.offset;
            }

            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
