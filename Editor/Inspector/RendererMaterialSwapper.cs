#nullable enable
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// 「色が合わないとき？」用: Renderer のマテリアルスロットを別マテリアルへ差し替えるユーティリティ。
    /// SkinnedMeshRenderer のマテリアル欄と同じ挙動（Undo 対応・アセットは変更しない・Prefab インスタンスならオーバーライド）。
    /// 差し替えたマテリアルにも CHM の色合わせはそのまま適用される。
    /// </summary>
    internal static class RendererMaterialSwapper
    {
        /// <summary>
        /// slot のマテリアルを newMaterial に差し替える。
        /// null（空スロットを作らない）・範囲外・同一マテリアルの場合は何もしない。差し替えたら true。
        /// </summary>
        internal static bool TryReplaceSlot(Renderer renderer, int slot, Material? newMaterial)
        {
            if (renderer == null || newMaterial == null) return false;

            var materials = renderer.sharedMaterials;
            if (materials == null || slot < 0 || slot >= materials.Length) return false;
            if (materials[slot] == newMaterial) return false;

            Undo.RecordObject(renderer, "CHM Swap Material");
            materials[slot] = newMaterial;
            renderer.sharedMaterials = materials;

            if (PrefabUtility.IsPartOfPrefabInstance(renderer))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            EditorUtility.SetDirty(renderer);
            return true;
        }

        /// <summary>
        /// 対象 Renderer の全スロットのマテリアル InstanceID からハッシュを計算する（差し替え検知用）
        /// </summary>
        internal static int ComputeSlotMaterialsHash(ChimeraHairMaster component)
        {
            if (component == null || component.targetRenderers == null) return 0;

            unchecked
            {
                int hash = 17;
                foreach (var renderer in component.targetRenderers)
                {
                    if (renderer == null)
                    {
                        hash = hash * 31;
                        continue;
                    }

                    var materials = renderer.sharedMaterials;
                    hash = hash * 31 + materials.Length;
                    foreach (var material in materials)
                    {
                        hash = hash * 31 + (material != null ? material.GetInstanceID() : 0);
                    }
                }
                return hash;
            }
        }
    }
}
