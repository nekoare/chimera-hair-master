using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ変形時の距離計算方式
    /// 減衰の基準となる頂点間の距離をどのように計算するかを定義する
    /// </summary>
    public enum DistanceMetric
    {
        /// <summary>
        /// 直線距離（高速、ほとんどのケースで十分）
        /// </summary>
        [InspectorName("空間上の距離")]
        Euclidean,

        /// <summary>
        /// メッシュ表面に沿った距離（裏側に影響が貫通しない）
        /// </summary>
        [InspectorName("つかんでいる表面の距離")]
        Geodesic
    }
}
