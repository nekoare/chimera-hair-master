using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// UVアイランドの配置情報（Renderer単位、後方互換性のため残す）
    /// </summary>
    [Serializable]
    public class UVIslandPlacement
    {
        /// <summary>
        /// 対象のRenderer
        /// </summary>
        public SkinnedMeshRenderer renderer;

        /// <summary>
        /// UV空間での位置（0-1）
        /// </summary>
        public Vector2 position;

        /// <summary>
        /// UV空間でのスケール（0-1）
        /// </summary>
        public Vector2 scale = Vector2.one;

        /// <summary>
        /// 回転角度（度）
        /// </summary>
        public float rotation;

        public UVIslandPlacement()
        {
            position = Vector2.zero;
            scale = Vector2.one;
            rotation = 0f;
        }

        public UVIslandPlacement(SkinnedMeshRenderer renderer)
        {
            this.renderer = renderer;
            position = Vector2.zero;
            scale = Vector2.one;
            rotation = 0f;
        }
    }

    /// <summary>
    /// 個別UVアイランドの配置情報（アイランド単位）
    /// </summary>
    [Serializable]
    public class IslandPlacement
    {
        /// <summary>
        /// 対象のRendererインデックス（targetRenderers内のインデックス）
        /// </summary>
        public int rendererIndex;

        /// <summary>
        /// サブメッシュインデックス（後方互換性のためデフォルト0）
        /// </summary>
        public int submeshIndex;

        /// <summary>
        /// このサブメッシュ内でのアイランドインデックス
        /// </summary>
        public int localIslandIndex;

        /// <summary>
        /// 元のUV境界（メッシュ内でのアイランドの位置・サイズ）
        /// </summary>
        public Rect originalBounds;

        /// <summary>
        /// アトラス上での配置位置（左下隅、0-1）
        /// </summary>
        public Vector2 atlasPosition;

        /// <summary>
        /// アトラス上でのサイズ（0-1）
        /// </summary>
        public Vector2 atlasScale = Vector2.one;

        public IslandPlacement()
        {
            rendererIndex = 0;
            submeshIndex = 0;
            localIslandIndex = 0;
            originalBounds = new Rect(0, 0, 1, 1);
            atlasPosition = Vector2.zero;
            atlasScale = Vector2.one;
        }
    }
}
