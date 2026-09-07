#nullable enable
using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// FakeShadow用アニメーション参照書き換えパス。
    ///
    /// 顔スロットをマテリアル切替アニメーション（メイク差分など）で上書きすると、
    /// 切替先マテリアルにはステンシル書き込みが無いため、その間 FakeShadow が消えてしまう。
    /// このパスは、顔Rendererの選択スロットを対象とするアニメ内のマテリアル参照を
    /// ステンシル書き込み版クローンへ置き換えることで、どのマテリアルに切り替わっても
    /// 影が維持されるようにする。
    ///
    /// Modular Avatar がマージするアニメーターも対象にするため、MA処理後に実行する
    /// （FakeShadowPass が Transforming 前半で記録した要求をここで処理する）
    /// </summary>
    public class FakeShadowAnimationRemapPass : Pass<FakeShadowAnimationRemapPass>
    {
        public override string DisplayName => "CHM: FakeShadow Animation Remap";

        protected override void Execute(BuildContext context)
        {
            var requests = FakeShadowPass.AnimationRemapRequests;
            if (requests.Count == 0) return;

            var animatorServices = context.Extension<AnimatorServicesContext>();
            int rewrittenCurves = 0;

            foreach (var request in requests)
            {
                if (request.FaceRenderer == null) continue;

                string path = animatorServices.ObjectPathRemapper.GetVirtualPathForObject(request.FaceRenderer.gameObject);
                if (string.IsNullOrEmpty(path)) continue;

                foreach (var materialIndex in request.MaterialIndexes)
                {
                    var binding = EditorCurveBinding.PPtrCurve(
                        path, typeof(SkinnedMeshRenderer), $"m_Materials.Array.data[{materialIndex}]");

                    foreach (var clip in animatorServices.AnimationIndex.GetClipsForBinding(binding).ToList())
                    {
                        var curve = clip.GetObjectCurve(binding);
                        if (curve == null) continue;

                        bool changed = false;
                        for (int i = 0; i < curve.Length; i++)
                        {
                            if (curve[i].value is not Material material) continue;
                            // 非lilToon系（ステンシルプロパティなし）は触らない
                            if (!material.HasProperty("_StencilRef")) continue;
                            // 既に書き込み済み（自前クローン含む）はそのまま
                            if (FakeShadowSetup.IsStencilWriter(material)) continue;

                            if (!request.CloneCache.TryGetValue(material, out var clone))
                            {
                                clone = FakeShadowSetup.CreateStencilWriterFaceMaterial(
                                    material, request.StencilRef, warnExistingStencilWrite: false);
                                request.CloneCache[material] = clone;
                            }

                            curve[i].value = clone;
                            changed = true;
                        }

                        if (changed)
                        {
                            clip.SetObjectCurve(binding, curve);
                            rewrittenCurves++;
                        }
                    }
                }
            }

            if (rewrittenCurves > 0)
            {
                Debug.Log($"[ChimeraHairMaster] FakeShadow: マテリアル切替アニメーションの顔マテリアル参照をステンシル書き込み版クローンへ置き換えました（{rewrittenCurves}カーブ）");
            }
        }
    }
}
