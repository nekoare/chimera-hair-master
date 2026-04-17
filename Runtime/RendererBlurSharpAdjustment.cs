using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// Renderer単位のブラー／シャープ調整設定
    /// 色変換の直前にテクスチャの塗り感を統一するための前処理として使用
    /// </summary>
    [Serializable]
    public class RendererBlurSharpAdjustment
    {
        /// <summary>
        /// 対象のRendererインデックス（targetRenderers内のインデックス）
        /// </summary>
        public int rendererIndex;

        /// <summary>
        /// ブラー／シャープ強度（-1.0 ~ 1.0）
        /// 負の値でガウシアンブラー、正の値でアンシャープマスクでシャープ化
        /// 0 で変換なし（スキップ）
        /// </summary>
        [Range(-1f, 1f)]
        public float blurSharp;

        public RendererBlurSharpAdjustment()
        {
            rendererIndex = 0;
            blurSharp = 0f;
        }

        public RendererBlurSharpAdjustment(int rendererIndex)
        {
            this.rendererIndex = rendererIndex;
            blurSharp = 0f;
        }
    }
}
