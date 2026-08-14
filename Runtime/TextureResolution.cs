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

    /// <summary>
    /// AO/ノーマルなどサブアトラスの解像度（メインアトラス解像度に対する比率）。
    /// enum 値は除数として使用する
    /// </summary>
    public enum AtlasSubResolution
    {
        /// <summary>
        /// メインアトラスと同じ解像度
        /// </summary>
        [InspectorName("×1")]
        Full = 1,

        /// <summary>
        /// メインアトラスの1/2解像度
        /// </summary>
        [InspectorName("×1/2")]
        Half = 2,

        /// <summary>
        /// メインアトラスの1/4解像度
        /// </summary>
        [InspectorName("×1/4")]
        Quarter = 4
    }
}
