using UnityEngine;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ変形機能で SkinnedMeshRenderer / MeshRenderer(+MeshFilter) を
    /// 統一的に扱うためのメッシュアクセスヘルパー。
    /// sharedMesh の取得・設定はこのクラス経由で行うこと。
    /// </summary>
    public static class RendererMeshAccess
    {
        /// <summary>
        /// 変形対象としてサポートされる Renderer か
        /// （SkinnedMeshRenderer または MeshFilter を持つ MeshRenderer）
        /// </summary>
        public static bool IsSupported(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer) return true;
            return renderer is MeshRenderer mr && mr.TryGetComponent<MeshFilter>(out _);
        }

        /// <summary>
        /// Renderer が参照しているメッシュを取得する
        /// （SMR は sharedMesh、MeshRenderer は MeshFilter.sharedMesh）
        /// </summary>
        public static Mesh GetSharedMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr:
                    return smr.sharedMesh;
                case MeshRenderer mr:
                    return mr.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Renderer が参照するメッシュを差し替える
        /// </summary>
        public static void SetSharedMesh(Renderer renderer, Mesh mesh)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr:
                    smr.sharedMesh = mesh;
                    break;
                case MeshRenderer mr:
                    if (mr.TryGetComponent<MeshFilter>(out var mf)) mf.sharedMesh = mesh;
                    break;
            }
        }

        /// <summary>
        /// sharedMesh を保持しているオブジェクト。
        /// Undo.RecordObject / EditorUtility.SetDirty の対象に使う
        /// （SMR は自身、MeshRenderer は MeshFilter）
        /// </summary>
        public static Object GetMeshHolder(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr:
                    return smr;
                case MeshRenderer mr:
                    return mr.TryGetComponent<MeshFilter>(out var mf) ? (Object)mf : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Blendshape を評価できる Renderer か（MeshRenderer は不可）
        /// </summary>
        public static bool SupportsBlendshapes(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer;
        }
    }
}
