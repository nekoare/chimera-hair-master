using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// マスクテクスチャに基づいてメッシュの三角形を除去する
    /// </summary>
    public static class MeshCutter
    {
        private const float Threshold = 0.5f;

        /// <summary>
        /// マスクに基づいてメッシュの指定サブメッシュから三角形を除去する
        /// </summary>
        /// <param name="mesh">対象メッシュ（複製済みであること）</param>
        /// <param name="submeshIndex">対象サブメッシュのインデックス</param>
        /// <param name="mask">マスクテクスチャ（白=残す、黒=削除）</param>
        /// <returns>除去された三角形の数</returns>
        public static int CutMeshByMask(Mesh mesh, int submeshIndex, Texture2D mask)
        {
            if (mesh == null || mask == null) return 0;
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return 0;

            var readableMask = GetReadableTexture(mask);
            var uv = mesh.uv; // UV0
            var triangles = mesh.GetTriangles(submeshIndex);

            var newTriangles = new List<int>();
            int removedCount = 0;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                // 3頂点のうち1つでも黒なら三角形を削除
                if (IsVertexMasked(uv[v0], readableMask) ||
                    IsVertexMasked(uv[v1], readableMask) ||
                    IsVertexMasked(uv[v2], readableMask))
                {
                    removedCount++;
                    continue;
                }

                newTriangles.Add(v0);
                newTriangles.Add(v1);
                newTriangles.Add(v2);
            }

            mesh.SetTriangles(newTriangles, submeshIndex);

            if (readableMask != mask)
                Object.DestroyImmediate(readableMask);

            return removedCount;
        }

        /// <summary>
        /// 退化三角形方式でマスクカットのプレビューを適用する
        /// 三角形を削除せず、3頂点を同じ頂点に置き換えて非表示にする
        /// </summary>
        public static int ApplyPreviewCut(Mesh mesh, int submeshIndex, Texture2D mask)
        {
            if (mesh == null || mask == null) return 0;
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return 0;

            var readableMask = GetReadableTexture(mask);
            var uv = mesh.uv;
            var triangles = mesh.GetTriangles(submeshIndex);
            int removedCount = 0;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                if (IsVertexMasked(uv[v0], readableMask) ||
                    IsVertexMasked(uv[v1], readableMask) ||
                    IsVertexMasked(uv[v2], readableMask))
                {
                    // 退化三角形: 3頂点を同じ頂点にして面積ゼロにする
                    triangles[i + 1] = v0;
                    triangles[i + 2] = v0;
                    removedCount++;
                }
            }

            mesh.SetTriangles(triangles, submeshIndex);

            if (readableMask != mask)
                Object.DestroyImmediate(readableMask);

            return removedCount;
        }

        /// <summary>
        /// 頂点がマスクにより削除対象かどうかを判定する
        /// </summary>
        private static bool IsVertexMasked(Vector2 uv, Texture2D mask)
        {
            // UV を 0-1 にラップ
            float u = Mathf.Repeat(uv.x, 1f);
            float v = Mathf.Repeat(uv.y, 1f);

            int x = Mathf.Clamp((int)(u * mask.width), 0, mask.width - 1);
            int y = Mathf.Clamp((int)(v * mask.height), 0, mask.height - 1);

            Color pixel = mask.GetPixel(x, y);
            float grayscale = pixel.grayscale;

            // 黒（< 0.5）= 削除対象
            return grayscale < Threshold;
        }

        /// <summary>
        /// テクスチャが読み取り不可の場合、RenderTexture 経由で読み取り可能なコピーを作成する
        /// </summary>
        private static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable) return source;

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);

            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            return readable;
        }
    }
}
