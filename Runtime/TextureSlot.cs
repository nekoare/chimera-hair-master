using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// テクスチャスロット情報（色変更対象の指定用）
    /// </summary>
    [Serializable]
    public class TextureSlot
    {
        /// <summary>
        /// シェーダープロパティ名
        /// </summary>
        public string propertyName;

        /// <summary>
        /// 表示名
        /// </summary>
        public string displayName;

        /// <summary>
        /// 色変更を適用するかどうか
        /// </summary>
        public bool applyColorChange;

        public TextureSlot()
        {
            propertyName = "_MainTex";
            displayName = "Main Texture";
            applyColorChange = true;
        }

        public TextureSlot(string propertyName, string displayName, bool applyColorChange = false)
        {
            this.propertyName = propertyName;
            this.displayName = displayName;
            this.applyColorChange = applyColorChange;
        }
    }
}
