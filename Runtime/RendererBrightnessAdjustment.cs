using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// Renderer単位の明度調整設定
    /// 色合わせ後の微調整に使用
    /// </summary>
    [Serializable]
    public class RendererBrightnessAdjustment
    {
        /// <summary>
        /// 対象のRendererインデックス（targetRenderers内のインデックス）
        /// </summary>
        public int rendererIndex;

        /// <summary>
        /// 明度オフセット（-1.0 ~ 1.0）
        /// 正の値で明るく、負の値で暗くなる
        /// </summary>
        [Range(-1f, 1f)]
        public float brightnessOffset;

        public RendererBrightnessAdjustment()
        {
            rendererIndex = 0;
            brightnessOffset = 0f;
        }

        public RendererBrightnessAdjustment(int rendererIndex)
        {
            this.rendererIndex = rendererIndex;
            brightnessOffset = 0f;
        }
    }
}
