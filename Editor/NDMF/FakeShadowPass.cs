using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// FakeShadowセットアップパス。
    /// MeshMergePassの後に実行し、最終状態の髪Renderer（統合ONなら統合Renderer＋残存Renderer、
    /// OFFなら対象Renderer）に対して影レンダラーの生成と顔マテリアルのステンシル設定を行う
    /// </summary>
    public class FakeShadowPass : Pass<FakeShadowPass>
    {
        public override string DisplayName => "CHM: FakeShadow";

        /// <summary>
        /// マテリアル切替アニメーションの参照書き換え要求。
        /// 顔スロットをアニメで別マテリアルに差し替えると（ステンシル書き込みが無く）影が消えるため、
        /// MA処理後の FakeShadowAnimationRemapPass がアニメ内の参照をステンシル書き込み版クローンへ置き換える
        /// </summary>
        internal class AnimationRemapRequest
        {
            public SkinnedMeshRenderer FaceRenderer;
            public List<int> MaterialIndexes = new List<int>();
            public int StencilRef;
            /// <summary>元→クローンの対応表（ベースの顔クローンを種にし、アニメ参照分を追加していく）</summary>
            public Dictionary<Material, Material> CloneCache = new Dictionary<Material, Material>();
        }

        internal static readonly List<AnimationRemapRequest> AnimationRemapRequests = new List<AnimationRemapRequest>();

        protected override void Execute(BuildContext context)
        {
            AnimationRemapRequests.Clear();

            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            // Quest/モバイル向けビルドでは lilToon 系シェーダーが標準シェーダーへ
            // フォールバックされ、影メッシュが「ずれた髪の不透明コピー」として
            // 見えてしまうため、FakeShadow のセットアップ自体をスキップする
            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            bool isMobileTarget = buildTarget == BuildTarget.Android || buildTarget == BuildTarget.iOS;
            bool warnedMobileSkip = false;

            // 同じ顔Rendererへの多重セットアップを防ぐ（ステンシル書き込みが競合するため先勝ち）
            var processedFaceRenderers = new HashSet<SkinnedMeshRenderer>();

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableFakeShadow) continue;

                if (isMobileTarget)
                {
                    if (!warnedMobileSkip)
                    {
                        Debug.LogWarning("[ChimeraHairMaster] FakeShadow: Quest/モバイル向けビルドではlilToon系シェーダーが使用できず影が正しく表示されないため、セットアップをスキップします");
                        warnedMobileSkip = true;
                    }
                    continue;
                }

                var faceRenderer = component.fakeShadowFaceRenderer;
                if (faceRenderer != null && !processedFaceRenderers.Add(faceRenderer))
                {
                    Debug.LogWarning($"[ChimeraHairMaster] FakeShadow: 同じ顔Rendererを対象とする設定が複数あるため '{component.gameObject.name}' をスキップします");
                    continue;
                }

                var hairRenderers = CollectHairRenderers(component);
                if (hairRenderers.Count == 0)
                {
                    Debug.LogWarning($"[ChimeraHairMaster] FakeShadow: 対象の髪Rendererがないためスキップします: {component.gameObject.name}");
                    continue;
                }

                var result = FakeShadowSetup.Apply(component, hairRenderers);
                if (result != null)
                {
                    Debug.Log($"[ChimeraHairMaster] FakeShadowセットアップ完了: {component.gameObject.name}, 影レンダラー {result.ShadowRenderers.Count}個");

                    // マテリアル切替アニメーションが顔スロットを上書きしても影が維持されるよう、
                    // MA処理後にアニメ内のマテリアル参照を書き換える要求を記録する
                    AnimationRemapRequests.Add(new AnimationRemapRequest
                    {
                        FaceRenderer = component.fakeShadowFaceRenderer,
                        MaterialIndexes = new List<int>(component.fakeShadowFaceMaterialIndexes),
                        StencilRef = result.UsedStencilRef,
                        CloneCache = result.FaceCloneBySource,
                    });
                }
            }
        }

        /// <summary>
        /// 影を落とす髪Renderer（このパス実行時点の最終状態）を収集する。
        /// - 統合ON: MeshMergePassが生成した統合Renderer＋除外サブメッシュで残った元Renderer
        /// - 統合OFF: 対象Rendererすべて
        /// 統合で無効化済み（クリーンアップ待ち）のRendererは除外する
        /// </summary>
        private static List<SkinnedMeshRenderer> CollectHairRenderers(ChimeraHairMaster component)
        {
            var result = new List<SkinnedMeshRenderer>();

            if (component.enableMeshMerge &&
                MeshMergePass.MergedRenderers.TryGetValue(component, out var mergedRenderer) &&
                mergedRenderer != null)
            {
                result.Add(mergedRenderer);
            }

            if (component.targetRenderers != null)
            {
                foreach (var renderer in component.targetRenderers)
                {
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    if (!renderer.enabled || !renderer.gameObject.activeSelf) continue;
                    result.Add(renderer);
                }
            }

            return result;
        }
    }
}
