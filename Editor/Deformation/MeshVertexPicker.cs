using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Deformation
{
    /// <summary>
    /// 遮蔽を考慮した頂点ピックの幾何。
    /// 頂点モードのクリック選択とホバー表示で使う。Scene View 依存（画面変換・レイ生成）は
    /// 呼び出し側が Func で注入するため、この型自体は純粋な幾何計算のみ
    /// </summary>
    public static class MeshVertexPicker
    {
        /// <summary>
        /// 遮蔽判定の許容誤差。頂点までの距離 − (絶対 + 相対 × 距離) より手前のヒットだけを遮蔽とみなす。
        /// UV 継ぎ目の重複頂点や共面の隣接三角形（t == 距離）を遮蔽扱いしないための余裕
        /// </summary>
        private const float OcclusionEpsilonAbs = 1e-5f;
        private const float OcclusionEpsilonRel = 1e-5f;

        /// <summary>
        /// レイと三角形の交差判定（Möller–Trumbore、両面扱い）。
        /// 遮蔽物は表裏を問わない（Cull Off の髪シェーダーでは裏面も描画されるため）
        /// </summary>
        /// <param name="t">ヒット時のレイ上の距離（ray.direction が正規化されていれば実距離）</param>
        /// <returns>レイの前方 (t > 0) でヒットしたら true</returns>
        public static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false; // レイと平行 or 退化三角形

            float inv = 1f / det;
            var s = ray.origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(ray.direction, q) * inv;
            if (v < 0f || u + v > 1f) return false;

            t = Vector3.Dot(e2, q) * inv;
            return t > 0f;
        }

        /// <summary>
        /// 頂点がレイの手前で他の三角形に遮られているかを返す。
        /// 対象頂点を含む三角形は除外する（自分の面で自分を隠さない）
        /// </summary>
        /// <param name="ray">頂点へ向かうレイ（direction は正規化済み）</param>
        /// <param name="targetT">レイ上の頂点までの距離</param>
        /// <param name="vertexIndex">対象頂点のインデックス</param>
        /// <param name="worldVertices">頂点位置（ray と同じ空間）</param>
        /// <param name="triangles">三角形インデックス（全サブメッシュ連結）</param>
        public static bool IsOccluded(Ray ray, float targetT, int vertexIndex, Vector3[] worldVertices, int[] triangles)
        {
            if (worldVertices == null || triangles == null) return false;

            float limit = targetT - (OcclusionEpsilonAbs + OcclusionEpsilonRel * Mathf.Abs(targetT));
            if (limit <= 0f) return false;

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                if (i0 == vertexIndex || i1 == vertexIndex || i2 == vertexIndex) continue;
                if (i0 >= worldVertices.Length || i1 >= worldVertices.Length || i2 >= worldVertices.Length) continue;

                if (RayTriangle(ray, worldVertices[i0], worldVertices[i1], worldVertices[i2], out float t) && t < limit)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 画面座標に最も近い可視頂点を返す（無ければ -1）。
        /// 画面距離が近い順に候補を並べ、遮蔽検査が有効なら他の三角形に隠れていない最初の頂点を返す。
        /// 候補が全部隠れていれば -1（隠れた頂点を掴まない）
        /// </summary>
        /// <param name="worldVertices">頂点位置（表示空間）</param>
        /// <param name="triangles">遮蔽検査に使う三角形（testOcclusion=false なら null 可）</param>
        /// <param name="visibleMask">背面カリング等で不可視の頂点を除外するマスク（null なら全頂点が候補）</param>
        /// <param name="worldToScreen">表示空間 → 画面座標</param>
        /// <param name="screenPoint">クリック/ホバー位置（画面座標）</param>
        /// <param name="maxScreenDistance">候補にする画面距離の上限（ピクセル）</param>
        /// <param name="rayToWorldPoint">表示空間の点へ向かうカメラレイを返す関数</param>
        /// <param name="testOcclusion">遮蔽検査を行うか（Z-test ON 相当）</param>
        /// <param name="maxCandidates">遮蔽検査する候補数の上限（1 候補につき三角形数分の走査）</param>
        public static int PickVisibleVertex(
            Vector3[] worldVertices,
            int[] triangles,
            bool[] visibleMask,
            Func<Vector3, Vector2> worldToScreen,
            Vector2 screenPoint,
            float maxScreenDistance,
            Func<Vector3, Ray> rayToWorldPoint,
            bool testOcclusion,
            int maxCandidates = 16)
        {
            if (worldVertices == null || worldToScreen == null) return -1;

            var candidates = new List<(float dist, int index)>();
            for (int i = 0; i < worldVertices.Length; i++)
            {
                if (visibleMask != null && i < visibleMask.Length && !visibleMask[i]) continue;

                float dist = Vector2.Distance(worldToScreen(worldVertices[i]), screenPoint);
                if (dist < maxScreenDistance)
                    candidates.Add((dist, i));
            }
            if (candidates.Count == 0) return -1;

            candidates.Sort((x, y) => x.dist.CompareTo(y.dist));

            if (!testOcclusion || triangles == null || rayToWorldPoint == null)
                return candidates[0].index;

            int limit = Mathf.Min(maxCandidates, candidates.Count);
            for (int c = 0; c < limit; c++)
            {
                int vi = candidates[c].index;
                var point = worldVertices[vi];
                var ray = rayToWorldPoint(point);
                float targetT = Vector3.Dot(point - ray.origin, ray.direction);
                if (!IsOccluded(ray, targetT, vi, worldVertices, triangles))
                    return vi;
            }
            return -1;
        }
    }
}
