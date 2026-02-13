using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// マテリアル選択エントリ
    /// どのRenderer/Submeshを統合対象にするかを記録
    /// </summary>
    [Serializable]
    public class MaterialSelectionEntry
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
        /// 統合対象かどうか(true = 統合する, false = 除外)
        /// </summary>
        public bool isIncluded = true;

        /// <summary>
        /// メッシュカット用マスクテクスチャ（null = マスクなし、白=残す、黒=削除）
        /// </summary>
        public Texture2D meshCutMask;

        // UI表示用キャッシュ(シリアライズ不要)
        [NonSerialized] public string materialName;
        [NonSerialized] public string shaderName;
        [NonSerialized] public Material materialRef;

        public MaterialSelectionEntry()
        {
            // フィールド初期化子が使用される
        }

        public MaterialSelectionEntry(int rendererIndex, int submeshIndex, bool isIncluded = true)
        {
            this.rendererIndex = rendererIndex;
            this.submeshIndex = submeshIndex;
            this.isIncluded = isIncluded;
        }
    }
}
