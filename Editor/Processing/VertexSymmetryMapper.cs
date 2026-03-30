using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 対称軸に基づいて頂点のミラーペアを検出する
    /// </summary>
    public static class VertexSymmetryMapper
    {
        /// <summary>
        /// 対称マッピングを構築する
        /// 各頂点について、指定軸で反転した位置に最も近い頂点をペアとして登録する
        /// </summary>
        /// <param name="vertices">メッシュの頂点配列</param>
        /// <param name="mirrorX">X軸で対称</param>
        /// <param name="mirrorY">Y軸で対称</param>
        /// <param name="mirrorZ">Z軸で対称</param>
        /// <param name="threshold">ペアリング閾値（メッシュBoundsサイズの0.1%をデフォルトに）</param>
        /// <returns>頂点インデックス → 対称頂点インデックスのマッピング</returns>
        public static Dictionary<int, int> BuildSymmetryMap(
            Vector3[] vertices,
            bool mirrorX, bool mirrorY, bool mirrorZ,
            Vector3 offset = default,
            float threshold = -1f)
        {
            var map = new Dictionary<int, int>();
            if (vertices == null || vertices.Length == 0) return map;
            if (!mirrorX && !mirrorY && !mirrorZ) return map;

            // 閾値の自動計算
            if (threshold < 0)
            {
                var bounds = CalculateBounds(vertices);
                float maxExtent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                threshold = maxExtent * 0.02f; // メッシュサイズの2%
            }

            // KD-Tree的な高速検索のため空間グリッドを使用
            float cellSize = threshold * 2f;
            var grid = BuildSpatialGrid(vertices, cellSize);

            for (int i = 0; i < vertices.Length; i++)
            {
                if (map.ContainsKey(i)) continue;

                var mirrored = MirrorPosition(vertices[i], mirrorX, mirrorY, mirrorZ, offset);
                int nearest = FindNearestInGrid(grid, vertices, mirrored, cellSize, threshold);

                if (nearest >= 0 && nearest != i)
                {
                    map[i] = nearest;
                    map[nearest] = i;
                }
            }

            return map;
        }

        private static Vector3 MirrorPosition(Vector3 pos, bool x, bool y, bool z, Vector3 offset)
        {
            return new Vector3(
                x ? 2f * offset.x - pos.x : pos.x,
                y ? 2f * offset.y - pos.y : pos.y,
                z ? 2f * offset.z - pos.z : pos.z);
        }

        private static Bounds CalculateBounds(Vector3[] vertices)
        {
            var bounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Length; i++)
                bounds.Encapsulate(vertices[i]);
            return bounds;
        }

        private static Dictionary<(int, int, int), List<int>> BuildSpatialGrid(Vector3[] vertices, float cellSize)
        {
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var cell = GetCell(vertices[i], cellSize);
                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    grid[cell] = list;
                }
                list.Add(i);
            }
            return grid;
        }

        private static (int, int, int) GetCell(Vector3 pos, float cellSize)
        {
            return (
                Mathf.FloorToInt(pos.x / cellSize),
                Mathf.FloorToInt(pos.y / cellSize),
                Mathf.FloorToInt(pos.z / cellSize));
        }

        private static int FindNearestInGrid(
            Dictionary<(int, int, int), List<int>> grid,
            Vector3[] vertices,
            Vector3 target,
            float cellSize,
            float maxDist)
        {
            var centerCell = GetCell(target, cellSize);
            int nearest = -1;
            float nearestDist = maxDist;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var cell = (centerCell.Item1 + dx, centerCell.Item2 + dy, centerCell.Item3 + dz);
                if (!grid.TryGetValue(cell, out var list)) continue;

                foreach (int idx in list)
                {
                    float dist = Vector3.Distance(vertices[idx], target);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = idx;
                    }
                }
            }

            return nearest;
        }
    }
}
