using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 色相シフトモードのアルゴリズム
    /// </summary>
    public enum HueShiftAlgorithm
    {
        /// <summary>
        /// HSV - 既存実装。色相を target に置き換え、彩度・明度の保持率は HSV 空間で計算
        /// </summary>
        [InspectorName("HSV（従来）")]
        HSV,

        /// <summary>
        /// Oklab - 新実装。Oklab/OKLCh 空間で処理。輝度と彩度が独立しており、
        /// テクスチャ毎に上位5%輝度を target.L に揃えるため複数テクスチャの色味が揃いやすい
        /// </summary>
        [InspectorName("Oklab")]
        Oklab,
    }
}
