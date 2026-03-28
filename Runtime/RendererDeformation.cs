using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// Renderer単位の変形データ
    /// targetRenderers内のRendererに対応し、変形した頂点のみスパースに保存する
    /// </summary>
    [Serializable]
    public class RendererDeformation
    {
        /// <summary>
        /// targetRenderers内のインデックス
        /// </summary>
        public int rendererIndex;

        /// <summary>
        /// メッシュの頂点数（メッシュ変更検知用）
        /// </summary>
        public int expectedVertexCount;

        /// <summary>
        /// 変形した頂点のデルタ一覧（スパース表現）
        /// </summary>
        public List<VertexDelta> deltas = new List<VertexDelta>();

        public RendererDeformation() { }

        public RendererDeformation(int rendererIndex, int expectedVertexCount)
        {
            this.rendererIndex = rendererIndex;
            this.expectedVertexCount = expectedVertexCount;
        }
    }
}
