using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ変形時の減衰カーブ種別
    /// ブラシ中心からの距離に応じた影響度の減衰方式を定義する
    /// </summary>
    public enum FalloffType
    {
        /// <summary>
        /// ブラシ範囲内は均一に影響
        /// </summary>
        [InspectorName("均一")]
        Constant,

        /// <summary>
        /// 距離に比例して直線的に減衰: w = 1 - t
        /// </summary>
        [InspectorName("直線")]
        Linear,

        /// <summary>
        /// 滑らかに減衰（SmoothStep）: w = 1 - (3t² - 2t³)
        /// </summary>
        [InspectorName("スムーズ")]
        Smooth,

        /// <summary>
        /// 中心付近に影響が集中: w = √(1 - t²)
        /// </summary>
        [InspectorName("中心優先")]
        Sphere
    }
}
