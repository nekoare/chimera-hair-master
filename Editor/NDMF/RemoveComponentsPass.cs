using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// コンポーネント削除パス（ビルド後のクリーンアップ）
    /// </summary>
    public class RemoveComponentsPass : Pass<RemoveComponentsPass>
    {
        public override string DisplayName => "CHM: Remove Components";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                // コンポーネントを削除
                Object.DestroyImmediate(component);
            }

            Debug.Log($"[ChimeraHairMaster] コンポーネント削除完了: {components.Length}個");
        }
    }
}
