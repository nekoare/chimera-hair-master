#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// メッシュの UV を指定解像度のピクセル空間にラスタライズして
    /// 「テクスチャの UV 使用領域マスク」を生成するユーティリティ
    /// </summary>
    public static class MeshUVRasterizer
    {
        /// <summary>
        /// 指定 Renderer のサブメッシュ群について、テクスチャ解像度の使用領域マスクを生成する
        /// </summary>
        /// <param name="renderer">対象 SkinnedMeshRenderer</param>
        /// <param name="submeshIndices">対象サブメッシュ index 集合</param>
        /// <param name="width">マスク幅（ピクセル）</param>
        /// <param name="height">マスク高さ（ピクセル）</param>
        /// <returns>長さ width*height の bool 配列。三角形内側のピクセルが true</returns>
        public static bool[] Rasterize(
            SkinnedMeshRenderer renderer,
            IReadOnlyCollection<int> submeshIndices,
            int width,
            int height)
        {
            var mask = new bool[width * height];
            if (renderer == null || renderer.sharedMesh == null) return mask;
            if (submeshIndices == null || submeshIndices.Count == 0) return mask;
            if (width <= 0 || height <= 0) return mask;

            var mesh = renderer.sharedMesh;
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            if (uvs.Count == 0) return mask;

            foreach (int submeshIndex in submeshIndices)
            {
                if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) continue;

                int[] triangles = mesh.GetTriangles(submeshIndex);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int i0 = triangles[t];
                    int i1 = triangles[t + 1];
                    int i2 = triangles[t + 2];
                    if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;

                    RasterizeTriangle(uvs[i0], uvs[i1], uvs[i2], mask, width, height);
                }
            }

            return mask;
        }

        /// <summary>
        /// 1 三角形をピクセル空間にラスタライズしてマスクを塗る
        /// バウンディングボックス + バリセントリック内部判定の素直な実装
        /// </summary>
        private static void RasterizeTriangle(
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            bool[] mask, int width, int height)
        {
            // UV を [0,1) で wrap した上でピクセル座標に変換
            Vector2 p0 = UVToPixel(uv0, width, height);
            Vector2 p1 = UVToPixel(uv1, width, height);
            Vector2 p2 = UVToPixel(uv2, width, height);

            // バウンディングボックス
            float minX = Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x));
            float maxX = Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x));
            float minY = Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y));
            float maxY = Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y));

            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX));
            int x1 = Mathf.Min(width - 1, Mathf.CeilToInt(maxX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY));
            int y1 = Mathf.Min(height - 1, Mathf.CeilToInt(maxY));

            // 縮退三角形はスキップ
            float denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Mathf.Abs(denom) < 1e-7f) return;
            float invDenom = 1f / denom;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    // ピクセル中心でバリセントリック判定
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float a = ((p1.y - p2.y) * (px - p2.x) + (p2.x - p1.x) * (py - p2.y)) * invDenom;
                    float b = ((p2.y - p0.y) * (px - p2.x) + (p0.x - p2.x) * (py - p2.y)) * invDenom;
                    float c = 1f - a - b;
                    if (a >= 0f && b >= 0f && c >= 0f)
                    {
                        mask[y * width + x] = true;
                    }
                }
            }
        }

        private static Vector2 UVToPixel(Vector2 uv, int width, int height)
        {
            // [0,1) で wrap
            float u = uv.x - Mathf.Floor(uv.x);
            float v = uv.y - Mathf.Floor(uv.y);
            return new Vector2(u * width, v * height);
        }
    }
}
