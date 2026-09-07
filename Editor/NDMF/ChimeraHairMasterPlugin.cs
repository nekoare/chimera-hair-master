using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

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
            // 順序: MeshDeform → MeshCut → ColorTransform → TextureAtlas → MeshMerge → FakeShadow
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(MeshDeformPass.Instance)
                .PreviewingWith(new MeshDeformationStandalonePreview())
                .Then.Run(MeshCutPass.Instance)
                .Then.Run(ColorTransformPass.Instance)
                .PreviewingWith(new ChimeraHairMasterPreview())
                .Then.Run(TextureAtlasPass.Instance)
                .Then.Run(MeshMergePass.Instance)
                // FakeShadowは統合後の最終状態の髪Rendererを複製するため MeshMerge の後
                .Then.Run(FakeShadowPass.Instance)
                .PreviewingWith(new FakeShadowPreview())
                // パス間共有の static キャッシュを解放（生成テクスチャの回収を妨げないため）
                .Then.Run(CacheCleanupPass.Instance);

            // Transforming Phase (MA後): マテリアル切替アニメーションの顔マテリアル参照を
            // FakeShadowのステンシル書き込み版クローンへ書き換える。
            // - MA後: MAがマージするアニメーターも対象にするため
            // - lilycalInventory/TexTransTool後: 両ツールはMA後にアニメ内マテリアルを
            //   自前クローンへ差し替える/置換するため、その最終状態に対して
            //   ステンシル書き込みの有無を判定・付与するのが最も確実
            //   （AfterPluginは順序制約のみで、未導入環境では単に無視される）
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .AfterPlugin("jp.lilxyzw.lilycalinventory")
                .WithRequiredExtension(typeof(AnimatorServicesContext), sequence =>
                {
                    sequence.Run(FakeShadowAnimationRemapPass.Instance);
                });

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
