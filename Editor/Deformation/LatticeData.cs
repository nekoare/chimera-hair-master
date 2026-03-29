using UnityEngine;

namespace ChimeraHairMaster.Editor.Deformation
{
    /// <summary>
    /// ラティス変形の一時データ。
    /// シリアライズ不要のエディタ専用データ。
    /// ラティス確定時にデルタに変換して破棄される。
    /// </summary>
    public class LatticeData
    {
        public int DivisionsX;
        public int DivisionsY;
        public int DivisionsZ;

        /// <summary>バウンディングボックス（ローカル空間）</summary>
        public Bounds OriginalBounds;

        /// <summary>制御点の初期位置（ローカル空間）</summary>
        public Vector3[] OriginalControlPoints;

        /// <summary>制御点の現在位置（操作中に変化）</summary>
        public Vector3[] ControlPoints;

        /// <summary>各メッシュ頂点のラティス空間座標 (0-1)^3</summary>
        public Vector3[] LatticeCoords;

        /// <summary>ラティス作成前のデルタスナップショット（キャンセル用）</summary>
        public System.Collections.Generic.Dictionary<int, Vector3> PreLatticeDeltas;

        public int ControlPointCount => (DivisionsX + 1) * (DivisionsY + 1) * (DivisionsZ + 1);

        public int GridToIndex(int x, int y, int z)
        {
            return x + (DivisionsX + 1) * (y + (DivisionsY + 1) * z);
        }

        public (int x, int y, int z) IndexToGrid(int index)
        {
            int nx = DivisionsX + 1;
            int ny = DivisionsY + 1;
            int x = index % nx;
            int y = (index / nx) % ny;
            int z = index / (nx * ny);
            return (x, y, z);
        }

        /// <summary>
        /// ラティス空間座標からトリリニア補間で位置を計算
        /// </summary>
        public Vector3 EvaluateLattice(Vector3 uvw)
        {
            float u = uvw.x * DivisionsX;
            float v = uvw.y * DivisionsY;
            float w = uvw.z * DivisionsZ;

            int i0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, DivisionsX - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, DivisionsY - 1);
            int k0 = Mathf.Clamp(Mathf.FloorToInt(w), 0, DivisionsZ - 1);

            float fu = u - i0;
            float fv = v - j0;
            float fw = w - k0;

            // 8つの隣接制御点
            var c000 = ControlPoints[GridToIndex(i0, j0, k0)];
            var c100 = ControlPoints[GridToIndex(i0 + 1, j0, k0)];
            var c010 = ControlPoints[GridToIndex(i0, j0 + 1, k0)];
            var c110 = ControlPoints[GridToIndex(i0 + 1, j0 + 1, k0)];
            var c001 = ControlPoints[GridToIndex(i0, j0, k0 + 1)];
            var c101 = ControlPoints[GridToIndex(i0 + 1, j0, k0 + 1)];
            var c011 = ControlPoints[GridToIndex(i0, j0 + 1, k0 + 1)];
            var c111 = ControlPoints[GridToIndex(i0 + 1, j0 + 1, k0 + 1)];

            // トリリニア補間
            var c00 = Vector3.Lerp(c000, c100, fu);
            var c10 = Vector3.Lerp(c010, c110, fu);
            var c01 = Vector3.Lerp(c001, c101, fu);
            var c11 = Vector3.Lerp(c011, c111, fu);

            var c0 = Vector3.Lerp(c00, c10, fv);
            var c1 = Vector3.Lerp(c01, c11, fv);

            return Vector3.Lerp(c0, c1, fw);
        }

        /// <summary>
        /// 初期ラティスでの位置（変形なし時の基準位置）
        /// </summary>
        public Vector3 EvaluateOriginalLattice(Vector3 uvw)
        {
            // OriginalControlPointsを使って同じ補間
            var saved = ControlPoints;
            ControlPoints = OriginalControlPoints;
            var result = EvaluateLattice(uvw);
            ControlPoints = saved;
            return result;
        }
    }
}
