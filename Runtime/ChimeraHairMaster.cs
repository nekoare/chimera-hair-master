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
    [ExecuteAlways]
    public class ChimeraHairMaster : MonoBehaviour, IEditorOnly, IMeshDeformationTarget
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
        /// Prefab出力時、マテリアル設定（shader 数値設定）をbaseMaterialで統一するかどうか
        /// trueの場合、baseMaterialの shader 設定を全マテリアルにコピーする
        /// falseの場合、各Rendererの元マテリアル設定をそのまま保持する（色変換による _MainTex 差替は別途実施）
        /// </summary>
        [SerializeField]
        public bool unifyMaterialSettings = true;

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
        /// 明度の保持率（ColorTransformMode.HueShift + HSV algorithm時に使用）
        /// 1.0で元の明度を完全に保持、0.0でターゲット色の明度に合わせる
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float valuePreserve = 1.0f;

        /// <summary>
        /// 色相シフトモード時に使用するアルゴリズム
        /// HSV: 従来実装（互換性維持）
        /// Oklab: 新実装（テクスチャ毎の輝度を target に自動補正）
        /// </summary>
        [SerializeField]
        public HueShiftAlgorithm hueShiftAlgorithm = HueShiftAlgorithm.Oklab;

        #region HueShift (Oklab algorithm) パラメータ

        /// <summary>
        /// 色相保持率（Oklab algorithm 時に使用）
        /// 0.0=target.h で色相完全固定、1.0=元の色相を保持
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float oklabHueRetain = 0f;

        /// <summary>
        /// 彩度を target.C に寄せる強さ（Oklab algorithm 時に使用）
        /// 0.0=元の彩度を保持、1.0=target.C に寄せる
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float oklabSaturationToTarget = 1.0f;

        /// <summary>
        /// L（明度）を target.L に寄せる強さ（Oklab algorithm 時に使用）
        /// 0.0=L 補正なし（元のまま）、1.0=完全に線形リマップを適用
        /// 内部では [source.L_p05, source.L_p95] を [target.L * darkEndRatio, target.L] に線形リマップする
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float oklabLToTarget = 1.0f;

        /// <summary>
        /// 線形リマップ時、暗端（source.L_p05）を target.L の何倍にマップするか（Oklab algorithm 時に使用）
        /// 0.0=暗端を黒に / 1.0=暗端も target.L に寄せる（明暗差消失）
        /// target 色変更時に自動で再計算される（UpdateOklabLDarkEndRatioFromTargetColor）
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float oklabLDarkEndRatio = 0.5f;

        /// <summary>
        /// 前回 target 色変更を検出するためのシャドウコピー（UI 非表示）
        /// </summary>
        [SerializeField, HideInInspector]
        private Color _lastTargetColorForOklabAutoAdjust = Color.white;

        #endregion

        #region RGBDelta モードパラメータ

        /// <summary>
        /// RGB差分の加算強度（RGBDelta モード時に使用）
        /// 0.0=変化なし、1.0=完全に target.base 差分を加算
        /// </summary>
        [SerializeField]
        [Range(0f, 1f)]
        public float rgbDeltaIntensity = 1.0f;

        /// <summary>
        /// RGB差分のソフトクリップ領域幅（RGBDelta モード時に使用）
        /// 大きいほど範囲外の漸近領域が広い。0.05 程度が標準
        /// </summary>
        [SerializeField]
        [Range(0.01f, 0.3f)]
        public float rgbDeltaSoftClipZone = 0.05f;

        #endregion

        /// <summary>
        /// Renderer単位の明度調整リスト
        /// 各Rendererに対して個別に明度を調整可能
        /// </summary>
        [SerializeField]
        public List<RendererBrightnessAdjustment> rendererBrightnessAdjustments = new List<RendererBrightnessAdjustment>();

        /// <summary>
        /// Renderer単位のブラー／シャープ調整リスト
        /// 色変換の直前にテクスチャの塗り感を統一するための前処理
        /// </summary>
        [SerializeField]
        public List<RendererBlurSharpAdjustment> rendererBlurSharpAdjustments = new List<RendererBlurSharpAdjustment>();

        /// <summary>
        /// 毛束パターン統一設定
        /// お手本 Renderer の毛束模様を他 Renderer に転送して質感を統一
        /// </summary>
        [SerializeField]
        public StrandPatternSettings strandPatternSettings = new StrandPatternSettings();

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
        /// 通常は常に true（UIからは操作しない、スタンドアロンとの重複時のみ false）
        /// </summary>
        [SerializeField]
        public bool enableMeshDeformation = true;

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

        /// <summary>
        /// メッシュ変形を Blendshape として出力するか（既存頂点位置は不変、Blendshape を末尾追加）
        /// </summary>
        [SerializeField]
        public bool exportDeformAsBlendshape = false;

        /// <summary>
        /// Blendshape として出力する際の名前（既定 "CHMDeform"、同名衝突時は unique 化）
        /// </summary>
        [SerializeField]
        public string deformBlendshapeName = "CHMDeform";

        #endregion

        #region IMeshDeformationTarget 実装

        public List<SkinnedMeshRenderer> DeformTargetRenderers => targetRenderers;
        public List<RendererDeformation> RendererDeformations => rendererDeformations;

        public int DeformEditingRendererIndex
        {
            get => deformEditingRendererIndex;
            set => deformEditingRendererIndex = value;
        }

        public Mesh DeformOriginalMesh
        {
            get => deformOriginalMesh;
            set => deformOriginalMesh = value;
        }

        public UnityEngine.Object UndoTarget => this;

        public bool ExportAsBlendshape
        {
            get => exportDeformAsBlendshape;
            set => exportDeformAsBlendshape = value;
        }

        public string BlendshapeName
        {
            get => deformBlendshapeName;
            set => deformBlendshapeName = value;
        }

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

            // Oklab 暗端比率もターゲット色から初期化
            UpdateOklabLDarkEndRatioFromTargetColor();
            _lastTargetColorForOklabAutoAdjust = targetColor;
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

        /// <summary>
        /// ターゲット色の V 値に基づいて Oklab algorithm の暗端 target 追従率を更新。
        ///
        /// 経験則に基づく式（Oklab 線形リマップと組み合わせたときに破綻しにくい値）:
        ///   - target.V が極端（V≈0 の暗い target / V≈1 の白い target）: dark_ratio を小さく（0.3）
        ///     → テクスチャ元の明暗 range をより活かしたリマップになる。
        ///       target.L が 0 付近 / 1 付近では p95 が target.L に近づくので、
        ///       暗端も target に近い位置で良く、range 圧縮を控える。
        ///   - target.V が中間（V≈0.5）: dark_ratio を大きく（0.9）
        ///     → [target.L * 0.9, target.L] の狭い range に圧縮され、
        ///       複数テクスチャの明度感が揃いやすい。
        ///
        /// 式:  0.3 + 0.6 * (1 - |2V - 1|)
        ///   - |2V - 1| は V=0.5 で 0、V=0 / V=1 で 1 になる三角波
        ///   - 結果として V=0.5 で 0.9、V=0 / V=1 で 0.3 の山形カーブ
        ///
        /// ユーザーが手動微調整したい場合は、このメソッドを呼ばずに oklabLDarkEndRatio を直接設定する
        /// （OnValidate で target 色変更時に再計算されるので注意）。
        /// </summary>
        public void UpdateOklabLDarkEndRatioFromTargetColor()
        {
            Color.RGBToHSV(targetColor, out _, out _, out float v);
            oklabLDarkEndRatio = 0.3f + 0.6f * (1f - Mathf.Abs(2f * v - 1f));
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

            // target 色が変わったら Oklab 暗端比率を自動更新
            if (_lastTargetColorForOklabAutoAdjust != targetColor)
            {
                _lastTargetColorForOklabAutoAdjust = targetColor;
                UpdateOklabLDarkEndRatioFromTargetColor();
            }

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        private void OnEnable()
        {
            SyncMaterialSelectionsFromRenderers();
        }

#if UNITY_EDITOR
        /// <summary>
        /// 編集中にコンポーネント / GameObject が削除された時の保険:
        /// renderer.sharedMesh が編集中の workingMesh のままになっていたら、保持していた originalMesh で復元する
        /// （[ExecuteAlways] によりエディタモードでも OnDestroy が呼ばれる）
        /// </summary>
        private void OnDestroy()
        {
            if (Application.isPlaying) return;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (deformEditingRendererIndex >= 0 && deformOriginalMesh != null
                && deformEditingRendererIndex < targetRenderers.Count)
            {
                var r = targetRenderers[deformEditingRendererIndex];
                if (r != null)
                {
                    r.sharedMesh = deformOriginalMesh;
                    UnityEditor.EditorUtility.SetDirty(r);
                }
            }
        }
#endif

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
