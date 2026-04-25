using System.Collections.Generic;
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
            // CHMで変形済みのRendererを追跡（二重適用防止）
            var deformedRenderers = new HashSet<int>();

            // CHMコンポーネントのメッシュ変形を処理
            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);
            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableMeshDeformation) continue;

                // 変形対象のRendererを記録（CHM固有フィールドへの直接アクセス: ビルドパスのみ）
                if (component.rendererDeformations != null)
                {
                    foreach (var deformation in component.rendererDeformations)
                    {
                        if (deformation.deltas != null && deformation.deltas.Count > 0
                            && deformation.rendererIndex >= 0
                            && deformation.rendererIndex < component.targetRenderers.Count)
                        {
                            var renderer = component.targetRenderers[deformation.rendererIndex];
                            if (renderer != null)
                                deformedRenderers.Add(renderer.GetInstanceID());
                        }
                    }
                }

                WarnIfBlendshapeModeUnapplied(component);
                MeshDeformer.ApplyDeformation(component);

                component.deformEditingRendererIndex = -1;
                component.deformOriginalMesh = null;
            }

            // スタンドアロンコンポーネントのメッシュ変形を処理
            var standaloneComponents = context.AvatarRootObject.GetComponentsInChildren<MeshDeformationStandalone>(true);
            foreach (var standalone in standaloneComponents)
            {
                // CHMで既に変形済みのRendererがあれば警告
                if (standalone.targetRenderer != null
                    && deformedRenderers.Contains(standalone.targetRenderer.GetInstanceID()))
                {
                    Debug.LogWarning(
                        $"[ChimeraHairMaster] {standalone.targetRenderer.name} はキメラヘアマスターとメッシュ変形(スタンドアロン)の両方で変形されています。" +
                        "二重に変形が適用されます。");
                }

                WarnIfBlendshapeModeUnapplied(standalone);
                MeshDeformer.ApplyDeformation(standalone);

                standalone.deformEditingRendererIndex = -1;
                standalone.deformOriginalMesh = null;
            }
        }

        /// <summary>
        /// 「Blendshape として出力」トグル ON だが手動「保存して入れ替え」されていないケースを検出して警告する。
        /// ビルド時の MeshDeformPass は焼き込み専用のため、Blendshape モードの意図がビルドに反映されない。
        /// 事前に Inspector から「メッシュを保存して入れ替え」を押すよう案内する。
        /// </summary>
        private static void WarnIfBlendshapeModeUnapplied(IMeshDeformationTarget target)
        {
            if (target == null || !target.ExportAsBlendshape) return;
            if (target.RendererDeformations == null) return;

            int unappliedRenderers = 0;
            foreach (var d in target.RendererDeformations)
            {
                if (d?.deltas != null && d.deltas.Count > 0) unappliedRenderers++;
            }
            if (unappliedRenderers == 0) return;

            string componentName = target.UndoTarget != null ? target.UndoTarget.name : "(unknown)";
            Debug.LogWarning(
                $"[ChimeraHairMaster] {componentName}: 「Blendshape として出力」が ON ですが、" +
                $"未入れ替えの変形が {unappliedRenderers} 個あります。" +
                "ビルド時のメッシュ変形パスは焼き込み専用なので、変形は通常通り頂点位置に焼き込まれます。" +
                "Blendshape として残したい場合は事前に Inspector から「メッシュを保存して入れ替え」を押してください。");
        }
    }
}
