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
        public static void ApplyDeformation(IMeshDeformationTarget target)
        {
            if (target.RendererDeformations == null || target.RendererDeformations.Count == 0)
                return;

            foreach (var deformation in target.RendererDeformations)
            {
                if (deformation.deltas == null || deformation.deltas.Count == 0)
                    continue;

                int rendererIndex = deformation.rendererIndex;
                if (rendererIndex < 0 || rendererIndex >= target.DeformTargetRenderers.Count)
                    continue;

                var renderer = target.DeformTargetRenderers[rendererIndex];
                var sharedMesh = RendererMeshAccess.GetSharedMesh(renderer);
                if (renderer == null || sharedMesh == null)
                    continue;

                // 頂点数の検証
                if (sharedMesh.vertexCount != deformation.expectedVertexCount)
                {
                    Debug.LogWarning(
                        $"[ChimeraHairMaster] メッシュ変形スキップ: {renderer.name} の頂点数が変更されています " +
                        $"(期待: {deformation.expectedVertexCount}, 実際: {sharedMesh.vertexCount})");
                    continue;
                }

                // メッシュを複製してデルタを適用
                var meshCopy = Object.Instantiate(sharedMesh);
                meshCopy.name = sharedMesh.name + "_Deformed";

                ApplyDeltas(meshCopy, deformation.deltas);
                RendererMeshAccess.SetSharedMesh(renderer, meshCopy);
            }
        }

        /// <summary>
        /// 変形済みメッシュを新規アセットとしてエクスポートする
        /// </summary>
        public static Mesh ExportDeformedMesh(Renderer renderer, RendererDeformation deformation)
        {
            var sourceMesh = RendererMeshAccess.GetSharedMesh(renderer);
            if (renderer == null || sourceMesh == null || deformation == null)
                return null;

            // ビルドパス(ApplyDeformation)と同じ頂点数ガード。
            // メッシュ差し替え/再インポート後に古いデルタを別頂点へ焼き込む破損を防ぐ
            if (sourceMesh.vertexCount != deformation.expectedVertexCount)
            {
                Debug.LogWarning(
                    $"[ChimeraHairMaster] メッシュ変形エクスポート中止: {renderer.name} の頂点数が変形時から変わっています " +
                    $"(期待: {deformation.expectedVertexCount}, 実際: {sourceMesh.vertexCount})");
                return null;
            }

            var mesh = Object.Instantiate(sourceMesh);
            mesh.name = sourceMesh.name + "_Deformed";

            if (deformation.deltas != null && deformation.deltas.Count > 0)
            {
                ApplyDeltas(mesh, deformation.deltas);
            }

            return mesh;
        }

        /// <summary>
        /// 変形を Blendshape として追加した新メッシュをエクスポートする。
        /// 元の頂点位置は保持し、Blendshape として deltas を末尾に追加する。
        /// </summary>
        /// <param name="renderer">対象 Renderer</param>
        /// <param name="deformation">変形データ</param>
        /// <param name="requestedName">希望する Blendshape 名</param>
        /// <param name="actualName">実際に使われた名前（同名衝突時は unique 化される）</param>
        /// <returns>新メッシュ（Blendshape 追加済み）。失敗時 null</returns>
        public static Mesh ExportDeformedMeshAsBlendshape(
            Renderer renderer,
            RendererDeformation deformation,
            string requestedName,
            out string actualName)
        {
            actualName = requestedName;
            var sourceMesh = RendererMeshAccess.GetSharedMesh(renderer);
            if (renderer == null || sourceMesh == null || deformation == null)
                return null;
            if (deformation.deltas == null || deformation.deltas.Count == 0)
                return null;
            // ビルドパスと同じ頂点数ガード（頂点不一致なら焼き込まず中止）
            if (sourceMesh.vertexCount != deformation.expectedVertexCount)
            {
                Debug.LogWarning(
                    $"[ChimeraHairMaster] Blendshape 出力中止: {renderer.name} の頂点数が変形時から変わっています " +
                    $"(期待: {deformation.expectedVertexCount}, 実際: {sourceMesh.vertexCount})");
                return null;
            }
            if (string.IsNullOrEmpty(requestedName))
                requestedName = "CHMDeform";
            var mesh = Object.Instantiate(sourceMesh);
            mesh.name = sourceMesh.name + "_DeformedBS";

            // 既存 Blendshape 名と衝突しない名前を作る
            var existingNames = new HashSet<string>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                existingNames.Add(mesh.GetBlendShapeName(i));
            }
            actualName = MakeUniqueName(requestedName, existingNames);

            // sparse deltas → dense Vector3[] 配列
            var deltaArray = new Vector3[mesh.vertexCount];
            foreach (var d in deformation.deltas)
            {
                if (d.vertexIndex < 0 || d.vertexIndex >= deltaArray.Length) continue;
                deltaArray[d.vertexIndex] = d.offset;
            }

            // 1 frame, weight=100 で末尾に追加
            // normals/tangents は null（自動再計算なし、視覚的にはほぼ問題なし）
            mesh.AddBlendShapeFrame(actualName, 100f, deltaArray, null, null);

            return mesh;
        }

        private static string MakeUniqueName(string baseName, HashSet<string> existing)
        {
            if (!existing.Contains(baseName)) return baseName;
            for (int i = 1; i < 1000; i++)
            {
                string candidate = $"{baseName}_{i}";
                if (!existing.Contains(candidate)) return candidate;
            }
            return baseName + "_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
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
