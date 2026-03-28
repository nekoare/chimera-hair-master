using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace ChimeraHairMaster
{
    /// <summary>
    /// キメラヘアマスター - 複数の髪テクスチャの色合わせ・UV統合・マテリアル統合を行うコンポーネント
    /// IEditorOnlyを実装することでNDMFによる処理後にSDKが自動削除する
    /// </summary>
    [AddComponentMenu("Chimera Hair Master/Chimera Hair Master")]
    [DisallowMultipleComponent]
    [HelpURL("")]
    public class ChimeraHairMaster : MonoBehaviour, IEditorOnly
    {
        #region 基本設定

        /// <summary>
        /// 処理を有効にするかどうか
        /// </summary>
        [SerializeField]
        public bool isEnabled = true;

        /// <summary>
        /// メッシュ統合を有効にするかどうか
        /// falseの場合、色合わせとマテリアル設定の統一のみ行い、
        /// テクスチャアトラス化・UV再マッピング・メッシュ統合はスキップする
        /// </summary>
        [SerializeField]
        public bool enableMeshMerge = false;

        /// <summary>
        /// マットキャップ設定をbaseMaterialで統一するかどうか（enableMeshMerge=false時のみ有効）
        /// trueの場合、baseMaterialのMatCap設定（1st/2nd）で統一する
        /// falseの場合、各Rendererの元マテリアルのMatCap設定をそのまま保持する
        /// </summary>
        [SerializeField]
        public bool unifyMatCap = false;

        /// <summary>
        /// 対象の髪Renderer一覧
        /// </summary>
        [SerializeField]
        public List<SkinnedMeshRenderer> targetRenderers = new List<SkinnedMeshRenderer>();

        /// <summary>
        /// マテリアル選択エントリ一覧
        /// 各Renderer/Submeshを統合対象にするかどうかを記録
        /// </summary>
        [SerializeField]
        public List<MaterialSelectionEntry> materialSelections = new List<MaterialSelectionEntry>();

        #endregion

        #region 色合わせ設定

        /// <summary>
        /// 色合わせを有効にするかどうか
        /// falseの場合、テクスチャの色変換を行わずにアトラス化のみ行う
        /// </summary>
        [SerializeField]
        public bool enableColorTransform = true;

        /// <summary>
        /// ターゲット色
        /// </summary>
        [SerializeField]
        public Color targetColor = Color.white;

        /// <summary>
        /// 色変換モード
        /// </summary>
        [SerializeField]
        public ColorTransformMode colorTransformMode = ColorTransformMode.HueShift;

        /// <summary>
        /// グラデーションカーブ（ColorTransformMode.Gradient時に使用）
        /// </summary>
        [SerializeField]
        public Gradient gradientCurve = new Gradient();

        /// <summary>
        /// 彩度の保持率（ColorTransformMode.HueShift時に使用）
        /// 1.0で元の彩度を完全に保持、0.0でターゲット色の彩度に合わせる
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float saturationPreserve = 0f;

        /// <summary>
        /// 明度の保持率（ColorTransformMode.HueShift時に使用）
        /// 1.0で元の明度を完全に保持、0.0でターゲット色の明度に合わせる
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float valuePreserve = 1.0f;

        /// <summary>
        /// 輝度統一モード（ColorTransformMode.HueShift時に使用）
        /// 複数のテクスチャの明るさを統一する処理（処理コストが高いため、必要な場合のみ有効化）
        /// </summary>
        [SerializeField]
        public BrightnessUnifyMode brightnessUnifyMode = BrightnessUnifyMode.Off;

        /// <summary>
        /// Renderer単位の明度調整リスト
        /// 各Rendererに対して個別に明度を調整可能
        /// </summary>
        [SerializeField]
        public List<RendererBrightnessAdjustment> rendererBrightnessAdjustments = new List<RendererBrightnessAdjustment>();

        /// <summary>
        /// 色合わせ無視マスク一覧
        /// サブメッシュごとにマスクを設定し、黒い部分を色合わせの対象から外す（元の色を維持）
        /// </summary>
        [SerializeField]
        public List<ColorMaskEntry> colorMasks = new List<ColorMaskEntry>();

        #endregion

        #region テクスチャ設定

        /// <summary>
        /// 出力テクスチャ解像度
        /// </summary>
        [SerializeField]
        public TextureResolution textureResolution = TextureResolution._2048;

        /// <summary>
        /// 色変更を適用するテクスチャスロット一覧
        /// </summary>
        [SerializeField]
        public List<TextureSlot> colorChangeTargets = new List<TextureSlot>();

        /// <summary>
        /// UV配置情報一覧（Renderer単位、後方互換性のため残す）
        /// </summary>
        [SerializeField]
        public List<UVIslandPlacement> uvPlacements = new List<UVIslandPlacement>();

        /// <summary>
        /// アイランド単位のUV配置情報
        /// </summary>
        [SerializeField]
        public List<IslandPlacement> islandPlacements = new List<IslandPlacement>();

        #endregion

        #region マテリアル設定

        /// <summary>
        /// 基準マテリアル（このマテリアルの設定を継承）
        /// </summary>
        [SerializeField]
        public Material baseMaterial;

        /// <summary>
        /// プレビュー用マテリアル（編集可能、ビルド時に継承される）
        /// ユーザーはこのマテリアルを編集して影、リムシェード等の質感を調整
        /// </summary>
        [SerializeField]
        public Material previewMaterial;

        /// <summary>
        /// 出力マテリアル（ビルド時に自動生成・内部用）
        /// Inspectorには表示しない
        /// </summary>
        [SerializeField]
        [HideInInspector]
        public Material outputMaterial;

        #endregion

        #region 連携設定

        /// <summary>
        /// メッシュ統合モード
        /// </summary>
        [SerializeField]
        public MeshMergeMode meshMergeMode = MeshMergeMode.Independent;

        #endregion

        #region メッシュ設定

        /// <summary>
        /// Probe Anchor継承モード
        /// </summary>
        [SerializeField]
        public MeshSettingsInheritMode inheritProbeAnchor = MeshSettingsInheritMode.SetOrInherit;

        /// <summary>
        /// Probe Anchor（ライティング基準点）
        /// </summary>
        [SerializeField]
        public Transform probeAnchor;

        /// <summary>
        /// Bounds継承モード
        /// </summary>
        [SerializeField]
        public MeshSettingsInheritMode inheritBounds = MeshSettingsInheritMode.SetOrInherit;

        /// <summary>
        /// Root Bone（Bounds計算の基準ボーン）
        /// </summary>
        [SerializeField]
        public Transform rootBone;

        /// <summary>
        /// Bounds（統合メッシュのバウンディングボックス）
        /// </summary>
        [SerializeField]
        public Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 2);

        #endregion

        #region メッシュ変形設定

        /// <summary>
        /// メッシュ変形を有効にするかどうか
        /// </summary>
        [SerializeField]
        public bool enableMeshDeformation = false;

        /// <summary>
        /// Renderer単位の変形データ一覧
        /// </summary>
        [SerializeField]
        public List<RendererDeformation> rendererDeformations = new List<RendererDeformation>();

        /// <summary>
        /// Scene Editorで変形編集中のRendererインデックス（-1 = 編集中でない）
        /// NDMFプレビューがこの値を監視し、編集中のRendererをプロキシ対象から除外する
        /// ビルド時には無視される（エディタ専用）
        /// </summary>
        [SerializeField]
        public int deformEditingRendererIndex = -1;

        /// <summary>
        /// 変形編集前の元メッシュ参照（ドメインリロード対策）
        /// 編集中にスクリプトコンパイルが走ると EndEdit が呼ばれず
        /// renderer.sharedMesh がデルタ適用済みのまま残るため、
        /// 本当の原本メッシュをここに保持しておく。
        /// ビルド時には無視される（エディタ専用）
        /// </summary>
        [SerializeField]
        public Mesh deformOriginalMesh;

        #endregion

        #region プレビュー設定

        /// <summary>
        /// プレビューを有効にするかどうか
        /// </summary>
        [SerializeField]
        public bool previewEnabled = true;

        /// <summary>
        /// プレビュー用テクスチャ解像度（ビルド解像度とは独立）
        /// </summary>
        [SerializeField]
        public TextureResolution previewResolution = TextureResolution._1024;

        /// <summary>
        /// プレビュー用マテリアルのハッシュ値（変更検知用）
        /// この値が変わるとNDMFプレビューが更新される
        /// </summary>
        [SerializeField]
        [HideInInspector]
        public int previewMaterialHash = 0;

        #endregion

        #region Unity Lifecycle

        private void Reset()
        {
            // デフォルトのグラデーション設定
            gradientCurve = new Gradient();
            gradientCurve.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                }
            );

            // デフォルトの色変更対象（メインテクスチャのみ）
            colorChangeTargets = new List<TextureSlot>
            {
                new TextureSlot("_MainTex", "メインテクスチャ", true)
            };

            // 明度保持をターゲット色のV値に基づいて設定
            UpdateValuePreserveFromTargetColor();
        }

        /// <summary>
        /// ターゲット色のV値に基づいて明度保持の値を更新
        /// V=0の時は0.3、V=1の時は1.0、その間は線形補間
        /// </summary>
        public void UpdateValuePreserveFromTargetColor()
        {
            Color.RGBToHSV(targetColor, out _, out _, out float v);
            valuePreserve = 0.3f + v * 0.7f;
        }

        // ----------------------- Material selection sync -----------------------
        /// <summary>
        /// Rendererとそのサブメッシュに合わせてmaterialSelectionsを同期します。
        /// 既存のエントリのisIncludedは可能な限り保持されます。
        /// </summary>
        public void SyncMaterialSelectionsFromRenderers()
        {
            // Remove invalid entries (renderer index out of range, null renderer, or submesh index out of range)
            materialSelections.RemoveAll(e =>
                e.rendererIndex < 0 || e.rendererIndex >= targetRenderers.Count ||
                targetRenderers[e.rendererIndex] == null ||
                targetRenderers[e.rendererIndex].sharedMesh == null ||
                e.submeshIndex < 0 ||
                e.submeshIndex >= targetRenderers[e.rendererIndex].sharedMesh.subMeshCount
            );

            // Ensure an entry exists for each renderer/submesh; preserve existing isIncluded when possible
            for (int r = 0; r < targetRenderers.Count; r++)
            {
                var renderer = targetRenderers[r];
                if (renderer == null || renderer.sharedMesh == null) continue;

                int submeshCount = renderer.sharedMesh.subMeshCount;
                for (int s = 0; s < submeshCount; s++)
                {
                    var existing = materialSelections.Find(e => e.rendererIndex == r && e.submeshIndex == s);
                    if (existing == null)
                    {
                        materialSelections.Add(new MaterialSelectionEntry(r, s, true));
                    }
                }
            }

            // Keep entries ordered for stable UI
            materialSelections.Sort((a, b) => a.rendererIndex != b.rendererIndex ? a.rendererIndex - b.rendererIndex : a.submeshIndex - b.submeshIndex);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            SyncMaterialSelectionsFromRenderers();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        private void OnEnable()
        {
            SyncMaterialSelectionsFromRenderers();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 対象Rendererを追加
        /// </summary>
        public void AddRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer != null && !targetRenderers.Contains(renderer))
            {
                targetRenderers.Add(renderer);
                uvPlacements.Add(new UVIslandPlacement(renderer));
            }
        }

        /// <summary>
        /// 対象Rendererを削除
        /// </summary>
        public void RemoveRenderer(SkinnedMeshRenderer renderer)
        {
            int index = targetRenderers.IndexOf(renderer);
            if (index >= 0)
            {
                targetRenderers.RemoveAt(index);
                if (index < uvPlacements.Count)
                {
                    uvPlacements.RemoveAt(index);
                }
            }
        }

        /// <summary>
        /// 出力解像度を取得
        /// </summary>
        public int GetResolutionValue()
        {
            return (int)textureResolution;
        }

        /// <summary>
        /// 指定したRenderer/Submeshが統合対象かどうかを判定
        /// </summary>
        /// <param name="rendererIndex">targetRenderers内のインデックス</param>
        /// <param name="submeshIndex">サブメッシュインデックス</param>
        /// <returns>統合対象ならtrue、除外ならfalse</returns>
        public bool IsSubmeshIncluded(int rendererIndex, int submeshIndex)
        {
            // 境界チェック
            if (rendererIndex < 0 || rendererIndex >= targetRenderers.Count)
                return false;

            var renderer = targetRenderers[rendererIndex];
            if (renderer == null || renderer.sharedMesh == null)
                return false;

            if (submeshIndex < 0 || submeshIndex >= renderer.sharedMesh.subMeshCount)
                return false;

            var entry = materialSelections.Find(e =>
                e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);

            // エントリが存在しない場合はデフォルトでtrue(統合対象)
            return entry?.isIncluded ?? true;
        }

        /// <summary>
        /// 指定したRenderer/Submeshの色合わせ無視マスクを取得
        /// </summary>
        /// <param name="rendererIndex">targetRenderers内のインデックス</param>
        /// <param name="submeshIndex">サブメッシュインデックス</param>
        /// <returns>マスクテクスチャ（なければnull）</returns>
        public Texture2D GetColorMask(int rendererIndex, int submeshIndex)
        {
            var entry = colorMasks.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
            return entry?.mask;
        }

        /// <summary>
        /// 統合対象として選択されているすべてのサブメッシュを取得します。
        /// </summary>
        /// <returns>統合対象の(rendererIndex, submeshIndex)ペア一覧</returns>
        public List<(int rendererIndex, int submeshIndex)> GetIncludedSubmeshes()
        {
            var includedSubmeshes = new List<(int rendererIndex, int submeshIndex)>();

            for (int rendererIndex = 0; rendererIndex < targetRenderers.Count; rendererIndex++)
            {
                var renderer = targetRenderers[rendererIndex];
                if (renderer == null || renderer.sharedMesh == null)
                    continue;

                int submeshCount = renderer.sharedMesh.subMeshCount;
                for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
                {
                    if (IsSubmeshIncluded(rendererIndex, submeshIndex))
                    {
                        includedSubmeshes.Add((rendererIndex, submeshIndex));
                    }
                }
            }

            return includedSubmeshes;
        }

        #endregion
    }
}
