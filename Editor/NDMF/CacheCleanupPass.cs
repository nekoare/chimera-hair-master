using nadena.dev.ndmf;
using UnityEditor;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// ビルド終了時に NDMF パス間で共有している static キャッシュを解放するパス。
    /// 辞書参照を static に残すと、生成テクスチャが Resources.UnloadUnusedAssets で
    /// 回収されなくなる（破棄済みコンポーネントへのキー参照も残る）。
    /// テクスチャ実体はビルド成果物のマテリアルが参照しているため破棄しない（参照のみ手放す）。
    /// </summary>
    internal class CacheCleanupPass : Pass<CacheCleanupPass>
    {
        public override string DisplayName => "CHM: Cleanup Caches";

        protected override void Execute(BuildContext context)
        {
            ColorTransformPass.ProcessedTextureCache.Clear();
            TextureAtlasPass.AtlasResultCache.Clear();
        }
    }

    /// <summary>
    /// ビルドが途中で失敗した場合の保険: ドメインリロード前にも解放する
    /// </summary>
    [InitializeOnLoad]
    internal static class BuildCacheReloadGuard
    {
        static BuildCacheReloadGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        private static void Clear()
        {
            ColorTransformPass.ProcessedTextureCache.Clear();
            TextureAtlasPass.AtlasResultCache.Clear();
        }
    }
}
