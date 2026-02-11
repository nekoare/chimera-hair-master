using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 輝度統一モード
    /// </summary>
    public enum BrightnessUnifyMode
    {
        /// <summary>
        /// オフ - 輝度統一を行わない
        /// </summary>
        [InspectorName("オフ")]
        Off,

        /// <summary>
        /// 明るい方に合わせる
        /// </summary>
        [InspectorName("明るい方に合わせる（高負荷）")]
        ToBrightest,

        /// <summary>
        /// 暗い方に合わせる
        /// </summary>
        [InspectorName("暗い方に合わせる（高負荷）")]
        ToDarkest
    }
}
