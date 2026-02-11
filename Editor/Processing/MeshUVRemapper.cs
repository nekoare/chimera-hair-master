using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// メッシュのUV座標を再マッピングする
    /// </summary>
    public static class MeshUVRemapper
    {
        /// <summary>
        /// メッシュのUVを再マッピング
        /// </summary>
        public static Mesh RemapUVs(
            Mesh sourceMesh,
            TextureAtlasBuilder.UVTransform transform)
        {
            if (sourceMesh == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source mesh is null");
                return null;
            }

            // メッシュを複製
            Mesh newMesh = Object.Instantiate(sourceMesh);
            newMesh.name = sourceMesh.name + "_UVRemapped";

            // UV0を取得
            var uvs = new List<Vector2>();
            newMesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
            {
                Debug.LogWarning($"[ChimeraHairMaster] Mesh '{sourceMesh.name}' has no UV0");
                return newMesh;
            }

            // UV変換を適用
            for (int i = 0; i < uvs.Count; i++)
            {
                uvs[i] = transform.TransformUV(uvs[i]);
            }

            // 変換後のUVを設定
            newMesh.SetUVs(0, uvs);

            return newMesh;
        }

        /// <summary>
        /// 複数のRendererのメッシュUVを再マッピング
        /// </summary>
        public static void RemapRendererUVs(
            List<SkinnedMeshRenderer> renderers,
            Dictionary<SkinnedMeshRenderer, TextureAtlasBuilder.UVTransform> transforms)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!transforms.TryGetValue(renderer, out var transform)) continue;

                var sourceMesh = renderer.sharedMesh;
                if (sourceMesh == null) continue;

                var newMesh = RemapUVs(sourceMesh, transform);
                if (newMesh != null)
                {
                    renderer.sharedMesh = newMesh;
                    Debug.Log($"[ChimeraHairMaster] UV再マッピング完了: {renderer.name}");
                }
            }
        }

        /// <summary>
        /// UV境界ボックスを計算
        /// </summary>
        public static Rect CalculateUVBounds(Mesh mesh)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
            {
                return new Rect(0, 0, 1, 1);
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (var uv in uvs)
            {
                minX = Mathf.Min(minX, uv.x);
                minY = Mathf.Min(minY, uv.y);
                maxX = Mathf.Max(maxX, uv.x);
                maxY = Mathf.Max(maxY, uv.y);
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// UVを0-1範囲に正規化してから変換
        /// </summary>
        public static Mesh RemapUVsNormalized(
            Mesh sourceMesh,
            TextureAtlasBuilder.UVTransform transform)
        {
            if (sourceMesh == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source mesh is null");
                return null;
            }

            // メッシュを複製
            Mesh newMesh = Object.Instantiate(sourceMesh);
            newMesh.name = sourceMesh.name + "_UVRemapped";

            // UV0を取得
            var uvs = new List<Vector2>();
            newMesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
            {
                Debug.LogWarning($"[ChimeraHairMaster] Mesh '{sourceMesh.name}' has no UV0");
                return newMesh;
            }

            // まずUV境界を計算
            Rect bounds = CalculateUVBounds(sourceMesh);

            // UV変換を適用（正規化 -> 変換）
            for (int i = 0; i < uvs.Count; i++)
            {
                // 0-1範囲に正規化
                Vector2 normalizedUV = new Vector2(
                    bounds.width > 0 ? (uvs[i].x - bounds.x) / bounds.width : 0,
                    bounds.height > 0 ? (uvs[i].y - bounds.y) / bounds.height : 0
                );

                // 変換を適用
                uvs[i] = transform.TransformUV(normalizedUV);
            }

            // 変換後のUVを設定
            newMesh.SetUVs(0, uvs);

            return newMesh;
        }

        /// <summary>
        /// アイランド単位でUVを変換（後方互換用、rendererIndex=0として扱う）
        /// 各頂点がどのアイランドに属するかを判定し、そのアイランドの変換を適用
        /// </summary>
        public static Mesh RemapUVsByIslands(Mesh sourceMesh, List<IslandPlacement> islands)
        {
            return RemapUVsByIslands(sourceMesh, islands, 0);
        }

        /// <summary>
        /// アイランド単位でUVを変換（サブメッシュ対応版）
        /// 各頂点がどのサブメッシュ・アイランドに属するかを判定し、そのアイランドの変換を適用
        /// </summary>
        /// <param name="sourceMesh">変換元のメッシュ</param>
        /// <param name="islands">アイランド配置情報のリスト</param>
        /// <param name="rendererIndex">このメッシュに対応するRendererのインデックス</param>
        public static Mesh RemapUVsByIslands(Mesh sourceMesh, List<IslandPlacement> islands, int rendererIndex)
        {
            if (sourceMesh == null)
            {
                Debug.LogError("[ChimeraHairMaster] Source mesh is null");
                return null;
            }

            if (islands == null || islands.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] No islands provided for UV remapping");
                return Object.Instantiate(sourceMesh);
            }

            // メッシュを複製
            Mesh newMesh = Object.Instantiate(sourceMesh);
            newMesh.name = sourceMesh.name + "_UVRemapped";

            // UV0を取得
            var uvs = new List<Vector2>();
            newMesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
            {
                Debug.LogWarning($"[ChimeraHairMaster] Mesh '{sourceMesh.name}' has no UV0");
                return newMesh;
            }

            // このRendererに関連するアイランドのみをフィルタリング
            var relevantIslands = new List<IslandPlacement>();
            foreach (var island in islands)
            {
                if (island.rendererIndex == rendererIndex)
                {
                    relevantIslands.Add(island);
                }
            }

            // サブメッシュごとの頂点インデックスを取得
            var vertexToSubmesh = new int[uvs.Count];
            for (int i = 0; i < vertexToSubmesh.Length; i++)
            {
                vertexToSubmesh[i] = -1; // 未割り当て
            }

            int submeshCount = sourceMesh.subMeshCount;
            for (int submeshIdx = 0; submeshIdx < submeshCount; submeshIdx++)
            {
                var triangles = sourceMesh.GetTriangles(submeshIdx);
                foreach (var vertexIdx in triangles)
                {
                    if (vertexToSubmesh[vertexIdx] == -1)
                    {
                        vertexToSubmesh[vertexIdx] = submeshIdx;
                    }
                }
            }

            // 各頂点のUVを変換
            for (int i = 0; i < uvs.Count; i++)
            {
                Vector2 uv = uvs[i];
                int submeshIdx = vertexToSubmesh[i];

                // この頂点が属するアイランドを探す（サブメッシュを考慮）
                IslandPlacement matchedIsland = null;
                foreach (var island in relevantIslands)
                {
                    // サブメッシュが一致し、アイランドの境界内にあるかチェック
                    if (island.submeshIndex == submeshIdx && IsUVInBounds(uv, island.originalBounds, 0.001f))
                    {
                        matchedIsland = island;
                        break;
                    }
                }

                // サブメッシュが一致しない場合、UV境界のみでマッチング（フォールバック）
                if (matchedIsland == null)
                {
                    foreach (var island in relevantIslands)
                    {
                        if (IsUVInBounds(uv, island.originalBounds, 0.001f))
                        {
                            matchedIsland = island;
                            break;
                        }
                    }
                }

                if (matchedIsland != null)
                {
                    // 元の境界内での正規化座標を計算 (0-1)
                    Vector2 normalizedInIsland = new Vector2(
                        matchedIsland.originalBounds.width > 0
                            ? (uv.x - matchedIsland.originalBounds.x) / matchedIsland.originalBounds.width
                            : 0.5f,
                        matchedIsland.originalBounds.height > 0
                            ? (uv.y - matchedIsland.originalBounds.y) / matchedIsland.originalBounds.height
                            : 0.5f
                    );

                    // アトラス上の座標に変換
                    uvs[i] = new Vector2(
                        matchedIsland.atlasPosition.x + normalizedInIsland.x * matchedIsland.atlasScale.x,
                        matchedIsland.atlasPosition.y + normalizedInIsland.y * matchedIsland.atlasScale.y
                    );
                }
                else
                {
                    // どのアイランドにも属さない場合はそのまま
                    Debug.LogWarning($"[ChimeraHairMaster] UV ({uv.x:F3}, {uv.y:F3}) in submesh {submeshIdx} does not belong to any island");
                }
            }

            // 変換後のUVを設定
            newMesh.SetUVs(0, uvs);

            return newMesh;
        }

        /// <summary>
        /// UVが指定された境界内にあるかチェック
        /// </summary>
        private static bool IsUVInBounds(Vector2 uv, Rect bounds, float margin = 0f)
        {
            return uv.x >= bounds.x - margin &&
                   uv.x <= bounds.x + bounds.width + margin &&
                   uv.y >= bounds.y - margin &&
                   uv.y <= bounds.y + bounds.height + margin;
        }
    }
}
