using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// テクスチャ解像度プリセット
    /// </summary>
    public enum TextureResolution
    {
        /// <summary>
        /// 512x512
        /// </summary>
        [InspectorName("512x512")]
        _512 = 512,

        /// <summary>
        /// 1024x1024
        /// </summary>
        [InspectorName("1024x1024")]
        _1024 = 1024,

        /// <summary>
        /// 2048x2048
        /// </summary>
        [InspectorName("2048x2048")]
        _2048 = 2048,

        /// <summary>
        /// 4096x4096
        /// </summary>
        [InspectorName("4096x4096")]
        _4096 = 4096
    }
}
