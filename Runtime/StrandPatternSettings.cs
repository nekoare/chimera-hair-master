using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 塗り感統一機能の設定（旧名: 毛束パターン統一）
    /// reference Renderer の質感（線の細さ・塗りの濃淡）に target を寄せる。
    /// 内部実装はラプラシアンピラミッド + 2 バンドゲイン転送（approach B）。
    /// </summary>
    [Serializable]
    public class StrandPatternSettings
    {
        /// <summary>
        /// 塗り感統一を有効にするか
        /// </summary>
        public bool enabled = false;

        /// <summary>
        /// お手本 Renderer の targetRenderers 内インデックス
        /// -1 またはリスト範囲外ならリスト先頭を使用
        /// </summary>
        public int referenceRendererIndex = -1;

        /// <summary>
        /// 線の細さの強度（B_high バンドの rescale 量、0 = 影響なし、1 = ratio 通り適用）
        /// 細かいディテール（毛束エッジ・線の細さ・テクスチャの粒度）の reference 寄せ度合い
        /// </summary>
        [Range(0f, 1f)]
        public float strengthFine = 1f;

        /// <summary>
        /// 塗りの濃淡の強度（B_mid バンドの rescale 量、0 = 影響なし、1 = ratio 通り適用）
        /// 中間スケールの変動（シェーディングのメリハリ・大きめの濃淡）の reference 寄せ度合い
        /// </summary>
        [Range(0f, 1f)]
        public float strengthShade = 1f;

        /// <summary>
        /// 毛束パターン抽出の境界スケール（Gaussian sigma）
        /// バンド分解の境界: σ_h = sigma, σ_l = sigma × 3
        /// 値が大きいほど広いスケールの変動を「ディテール」として扱う
        /// 5.0: 一般的な毛束サイズ前後（デフォルト）
        /// </summary>
        [Range(1f, 15f)]
        public float sigma = 5f;
    }
}
