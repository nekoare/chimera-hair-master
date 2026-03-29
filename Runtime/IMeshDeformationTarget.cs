using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ変形機能のデータアクセスインターフェース。
    /// ChimeraHairMaster と MeshDeformationStandalone の両方が実装し、
    /// MeshDeformationSceneEditor が統一的にアクセスできるようにする。
    /// </summary>
    public interface IMeshDeformationTarget
    {
        /// <summary>
        /// 変形対象のRenderer一覧
        /// </summary>
        List<SkinnedMeshRenderer> DeformTargetRenderers { get; }

        /// <summary>
        /// Renderer毎の変形データ（スパースなデルタ）
        /// </summary>
        List<RendererDeformation> RendererDeformations { get; }

        /// <summary>
        /// 編集中のRendererインデックス（-1 = 編集中でない）
        /// NDMFプレビューの二重適用防止に使用
        /// </summary>
        int DeformEditingRendererIndex { get; set; }

        /// <summary>
        /// ドメインリロード復旧用の原本メッシュ参照
        /// </summary>
        Mesh DeformOriginalMesh { get; set; }

        /// <summary>
        /// Undo/SetDirty 用の UnityEngine.Object 参照。
        /// MonoBehaviour 実装では this を返す。
        /// </summary>
        UnityEngine.Object UndoTarget { get; }

        /// <summary>
        /// コンポーネントが付いている GameObject。
        /// MonoBehaviour が暗黙的に満たす。
        /// </summary>
        GameObject gameObject { get; }
    }
}
