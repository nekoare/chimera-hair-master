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
            // 順序: ColorTransform → TextureAtlas → MeshMerge
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(ColorTransformPass.Instance)
                .PreviewingWith(new ChimeraHairMasterPreview())
                .Then.Run(TextureAtlasPass.Instance)
                .Then.Run(MeshMergePass.Instance);

            // Optimizing Phase: コンポーネント削除
            InPhase(BuildPhase.Optimizing)
                .Run(RemoveComponentsPass.Instance);
        }
    }
}
