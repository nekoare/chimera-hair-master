using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 色変換モード
    /// </summary>
    public enum ColorTransformMode
    {
        /// <summary>
        /// グラデーション - 輝度に基づいてグラデーションから色を取得
        /// 完全に色を置き換えたい場合に使用
        /// </summary>
        [InspectorName("グラデーション")]
        Gradient,

        /// <summary>
        /// 色相シフト - HSV または Oklab で色相を target に揃える
        /// 陰影を保ちつつ色を変えたい場合に使用（推奨）
        /// </summary>
        [InspectorName("色指定（デフォルト）")]
        HueShift,

        /// <summary>
        /// RGB差分 - テクスチャ代表色から target への RGB差分を全ピクセルに加算
        /// 色のバリエーション（ハイライト等のニュアンス）を残しつつ色味を寄せたい場合に使用
        /// </summary>
        [InspectorName("RGB差分")]
        RGBDelta,
    }
}
