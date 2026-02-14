using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 色合わせ無視マスクエントリ
    /// サブメッシュごとにマスクを設定し、黒い部分を色合わせの対象から外す
    /// </summary>
    [Serializable]
    public class ColorMaskEntry
    {
        /// <summary>
        /// targetRenderers内のインデックス
        /// </summary>
        public int rendererIndex;

        /// <summary>
        /// サブメッシュインデックス
        /// </summary>
        public int submeshIndex;

        /// <summary>
        /// マスクテクスチャ（黒=元の色を維持、白=色合わせを適用）
        /// </summary>
        public Texture2D mask;

        public ColorMaskEntry() { }

        public ColorMaskEntry(int rendererIndex, int submeshIndex)
        {
            this.rendererIndex = rendererIndex;
            this.submeshIndex = submeshIndex;
        }
    }
}
