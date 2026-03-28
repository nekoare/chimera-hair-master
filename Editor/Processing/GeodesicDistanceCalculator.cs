using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// メッシュ表面に沿った測地線距離を計算する
    /// ダイクストラ法でメッシュのエッジグラフ上の最短経路を求める
    /// </summary>
    public class GeodesicDistanceCalculator
    {
        private readonly List<List<Edge>> _adjacency;
        private readonly Vector3[] _vertices;

        private struct Edge
        {
            public int to;
            public float weight;
        }

        /// <summary>
        /// メッシュからエッジ隣接リストを構築する
        /// </summary>
        public GeodesicDistanceCalculator(Mesh mesh)
        {
            _vertices = mesh.vertices;
            int vertexCount = _vertices.Length;

            _adjacency = new List<List<Edge>>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
                _adjacency.Add(new List<Edge>());

            // 全サブメッシュの三角形からエッジを収集
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var triangles = mesh.GetTriangles(s);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    AddEdge(triangles[i], triangles[i + 1]);
                    AddEdge(triangles[i + 1], triangles[i + 2]);
                    AddEdge(triangles[i + 2], triangles[i]);
                }
            }

            // 同一位置の頂点を溶接（UV境界で分割された頂点を接続）
            WeldCoincidentVertices(0.0001f);
        }

        /// <summary>
        /// 起点頂点からの測地線距離を計算する
        /// </summary>
        public Dictionary<int, float> ComputeDistances(int sourceVertex, float maxDistance)
        {
            return ComputeDistances(new HashSet<int> { sourceVertex }, maxDistance);
        }

        /// <summary>
        /// 複数の起点頂点からの測地線距離を計算する（マルチソースダイクストラ）
        /// 各頂点について「最も近い起点からの距離」を返す
        /// </summary>
        /// <param name="sourceVertices">起点の頂点インデックス群</param>
        /// <param name="maxDistance">最大距離（これを超える頂点は無視）</param>
        /// <returns>到達可能な頂点のインデックスと距離のペア</returns>
        public Dictionary<int, float> ComputeDistances(HashSet<int> sourceVertices, float maxDistance)
        {
            var dist = new Dictionary<int, float>();
            var pq = new SortedSet<(float dist, int vertex)>(
                Comparer<(float, int)>.Create((a, b) =>
                {
                    int c = a.Item1.CompareTo(b.Item1);
                    return c != 0 ? c : a.Item2.CompareTo(b.Item2);
                }));

            // 全起点を距離0でキューに投入
            foreach (int sv in sourceVertices)
            {
                dist[sv] = 0;
                pq.Add((0, sv));
            }

            while (pq.Count > 0)
            {
                var (d, u) = pq.Min;
                pq.Remove(pq.Min);

                if (d > dist.GetValueOrDefault(u, float.MaxValue))
                    continue;

                if (d > maxDistance)
                    continue;

                if (u < 0 || u >= _adjacency.Count) continue;

                foreach (var edge in _adjacency[u])
                {
                    float newDist = d + edge.weight;
                    if (newDist > maxDistance) continue;

                    if (!dist.TryGetValue(edge.to, out float currentDist) || newDist < currentDist)
                    {
                        dist[edge.to] = newDist;
                        pq.Add((newDist, edge.to));
                    }
                }
            }

            return dist;
        }

        private void AddEdge(int from, int to)
        {
            if (from == to) return;
            float weight = Vector3.Distance(_vertices[from], _vertices[to]);

            // 重複チェック
            foreach (var e in _adjacency[from])
            {
                if (e.to == to) return;
            }

            _adjacency[from].Add(new Edge { to = to, weight = weight });
            _adjacency[to].Add(new Edge { to = from, weight = weight });
        }

        /// <summary>
        /// 同一位置の頂点同士にゼロコストのエッジを追加する（UV境界の溶接）
        /// </summary>
        private void WeldCoincidentVertices(float epsilon)
        {
            // 空間グリッドで高速にペアを検出
            var grid = new Dictionary<(int, int, int), List<int>>();
            float cellSize = epsilon * 2f;

            for (int i = 0; i < _vertices.Length; i++)
            {
                var cell = (
                    Mathf.FloorToInt(_vertices[i].x / cellSize),
                    Mathf.FloorToInt(_vertices[i].y / cellSize),
                    Mathf.FloorToInt(_vertices[i].z / cellSize));

                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    grid[cell] = list;
                }
                list.Add(i);
            }

            // 各セルとその隣接セルの頂点ペアをチェック
            foreach (var kvp in grid)
            {
                var (cx, cy, cz) = kvp.Key;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var neighborCell = (cx + dx, cy + dy, cz + dz);
                    if (!grid.TryGetValue(neighborCell, out var neighbors)) continue;

                    foreach (int i in kvp.Value)
                    {
                        foreach (int j in neighbors)
                        {
                            if (i >= j) continue;
                            if (Vector3.Distance(_vertices[i], _vertices[j]) <= epsilon)
                            {
                                // ゼロコストエッジ（溶接）
                                bool exists = false;
                                foreach (var e in _adjacency[i])
                                {
                                    if (e.to == j) { exists = true; break; }
                                }
                                if (!exists)
                                {
                                    _adjacency[i].Add(new Edge { to = j, weight = 0 });
                                    _adjacency[j].Add(new Edge { to = i, weight = 0 });
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
