using System;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// 1頂点分の変形データ（スパース表現）
    /// </summary>
    [Serializable]
    public struct VertexDelta
    {
        /// <summary>
        /// メッシュ内の頂点インデックス
        /// </summary>
        public int vertexIndex;

        /// <summary>
        /// ローカル空間でのオフセット
        /// </summary>
        public Vector3 offset;

        public VertexDelta(int vertexIndex, Vector3 offset)
        {
            this.vertexIndex = vertexIndex;
            this.offset = offset;
        }
    }
}
