using System;
using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Deformation
{
    /// <summary>
    /// 頂点モードの減衰ウェイト計算。
    /// ドラッグ開始時のウェイト確定と、ドラッグ前の影響範囲プレビューの両方から使う。
    /// </summary>
    public static class FalloffWeightCalculator
    {
        /// <summary>
        /// フォールオフカーブを評価する（t: 0=中心, 1=半径の端）
        /// </summary>
        public static float EvaluateFalloff(float t, FalloffType type)
        {
            t = Mathf.Clamp01(t);
            switch (type)
            {
                case FalloffType.Constant:
                    return 1.0f;
                case FalloffType.Linear:
                    return 1.0f - t;
                case FalloffType.Smooth:
                    return 1.0f - (3.0f * t * t - 2.0f * t * t * t);
                case FalloffType.Sphere:
                    return Mathf.Sqrt(1.0f - t * t);
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// 選択頂点の周囲のウェイトを計算する。
        /// </summary>
        /// <param name="vertices">頂点位置（ローカル空間）</param>
        /// <param name="selected">選択頂点インデックス</param>
        /// <param name="radius">ブラシ半径</param>
        /// <param name="falloff">減衰カーブ</param>
        /// <param name="metric">距離方式</param>
        /// <param name="geodesic">測地線計算機（Geodesic 指定時に使用。null なら直線距離にフォールバック）</param>
        /// <param name="weights">出力: 半径内の頂点 → ウェイト</param>
        /// <param name="distances">出力: 距離キャッシュ（直線距離は全頂点、測地線は 2R まで）。不要なら null</param>
        public static void Compute(
            Vector3[] vertices,
            HashSet<int> selected,
            float radius,
            FalloffType falloff,
            DistanceMetric metric,
            GeodesicDistanceCalculator geodesic,
            Dictionary<int, float> weights,
            Dictionary<int, float> distances)
        {
            weights.Clear();
            distances?.Clear();

            if (vertices == null || selected == null || selected.Count == 0) return;
            if (radius <= 0f) return;

            if (metric == DistanceMetric.Geodesic && geodesic != null)
            {
                // マルチソースダイクストラ: 各頂点への「最寄りの選択頂点からの表面距離」。
                // 距離キャッシュは半径拡大時の再計算のため 2R まで残すが、
                // ウェイトを張るのは半径内のみ（ここをゲートしないと Constant で球の外まで変形する）
                var geoDistances = geodesic.ComputeDistances(selected, radius * 2f);
                foreach (var kvp in geoDistances)
                {
                    if (distances != null) distances[kvp.Key] = kvp.Value;
                    if (kvp.Value <= radius)
                        weights[kvp.Key] = EvaluateFalloff(kvp.Value / radius, falloff);
                }
                return;
            }

            // 直線距離: 選択中心からの距離
            var center = Centroid(vertices, selected);
            for (int i = 0; i < vertices.Length; i++)
            {
                float dist = Vector3.Distance(vertices[i], center);
                if (distances != null) distances[i] = dist;
                if (dist <= radius)
                    weights[i] = EvaluateFalloff(dist / radius, falloff);
            }
        }

        /// <summary>
        /// 対称側のウェイトを計算する。選択中心を鏡像化し、その周囲に独立して減衰を張る。
        /// 主側（primaryWeights）に含まれる頂点は二重適用を避けるため除外する。
        /// </summary>
        /// <param name="vertices">頂点位置（ローカル空間）</param>
        /// <param name="selected">選択頂点インデックス</param>
        /// <param name="radius">ブラシ半径</param>
        /// <param name="falloff">減衰カーブ</param>
        /// <param name="mirror">位置を鏡像化する関数</param>
        /// <param name="primaryWeights">主側のウェイト（除外判定に使う）</param>
        /// <param name="output">出力: 対称側の頂点 → ウェイト</param>
        public static void ComputeMirrored(
            Vector3[] vertices,
            HashSet<int> selected,
            float radius,
            FalloffType falloff,
            Func<Vector3, Vector3> mirror,
            Dictionary<int, float> primaryWeights,
            Dictionary<int, float> output)
        {
            output.Clear();

            if (vertices == null || selected == null || selected.Count == 0) return;
            if (radius <= 0f || mirror == null) return;

            var mirrorCenter = mirror(Centroid(vertices, selected));
            for (int i = 0; i < vertices.Length; i++)
            {
                if (primaryWeights != null && primaryWeights.ContainsKey(i)) continue;

                float dist = Vector3.Distance(vertices[i], mirrorCenter);
                if (dist > radius) continue;

                output[i] = EvaluateFalloff(dist / radius, falloff);
            }
        }

        private static Vector3 Centroid(Vector3[] vertices, HashSet<int> selected)
        {
            var center = Vector3.zero;
            int count = 0;
            foreach (int vi in selected)
            {
                if (vi < 0 || vi >= vertices.Length) continue;
                center += vertices[vi];
                count++;
            }
            return count > 0 ? center / count : Vector3.zero;
        }
    }
}
