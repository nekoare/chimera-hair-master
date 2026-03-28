using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// メッシュ変形処理パス
    /// MeshCutPassの前に実行し、頂点デルタを適用する
    /// </summary>
    public class MeshDeformPass : Pass<MeshDeformPass>
    {
        public override string DisplayName => "CHM: Mesh Deform";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableMeshDeformation) continue;

                MeshDeformer.ApplyDeformation(component);

                // エディタ専用フィールドをクリーンアップ（ビルド時に残留しないよう）
                component.deformEditingRendererIndex = -1;
                component.deformOriginalMesh = null;
            }
        }
    }
}
