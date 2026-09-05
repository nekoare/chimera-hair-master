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
        /// targetRenderers の前回構成（InstanceID 列）のスナップショット。
        /// Renderer の削除・並べ替え時に rendererIndex ベースの各設定
        /// （materialSelections / 明度 / ブラー / islandPlacements / rendererDeformations）を
        /// 追従させるためにエディタが使用する（エディタ専用、ビルドでは無視される）
        /// </summary>
        [SerializeField, HideInInspector]
        public List<int> editorRendererIdsSnapshot = new List<int>();

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
        /// カラーアトラスのアルファチャンネルを常に保持する（PC ビルドのみ影響）。
        /// false（既定）の場合、アトラスが実質不透明ならメモリ半減のため DXT1 で圧縮する。
        /// アルファを透過以外の用途に使う特殊なシェーダ向けのエスケープハッチ
        /// </summary>
        [SerializeField]
        public bool preserveAtlasAlpha = false;

        /// <summary>
        /// ノーマルマップアトラスの解像度（メインアトラス比）
        /// </summary>
        [SerializeField]
        public AtlasSubResolution normalAtlasResolution = AtlasSubResolution.Full;

        /// <summary>
        /// AOマップアトラスの解像度（メインアトラス比）
        /// </summary>
        [SerializeField]
        public AtlasSubResolution aoAtlasResolution = AtlasSubResolution.Full;

        /// <summary>
        /// 「マスクテクスチャを生成」で書き出した再配置済みマスク（プロパティ名 → PNGアセット）。
        /// ビルドでは自動適用しない——ユーザーがマテリアル設定（previewMaterial）へ
        /// 割り当てたものだけが出力に乗る
        /// </summary>
        [SerializeField]
        public List<GeneratedMaskEntry> generatedMasks = new List<GeneratedMaskEntry>();

        /// <summary>
        /// 生成時点の入力ハッシュ（UV配置・素材のマスク/トグル）。
        /// 現在値と食い違う場合、インスペクタで「再生成が必要」の警告に使う
        /// </summary>
        [SerializeField]
        public int generatedMasksInputHash;

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

        // CHM 本体の targetRenderers はアトラス/メッシュ統合がボーン前提のため
        // SkinnedMeshRenderer のまま。変形機能のインターフェースには
        // Renderer 基底型のビューとして渡す（毎回再構築するが要素数は小さい）
        [System.NonSerialized]
        private List<Renderer> _deformTargetRenderersView;

        public List<Renderer> DeformTargetRenderers
        {
            get
            {
                _deformTargetRenderersView ??= new List<Renderer>();
                _deformTargetRenderersView.Clear();
                if (targetRenderers != null)
                {
                    foreach (var r in targetRenderers)
                        _deformTargetRenderersView.Add(r);
                }
                return _deformTargetRenderersView;
            }
        }

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

        /// <summary>
        /// 色合わせ無視マスクの内容ハッシュ（変更検知用）
        /// 同じPNGアセットへの上書き保存（塗り直し）は InstanceID が変わらず
        /// コンポーネントの変更イベントも発火しないため、Inspector がポーリングで
        /// この値を更新することでNDMFプレビューの再評価を起こす
        /// </summary>
        [SerializeField]
        [HideInInspector]
        public int colorMaskContentsHash = 0;

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

            // Oklab 暗部の明るさもターゲット色から初期化
            UpdateOklabLDarkEndRatioFromTargetColor();
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
        /// ターゲット色から Oklab algorithm の暗部の明るさ（oklabLDarkEndRatio）を自動計算する。
        ///
        /// 従来の V 三角波式（0.3 + 0.6*(1-|2V-1|)）は白も純赤も「V=1 の極端色」として 0.3 を
        /// 返し、白指定で暗部が中間グレーまで落ちて銀髪化していた。
        /// 修正後は三角波の暗い側（V≤0.5）と中間ピーク（V=0.5 で 0.9 = 複数テクスチャの
        /// 明度感を揃えるフラット化）は従来どおり維持し、明るい側（V>0.5）の端点だけを
        /// 彩度（Oklab C）に応じて振り分ける:
        ///   - 無彩色（白・明グレー）: 0.7 → ハイキーな白髪
        ///   - ビビッド（純赤・純青）: 0.4 → 深い影のルックをほぼ維持
        ///   - 中間彩度（パステル・金髪）: smoothstep でブレンド
        /// 端点の値は Unity 上での目視チューニングで決定（2026-08-26）。
        ///
        /// 自動計算はセットアップ時とインスペクタの「自動調整」ボタンでのみ実行される。
        /// スライダーで手動設定した値が target 色変更で上書きされることはない
        /// （以前は OnValidate で自動再計算していたが、スライダー露出に伴い廃止）。
        /// </summary>
        public void UpdateOklabLDarkEndRatioFromTargetColor()
        {
            Color.RGBToHSV(targetColor, out _, out _, out float v);

            if (v <= 0.5f)
            {
                // 暗い側は従来式のまま（0.3 + 1.2V と同値）
                oklabLDarkEndRatio = Mathf.Lerp(OklabDarkEndAtDark, OklabDarkEndAtMid, v * 2f);
                return;
            }

            GetOklabLC(targetColor, out _, out float targetC);
            float vividness = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(targetC / OklabChromaBlendRef));
            float brightEnd = Mathf.Lerp(OklabDarkEndAtBrightAchromatic, OklabDarkEndAtBrightVivid, vividness);
            oklabLDarkEndRatio = Mathf.Lerp(OklabDarkEndAtMid, brightEnd, v * 2f - 1f);
        }

        /// <summary>V=0（暗い target）での暗部の明るさ（従来値を維持）</summary>
        private const float OklabDarkEndAtDark = 0.3f;

        /// <summary>V=0.5（中間 target）での暗部の明るさ（従来値を維持。フラット化で明度感を揃える）</summary>
        private const float OklabDarkEndAtMid = 0.9f;

        /// <summary>V=1 かつ無彩色（白など）での暗部の明るさ</summary>
        private const float OklabDarkEndAtBrightAchromatic = 0.7f;

        /// <summary>V=1 かつビビッド（純赤など）での暗部の明るさ</summary>
        private const float OklabDarkEndAtBrightVivid = 0.4f;

        /// <summary>この彩度（Oklab C）以上でビビッド側の端点に完全に切り替わる</summary>
        private const float OklabChromaBlendRef = 0.2f;

        /// <summary>
        /// sRGB 色の Oklab L（明度）と C（彩度）を計算する。
        /// Editor 側 OklabConverter と同じ標準の Oklab 行列だが、
        /// Runtime アセンブリから Editor を参照できないため必要最小限をここに持つ
        /// </summary>
        private static void GetOklabLC(Color srgb, out float lightness, out float chroma)
        {
            float r = SrgbChannelToLinear(srgb.r);
            float g = SrgbChannelToLinear(srgb.g);
            float b = SrgbChannelToLinear(srgb.b);

            float l = Mathf.Pow(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b, 1f / 3f);
            float m = Mathf.Pow(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b, 1f / 3f);
            float s = Mathf.Pow(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b, 1f / 3f);

            lightness = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
            float a = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
            float bb = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
            chroma = Mathf.Sqrt(a * a + bb * bb);
        }

        private static float SrgbChannelToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
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
        /// 指定プロパティが colorChangeTargets に登録され、かつ applyColorChange が明示的に false かを返す。
        /// NDMFビルドはこの OFF を尊重してスキップするため、プレビュー/Apply/Prefab のフォールバック処理も
        /// これを見て一致させる（設定画面の結果とビルド結果を揃える目的）。
        /// </summary>
        public bool IsColorChangeExplicitlyDisabled(string propertyName)
        {
            if (colorChangeTargets == null) return false;
            foreach (var slot in colorChangeTargets)
            {
                if (slot != null && slot.propertyName == propertyName)
                    return !slot.applyColorChange;
            }
            return false;
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

    /// <summary>
    /// 「マスクテクスチャを生成」で書き出した再配置済みマスクの1エントリ
    /// </summary>
    [Serializable]
    public class GeneratedMaskEntry
    {
        /// <summary>割り当て先のテクスチャプロパティ名（例 "_MatCapBlendMask"）</summary>
        public string propertyName;

        /// <summary>生成されたPNGアセット</summary>
        public Texture2D texture;
    }
}
