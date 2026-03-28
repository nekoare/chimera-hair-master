using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// UVアイランドの情報（頂点インデックス付き）
    /// </summary>
    public struct IslandInfo
    {
        /// <summary>
        /// アイランドのUVバウンディングボックス
        /// </summary>
        public Rect bounds;

        /// <summary>
        /// このアイランドに属する頂点インデックスの集合
        /// </summary>
        public HashSet<int> vertexIndices;
    }

    /// <summary>
    /// メッシュのUVアイランド（連結したUV領域）を検出するユーティリティ
    /// </summary>
    public static class UVIslandDetector
    {
        /// <summary>
        /// メッシュからUVアイランドのバウンディングボックスを検出（全サブメッシュを統合）
        /// </summary>
        /// <param name="mesh">対象のメッシュ</param>
        /// <param name="padding">パディング率（0-1）</param>
        /// <param name="textureResolution">テクスチャ解像度（全サブメッシュ共通、0の場合は最小値制約なし）</param>
        /// <returns>各アイランドのバウンディングボックスのリスト</returns>
        public static List<Rect> DetectIslandBounds(Mesh mesh, float padding = 0.02f, int textureResolution = 0)
        {
            // サブメッシュごとの結果をフラット化
            var perSubmesh = DetectIslandBoundsPerSubmesh(mesh, padding, textureResolution: textureResolution);
            var result = new List<Rect>();
            foreach (var submeshIslands in perSubmesh)
            {
                result.AddRange(submeshIslands);
            }

            if (result.Count == 0)
            {
                return new List<Rect> { new Rect(0, 0, 1, 1) };
            }

            return result;
        }

        /// <summary>
        /// メッシュからサブメッシュごとにUVアイランドのバウンディングボックスを検出
        /// </summary>
        /// <param name="mesh">対象のメッシュ</param>
        /// <param name="padding">パディング率（0-1）</param>
        /// <param name="submeshTextureResolutions">サブメッシュごとのテクスチャ解像度配列（nullの場合は最小値制約なし）</param>
        /// <param name="textureResolution">全サブメッシュ共通のテクスチャ解像度（submeshTextureResolutionsが優先、0の場合は最小値制約なし）</param>
        /// <returns>サブメッシュごとのアイランドリスト（外側: サブメッシュインデックス、内側: アイランドリスト）</returns>
        public static List<List<Rect>> DetectIslandBoundsPerSubmesh(Mesh mesh, float padding = 0.02f, int[] submeshTextureResolutions = null, int textureResolution = 0)
        {
            var result = new List<List<Rect>>();

            if (mesh == null)
            {
                result.Add(new List<Rect> { new Rect(0, 0, 1, 1) });
                return result;
            }

            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
            {
                result.Add(new List<Rect> { new Rect(0, 0, 1, 1) });
                return result;
            }

            int submeshCount = mesh.subMeshCount;

            for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
            {
                int texRes = (submeshTextureResolutions != null && submeshIndex < submeshTextureResolutions.Length)
                    ? submeshTextureResolutions[submeshIndex]
                    : textureResolution;
                var submeshIslands = DetectIslandBoundsForSubmesh(mesh, uvs, submeshIndex, padding, texRes);
                result.Add(submeshIslands);
            }

            // 結果がなければデフォルトを返す
            if (result.Count == 0)
            {
                result.Add(new List<Rect> { new Rect(0, 0, 1, 1) });
            }

            return result;
        }

        /// <summary>
        /// 特定サブメッシュのUVアイランドを検出
        /// </summary>
        private static List<Rect> DetectIslandBoundsForSubmesh(Mesh mesh, List<Vector2> uvs, int submeshIndex, float padding, int textureResolution)
        {
            var triangles = mesh.GetTriangles(submeshIndex);

            if (triangles.Length == 0)
            {
                return new List<Rect>();
            }

            // このサブメッシュで使用されている頂点インデックスを収集
            var usedVertices = new HashSet<int>();
            foreach (var idx in triangles)
            {
                usedVertices.Add(idx);
            }

            // Union-Find構造でUVの連結成分を検出
            var parent = new int[uvs.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            // 三角形で繋がっている頂点を同じグループにする
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                Union(parent, v0, v1);
                Union(parent, v1, v2);
            }

            // 各グループのUV境界を計算（このサブメッシュで使用されている頂点のみ）
            var groupBounds = new Dictionary<int, Rect>();

            foreach (var vertexIndex in usedVertices)
            {
                int root = Find(parent, vertexIndex);
                var uv = uvs[vertexIndex];

                // UV座標を0-1範囲にラップ
                float u = Mathf.Repeat(uv.x, 1.0f);
                float v = Mathf.Repeat(uv.y, 1.0f);

                if (!groupBounds.ContainsKey(root))
                {
                    groupBounds[root] = new Rect(u, v, 0, 0);
                }
                else
                {
                    var bounds = groupBounds[root];
                    float minX = Mathf.Min(bounds.xMin, u);
                    float minY = Mathf.Min(bounds.yMin, v);
                    float maxX = Mathf.Max(bounds.xMax, u);
                    float maxY = Mathf.Max(bounds.yMax, v);
                    groupBounds[root] = new Rect(minX, minY, maxX - minX, maxY - minY);
                }
            }

            // 小さすぎるグループをフィルタリングし、パディングを追加
            var result = new List<Rect>();
            const float minIslandSize = 0.01f; // 最小サイズ（1%以下は無視）

            foreach (var kvp in groupBounds)
            {
                var bounds = kvp.Value;

                // 最小サイズチェック
                if (bounds.width < minIslandSize && bounds.height < minIslandSize)
                {
                    continue;
                }

                // パディングを追加（比率ベース + ピクセル最小値4px）
                float padX = bounds.width * padding;
                float padY = bounds.height * padding;

                if (textureResolution > 0)
                {
                    float minPadUV = 4.0f / textureResolution;
                    padX = Mathf.Max(padX, minPadUV);
                    padY = Mathf.Max(padY, minPadUV);
                }

                float minX = Mathf.Max(0, bounds.xMin - padX);
                float minY = Mathf.Max(0, bounds.yMin - padY);
                float maxX = Mathf.Min(1, bounds.xMax + padX);
                float maxY = Mathf.Min(1, bounds.yMax + padY);

                result.Add(new Rect(minX, minY, maxX - minX, maxY - minY));
            }

            // 重なるアイランドをマージ
            result = MergeOverlappingBounds(result);

            return result;
        }
        /// <summary>
        /// 特定サブメッシュのUVアイランドを頂点インデックス付きで検出する
        /// メッシュ変形でアイランド単位の操作に使用
        /// </summary>
        /// <param name="mesh">対象のメッシュ</param>
        /// <param name="submeshIndex">対象サブメッシュのインデックス</param>
        /// <returns>IslandInfo（バウンドと頂点インデックス）のリスト</returns>
        public static List<IslandInfo> DetectIslandsWithVertices(Mesh mesh, int submeshIndex)
        {
            var result = new List<IslandInfo>();
            if (mesh == null) return result;

            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            if (uvs.Count == 0) return result;
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return result;

            var triangles = mesh.GetTriangles(submeshIndex);
            if (triangles.Length == 0) return result;

            // このサブメッシュで使用されている頂点インデックスを収集
            var usedVertices = new HashSet<int>();
            foreach (var idx in triangles)
                usedVertices.Add(idx);

            // Union-Find構造でUVの連結成分を検出
            var parent = new int[uvs.Count];
            for (int i = 0; i < parent.Length; i++)
                parent[i] = i;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Union(parent, triangles[i], triangles[i + 1]);
                Union(parent, triangles[i + 1], triangles[i + 2]);
            }

            // 各グループのUV境界と頂点インデックスを収集
            var groupBounds = new Dictionary<int, Rect>();
            var groupVertices = new Dictionary<int, HashSet<int>>();

            foreach (var vertexIndex in usedVertices)
            {
                int root = Find(parent, vertexIndex);
                var uv = uvs[vertexIndex];
                float u = Mathf.Repeat(uv.x, 1.0f);
                float v = Mathf.Repeat(uv.y, 1.0f);

                if (!groupBounds.ContainsKey(root))
                {
                    groupBounds[root] = new Rect(u, v, 0, 0);
                    groupVertices[root] = new HashSet<int> { vertexIndex };
                }
                else
                {
                    var bounds = groupBounds[root];
                    float minX = Mathf.Min(bounds.xMin, u);
                    float minY = Mathf.Min(bounds.yMin, v);
                    float maxX = Mathf.Max(bounds.xMax, u);
                    float maxY = Mathf.Max(bounds.yMax, v);
                    groupBounds[root] = new Rect(minX, minY, maxX - minX, maxY - minY);
                    groupVertices[root].Add(vertexIndex);
                }
            }

            // 小さすぎるグループをフィルタリング
            const float minIslandSize = 0.01f;
            foreach (var kvp in groupBounds)
            {
                var bounds = kvp.Value;
                if (bounds.width < minIslandSize && bounds.height < minIslandSize)
                    continue;

                result.Add(new IslandInfo
                {
                    bounds = bounds,
                    vertexIndices = groupVertices[kvp.Key]
                });
            }

            return result;
        }

        /// <summary>
        /// アイランド面積比を維持しつつ、正方形枠に収まるようリサイズして棚詰め配置
        /// </summary>
        /// <param name="islands">元のアイランドRectリスト</param>
        /// <param name="targetSize">配置枠の一辺長（通常1.0）</param>
        /// <returns>配置後のRectリスト</returns>
        public static List<Rect> PackIslandsWithRatio(List<Rect> islands, float targetSize = 1.0f)
        {
            if (islands == null || islands.Count == 0)
                return new List<Rect> { new Rect(0, 0, targetSize, targetSize) };

            // 1. 各アイランドの面積を計算
            var areas = new List<float>();
            float totalArea = 0f;
            foreach (var rect in islands)
            {
                float area = Mathf.Max(0.0001f, rect.width * rect.height); // 0面積防止
                areas.Add(area);
                totalArea += area;
            }

            // 2. 合計面積が枠面積(targetSize^2)を超えないよう一括スケール
            float scale = Mathf.Sqrt((targetSize * targetSize) / totalArea);

            // 3. スケール後のRectを生成
            var scaledRects = new List<Rect>();
            for (int i = 0; i < islands.Count; i++)
            {
                var r = islands[i];
                float w = r.width * scale;
                float h = r.height * scale;
                scaledRects.Add(new Rect(0, 0, w, h)); // 位置は後で決定
            }

            // 4. 棚詰め配置（Shelf Packing）
            var packedRects = ShelfPackRects(scaledRects, targetSize);
            return packedRects;
        }

        /// <summary>
        /// 棚詰め方式でRectリストを正方形枠内に配置
        /// </summary>
        private static List<Rect> ShelfPackRects(List<Rect> rects, float targetSize)
        {
            var result = new List<Rect>();
            float shelfY = 0f;
            float shelfHeight = 0f;
            float shelfX = 0f;

            foreach (var r in rects)
            {
                // 枠を超える場合は次の棚へ
                if (shelfX + r.width > targetSize)
                {
                    shelfY += shelfHeight;
                    shelfX = 0f;
                    shelfHeight = 0f;
                }
                // 配置
                var placed = new Rect(shelfX, shelfY, r.width, r.height);
                result.Add(placed);
                shelfX += r.width;
                shelfHeight = Mathf.Max(shelfHeight, r.height);
            }
            return result;
        }

        /// <summary>
        /// 重なっている矩形をマージ
        /// </summary>
        private static List<Rect> MergeOverlappingBounds(List<Rect> bounds)
        {
            if (bounds.Count <= 1) return bounds;

            var result = new List<Rect>();
            var merged = new bool[bounds.Count];

            for (int i = 0; i < bounds.Count; i++)
            {
                if (merged[i]) continue;

                var current = bounds[i];
                bool didMerge;

                do
                {
                    didMerge = false;
                    for (int j = i + 1; j < bounds.Count; j++)
                    {
                        if (merged[j]) continue;

                        // 重なりまたは近接チェック（マージン5%）
                        var other = bounds[j];
                        if (BoundsOverlapOrClose(current, other, 0.05f))
                        {
                            // マージ
                            float minX = Mathf.Min(current.xMin, other.xMin);
                            float minY = Mathf.Min(current.yMin, other.yMin);
                            float maxX = Mathf.Max(current.xMax, other.xMax);
                            float maxY = Mathf.Max(current.yMax, other.yMax);
                            current = new Rect(minX, minY, maxX - minX, maxY - minY);
                            merged[j] = true;
                            didMerge = true;
                        }
                    }
                } while (didMerge);

                result.Add(current);
            }

            return result;
        }

        /// <summary>
        /// 2つの矩形が重なっているか、近接しているかチェック
        /// </summary>
        private static bool BoundsOverlapOrClose(Rect a, Rect b, float margin)
        {
            // マージンを考慮して拡張した矩形で判定
            var expandedA = new Rect(
                a.x - margin, 
                a.y - margin, 
                a.width + margin * 2, 
                a.height + margin * 2);

            return expandedA.Overlaps(b);
        }

        /// <summary>
        /// Rendererのサブメッシュごとのテクスチャ解像度を取得
        /// テクスチャがnullの場合はフォールバック解像度（デフォルト256）を返す
        /// </summary>
        /// <param name="renderer">対象のRenderer</param>
        /// <param name="fallbackResolution">テクスチャがnullの場合のフォールバック解像度</param>
        /// <returns>サブメッシュごとの解像度配列（max(width, height)）</returns>
        public static int[] GetSubmeshTextureResolutions(SkinnedMeshRenderer renderer, int fallbackResolution = 256)
        {
            if (renderer == null) return new int[0];

            var materials = renderer.sharedMaterials;
            int count = renderer.sharedMesh != null ? renderer.sharedMesh.subMeshCount : materials.Length;
            var resolutions = new int[count];

            for (int i = 0; i < count; i++)
            {
                if (i < materials.Length && materials[i] != null && materials[i].HasProperty("_MainTex"))
                {
                    var tex = materials[i].GetTexture("_MainTex") as Texture2D;
                    if (tex != null)
                    {
                        resolutions[i] = Mathf.Max(tex.width, tex.height);
                        continue;
                    }
                }
                resolutions[i] = fallbackResolution;
            }

            return resolutions;
        }

        private static int Find(int[] parent, int i)
        {
            if (parent[i] != i)
            {
                parent[i] = Find(parent, parent[i]); // Path compression
            }
            return parent[i];
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[rootA] = rootB;
            }
        }
    }
}
