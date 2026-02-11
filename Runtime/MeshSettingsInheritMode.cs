using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ設定の継承モード
    /// </summary>
    public enum MeshSettingsInheritMode
    {
        /// <summary>
        /// 親から継承する
        /// </summary>
        [InspectorName("継承")]
        Inherit,

        /// <summary>
        /// 自分で設定する
        /// </summary>
        [InspectorName("設定")]
        Set,

        /// <summary>
        /// 設定しない
        /// </summary>
        [InspectorName("設定しない")]
        DontSet,

        /// <summary>
        /// 親で指定されている時は継承、それ以外では指定
        /// </summary>
        [InspectorName("親で指定されている時は継承、それ以外では指定")]
        SetOrInherit
    }
}
