using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(ChimeraHairMaster.Editor.NDMF.ChimeraHairMasterPlugin))]

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// キメラヘアマスター NDMFプラグイン
    /// </summary>
    public class ChimeraHairMasterPlugin : Plugin<ChimeraHairMasterPlugin>
    {
        public override string DisplayName => "Chimera Hair Master";
        public override string QualifiedName => "com.nekoare.chimera-hair-master";

        protected override void Configure()
        {
            // Transforming Phase - 全パスをチェーン接続で順序を保証
            // 順序: MeshDeform → MeshCut → ColorTransform → TextureAtlas → MeshMerge
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(MeshDeformPass.Instance)
                .Then.Run(MeshCutPass.Instance)
                .Then.Run(ColorTransformPass.Instance)
                .PreviewingWith(new ChimeraHairMasterPreview())
                .Then.Run(TextureAtlasPass.Instance)
                .Then.Run(MeshMergePass.Instance);

            // Optimizing Phase: コンポーネント削除（AAOより前に実行し、未知コンポーネント警告を回避）
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(RemoveComponentsPass.Instance);

            // Optimizing Phase: AAO後に不要なGameObjectをクリーンアップ
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run(RendererCleanupPass.Instance);
        }
    }
}
