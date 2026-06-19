using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using ChimeraHairMaster.Editor.Processing;
using ChimeraHairMaster.Editor.Localization;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// キメラヘアマスター EditorWindow
    /// </summary>
    public class ChimeraHairMasterWindow : EditorWindow
    {
        #region Window State

        // ユーザーの作業内容（UV配置・色設定等）はドメインリロード
        // （スクリプトコンパイル / Play遷移）で消えないよう [SerializeField] を付与する。
        // EditorWindow は標準のホットリロード機構でシリアライズ可能フィールドを復元する
        [SerializeField] private GameObject selectedAvatar;
        [SerializeField] private List<SkinnedMeshRenderer> targetRenderers = new List<SkinnedMeshRenderer>();
        [SerializeField] private List<MaterialSelectionEntry> materialSelections = new List<MaterialSelectionEntry>();

        // 色合わせ設定
        [SerializeField] private bool enableColorTransform = true;
        [SerializeField] private Color targetColor = Color.white;
        [SerializeField] private ColorTransformMode colorTransformMode = ColorTransformMode.HueShift;
        [SerializeField] private Gradient gradientCurve = new Gradient();
        [SerializeField] private float saturationPreserve = 0f;
        [SerializeField] private float valuePreserve = 1.0f;
        [SerializeField] private HueShiftAlgorithm hueShiftAlgorithm = HueShiftAlgorithm.Oklab;
        [SerializeField] private float oklabHueRetain = 0f;
        [SerializeField] private float oklabSaturationToTarget = 1.0f;
        [SerializeField] private float oklabLToTarget = 1.0f;
        [SerializeField] private float oklabLDarkEndRatio = 0.5f;
        [SerializeField] private float rgbDeltaIntensity = 1.0f;
        [SerializeField] private float rgbDeltaSoftClipZone = 0.05f;

        // テクスチャ設定
        [SerializeField] private TextureResolution textureResolution = TextureResolution._2048;

        // マテリアル設定
        [SerializeField] private Material baseMaterial;
        [SerializeField] private MeshMergeMode meshMergeMode = MeshMergeMode.Independent;
        [SerializeField] private bool enableMeshMerge = false;
        [SerializeField] private bool unifyMatCap = false;

        // UI状態
        private Vector2 scrollPosition;
        private bool showTargetSelection = true;
        private bool showMaterialSelection = false;
        private bool showColorSettings = true;
        private bool showUVSettings = true;
        private bool showOutputSettings = true;
        private bool hasUVOverlap = false;
        private bool hasUVOutOfBounds = false;
        private string cachedOverlapMessage;

        // テクスチャ色ピッカー用
        private bool showTextureColorPicker = false;
        private int selectedColorPickerRendererIndex = 0;
        private Texture2D cachedTexturePreview;
        private SkinnedMeshRenderer cachedTextureRenderer;
        private Color[] extractedColors;

        // Gradient用テクスチャ色ピッカー
        private bool showGradientTextureColorPicker = false;
        private int selectedGradientColorPickerRendererIndex = 0;
        private Texture2D cachedGradientTexturePreview;
        private SkinnedMeshRenderer cachedGradientTextureRenderer;
        private Color[] extractedGradientColors;
        private float gradientColorKeyPosition = 0.5f; // 追加するキーの位置

        // UV配置（アイランド毎）
        [SerializeField] private List<IslandPlacementData> islandPlacements = new List<IslandPlacementData>();
        private int selectedIslandIndex = -1; // フラットなアイランドインデックス
        private bool isDraggingUV = false;
        private bool isResizingUV = false;
        private int resizeCorner = -1; // 0=左下, 1=右下, 2=右上, 3=左上
        private Vector2 dragStartPos;
        private Vector2 resizeStartScale;
        private Vector2 resizeStartPos;
        private Rect uvPreviewRect;

        // ShowWindowWithComponent で開いた場合の読み込み元（上書き保存用）
        [SerializeField] private ChimeraHairMaster loadedComponent;

        private const float RESIZE_HANDLE_SIZE = 8f;
        private const float RESIZE_HANDLE_HITBOX_SIZE = 16f; // 判定範囲（表示より大きく）

        /// <summary>
        /// 個別アイランドの配置情報
        /// </summary>
        [System.Serializable]
        private class IslandPlacementData
        {
            public int rendererIndex;
            public int submeshIndex;
            public int localIslandIndex;
            public Rect originalBounds; // 元のUV境界
            public Vector2 position;    // オフセット
            public Vector2 scale = Vector2.one;
        }

        // UVプレビュー用キャッシュ
        private Dictionary<SkinnedMeshRenderer, List<Vector2>> uvCache = new Dictionary<SkinnedMeshRenderer, List<Vector2>>();
        private Dictionary<SkinnedMeshRenderer, List<Vector2>> uvFullCache = new Dictionary<SkinnedMeshRenderer, List<Vector2>>();
        private Dictionary<SkinnedMeshRenderer, int[]> triangleCache = new Dictionary<SkinnedMeshRenderer, int[]>();
        private static Material glLineMaterial;
        // サブメッシュごとのアイランド境界キャッシュ（外側: サブメッシュインデックス、内側: アイランドリスト）
        private Dictionary<SkinnedMeshRenderer, List<List<Rect>>> uvIslandBoundsCache = new Dictionary<SkinnedMeshRenderer, List<List<Rect>>>();
        // 背景テクスチャキャッシュ（キー: (submeshIndex, localIslandIndex)のタプル文字列）
        private Dictionary<SkinnedMeshRenderer, Dictionary<(int submesh, int island), Texture2D>> backgroundTextureCache = new Dictionary<SkinnedMeshRenderer, Dictionary<(int, int), Texture2D>>();
        private Dictionary<SkinnedMeshRenderer, Dictionary<(int submesh, int island), Texture2D>> lowResBackgroundTextureCache = new Dictionary<SkinnedMeshRenderer, Dictionary<(int, int), Texture2D>>();

        private const int BACKGROUND_TEXTURE_SIZE = 256;
        private const int BACKGROUND_TEXTURE_SIZE_LOW = 128; // ドラッグ中の低解像度
        private const int MAX_TRIANGLES_TO_DRAW = 5000; // パフォーマンスのため三角形数を制限

        // 色パレット
        private static readonly Color[] uvColors = new Color[]
        {
            new Color(1f, 0.3f, 0.3f, 0.8f),
            new Color(0.3f, 1f, 0.3f, 0.8f),
            new Color(0.3f, 0.3f, 1f, 0.8f),
            new Color(1f, 1f, 0.3f, 0.8f),
            new Color(1f, 0.3f, 1f, 0.8f),
            new Color(0.3f, 1f, 1f, 0.8f),
        };

        #endregion

        #region Menu Item

        [MenuItem("キメラヘアマスター/ウィンドウを開く")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChimeraHairMasterWindow>();
            window.titleContent = new GUIContent(CHMLocales.Tr("Window:Title"));
            window.minSize = new Vector2(400, 600);
            window.Show();
        }

        /// <summary>
        /// 指定したコンポーネントの情報を読み込んでウィンドウを開く
        /// </summary>
        public static void ShowWindowWithComponent(ChimeraHairMaster component)
        {
            var window = GetWindow<ChimeraHairMasterWindow>();
            window.titleContent = new GUIContent(CHMLocales.Tr("Window:Title"));
            window.minSize = new Vector2(400, 600);
            window.LoadFromComponent(component);
            window.Show();
            window.Focus();
        }

        /// <summary>
        /// コンポーネントから設定を読み込む
        /// </summary>
        private void LoadFromComponent(ChimeraHairMaster component)
        {
            if (component == null) return;

            // 読み込み元を記録（セットアップ実行時に新規作成ではなく上書き更新する）
            loadedComponent = component;

            // アバター設定
            var avatarDescriptor = component.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            selectedAvatar = avatarDescriptor != null ? avatarDescriptor.gameObject : null;

            // 対象Renderer
            targetRenderers = new List<SkinnedMeshRenderer>(component.targetRenderers);

            // マテリアル選択（統合対象フラグ・カットマスク）をディープコピーで読み込む。
            // 読み込まないと前回セッションの内容が残留し、別の Renderer 構成に
            // 誤った rendererIndex のまま適用されてしまう
            materialSelections = new List<MaterialSelectionEntry>();
            if (component.materialSelections != null)
            {
                foreach (var entry in component.materialSelections)
                {
                    if (entry == null) continue;
                    materialSelections.Add(new MaterialSelectionEntry(
                        entry.rendererIndex, entry.submeshIndex, entry.isIncluded)
                    {
                        meshCutMask = entry.meshCutMask
                    });
                }
            }

            // 色合わせ設定
            enableColorTransform = component.enableColorTransform;
            targetColor = component.targetColor;
            colorTransformMode = component.colorTransformMode;
            if (component.gradientCurve != null)
            {
                gradientCurve = new Gradient();
                gradientCurve.SetKeys(component.gradientCurve.colorKeys, component.gradientCurve.alphaKeys);
            }
            saturationPreserve = component.saturationPreserve;
            valuePreserve = component.valuePreserve;
            hueShiftAlgorithm = component.hueShiftAlgorithm;
            oklabHueRetain = component.oklabHueRetain;
            oklabSaturationToTarget = component.oklabSaturationToTarget;
            oklabLToTarget = component.oklabLToTarget;
            oklabLDarkEndRatio = component.oklabLDarkEndRatio;
            rgbDeltaIntensity = component.rgbDeltaIntensity;
            rgbDeltaSoftClipZone = component.rgbDeltaSoftClipZone;

            // テクスチャ・マテリアル設定
            textureResolution = component.textureResolution;
            baseMaterial = component.baseMaterial;
            meshMergeMode = component.meshMergeMode;
            enableMeshMerge = component.enableMeshMerge;
            unifyMatCap = component.unifyMatCap;

            // UV配置を読み込み
            ClearUVCache();
            UpdateIslandPlacements();

            // コンポーネントのUV配置情報を反映
            // アイランド単位の配置（islandPlacements）があればそちらを優先して復元する。
            // 保存時の変換: atlasPosition = bounds.xy * scale + position,
            //               atlasScale   = bounds.size * scale
            // の逆変換で position/scale を求める
            bool restoredIslandPlacements = false;
            if (component.islandPlacements != null && component.islandPlacements.Count > 0)
            {
                foreach (var saved in component.islandPlacements)
                {
                    if (saved == null) continue;

                    foreach (var island in islandPlacements)
                    {
                        if (island.rendererIndex != saved.rendererIndex) continue;
                        if (island.submeshIndex != saved.submeshIndex) continue;
                        if (island.localIslandIndex != saved.localIslandIndex) continue;

                        var bounds = island.originalBounds;
                        float scaleX = bounds.width > 1e-6f ? saved.atlasScale.x / bounds.width : 1f;
                        float scaleY = bounds.height > 1e-6f ? saved.atlasScale.y / bounds.height : 1f;
                        island.scale = new Vector2(scaleX, scaleY);
                        island.position = new Vector2(
                            saved.atlasPosition.x - bounds.x * scaleX,
                            saved.atlasPosition.y - bounds.y * scaleY);
                        restoredIslandPlacements = true;
                        break;
                    }
                }
            }

            // 旧形式（Renderer単位の uvPlacements）からのフォールバック復元
            if (!restoredIslandPlacements
                && component.uvPlacements != null && component.uvPlacements.Count > 0)
            {
                foreach (var compPlacement in component.uvPlacements)
                {
                    if (compPlacement.renderer == null) continue;

                    // このRendererに対応するアイランドを探す
                    int rendererIdx = targetRenderers.IndexOf(compPlacement.renderer);
                    if (rendererIdx < 0) continue;

                    // このRendererの全アイランドに同じ変換を適用
                    foreach (var island in islandPlacements)
                    {
                        if (island.rendererIndex == rendererIdx)
                        {
                            island.position = compPlacement.position;
                            island.scale = compPlacement.scale;
                        }
                    }
                }
            }

            UpdateUVOverlapCache();
            Repaint();
        }

        #endregion

        #region Unity Lifecycle

        private void OnEnable()
        {
            // ドメインリロードで復元されたグラデーションを初期値で上書きしない
            // （colorKeys が空 = 未初期化の場合のみデフォルトを設定する）
            if (gradientCurve == null || gradientCurve.colorKeys == null || gradientCurve.colorKeys.Length == 0)
            {
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
            }
        }

        private void OnDisable()
        {
            // キャッシュをクリーンアップ
            ClearUVCache();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(10);

            DrawTargetSelection();
            EditorGUILayout.Space(5);

            // 色合わせ・メッシュ統合の有効化ボタン
            EditorGUILayout.BeginHorizontal();
            enableColorTransform = GUILayout.Toggle(
                enableColorTransform,
                enableColorTransform ? CHMLocales.Tr("Window:ColorTransform:Enabled") : CHMLocales.Tr("Window:ColorTransform:Disabled"),
                "Button",
                GUILayout.Height(38)
            );
            enableMeshMerge = GUILayout.Toggle(
                enableMeshMerge,
                enableMeshMerge ? CHMLocales.Tr("Window:MeshMerge:Enabled") : CHMLocales.Tr("Window:MeshMerge:Disabled"),
                "Button",
                GUILayout.Height(38)
            );
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);

            DrawMaterialSelection();
            EditorGUILayout.Space(5);

            DrawColorSettings();
            EditorGUILayout.Space(5);

            if (enableMeshMerge)
            {
                DrawUVSettings();
                EditorGUILayout.Space(5);
            }

            DrawOutputSettings();
            EditorGUILayout.Space(10);

            DrawActionButtons();

            EditorGUILayout.EndScrollView();

            HandleDragAndDrop();
        }

        #endregion

        #region Draw Methods

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:Title"), EditorStyles.boldLabel);
                CHMLocales.DrawLanguagePicker();
            }
            EditorGUILayout.HelpBox(
                CHMLocales.Tr("Window:Header:Description"),
                MessageType.Info
            );
        }

        /// <summary>
        /// targetRenderers（髪パーツ）の親を遡って VRC_AvatarDescriptor を持つ GameObject を探す
        /// 見つからない場合は null を返す
        /// </summary>
        private GameObject DetectAvatarFromHairParts()
        {
            if (targetRenderers == null || targetRenderers.Count == 0) return null;

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null) continue;
                var descriptor = renderer.GetComponentInParent<VRC_AvatarDescriptor>();
                if (descriptor != null) return descriptor.gameObject;
            }
            return null;
        }

        private void DrawTargetSelection()
        {
            showTargetSelection = EditorGUILayout.BeginFoldoutHeaderGroup(showTargetSelection, CHMLocales.Tr("Window:TargetSelection:Header"));
            if (showTargetSelection)
            {
                EditorGUI.indentLevel++;

                // 髪パーツからアバターを自動検出
                var autoAvatar = DetectAvatarFromHairParts();
                if (autoAvatar != selectedAvatar)
                {
                    selectedAvatar = autoAvatar;
                    ClearUVCache();
                }

                if (targetRenderers.Count > 0 && selectedAvatar == null)
                {
                    EditorGUILayout.HelpBox(
                        CHMLocales.Tr("Window:TargetSelection:NoAvatarDetected"),
                        MessageType.Warning
                    );
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:TargetSelection:HairPartsList"));

                // 要素数+1の高さを計算（空の場合は最低1行分のスペース）
                float rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                float minHeight = Mathf.Max((targetRenderers.Count + 1) * rowHeight + 8, rowHeight * 2);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinHeight(minHeight));

                if (targetRenderers.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        CHMLocales.Tr("Window:TargetSelection:DragHint"),
                        EditorStyles.centeredGreyMiniLabel,
                        GUILayout.Height(rowHeight * 2)
                    );
                }
                else
                {
                    for (int i = 0; i < targetRenderers.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();

                        // 色インジケータ
                        var colorRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16));
                        EditorGUI.DrawRect(colorRect, uvColors[i % uvColors.Length]);

                        var newRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                            targetRenderers[i],
                            typeof(SkinnedMeshRenderer),
                            true
                        );

                        if (newRenderer != targetRenderers[i])
                        {
                            targetRenderers[i] = newRenderer;
                            UpdateUVPlacements();
                            SyncMaterialSelections();
                            ClearUVCache();
                        }

                        if (GUILayout.Button("×", GUILayout.Width(25)))
                        {
                            targetRenderers.RemoveAt(i);
                            // 削除した index に紐づく設定を除去し、後続 index を 1 つ詰める
                            // （これをしないと meshCutMask・除外・UV配置が後続 Renderer にズレる）
                            RemapEntriesForRendererRemoval(i);
                            ClearUVCache();
                            SyncMaterialSelections();
                            UpdateUVPlacements();
                            i--;
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.EndVertical();

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+", GUILayout.Width(30)))
                {
                    targetRenderers.Add(null);
                    UpdateUVPlacements();
                    SyncMaterialSelections();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawMaterialSelection()
        {
            showMaterialSelection = EditorGUILayout.BeginFoldoutHeaderGroup(showMaterialSelection, CHMLocales.Tr("Window:MaterialSelection:Header"));
            if (showMaterialSelection)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.HelpBox(
                    enableMeshMerge
                        ? CHMLocales.Tr("Window:MaterialSelection:HintMerge")
                        : CHMLocales.Tr("Window:MaterialSelection:HintNoMerge"),
                    MessageType.Info
                );

                EditorGUILayout.Space(5);

                if (targetRenderers.Count == 0)
                {
                    EditorGUILayout.LabelField(CHMLocales.Tr("Window:MaterialSelection:NoRenderer"), EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    // 各Rendererごとにマテリアル一覧を表示
                    for (int r = 0; r < targetRenderers.Count; r++)
                    {
                        var renderer = targetRenderers[r];
                        if (renderer == null || renderer.sharedMesh == null) continue;

                        // Renderer名を表示（色インジケータ付き）
                        EditorGUILayout.BeginHorizontal();
                        var colorRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16));
                        EditorGUI.DrawRect(colorRect, uvColors[r % uvColors.Length]);
                        EditorGUILayout.LabelField(renderer.name, EditorStyles.boldLabel);
                        EditorGUILayout.EndHorizontal();

                        EditorGUI.indentLevel++;

                        int submeshCount = renderer.sharedMesh.subMeshCount;
                        var materials = renderer.sharedMaterials;

                        for (int s = 0; s < submeshCount; s++)
                        {
                            EditorGUILayout.BeginHorizontal();

                            // チェックボックス - GUILayout.Toggleを使用
                            bool currentValue = GetMaterialIncluded(r, s);
                            bool newValue = GUILayout.Toggle(currentValue, "", GUILayout.Width(20));

                            if (newValue != currentValue)
                            {
                                SetMaterialIncluded(r, s, newValue);
                                UpdateIslandPlacements();
                                GUI.changed = true;
                            }

                            // マテリアル名表示
                            string matName = s < materials.Length && materials[s] != null
                                ? materials[s].name
                                : $"(Material {s})";
                            EditorGUILayout.LabelField($"SubMesh {s}: {matName}");

                            EditorGUILayout.EndHorizontal();

                            // マスクカット設定（統合対象 かつ メッシュ統合有効時のみ表示）
                            if (currentValue && enableMeshMerge)
                            {
                                EditorGUI.indentLevel++;
                                EditorGUILayout.BeginHorizontal();

                                var currentMask = GetMeshCutMask(r, s);
                                var newMask = (Texture2D)EditorGUILayout.ObjectField(
                                    CHMLocales.Tr("Window:MaterialSelection:CutMask"), currentMask, typeof(Texture2D), false);

                                if (newMask != currentMask)
                                {
                                    SetMeshCutMask(r, s, newMask);
                                    GUI.changed = true;
                                }

                                EditorGUILayout.EndHorizontal();
                                EditorGUI.indentLevel--;
                            }
                        }

                        EditorGUI.indentLevel--;
                        EditorGUILayout.Space(3);
                    }
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawColorSettings()
        {
            if (!enableColorTransform) return;

            showColorSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showColorSettings, CHMLocales.Tr("Window:ColorSettings:Header"));
            if (showColorSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.Space(5);

                // 色変換モード
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorSettings:Mode"), GUILayout.Width(80));
                {
                    // 表示順: Mode1=HueShift, Mode2=Gradient, Mode3=RGBDelta
                    var modeNames = new[]
                    {
                        CHMLocales.Tr("ColorTransformMode:HueShift"),        // display 0 (enum 1)
                        CHMLocales.Tr("ColorTransformMode:Gradient"),        // display 1 (enum 0)
                        CHMLocales.Tr("Inspector:ColorTransformMode:RGBDelta"), // display 2 (enum 2)
                    };
                    int[] displayToEnum = { 1, 0, 2 };
                    int[] enumToDisplay = { 1, 0, 2 };
                    int currentDisplay = enumToDisplay[(int)colorTransformMode];
                    int newDisplay = EditorGUILayout.Popup(currentDisplay, modeNames);
                    colorTransformMode = (ColorTransformMode)displayToEnum[newDisplay];
                }
                EditorGUILayout.EndHorizontal();

                switch (colorTransformMode)
                {
                    case ColorTransformMode.Gradient:
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorSettings:Gradient"), GUILayout.Width(80));
                        gradientCurve = EditorGUILayout.GradientField(gradientCurve);
                        EditorGUILayout.EndHorizontal();

                        // テクスチャ色ピッカー（Gradient用）
                        DrawGradientTextureColorPicker();
                        break;

                    case ColorTransformMode.HueShift:
                        EditorGUILayout.Space(5);

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorSettings:ChangeToThisColor"), GUILayout.Width(80));
                        targetColor = EditorGUILayout.ColorField(targetColor);
                        EditorGUILayout.EndHorizontal();

                        // テクスチャ色ピッカー
                        DrawTextureColorPicker();


                        EditorGUILayout.HelpBox(
                            CHMLocales.Tr("Window:ColorSettings:HueShiftHint"),
                            MessageType.Info
                        );
                        break;

                    case ColorTransformMode.RGBDelta:
                        EditorGUILayout.Space(5);

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorSettings:ChangeToThisColor"), GUILayout.Width(80));
                        targetColor = EditorGUILayout.ColorField(targetColor);
                        EditorGUILayout.EndHorizontal();

                        // テクスチャ色ピッカー（HueShift と共有）
                        DrawTextureColorPicker();

                        EditorGUILayout.HelpBox(
                            CHMLocales.Tr("Inspector:RgbDeltaHelp"),
                            MessageType.Info
                        );
                        break;
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        /// <summary>
        /// テクスチャから色を選択するUI
        /// </summary>
        private void DrawTextureColorPicker()
        {
            if (targetRenderers == null || targetRenderers.Count == 0)
                return;

            EditorGUILayout.Space(5);
            showTextureColorPicker = EditorGUILayout.Foldout(showTextureColorPicker, CHMLocales.Tr("Window:ColorPicker:FoldoutFromTexture"), true);

            if (!showTextureColorPicker) return;

            EditorGUI.indentLevel++;

            // Renderer選択ボタン
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < targetRenderers.Count; i++)
            {
                var renderer = targetRenderers[i];
                if (renderer == null) continue;

                string name = renderer.name;
                if (name.Length > 10) name = name.Substring(0, 10) + "...";

                bool isSelected = (i == selectedColorPickerRendererIndex);
                GUI.backgroundColor = isSelected ? Color.cyan : Color.white;

                if (GUILayout.Button(name, GUILayout.MaxWidth(80)))
                {
                    selectedColorPickerRendererIndex = i;
                    cachedTexturePreview = null;
                    extractedColors = null;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 選択中のRendererからテクスチャを取得
            if (selectedColorPickerRendererIndex >= 0 && selectedColorPickerRendererIndex < targetRenderers.Count)
            {
                var selectedRenderer = targetRenderers[selectedColorPickerRendererIndex];
                if (selectedRenderer != null)
                {
                    // テクスチャプレビューをキャッシュ
                    if (cachedTexturePreview == null || cachedTextureRenderer != selectedRenderer)
                    {
                        cachedTextureRenderer = selectedRenderer;
                        var materials = selectedRenderer.sharedMaterials;
                        foreach (var mat in materials)
                        {
                            if (mat != null && mat.HasProperty("_MainTex"))
                            {
                                cachedTexturePreview = mat.GetTexture("_MainTex") as Texture2D;
                                if (cachedTexturePreview != null)
                                {
                                    extractedColors = Processing.ColorProcessor.ExtractDominantColors(cachedTexturePreview, 5);
                                    break;
                                }
                            }
                        }
                    }

                    // テクスチャプレビュー表示
                    if (cachedTexturePreview != null)
                    {
                        EditorGUILayout.Space(5);

                        // プレビュー領域
                        float previewSize = position.width - 60;
                        previewSize = Mathf.Min(previewSize, 200);

                        Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                        previewRect.x += 15; // インデント調整
                        previewRect.width = previewSize;
                        previewRect.height = previewSize;

                        // テクスチャを描画
                        GUI.DrawTexture(previewRect, cachedTexturePreview, ScaleMode.ScaleToFit);

                        // クリック検出
                        Event e = Event.current;
                        if (e.type == EventType.MouseDown && previewRect.Contains(e.mousePosition))
                        {
                            // クリック位置からテクスチャ座標を計算
                            float aspectRatio = (float)cachedTexturePreview.width / cachedTexturePreview.height;
                            Rect actualRect;

                            if (aspectRatio > 1)
                            {
                                float h = previewRect.width / aspectRatio;
                                actualRect = new Rect(previewRect.x, previewRect.y + (previewRect.height - h) / 2, previewRect.width, h);
                            }
                            else
                            {
                                float w = previewRect.height * aspectRatio;
                                actualRect = new Rect(previewRect.x + (previewRect.width - w) / 2, previewRect.y, w, previewRect.height);
                            }

                            if (actualRect.Contains(e.mousePosition))
                            {
                                float u = (e.mousePosition.x - actualRect.x) / actualRect.width;
                                float v = 1f - (e.mousePosition.y - actualRect.y) / actualRect.height;

                                // 読み取り可能なテクスチャを取得
                                var readableTex = Processing.ColorProcessor.GetReadableTexture(cachedTexturePreview);
                                int px = Mathf.FloorToInt(u * readableTex.width);
                                int py = Mathf.FloorToInt(v * readableTex.height);
                                px = Mathf.Clamp(px, 0, readableTex.width - 1);
                                py = Mathf.Clamp(py, 0, readableTex.height - 1);

                                Color pickedColor = readableTex.GetPixel(px, py);

                                if (readableTex != cachedTexturePreview)
                                {
                                    Object.DestroyImmediate(readableTex);
                                }

                                // ターゲット色に設定
                                targetColor = pickedColor;
                                e.Use();
                                Repaint();
                            }
                        }

                        EditorGUILayout.HelpBox(CHMLocales.Tr("Window:ColorPicker:ClickToPick"), MessageType.None);

                        // 代表色パレット
                        if (extractedColors != null && extractedColors.Length > 0)
                        {
                            EditorGUILayout.Space(5);
                            EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorPicker:CandidateColors"));
                            EditorGUILayout.BeginHorizontal();

                            foreach (var color in extractedColors)
                            {
                                Rect colorRect = GUILayoutUtility.GetRect(30, 30);
                                EditorGUI.DrawRect(colorRect, color);

                                // 枠線
                                Handles.color = Color.black;
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.y), new Vector3(colorRect.xMax, colorRect.y));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.y), new Vector3(colorRect.xMax, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.yMax), new Vector3(colorRect.x, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.yMax), new Vector3(colorRect.x, colorRect.y));

                                // クリック検出
                                if (Event.current.type == EventType.MouseDown && colorRect.Contains(Event.current.mousePosition))
                                {
                                    targetColor = color;
                                    Event.current.Use();
                                    Repaint();
                                }
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(CHMLocales.Tr("Window:ColorPicker:TextureNotFound"), MessageType.Warning);
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Gradient用：テクスチャから色を選択するUI
        /// </summary>
        private void DrawGradientTextureColorPicker()
        {
            if (targetRenderers == null || targetRenderers.Count == 0)
                return;

            EditorGUILayout.Space(5);
            showGradientTextureColorPicker = EditorGUILayout.Foldout(showGradientTextureColorPicker, CHMLocales.Tr("Window:GradientColorPicker:FoldoutAddFromTexture"), true);

            if (!showGradientTextureColorPicker) return;

            EditorGUI.indentLevel++;

            // Renderer選択ボタン
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < targetRenderers.Count; i++)
            {
                var renderer = targetRenderers[i];
                if (renderer == null) continue;

                string name = renderer.name;
                if (name.Length > 10) name = name.Substring(0, 10) + "...";

                bool isSelected = (i == selectedGradientColorPickerRendererIndex);
                GUI.backgroundColor = isSelected ? Color.cyan : Color.white;

                if (GUILayout.Button(name, GUILayout.MaxWidth(80)))
                {
                    selectedGradientColorPickerRendererIndex = i;
                    cachedGradientTexturePreview = null;
                    extractedGradientColors = null;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 選択中のRendererからテクスチャを取得
            if (selectedGradientColorPickerRendererIndex >= 0 && selectedGradientColorPickerRendererIndex < targetRenderers.Count)
            {
                var selectedRenderer = targetRenderers[selectedGradientColorPickerRendererIndex];
                if (selectedRenderer != null)
                {
                    // テクスチャプレビューをキャッシュ
                    if (cachedGradientTexturePreview == null || cachedGradientTextureRenderer != selectedRenderer)
                    {
                        cachedGradientTextureRenderer = selectedRenderer;
                        var materials = selectedRenderer.sharedMaterials;
                        foreach (var mat in materials)
                        {
                            if (mat != null && mat.HasProperty("_MainTex"))
                            {
                                cachedGradientTexturePreview = mat.GetTexture("_MainTex") as Texture2D;
                                if (cachedGradientTexturePreview != null)
                                {
                                    extractedGradientColors = Processing.ColorProcessor.ExtractDominantColors(cachedGradientTexturePreview, 5);
                                    break;
                                }
                            }
                        }
                    }

                    // テクスチャプレビュー表示
                    if (cachedGradientTexturePreview != null)
                    {
                        EditorGUILayout.Space(5);

                        // キー位置スライダー
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CHMLocales.Tr("Window:GradientColorPicker:DarkColor"), GUILayout.Width(80));
                        gradientColorKeyPosition = EditorGUILayout.Slider(gradientColorKeyPosition, 0f, 1f);
                        EditorGUILayout.LabelField(CHMLocales.Tr("Window:GradientColorPicker:LightColor"), GUILayout.Width(80));
                        EditorGUILayout.EndHorizontal();

                        EditorGUILayout.Space(5);

                        // プレビュー領域
                        float previewSize = position.width - 60;
                        previewSize = Mathf.Min(previewSize, 200);

                        Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                        previewRect.x += 15; // インデント調整
                        previewRect.width = previewSize;
                        previewRect.height = previewSize;

                        // テクスチャを描画
                        GUI.DrawTexture(previewRect, cachedGradientTexturePreview, ScaleMode.ScaleToFit);

                        // クリック検出
                        Event e = Event.current;
                        if (e.type == EventType.MouseDown && previewRect.Contains(e.mousePosition))
                        {
                            // クリック位置からテクスチャ座標を計算
                            float aspectRatio = (float)cachedGradientTexturePreview.width / cachedGradientTexturePreview.height;
                            Rect actualRect;

                            if (aspectRatio > 1)
                            {
                                float h = previewRect.width / aspectRatio;
                                actualRect = new Rect(previewRect.x, previewRect.y + (previewRect.height - h) / 2, previewRect.width, h);
                            }
                            else
                            {
                                float w = previewRect.height * aspectRatio;
                                actualRect = new Rect(previewRect.x + (previewRect.width - w) / 2, previewRect.y, w, previewRect.height);
                            }

                            if (actualRect.Contains(e.mousePosition))
                            {
                                float u = (e.mousePosition.x - actualRect.x) / actualRect.width;
                                float v = 1f - (e.mousePosition.y - actualRect.y) / actualRect.height;

                                // 読み取り可能なテクスチャを取得
                                var readableTex = Processing.ColorProcessor.GetReadableTexture(cachedGradientTexturePreview);
                                int px = Mathf.FloorToInt(u * readableTex.width);
                                int py = Mathf.FloorToInt(v * readableTex.height);
                                px = Mathf.Clamp(px, 0, readableTex.width - 1);
                                py = Mathf.Clamp(py, 0, readableTex.height - 1);

                                Color pickedColor = readableTex.GetPixel(px, py);

                                if (readableTex != cachedGradientTexturePreview)
                                {
                                    Object.DestroyImmediate(readableTex);
                                }

                                // Gradientにキーを追加
                                AddColorKeyToGradient(pickedColor, gradientColorKeyPosition);
                                e.Use();
                                Repaint();
                            }
                        }

                        EditorGUILayout.HelpBox(CHMLocales.Tr("Window:GradientColorPicker:ClickToAdd"), MessageType.None);

                        // 代表色パレット
                        if (extractedGradientColors != null && extractedGradientColors.Length > 0)
                        {
                            EditorGUILayout.Space(5);
                            EditorGUILayout.LabelField(CHMLocales.Tr("Window:ColorPicker:CandidateColors"));
                            EditorGUILayout.BeginHorizontal();

                            foreach (var color in extractedGradientColors)
                            {
                                Rect colorRect = GUILayoutUtility.GetRect(30, 30);
                                EditorGUI.DrawRect(colorRect, color);

                                // 枠線
                                Handles.color = Color.black;
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.y), new Vector3(colorRect.xMax, colorRect.y));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.y), new Vector3(colorRect.xMax, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.xMax, colorRect.yMax), new Vector3(colorRect.x, colorRect.yMax));
                                Handles.DrawLine(new Vector3(colorRect.x, colorRect.yMax), new Vector3(colorRect.x, colorRect.y));

                                // クリック検出
                                if (Event.current.type == EventType.MouseDown && colorRect.Contains(Event.current.mousePosition))
                                {
                                    AddColorKeyToGradient(color, gradientColorKeyPosition);
                                    Event.current.Use();
                                    Repaint();
                                }
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(CHMLocales.Tr("Window:ColorPicker:TextureNotFound"), MessageType.Warning);
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Gradientにカラーキーを追加
        /// </summary>
        private void AddColorKeyToGradient(Color color, float position)
        {
            var colorKeys = new List<GradientColorKey>(gradientCurve.colorKeys);
            var alphaKeys = new List<GradientAlphaKey>(gradientCurve.alphaKeys);

            // 同じ位置にキーがあれば置き換え、なければ追加
            bool found = false;
            for (int i = 0; i < colorKeys.Count; i++)
            {
                if (Mathf.Abs(colorKeys[i].time - position) < 0.01f)
                {
                    colorKeys[i] = new GradientColorKey(color, position);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // 最大8キーまで
                if (colorKeys.Count < 8)
                {
                    colorKeys.Add(new GradientColorKey(color, position));
                }
                else
                {
                    // 最も近い位置のキーを置き換え
                    float minDist = float.MaxValue;
                    int minIndex = 0;
                    for (int i = 0; i < colorKeys.Count; i++)
                    {
                        float dist = Mathf.Abs(colorKeys[i].time - position);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            minIndex = i;
                        }
                    }
                    colorKeys[minIndex] = new GradientColorKey(color, position);
                }
            }

            // 時間順にソート
            colorKeys.Sort((a, b) => a.time.CompareTo(b.time));

            gradientCurve.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
        }

        private void DrawUVSettings()
        {
            showUVSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showUVSettings, CHMLocales.Tr("Window:UVSettings:Header"));
            if (showUVSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:UVSettings:Resolution"), GUILayout.Width(80));
                textureResolution = (TextureResolution)EditorGUILayout.EnumPopup(textureResolution);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // プレビューヘッダー（ラベル + 拡大ビューボタン）
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:UVSettings:Preview"));
                if (GUILayout.Button(CHMLocales.Tr("Window:UVSettings:ExpandView"), GUILayout.Width(80)))
                {
                    UVPlacementPreviewWindow.Open(this);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:UVSettings:FitHint"), EditorStyles.miniLabel);

                // プレビュー領域を正方形で確保
                float availableWidth = EditorGUIUtility.currentViewWidth - 60; // マージン考慮
                float previewSize = Mathf.Min(availableWidth, 400); // 最大400px
                uvPreviewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
                // 中央寄せ
                uvPreviewRect.x += (EditorGUIUtility.currentViewWidth - previewSize - 40) * 0.5f;

                DrawUVPreview(uvPreviewRect);

                EditorGUILayout.Space(5);

                // 凡例（クリックで対応アイランドを選択）
                if (targetRenderers.Count > 0)
                {
                    EditorGUILayout.LabelField(CHMLocales.Tr("Window:UVSettings:Legend"), EditorStyles.miniLabel);
                    for (int i = 0; i < targetRenderers.Count && i < uvColors.Length; i++)
                    {
                        if (targetRenderers[i] == null) continue;
                        
                        EditorGUILayout.BeginHorizontal();
                        
                        // クリック可能な領域を作成
                        var rowRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                        
                        // ハイライト表示（このRendererのアイランドが選択されている場合）
                        bool isSelected = selectedIslandIndex >= 0 && 
                                          selectedIslandIndex < islandPlacements.Count &&
                                          islandPlacements[selectedIslandIndex].rendererIndex == i;
                        if (isSelected)
                        {
                            EditorGUI.DrawRect(rowRect, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                        }
                        
                        // 色インジケータ
                        var legendRect = new Rect(rowRect.x, rowRect.y + 1, 12, 12);
                        EditorGUI.DrawRect(legendRect, uvColors[i]);
                        
                        // ラベル
                        var labelRect = new Rect(rowRect.x + 16, rowRect.y, rowRect.width - 16, rowRect.height);
                        EditorGUI.LabelField(labelRect, targetRenderers[i].name, EditorStyles.miniLabel);
                        
                        // クリック処理
                        if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                        {
                            // このRendererのアイランドを前面に移動（リストの最後に）
                            BringRendererIslandsToFront(i);
                            
                            // このRendererの最初のアイランドを選択
                            for (int j = 0; j < islandPlacements.Count; j++)
                            {
                                if (islandPlacements[j].rendererIndex == i)
                                {
                                    selectedIslandIndex = j;
                                    break;
                                }
                            }
                            Event.current.Use();
                            Repaint();
                        }
                        
                        EditorGUILayout.EndHorizontal();
                    }
                }

                // 重なり警告を表示（キャッシュ結果を使用）
                if (hasUVOverlap && cachedOverlapMessage != null)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox(cachedOverlapMessage, MessageType.Warning);
                }

                // はみ出し警告を表示
                if (hasUVOutOfBounds)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox(CHMLocales.Tr("Window:UVSettings:OutOfBounds"), MessageType.Warning);
                }

                EditorGUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(CHMLocales.Tr("Window:UVSettings:AutoArrange"), GUILayout.Width(100)))
                {
                    AutoArrangeUVs();
                }
                if (GUILayout.Button(CHMLocales.Tr("Window:UVSettings:Reset"), GUILayout.Width(80)))
                {
                    ResetUVPlacements();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Window:UVSettings:DragHint"),
                    MessageType.Info
                );

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        /// <summary>
        /// UV配置プレビューを指定の Rect に描画する
        /// 背景・グリッド・アイランド・枠線をまとめて描く
        /// uvPreviewRect フィールドはマウス操作用に一時的に rect で上書きされる
        /// </summary>
        internal void DrawUVPreview(Rect rect)
        {
            // マウス操作用に uvPreviewRect を上書き（HandleUVInteraction が参照する）
            uvPreviewRect = rect;

            // 背景を描画
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));

            // マウスインタラクション処理
            HandleUVInteraction();

            // グリッド描画
            DrawUVGrid(rect);

            // UVアイランド描画
            DrawUVIslands(rect);

            // 枠線を描画
            Handles.color = Color.white;
            Handles.DrawWireDisc(rect.center, Vector3.forward, 0); // ダミー
            var corners = new Vector3[]
            {
                new Vector3(rect.xMin, rect.yMin, 0),
                new Vector3(rect.xMax, rect.yMin, 0),
                new Vector3(rect.xMax, rect.yMax, 0),
                new Vector3(rect.xMin, rect.yMax, 0),
                new Vector3(rect.xMin, rect.yMin, 0)
            };
            Handles.DrawPolyLine(corners);
        }

        private void DrawUVGrid(Rect rect)
        {
            Handles.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);

            // 縦線
            for (int i = 0; i <= 4; i++)
            {
                float x = rect.x + rect.width * i / 4f;
                Handles.DrawLine(new Vector3(x, rect.y, 0), new Vector3(x, rect.yMax, 0));
            }

            // 横線
            for (int i = 0; i <= 4; i++)
            {
                float y = rect.y + rect.height * i / 4f;
                Handles.DrawLine(new Vector3(rect.x, y, 0), new Vector3(rect.xMax, y, 0));
            }
        }

        private void DrawUVIslands(Rect previewRect)
        {
            // まず全アイランドの背景テクスチャを描画（重なり順序のため先に描画）
            for (int i = 0; i < islandPlacements.Count; i++)
            {
                var placement = islandPlacements[i];
                if (placement.rendererIndex >= targetRenderers.Count) continue;

                var renderer = targetRenderers[placement.rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;

                var bounds = placement.originalBounds;

                // アイランドの表示位置・サイズを計算
                float displayX = bounds.x * placement.scale.x + placement.position.x;
                float displayY = bounds.y * placement.scale.y + placement.position.y;
                float displayW = bounds.width * placement.scale.x;
                float displayH = bounds.height * placement.scale.y;

                // UV空間(0-1)からスクリーン座標に変換
                var islandRect = new Rect(
                    previewRect.x + displayX * previewRect.width,
                    previewRect.yMax - (displayY + displayH) * previewRect.height,
                    displayW * previewRect.width,
                    displayH * previewRect.height
                );

                // 背景テクスチャを描画（サブメッシュごとに対応するマテリアルのテクスチャを使用）
                var backgroundTexture = (isDraggingUV || isResizingUV)
                    ? GetCachedBackgroundTextureLowRes(renderer, placement.submeshIndex, placement.localIslandIndex, placement.originalBounds)
                    : GetCachedBackgroundTexture(renderer, placement.submeshIndex, placement.localIslandIndex, placement.originalBounds);

                if (backgroundTexture != null)
                {
                    GUI.DrawTexture(islandRect, backgroundTexture, ScaleMode.StretchToFill);
                }
            }

            // 次にワイヤーフレームと境界ボックスを描画
            for (int i = 0; i < islandPlacements.Count; i++)
            {
                var placement = islandPlacements[i];
                if (placement.rendererIndex >= targetRenderers.Count) continue;

                var renderer = targetRenderers[placement.rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;

                var bounds = placement.originalBounds;

                // 色を設定（Renderer毎に色分け）
                Color color = uvColors[placement.rendererIndex % uvColors.Length];
                // ドラッグ中またはリサイズ中のみ白色にする
                if (selectedIslandIndex == i && (isDraggingUV || isResizingUV))
                {
                    color = Color.white;
                }

                // アイランドの表示位置・サイズを計算
                float displayX = bounds.x * placement.scale.x + placement.position.x;
                float displayY = bounds.y * placement.scale.y + placement.position.y;
                float displayW = bounds.width * placement.scale.x;
                float displayH = bounds.height * placement.scale.y;

                var islandRect = new Rect(
                    previewRect.x + displayX * previewRect.width,
                    previewRect.yMax - (displayY + displayH) * previewRect.height,
                    displayW * previewRect.width,
                    displayH * previewRect.height
                );

                // ドラッグ中またはリサイズ中はワイヤーフレームをスキップ
                if (!isDraggingUV && !isResizingUV)
                {
                    // UV三角形をワイヤーフレームで描画（このアイランドに含まれる三角形のみ）
                    DrawUVTrianglesForIsland(renderer, bounds, islandRect, color);
                }

                // 各アイランドの枠を描画
                Handles.color = new Color(color.r, color.g, color.b, 1f);
                var boxCorners = new Vector3[]
                {
                    new Vector3(islandRect.xMin, islandRect.yMin, 0),
                    new Vector3(islandRect.xMax, islandRect.yMin, 0),
                    new Vector3(islandRect.xMax, islandRect.yMax, 0),
                    new Vector3(islandRect.xMin, islandRect.yMax, 0),
                    new Vector3(islandRect.xMin, islandRect.yMin, 0)
                };
                Handles.DrawPolyLine(boxCorners);

                // リサイズハンドルを描画（選択中のみ）
                if (selectedIslandIndex == i)
                {
                    DrawResizeHandles(islandRect, color);
                }
            }
        }

        /// <summary>
        /// GL描画用マテリアルを取得（遅延初期化）
        /// </summary>
        private static void EnsureGLLineMaterial()
        {
            if (glLineMaterial != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;

            // HideAndDontSave の static Material はドメインリロードで参照だけ消えて
            // ネイティブオブジェクトが残るため、リロード前に破棄する
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DestroyGLLineMaterial;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DestroyGLLineMaterial;

            glLineMaterial = new Material(shader);
            glLineMaterial.hideFlags = HideFlags.HideAndDontSave;
            glLineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            glLineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            glLineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            glLineMaterial.SetInt("_ZWrite", 0);
        }

        private static void DestroyGLLineMaterial()
        {
            if (glLineMaterial != null)
            {
                Object.DestroyImmediate(glLineMaterial);
                glLineMaterial = null;
            }
        }

        /// <summary>
        /// フルUVデータをキャッシュ付きで取得（サンプリングなし）
        /// </summary>
        private List<Vector2> GetCachedFullUVs(SkinnedMeshRenderer renderer)
        {
            if (uvFullCache.TryGetValue(renderer, out var cached))
            {
                return cached;
            }

            var mesh = renderer.sharedMesh;
            if (mesh == null) return null;

            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);

            uvFullCache[renderer] = uvs;
            return uvs;
        }

        /// <summary>
        /// 特定のアイランド境界内のUV三角形をGL.LINESでバッチ描画
        /// </summary>
        private void DrawUVTrianglesForIsland(SkinnedMeshRenderer renderer, Rect uvBounds, Rect islandRect, Color color)
        {
            var triangles = GetCachedTriangles(renderer);
            if (triangles == null || triangles.Length == 0) return;

            var allUVs = GetCachedFullUVs(renderer);
            if (allUVs == null || allUVs.Count == 0) return;

            EnsureGLLineMaterial();
            if (glLineMaterial == null) return;
            glLineMaterial.SetPass(0);

            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            GL.Color(color);

            int triangleCount = triangles.Length / 3;
            int step = triangleCount > MAX_TRIANGLES_TO_DRAW ? triangleCount / MAX_TRIANGLES_TO_DRAW : 1;

            float invBoundsW = uvBounds.width > 0 ? 1f / uvBounds.width : 0;
            float invBoundsH = uvBounds.height > 0 ? 1f / uvBounds.height : 0;
            float rectX = islandRect.x;
            float rectYMax = islandRect.yMax;
            float rectW = islandRect.width;
            float rectH = islandRect.height;
            float boundsX = uvBounds.x;
            float boundsY = uvBounds.y;

            for (int t = 0; t < triangles.Length; t += 3 * step)
            {
                if (t + 2 >= triangles.Length) break;

                int idx0 = triangles[t];
                int idx1 = triangles[t + 1];
                int idx2 = triangles[t + 2];

                if (idx0 >= allUVs.Count || idx1 >= allUVs.Count || idx2 >= allUVs.Count) continue;

                Vector2 uv0 = allUVs[idx0];
                Vector2 uv1 = allUVs[idx1];
                Vector2 uv2 = allUVs[idx2];

                // 三角形の重心がこのアイランド境界内にあるかチェック
                float cx = Mathf.Repeat((uv0.x + uv1.x + uv2.x) / 3f, 1.0f);
                float cy = Mathf.Repeat((uv0.y + uv1.y + uv2.y) / 3f, 1.0f);

                if (cx < uvBounds.xMin || cx > uvBounds.xMax || cy < uvBounds.yMin || cy > uvBounds.yMax) continue;

                // UV→画面座標をインライン計算
                float p0x = rectX + (uv0.x - boundsX) * invBoundsW * rectW;
                float p0y = rectYMax - (uv0.y - boundsY) * invBoundsH * rectH;
                float p1x = rectX + (uv1.x - boundsX) * invBoundsW * rectW;
                float p1y = rectYMax - (uv1.y - boundsY) * invBoundsH * rectH;
                float p2x = rectX + (uv2.x - boundsX) * invBoundsW * rectW;
                float p2y = rectYMax - (uv2.y - boundsY) * invBoundsH * rectH;

                GL.Vertex3(p0x, p0y, 0);
                GL.Vertex3(p1x, p1y, 0);
                GL.Vertex3(p1x, p1y, 0);
                GL.Vertex3(p2x, p2y, 0);
                GL.Vertex3(p2x, p2y, 0);
                GL.Vertex3(p0x, p0y, 0);
            }

            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// 複数のアイランド境界を統合したスクリーン上の矩形を計算
        /// </summary>
        private Rect CalculateCombinedIslandRect(List<Rect> islandBounds, Vector2 offset, Vector2 scaleFactor, Rect previewRect)
        {
            if (islandBounds == null || islandBounds.Count == 0)
            {
                return Rect.zero;
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var bounds in islandBounds)
            {
                float displayX = bounds.x + offset.x;
                float displayY = bounds.y + offset.y;
                float displayW = bounds.width * scaleFactor.x;
                float displayH = bounds.height * scaleFactor.y;

                minX = Mathf.Min(minX, displayX);
                minY = Mathf.Min(minY, displayY);
                maxX = Mathf.Max(maxX, displayX + displayW);
                maxY = Mathf.Max(maxY, displayY + displayH);
            }

            // スクリーン座標に変換
            return new Rect(
                previewRect.x + minX * previewRect.width,
                previewRect.yMax - maxY * previewRect.height,
                (maxX - minX) * previewRect.width,
                (maxY - minY) * previewRect.height
            );
        }

        /// <summary>
        /// UVアイランド同士の重なりをチェック
        /// </summary>
        /// <returns>重なっているRenderer同士のインデックスペアのリスト</returns>
        private List<(int, int)> CheckUVOverlap()
        {
            var overlappingPairs = new List<(int, int)>();

            // 各アイランドの境界（スケール・オフセット適用後）を計算
            var allIslandRects = new List<(int rendererIndex, Rect bounds)>();

            foreach (var placement in islandPlacements)
            {
                var bounds = placement.originalBounds;

                float displayX = bounds.x * placement.scale.x + placement.position.x;
                float displayY = bounds.y * placement.scale.y + placement.position.y;
                float displayW = bounds.width * placement.scale.x;
                float displayH = bounds.height * placement.scale.y;

                var transformedBounds = new Rect(displayX, displayY, displayW, displayH);
                allIslandRects.Add((placement.rendererIndex, transformedBounds));
            }

            // 異なるRenderer同士の重なりをチェック
            var checkedPairs = new HashSet<(int, int)>();

            for (int i = 0; i < allIslandRects.Count; i++)
            {
                for (int j = i + 1; j < allIslandRects.Count; j++)
                {
                    int rendererIdx1 = allIslandRects[i].rendererIndex;
                    int rendererIdx2 = allIslandRects[j].rendererIndex;

                    // 同じRendererは無視
                    if (rendererIdx1 == rendererIdx2) continue;

                    // 既にチェック済みのペアは無視
                    var pairKey = rendererIdx1 < rendererIdx2 
                        ? (rendererIdx1, rendererIdx2) 
                        : (rendererIdx2, rendererIdx1);
                    if (checkedPairs.Contains(pairKey)) continue;

                    // 重なりチェック
                    if (allIslandRects[i].bounds.Overlaps(allIslandRects[j].bounds))
                    {
                        checkedPairs.Add(pairKey);
                        overlappingPairs.Add(pairKey);
                    }
                }
            }

            return overlappingPairs;
        }

        /// <summary>
        /// UV重複チェック結果をキャッシュに反映
        /// ドラッグ/リサイズ完了時、自動配置時、リセット時に呼ぶ
        /// </summary>
        private void UpdateUVOverlapCache()
        {
            var overlappingPairs = CheckUVOverlap();
            hasUVOverlap = overlappingPairs.Count > 0;
            if (hasUVOverlap)
            {
                string warningMessage = CHMLocales.Tr("Window:UVSettings:OverlapWarningHeader") + "\n";
                foreach (var pair in overlappingPairs)
                {
                    // ?. は UnityEngine.Object の破棄済み（fake null）を検出できないため、
                    // Unity の == 演算子で判定する（破棄済み Renderer で MissingReferenceException になる）
                    var r1 = targetRenderers[pair.Item1];
                    var r2 = targetRenderers[pair.Item2];
                    string name1 = r1 != null ? r1.name : $"Renderer {pair.Item1}";
                    string name2 = r2 != null ? r2.name : $"Renderer {pair.Item2}";
                    warningMessage += string.Format(CHMLocales.Tr("Window:UVSettings:OverlapWarningLine"), name1, name2) + "\n";
                }
                cachedOverlapMessage = warningMessage.TrimEnd('\n');
            }
            else
            {
                cachedOverlapMessage = null;
            }

            // 枠からのはみ出しチェック
            hasUVOutOfBounds = CheckUVOutOfBounds();
        }

        /// <summary>
        /// いずれかのアイランドがUV空間[0,1]の枠からはみ出しているかチェック
        /// </summary>
        private bool CheckUVOutOfBounds()
        {
            foreach (var placement in islandPlacements)
            {
                var bounds = placement.originalBounds;
                float x = bounds.x * placement.scale.x + placement.position.x;
                float y = bounds.y * placement.scale.y + placement.position.y;
                float w = bounds.width * placement.scale.x;
                float h = bounds.height * placement.scale.y;

                if (x < -0.001f || y < -0.001f || x + w > 1.001f || y + h > 1.001f)
                    return true;
            }
            return false;
        }

        private void DrawResizeHandles(Rect islandRect, Color color)
        {
            // 四隅にハンドルを描画
            Color handleColor = new Color(color.r, color.g, color.b, 1f);
            Color handleFill = new Color(0.2f, 0.2f, 0.2f, 1f);

            // 左下 (0)
            DrawHandle(new Vector2(islandRect.xMin, islandRect.yMax), handleColor, handleFill);
            // 右下 (1)
            DrawHandle(new Vector2(islandRect.xMax, islandRect.yMax), handleColor, handleFill);
            // 右上 (2)
            DrawHandle(new Vector2(islandRect.xMax, islandRect.yMin), handleColor, handleFill);
            // 左上 (3)
            DrawHandle(new Vector2(islandRect.xMin, islandRect.yMin), handleColor, handleFill);
        }

        private void DrawHandle(Vector2 center, Color borderColor, Color fillColor)
        {
            Rect handleRect = new Rect(
                center.x - RESIZE_HANDLE_SIZE * 0.5f,
                center.y - RESIZE_HANDLE_SIZE * 0.5f,
                RESIZE_HANDLE_SIZE,
                RESIZE_HANDLE_SIZE
            );
            EditorGUI.DrawRect(handleRect, fillColor);
            Handles.color = borderColor;
            Handles.DrawWireCube(new Vector3(center.x, center.y, 0), new Vector3(RESIZE_HANDLE_SIZE, RESIZE_HANDLE_SIZE, 0));
        }

        private Vector3 UVToScreenPos(Vector2 uv, Rect uvBounds, Rect islandRect)
        {
            // UV座標をUV境界に基づいて正規化（0-1範囲に変換）
            float normalizedU = uvBounds.width > 0 ? (uv.x - uvBounds.x) / uvBounds.width : 0;
            float normalizedV = uvBounds.height > 0 ? (uv.y - uvBounds.y) / uvBounds.height : 0;

            // 画面座標に変換
            // normalizedU/V(0,0)がislandRectの左下、(1,1)が右上
            float screenX = islandRect.x + normalizedU * islandRect.width;
            float screenY = islandRect.yMax - normalizedV * islandRect.height;

            return new Vector3(screenX, screenY, 0);
        }

        /// <summary>
        /// アイランドのスクリーン上の矩形を計算（全UVアイランドの統合矩形）
        /// </summary>
        private Rect CalculateIslandScreenRect(int islandIndex, Rect previewRect)
        {
            if (islandIndex < 0 || islandIndex >= islandPlacements.Count) return Rect.zero;

            var placement = islandPlacements[islandIndex];
            var bounds = placement.originalBounds;

            // アイランドの表示位置・サイズを計算
            float displayX = bounds.x * placement.scale.x + placement.position.x;
            float displayY = bounds.y * placement.scale.y + placement.position.y;
            float displayW = bounds.width * placement.scale.x;
            float displayH = bounds.height * placement.scale.y;

            return new Rect(
                previewRect.x + displayX * previewRect.width,
                previewRect.yMax - (displayY + displayH) * previewRect.height,
                displayW * previewRect.width,
                displayH * previewRect.height
            );
        }

        private void DrawOutputSettings()
        {
            showOutputSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showOutputSettings, CHMLocales.Tr("Window:OutputSettings:Header"));
            if (showOutputSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CHMLocales.Tr("Window:OutputSettings:BaseMaterial"), GUILayout.Width(200));

                // ObjectField用のRectを確保
                Rect baseMaterialRect = EditorGUILayout.GetControlRect();

                // GameObjectのドロップを先に処理（Rendererからマテリアルを取得）
                var baseMaterialEvt = Event.current;
                if (baseMaterialRect.Contains(baseMaterialEvt.mousePosition))
                {
                    if (baseMaterialEvt.type == EventType.DragUpdated || baseMaterialEvt.type == EventType.DragPerform)
                    {
                        var draggedObj = DragAndDrop.objectReferences.Length > 0 ? DragAndDrop.objectReferences[0] : null;
                        if (draggedObj is GameObject go)
                        {
                            var renderer = go.GetComponent<Renderer>();
                            if (renderer != null && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null)
                            {
                                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                                if (baseMaterialEvt.type == EventType.DragPerform)
                                {
                                    DragAndDrop.AcceptDrag();
                                    baseMaterial = renderer.sharedMaterials[0];
                                }
                                baseMaterialEvt.Use();
                            }
                        }
                    }
                }

                // 通常のMaterial ObjectField
                baseMaterial = (Material)EditorGUI.ObjectField(baseMaterialRect, baseMaterial, typeof(Material), false);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    CHMLocales.Tr("Window:OutputSettings:BaseMaterialHint"),
                    MessageType.Info
                );

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = CanExecuteSetup();
            if (GUILayout.Button(CHMLocales.Tr("Window:Action:Setup"), GUILayout.Width(120), GUILayout.Height(30)))
            {
                ExecuteSetup();
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!CanExecuteSetup())
            {
                EditorGUILayout.HelpBox(GetValidationMessage(), MessageType.Warning);
            }
        }

        #endregion

        #region UV Cache

        private List<Vector2> GetCachedUVs(SkinnedMeshRenderer renderer)
        {
            if (uvCache.TryGetValue(renderer, out var cached))
            {
                return cached;
            }

            var mesh = renderer.sharedMesh;
            if (mesh == null) return null;

            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);

            // サンプリング（頂点数が多い場合は間引く）
            if (uvs.Count > 500)
            {
                var sampled = new List<Vector2>();
                int step = uvs.Count / 500;
                for (int i = 0; i < uvs.Count; i += step)
                {
                    sampled.Add(uvs[i]);
                }
                uvs = sampled;
            }

            uvCache[renderer] = uvs;
            return uvs;
        }

        /// <summary>
        /// サブメッシュごとのUVアイランドバウンディングボックスを取得
        /// </summary>
        /// <returns>サブメッシュごとのアイランドリスト（外側: サブメッシュ、内側: アイランド）</returns>
        private List<List<Rect>> GetCachedUVIslandBoundsPerSubmesh(SkinnedMeshRenderer renderer)
        {
            if (uvIslandBoundsCache.TryGetValue(renderer, out var cached))
            {
                return cached;
            }

            var mesh = renderer.sharedMesh;
            if (mesh == null)
            {
                var defaultResult = new List<List<Rect>>();
                defaultResult.Add(new List<Rect> { new Rect(0, 0, 1, 1) });
                return defaultResult;
            }

            // サブメッシュごとにUVアイランド検出（テクスチャ解像度に基づく最小パディング付き）
            var texResolutions = Processing.UVIslandDetector.GetSubmeshTextureResolutions(renderer);
            var islandBoundsPerSubmesh = Processing.UVIslandDetector.DetectIslandBoundsPerSubmesh(mesh, 0.05f, texResolutions);
            uvIslandBoundsCache[renderer] = islandBoundsPerSubmesh;
            return islandBoundsPerSubmesh;
        }

        /// <summary>
        /// UVアイランドごとのバウンディングボックスを取得（後方互換用、全サブメッシュをフラット化）
        /// </summary>
        private List<Rect> GetCachedUVIslandBounds(SkinnedMeshRenderer renderer)
        {
            var perSubmesh = GetCachedUVIslandBoundsPerSubmesh(renderer);
            var result = new List<Rect>();
            foreach (var submeshIslands in perSubmesh)
            {
                result.AddRange(submeshIslands);
            }
            if (result.Count == 0)
            {
                result.Add(new Rect(0, 0, 1, 1));
            }
            return result;
        }

        /// <summary>
        /// 全UVアイランドを統合した境界を取得（移動・リサイズ用）
        /// </summary>
        private Rect GetCombinedUVBounds(SkinnedMeshRenderer renderer)
        {
            var islandBounds = GetCachedUVIslandBounds(renderer);
            
            if (islandBounds == null || islandBounds.Count == 0)
            {
                return new Rect(0, 0, 1, 1);
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var bounds in islandBounds)
            {
                minX = Mathf.Min(minX, bounds.xMin);
                minY = Mathf.Min(minY, bounds.yMin);
                maxX = Mathf.Max(maxX, bounds.xMax);
                maxY = Mathf.Max(maxY, bounds.yMax);
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private void ClearUVCache()
        {
            uvCache.Clear();
            uvFullCache.Clear();
            triangleCache.Clear();
            uvIslandBoundsCache.Clear();

            // 背景テクスチャを破棄
            foreach (var texDict in backgroundTextureCache.Values)
            {
                foreach (var tex in texDict.Values)
                {
                    if (tex != null)
                    {
                        DestroyImmediate(tex);
                    }
                }
            }
            backgroundTextureCache.Clear();

            // 低解像度背景テクスチャを破棄
            foreach (var texDict in lowResBackgroundTextureCache.Values)
            {
                foreach (var tex in texDict.Values)
                {
                    if (tex != null)
                    {
                        DestroyImmediate(tex);
                    }
                }
            }
            lowResBackgroundTextureCache.Clear();
        }

        private int[] GetCachedTriangles(SkinnedMeshRenderer renderer)
        {
            if (triangleCache.TryGetValue(renderer, out var cached))
            {
                return cached;
            }

            var mesh = renderer.sharedMesh;
            if (mesh == null) return null;

            var triangles = mesh.triangles;
            triangleCache[renderer] = triangles;
            return triangles;
        }

        /// <summary>
        /// 指定サブメッシュ・アイランドの背景テクスチャを取得
        /// </summary>
        private Texture2D GetCachedBackgroundTexture(SkinnedMeshRenderer renderer, int submeshIndex, int localIslandIndex, Rect bounds)
        {
            if (!backgroundTextureCache.TryGetValue(renderer, out var texDict))
            {
                texDict = new Dictionary<(int, int), Texture2D>();
                backgroundTextureCache[renderer] = texDict;
            }

            var key = (submeshIndex, localIslandIndex);
            if (texDict.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // サブメッシュに対応するマテリアルからテクスチャを取得
            var sourceTexture = GetSourceTextureForSubmesh(renderer, submeshIndex);
            if (sourceTexture == null) return null;

            var croppedTexture = CropTextureToUVBounds(sourceTexture, bounds, BACKGROUND_TEXTURE_SIZE);
            texDict[key] = croppedTexture;
            return croppedTexture;
        }

        /// <summary>
        /// 指定サブメッシュ・アイランドの低解像度背景テクスチャを取得
        /// </summary>
        private Texture2D GetCachedBackgroundTextureLowRes(SkinnedMeshRenderer renderer, int submeshIndex, int localIslandIndex, Rect bounds)
        {
            if (!lowResBackgroundTextureCache.TryGetValue(renderer, out var texDict))
            {
                texDict = new Dictionary<(int, int), Texture2D>();
                lowResBackgroundTextureCache[renderer] = texDict;
            }

            var key = (submeshIndex, localIslandIndex);
            if (texDict.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // サブメッシュに対応するマテリアルからテクスチャを取得
            var sourceTexture = GetSourceTextureForSubmesh(renderer, submeshIndex);
            if (sourceTexture == null) return null;

            var croppedTexture = CropTextureToUVBounds(sourceTexture, bounds, BACKGROUND_TEXTURE_SIZE_LOW);
            texDict[key] = croppedTexture;
            return croppedTexture;
        }

        /// <summary>
        /// サブメッシュに対応するマテリアルからメインテクスチャを取得
        /// </summary>
        private Texture2D GetSourceTextureForSubmesh(SkinnedMeshRenderer renderer, int submeshIndex)
        {
            var materials = renderer.sharedMaterials;

            // サブメッシュインデックスに対応するマテリアルを取得
            if (submeshIndex >= 0 && submeshIndex < materials.Length)
            {
                var mat = materials[submeshIndex];
                if (mat != null && mat.HasProperty("_MainTex"))
                {
                    var tex = mat.GetTexture("_MainTex") as Texture2D;
                    if (tex != null)
                    {
                        return tex;
                    }
                }
            }

            // サブメッシュに対応するテクスチャが見つからない場合はnullを返す
            // （フォールバックで他のマテリアルのテクスチャを使用しない）

            return null;
        }

        private Texture2D CropTextureToUVBounds(Texture2D source, Rect uvBounds, int targetSize)
        {
            // UV境界に対応するピクセル領域を計算
            int srcX = Mathf.FloorToInt(uvBounds.x * source.width);
            int srcY = Mathf.FloorToInt(uvBounds.y * source.height);
            int srcWidth = Mathf.CeilToInt(uvBounds.width * source.width);
            int srcHeight = Mathf.CeilToInt(uvBounds.height * source.height);

            // 範囲チェック
            srcX = Mathf.Clamp(srcX, 0, source.width - 1);
            srcY = Mathf.Clamp(srcY, 0, source.height - 1);
            srcWidth = Mathf.Clamp(srcWidth, 1, source.width - srcX);
            srcHeight = Mathf.Clamp(srcHeight, 1, source.height - srcY);

            // RenderTextureを使ってクロップ＆リサイズ
            RenderTexture rt = RenderTexture.GetTemporary(targetSize, targetSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.filterMode = FilterMode.Bilinear;

            // スケールとオフセットを計算してクロップ
            Vector2 scale = new Vector2(uvBounds.width, uvBounds.height);
            Vector2 offset = new Vector2(uvBounds.x, uvBounds.y);

            RenderTexture.active = rt;
            Graphics.Blit(source, rt, scale, offset);

            Texture2D result = new Texture2D(targetSize, targetSize, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, targetSize, targetSize), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            return result;
        }

        #endregion

        #region UV Interaction

        private void HandleUVInteraction()
        {
            if (uvPreviewRect.width <= 0 || uvPreviewRect.height <= 0) return;

            Event evt = Event.current;
            Vector2 mousePos = evt.mousePosition;

            if (!uvPreviewRect.Contains(mousePos) && !isDraggingUV && !isResizingUV)
            {
                return;
            }

            switch (evt.type)
            {
                case EventType.MouseDown:
                    if (evt.button == 0 && uvPreviewRect.Contains(mousePos))
                    {
                        // まずリサイズハンドルをチェック
                        int handleIndex;
                        int islandIndex = FindResizeHandleAtPosition(mousePos, out handleIndex);
                        if (islandIndex >= 0)
                        {
                            selectedIslandIndex = islandIndex;
                            isResizingUV = true;
                            resizeCorner = handleIndex;
                            dragStartPos = mousePos;
                            resizeStartScale = islandPlacements[islandIndex].scale;
                            resizeStartPos = islandPlacements[islandIndex].position;
                            evt.Use();
                            Repaint();
                        }
                        else
                        {
                            // アイランド本体をチェック
                            selectedIslandIndex = FindUVIslandAtPosition(mousePos);
                            if (selectedIslandIndex >= 0)
                            {
                                isDraggingUV = true;
                                dragStartPos = mousePos;
                                evt.Use();
                                Repaint();
                            }
                            else
                            {
                                // 空白部分をクリックした場合は選択を解除
                                selectedIslandIndex = -1;
                                evt.Use();
                                Repaint();
                            }
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (isResizingUV && selectedIslandIndex >= 0 && selectedIslandIndex < islandPlacements.Count)
                    {
                        HandleResize(mousePos);
                        evt.Use();
                        Repaint();
                    }
                    else if (isDraggingUV && selectedIslandIndex >= 0 && selectedIslandIndex < islandPlacements.Count)
                    {
                        Vector2 delta = mousePos - dragStartPos;
                        dragStartPos = mousePos;

                        // ピクセル座標をUV座標に変換
                        float deltaU = delta.x / uvPreviewRect.width;
                        float deltaV = -delta.y / uvPreviewRect.height;

                        var placement = islandPlacements[selectedIslandIndex];
                        placement.position += new Vector2(deltaU, deltaV);

                        // 範囲制限（このアイランドの境界を考慮）
                        var bounds = placement.originalBounds;

                        // スケール後のサイズ
                        float scaledW = bounds.width * placement.scale.x;
                        float scaledH = bounds.height * placement.scale.y;
                        float scaledX = bounds.x * placement.scale.x;
                        float scaledY = bounds.y * placement.scale.y;

                        // 表示位置が0-1範囲に収まるよう制限
                        float minOffsetX = -scaledX;
                        float maxOffsetX = 1f - scaledX - scaledW;
                        float minOffsetY = -scaledY;
                        float maxOffsetY = 1f - scaledY - scaledH;

                        placement.position.x = Mathf.Clamp(placement.position.x, minOffsetX, maxOffsetX);
                        placement.position.y = Mathf.Clamp(placement.position.y, minOffsetY, maxOffsetY);

                        evt.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (isDraggingUV || isResizingUV)
                    {
                        isDraggingUV = false;
                        isResizingUV = false;
                        resizeCorner = -1;
                        // selectedIslandIndexは保持して選択状態を維持
                        UpdateUVOverlapCache();
                        evt.Use();
                        Repaint();
                    }
                    break;
            }
        }

        private int FindUVIslandAtPosition(Vector2 mousePos)
        {
            // 後ろから検索（上に描画されているものを優先）
            for (int i = islandPlacements.Count - 1; i >= 0; i--)
            {
                var islandRect = CalculateIslandScreenRect(i, uvPreviewRect);

                if (islandRect.Contains(mousePos))
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindResizeHandleAtPosition(Vector2 mousePos, out int handleIndex)
        {
            handleIndex = -1;

            // 選択中のアイランドのみをチェック（リサイズは選択中のものだけ有効）
            if (selectedIslandIndex < 0 || selectedIslandIndex >= islandPlacements.Count)
            {
                return -1;
            }

            var islandRect = CalculateIslandScreenRect(selectedIslandIndex, uvPreviewRect);

            // 四隅のハンドルをチェック
            Vector2[] corners = new Vector2[]
            {
                new Vector2(islandRect.xMin, islandRect.yMax), // 左下 (0)
                new Vector2(islandRect.xMax, islandRect.yMax), // 右下 (1)
                new Vector2(islandRect.xMax, islandRect.yMin), // 右上 (2)
                new Vector2(islandRect.xMin, islandRect.yMin), // 左上 (3)
            };

            for (int j = 0; j < corners.Length; j++)
            {
                Rect handleRect = new Rect(
                    corners[j].x - RESIZE_HANDLE_HITBOX_SIZE * 0.5f,
                    corners[j].y - RESIZE_HANDLE_HITBOX_SIZE * 0.5f,
                    RESIZE_HANDLE_HITBOX_SIZE,
                    RESIZE_HANDLE_HITBOX_SIZE
                );

                if (handleRect.Contains(mousePos))
                {
                    handleIndex = j;
                    return selectedIslandIndex;
                }
            }
            return -1;
        }

        private void HandleResize(Vector2 mousePos)
        {
            if (selectedIslandIndex < 0 || selectedIslandIndex >= islandPlacements.Count) return;

            var placement = islandPlacements[selectedIslandIndex];
            var bounds = placement.originalBounds;

            // ドラッグ開始位置からの差分（UV座標系）
            Vector2 deltaPixels = mousePos - dragStartPos;
            float deltaU = deltaPixels.x / uvPreviewRect.width;
            float deltaV = -deltaPixels.y / uvPreviewRect.height; // Y軸反転

            // 元のスケールに基づいて新しいスケールを計算
            float newScaleX = resizeStartScale.x;
            float newScaleY = resizeStartScale.y;
            float newPosX = resizeStartPos.x;
            float newPosY = resizeStartPos.y;

            // コーナーに応じてスケールと位置を調整
            // 0=左下, 1=右下, 2=右上, 3=左上
            //
            // 表示位置 = bounds.x * scale.x + position.x
            // 左側・下側をドラッグする場合、反対側の端を固定するために
            // スケール変化による bounds * scale の変化分を position で補正する必要がある
            switch (resizeCorner)
            {
                case 0: // 左下：左と下を動かす（右上を固定）
                    newScaleX = resizeStartScale.x - deltaU / bounds.width;
                    newScaleY = resizeStartScale.y - deltaV / bounds.height;
                    // 右上を固定: bounds.x * newScaleX + newPosX = bounds.x * startScaleX + startPosX + bounds.width * startScaleX
                    //           → newPosX = startPosX + bounds.x * (startScaleX - newScaleX) + bounds.width * (startScaleX - newScaleX)
                    //           → newPosX = startPosX + (bounds.x + bounds.width) * (startScaleX - newScaleX)
                    newPosX = resizeStartPos.x + (bounds.x + bounds.width) * (resizeStartScale.x - newScaleX);
                    newPosY = resizeStartPos.y + (bounds.y + bounds.height) * (resizeStartScale.y - newScaleY);
                    break;
                case 1: // 右下：右と下を動かす（左上を固定）
                    newScaleX = resizeStartScale.x + deltaU / bounds.width;
                    newScaleY = resizeStartScale.y - deltaV / bounds.height;
                    // 左上を固定: X方向は左端固定なので position.x は変わらない
                    // Y方向は上端固定: bounds.y * newScaleY + newPosY + bounds.height * newScaleY = 上端(固定)
                    newPosY = resizeStartPos.y + (bounds.y + bounds.height) * (resizeStartScale.y - newScaleY);
                    break;
                case 2: // 右上：右と上を動かす（左下を固定）
                    newScaleX = resizeStartScale.x + deltaU / bounds.width;
                    newScaleY = resizeStartScale.y + deltaV / bounds.height;
                    // 左下を固定: position は変わらない
                    break;
                case 3: // 左上：左と上を動かす（右下を固定）
                    newScaleX = resizeStartScale.x - deltaU / bounds.width;
                    newScaleY = resizeStartScale.y + deltaV / bounds.height;
                    // 右下を固定: X方向は右端固定
                    newPosX = resizeStartPos.x + (bounds.x + bounds.width) * (resizeStartScale.x - newScaleX);
                    // Y方向は下端固定なので position.y は変わらない
                    break;
            }

            // 最小スケール制限
            const float minScale = 0.1f;
            newScaleX = Mathf.Max(newScaleX, minScale);
            newScaleY = Mathf.Max(newScaleY, minScale);

            // Shiftキーでアスペクト比維持
            if (Event.current.shift)
            {
                float avgScale = (newScaleX + newScaleY) * 0.5f;
                newScaleX = avgScale;
                newScaleY = avgScale;
            }

            placement.scale = new Vector2(newScaleX, newScaleY);
            placement.position = new Vector2(newPosX, newPosY);

            // 範囲制限
            float scaledW = bounds.width * placement.scale.x;
            float scaledH = bounds.height * placement.scale.y;
            float scaledX = bounds.x * placement.scale.x;
            float scaledY = bounds.y * placement.scale.y;

            float minOffsetX = -scaledX;
            float maxOffsetX = 1f - scaledX - scaledW;
            float minOffsetY = -scaledY;
            float maxOffsetY = 1f - scaledY - scaledH;

            placement.position.x = Mathf.Clamp(placement.position.x, minOffsetX, maxOffsetX);
            placement.position.y = Mathf.Clamp(placement.position.y, minOffsetY, maxOffsetY);
        }

        #endregion

        #region D&D Handling

        private void HandleDragAndDrop()
        {
            Event evt = Event.current;

            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();

                        foreach (Object draggedObject in DragAndDrop.objectReferences)
                        {
                            SkinnedMeshRenderer renderer = null;

                            if (draggedObject is SkinnedMeshRenderer smr)
                            {
                                renderer = smr;
                            }
                            else if (draggedObject is GameObject go)
                            {
                                renderer = go.GetComponent<SkinnedMeshRenderer>();
                            }

                            if (renderer != null && !targetRenderers.Contains(renderer))
                            {
                                targetRenderers.Add(renderer);
                            }
                        }

                        UpdateUVPlacements();
                        SyncMaterialSelections();
                        ClearUVCache();
                    }

                    evt.Use();
                    break;
            }
        }

        #endregion

        #region Actions

        /// <summary>
        /// アイランド配置データを更新（Renderer追加/削除時）
        /// </summary>
        private void UpdateIslandPlacements()
        {
            // 新しい配置リストを構築
            var newPlacements = new List<IslandPlacementData>();

            for (int i = 0; i < targetRenderers.Count; i++)
            {
                var renderer = targetRenderers[i];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // サブメッシュごとにアイランドを取得
                var islandBoundsPerSubmesh = GetCachedUVIslandBoundsPerSubmesh(renderer);

                for (int submeshIdx = 0; submeshIdx < islandBoundsPerSubmesh.Count; submeshIdx++)
                {
                    // 統合対象でないサブメッシュはスキップ
                    if (!GetMaterialIncluded(i, submeshIdx))
                        continue;

                    var submeshIslands = islandBoundsPerSubmesh[submeshIdx];

                    for (int localIdx = 0; localIdx < submeshIslands.Count; localIdx++)
                    {
                        // 既存の配置を探す（rendererIndex, submeshIndex, localIslandIndexで一致）
                        var existing = islandPlacements.Find(p =>
                            p.rendererIndex == i && p.submeshIndex == submeshIdx && p.localIslandIndex == localIdx);

                        if (existing != null)
                        {
                            // 既存の配置を維持
                            existing.originalBounds = submeshIslands[localIdx];
                            newPlacements.Add(existing);
                        }
                        else
                        {
                            // 新規作成
                            newPlacements.Add(new IslandPlacementData
                            {
                                rendererIndex = i,
                                submeshIndex = submeshIdx,
                                localIslandIndex = localIdx,
                                originalBounds = submeshIslands[localIdx],
                                position = Vector2.zero,
                                scale = Vector2.one
                            });
                        }
                    }
                }
            }

            islandPlacements = newPlacements;
            selectedIslandIndex = -1;
            UpdateUVOverlapCache();
        }

        /// <summary>
        /// 旧API互換用（内部でUpdateIslandPlacementsを呼ぶ）
        /// </summary>
        private void UpdateUVPlacements()
        {
            UpdateIslandPlacements();
        }

        /// <summary>
        /// Rendererに基づいてマテリアル選択リストを同期
        /// </summary>
        private void SyncMaterialSelections()
        {
            // 無効なエントリを削除
            materialSelections.RemoveAll(e =>
                e.rendererIndex < 0 || e.rendererIndex >= targetRenderers.Count ||
                targetRenderers[e.rendererIndex] == null ||
                targetRenderers[e.rendererIndex].sharedMesh == null ||
                e.submeshIndex < 0 ||
                e.submeshIndex >= targetRenderers[e.rendererIndex].sharedMesh.subMeshCount
            );

            // 不足しているエントリを追加
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

            // ソート
            materialSelections.Sort((a, b) => a.rendererIndex != b.rendererIndex ? a.rendererIndex - b.rendererIndex : a.submeshIndex - b.submeshIndex);
        }

        /// <summary>
        /// targetRenderers から index を削除した際に、rendererIndex で対象を参照する
        /// 各リスト（materialSelections / islandPlacements）の index を詰める。
        /// 削除された Renderer のエントリは除去し、それより後ろの rendererIndex は -1 する。
        /// これを行わないと、削除位置より後ろの Renderer に設定（除外・メッシュカットマスク・
        /// UV配置）がズレて適用され、最後尾の設定が失われる。
        /// </summary>
        private void RemapEntriesForRendererRemoval(int removedIndex)
        {
            RendererIndexRemap.RemoveRenderer(
                materialSelections, removedIndex,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RendererIndexRemap.RemoveRenderer(
                islandPlacements, removedIndex,
                p => p.rendererIndex, (p, i) => p.rendererIndex = i);
        }

        /// <summary>
        /// 指定したRenderer/Submeshが統合対象かどうかを取得（読み取り専用）
        /// </summary>
        private bool GetMaterialIncluded(int rendererIndex, int submeshIndex)
        {
            var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
            return entry?.isIncluded ?? true;
        }

        /// <summary>
        /// 指定したRenderer/Submeshの統合対象フラグを設定
        /// </summary>
        private void SetMaterialIncluded(int rendererIndex, int submeshIndex, bool included)
        {
            var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
            if (entry == null)
            {
                // エントリが存在しない場合は作成
                entry = new MaterialSelectionEntry(rendererIndex, submeshIndex, included);
                materialSelections.Add(entry);
            }
            else
            {
                entry.isIncluded = included;
            }
        }

        /// <summary>
        /// 指定したRenderer/Submeshのメッシュカットマスクを取得
        /// </summary>
        private Texture2D GetMeshCutMask(int rendererIndex, int submeshIndex)
        {
            var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
            return entry?.meshCutMask;
        }

        /// <summary>
        /// 指定したRenderer/Submeshのメッシュカットマスクを設定
        /// </summary>
        private void SetMeshCutMask(int rendererIndex, int submeshIndex, Texture2D mask)
        {
            var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
            if (entry == null)
            {
                entry = new MaterialSelectionEntry(rendererIndex, submeshIndex, true);
                entry.meshCutMask = mask;
                materialSelections.Add(entry);
            }
            else
            {
                entry.meshCutMask = mask;
            }
        }

        private void AutoArrangeUVs()
        {
            int count = islandPlacements.Count;
            if (count == 0) return;

            // アイランドを面積の大きい順にソート（効率的なパッキングのため）
            var sortedPlacements = new List<IslandPlacementData>(islandPlacements);
            sortedPlacements.Sort((a, b) =>
            {
                float areaA = a.originalBounds.width * a.originalBounds.height;
                float areaB = b.originalBounds.width * b.originalBounds.height;
                return areaB.CompareTo(areaA); // 大きい順
            });

            // Shelf Packing アルゴリズムでパッキング
            var packedRects = PackRectangles(sortedPlacements);

            // 結果を適用
            for (int i = 0; i < sortedPlacements.Count; i++)
            {
                var placement = sortedPlacements[i];
                var packedRect = packedRects[i];

                // パッキング結果から位置とスケールを設定
                placement.scale = new Vector2(packedRect.scale, packedRect.scale);
                placement.position = new Vector2(packedRect.x, packedRect.y);
            }

            UpdateUVOverlapCache();
            Repaint();
        }

        /// <summary>
        /// Rectangle Packing（面積比ベースのShelf Packingアルゴリズム）
        /// </summary>
        private List<PackedRect> PackRectangles(List<IslandPlacementData> placements)
        {
            var packedRects = new List<PackedRect>();

            if (placements.Count == 0) return packedRects;

            const float padding = 0.005f; // 最小限のパディング（0.5%）
            const float targetSize = 1.0f; // UV空間は0-1

            // ① 全アイランドのoriginalBounds面積を合計
            float totalOriginalArea = 0f;
            foreach (var placement in placements)
            {
                var bounds = placement.originalBounds;
                float area = Mathf.Max(0.0001f, bounds.width * bounds.height);
                totalOriginalArea += area;
            }

            // ② 利用可能面積を計算（パディング考慮）
            // パディングによる損失を概算：各アイランド周囲 + 棚間
            float paddingLoss = padding * 2 * placements.Count * 0.5f; // 概算
            float availableArea = (targetSize - padding * 2) * (targetSize - padding * 2) - paddingLoss;
            availableArea = Mathf.Max(availableArea, 0.5f); // 最低50%は確保

            // ③ 全体のスケール係数を計算（面積比で全体が収まるように）
            // totalOriginalArea * globalScale^2 = availableArea
            float globalScale = Mathf.Sqrt(availableArea / totalOriginalArea);
            globalScale = Mathf.Min(globalScale, 1f); // 1を超えない

            // ④ 各アイランドのスケールを事前計算
            var scaledSizes = new List<(float width, float height, float scale)>();
            foreach (var placement in placements)
            {
                var bounds = placement.originalBounds;
                float scale = globalScale;
                float scaledWidth = bounds.width * scale;
                float scaledHeight = bounds.height * scale;
                scaledSizes.Add((scaledWidth, scaledHeight, scale));
            }

            // ⑤ 棚詰め方式で配置（スケール固定）
            var shelves = new List<Shelf>();

            for (int i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                var bounds = placement.originalBounds;
                var (scaledWidth, scaledHeight, scale) = scaledSizes[i];

                // 既存の棚で配置可能な場所を探す
                Shelf bestShelf = null;
                float bestX = 0f;

                foreach (var shelf in shelves)
                {
                    float availableWidth = targetSize - shelf.currentX - padding;
                    // この棚の高さに収まり、幅も足りるか確認
                    if (availableWidth >= scaledWidth && shelf.height >= scaledHeight)
                    {
                        bestShelf = shelf;
                        bestX = shelf.currentX + padding;
                        break; // 最初に見つかった棚を使用
                    }
                }

                // 既存の棚に収まらない場合、新しい棚を作成
                if (bestShelf == null)
                {
                    float newShelfY = padding;
                    if (shelves.Count > 0)
                    {
                        var lastShelf = shelves[shelves.Count - 1];
                        newShelfY = lastShelf.y + lastShelf.height + padding;
                    }

                    var newShelf = new Shelf
                    {
                        y = newShelfY,
                        height = scaledHeight,
                        currentX = padding
                    };
                    shelves.Add(newShelf);

                    bestShelf = newShelf;
                    bestX = padding;
                }

                // 配置を確定
                float offsetX = bestX - bounds.x * scale;
                float offsetY = bestShelf.y - bounds.y * scale;

                packedRects.Add(new PackedRect
                {
                    x = offsetX,
                    y = offsetY,
                    width = scaledWidth,
                    height = scaledHeight,
                    scale = scale
                });

                // 棚の現在位置を更新
                bestShelf.currentX = bestX + scaledWidth + padding;
            }

            // ⑥ 枠からはみ出ていれば全体を縮小して収める
            float maxX = 0f;
            float maxY = 0f;
            for (int i = 0; i < packedRects.Count; i++)
            {
                var rect = packedRects[i];
                var bounds = placements[i].originalBounds;
                // 実際の配置位置の右端・上端を計算
                float rightEdge = rect.x + bounds.x * rect.scale + rect.width;
                float topEdge = rect.y + bounds.y * rect.scale + rect.height;
                maxX = Mathf.Max(maxX, rightEdge);
                maxY = Mathf.Max(maxY, topEdge);
            }

            // 枠内に収めるためのスケール係数を計算
            float fitScaleX = (maxX > targetSize - padding) ? (targetSize - padding * 2) / maxX : 1f;
            float fitScaleY = (maxY > targetSize - padding) ? (targetSize - padding * 2) / maxY : 1f;

            // はみ出ている場合のみ縮小を適用（uniform scalingで位置とスケールの整合性を保つ）
            float fitScale = Mathf.Min(fitScaleX, fitScaleY);
            if (fitScale < 1f)
            {
                for (int i = 0; i < packedRects.Count; i++)
                {
                    var rect = packedRects[i];
                    rect.x = rect.x * fitScale + padding * (1f - fitScale);
                    rect.y = rect.y * fitScale + padding * (1f - fitScale);
                    rect.width *= fitScale;
                    rect.height *= fitScale;
                    rect.scale *= fitScale;
                }
            }

            return packedRects;
        }

        /// <summary>
        /// パッキング用の棚構造
        /// </summary>
        private class Shelf
        {
            public float y;           // 棚のY座標
            public float height;      // 棚の高さ
            public float currentX;    // 現在の配置位置X
        }

        /// <summary>
        /// パッキング結果
        /// </summary>
        private class PackedRect
        {
            public float x;
            public float y;
            public float width;
            public float height;
            public float scale;
        }

        private void ResetUVPlacements()
        {
            foreach (var placement in islandPlacements)
            {
                placement.position = Vector2.zero;
                placement.scale = Vector2.one;
            }
            UpdateUVOverlapCache();
            Repaint();
        }

        /// <summary>
        /// 指定したRendererのアイランドをリストの最後に移動（前面に表示）
        /// </summary>
        private void BringRendererIslandsToFront(int rendererIndex)
        {
            var toMove = new List<IslandPlacementData>();
            var remaining = new List<IslandPlacementData>();

            foreach (var placement in islandPlacements)
            {
                if (placement.rendererIndex == rendererIndex)
                {
                    toMove.Add(placement);
                }
                else
                {
                    remaining.Add(placement);
                }
            }

            // 他のアイランドの後にこのRendererのアイランドを追加
            remaining.AddRange(toMove);
            islandPlacements = remaining;
        }

        private bool CanExecuteSetup()
        {
            if (selectedAvatar == null) return false;
            int minRenderers = enableMeshMerge ? 2 : 1;
            if (targetRenderers.Count < minRenderers) return false;
            if (baseMaterial == null) return false;

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null) return false;
            }

            if (enableMeshMerge && hasUVOverlap) return false;
            if (enableMeshMerge && hasUVOutOfBounds) return false;

            return true;
        }

        private string GetValidationMessage()
        {
            if (selectedAvatar == null)
                return CHMLocales.Tr("Window:Validation:SelectAvatar");
            int minRenderers = enableMeshMerge ? 2 : 1;
            if (targetRenderers.Count < minRenderers)
                return enableMeshMerge
                    ? CHMLocales.Tr("Window:Validation:AtLeastTwoRenderers")
                    : CHMLocales.Tr("Window:Validation:AtLeastOneRenderer");
            if (baseMaterial == null)
                return CHMLocales.Tr("Window:Validation:SelectBaseMaterial");

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null)
                    return CHMLocales.Tr("Window:Validation:UnsetRenderer");
            }

            if (enableMeshMerge && hasUVOverlap)
                return CHMLocales.Tr("Window:Validation:UVOverlap");
            if (enableMeshMerge && hasUVOutOfBounds)
                return CHMLocales.Tr("Window:UVSettings:OutOfBounds");

            return string.Empty;
        }

        private void ExecuteSetup()
        {
            if (!CanExecuteSetup())
            {
                EditorUtility.DisplayDialog(
                    CHMLocales.Tr("Window:Title"),
                    GetValidationMessage(),
                    "OK"
                );
                return;
            }

            ChimeraHairMaster chimeraComponent;
            if (loadedComponent != null)
            {
                // 既存コンポーネントの編集として開いた場合は、新規オブジェクトを作らず
                // 確認の上で読み込み元を上書き更新する（従来は常に新規作成され重複していた）
                if (!EditorUtility.DisplayDialog(
                    CHMLocales.Tr("Window:Title"),
                    string.Format(CHMLocales.Tr("Window:Setup:OverwriteConfirmMessage"), loadedComponent.gameObject.name),
                    CHMLocales.Tr("Window:Setup:OverwriteConfirmOk"),
                    CHMLocales.Tr("Window:Setup:OverwriteConfirmCancel")))
                {
                    return;
                }

                Undo.RegisterCompleteObjectUndo(loadedComponent, "Update Chimera Hair Master");
                ApplySettingsToComponent(loadedComponent);
                // Renderer 構成スナップショットを取り直す（index 再マップの誤発火防止）
                loadedComponent.editorRendererIdsSnapshot?.Clear();
                EditorUtility.SetDirty(loadedComponent);
                Selection.activeGameObject = loadedComponent.gameObject;
                chimeraComponent = loadedComponent;
            }
            else
            {
                chimeraComponent = CreateChimeraHairMasterObject();
            }

            if (chimeraComponent != null)
            {
                EditorUtility.DisplayDialog(
                    CHMLocales.Tr("Window:Title"),
                    CHMLocales.Tr("Window:Setup:CompleteMessage"),
                    "OK"
                );
                Close();
            }
        }

        /// <summary>
        /// ウィンドウの現在の設定をコンポーネントへ書き込む（新規作成・上書き共通）
        /// </summary>
        private void ApplySettingsToComponent(ChimeraHairMaster component)
        {
            component.targetRenderers = new List<SkinnedMeshRenderer>(targetRenderers);

            // エントリの参照共有を避けるためディープコピー
            component.materialSelections = new List<MaterialSelectionEntry>();
            foreach (var entry in materialSelections)
            {
                if (entry == null) continue;
                component.materialSelections.Add(new MaterialSelectionEntry(
                    entry.rendererIndex, entry.submeshIndex, entry.isIncluded)
                {
                    meshCutMask = entry.meshCutMask
                });
            }

            component.enableColorTransform = enableColorTransform;
            component.targetColor = targetColor;
            component.colorTransformMode = colorTransformMode;
            // Gradient はクラス参照のためコピーして渡す
            // （ウィンドウ側インスタンスを共有すると、以後のウィンドウ操作が
            // Undo/SetDirty なしでコンポーネントを書き換えてしまう）
            var gradientCopy = new Gradient();
            if (gradientCurve != null)
                gradientCopy.SetKeys(gradientCurve.colorKeys, gradientCurve.alphaKeys);
            component.gradientCurve = gradientCopy;
            component.saturationPreserve = saturationPreserve;
            component.valuePreserve = valuePreserve;
            component.hueShiftAlgorithm = hueShiftAlgorithm;
            component.oklabHueRetain = oklabHueRetain;
            component.oklabSaturationToTarget = oklabSaturationToTarget;
            component.oklabLToTarget = oklabLToTarget;
            component.oklabLDarkEndRatio = oklabLDarkEndRatio;
            component.rgbDeltaIntensity = rgbDeltaIntensity;
            component.rgbDeltaSoftClipZone = rgbDeltaSoftClipZone;
            component.textureResolution = textureResolution;
            component.baseMaterial = baseMaterial;
            component.meshMergeMode = meshMergeMode;
            component.enableMeshMerge = enableMeshMerge;
            component.unifyMatCap = unifyMatCap;

            // アイランド単位のUV配置をコピー
            component.islandPlacements = new List<IslandPlacement>();
            foreach (var island in islandPlacements)
            {
                var bounds = island.originalBounds;
                // ウィンドウでの配置: bounds.xy * scale + position がアトラス上の位置
                float atlasX = bounds.x * island.scale.x + island.position.x;
                float atlasY = bounds.y * island.scale.y + island.position.y;
                float atlasW = bounds.width * island.scale.x;
                float atlasH = bounds.height * island.scale.y;

                component.islandPlacements.Add(new IslandPlacement
                {
                    rendererIndex = island.rendererIndex,
                    submeshIndex = island.submeshIndex,
                    localIslandIndex = island.localIslandIndex,
                    originalBounds = island.originalBounds,
                    atlasPosition = new Vector2(atlasX, atlasY),
                    atlasScale = new Vector2(atlasW, atlasH)
                });
            }

            // 後方互換性のためRenderer単位の配置も保存
            component.uvPlacements = new List<UVIslandPlacement>();
            for (int i = 0; i < targetRenderers.Count; i++)
            {
                var renderer = targetRenderers[i];
                if (renderer == null) continue;
                component.uvPlacements.Add(new UVIslandPlacement(renderer));
            }
        }

        private ChimeraHairMaster CreateChimeraHairMasterObject()
        {
            GameObject chimeraObject = new GameObject("Chimera Hair Master");
            chimeraObject.transform.SetParent(selectedAvatar.transform);
            chimeraObject.transform.localPosition = Vector3.zero;
            chimeraObject.transform.localRotation = Quaternion.identity;
            chimeraObject.transform.localScale = Vector3.one;

            var component = chimeraObject.AddComponent<ChimeraHairMaster>();

            ApplySettingsToComponent(component);

            // Hipsボーンを自動検出してProbe AnchorとRoot Boneに設定
            var hips = FindAvatarHips(selectedAvatar);
            if (hips != null)
            {
                component.probeAnchor = hips;
                component.rootBone = hips;
            }

            Selection.activeGameObject = chimeraObject;

            Undo.RegisterCreatedObjectUndo(chimeraObject, "Create Chimera Hair Master");

            return component;
        }

        /// <summary>
        /// アバターのHipsボーンを検出する
        /// </summary>
        private static Transform FindAvatarHips(GameObject avatar)
        {
            if (avatar == null) return null;

            // Humanoid AnimatorからHipsボーンを取得
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) return hips;
            }

            // フォールバック: ボーン名で再帰検索
            var hipsNames = new[] { "Hips", "Hip", "pelvis" };
            foreach (var boneName in hipsNames)
            {
                var found = FindBoneByName(avatar.transform, boneName);
                if (found != null) return found;
            }

            return null;
        }

        private static Transform FindBoneByName(Transform root, string boneName)
        {
            foreach (Transform child in root)
            {
                if (string.Equals(child.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                    return child;

                var found = FindBoneByName(child, boneName);
                if (found != null) return found;
            }
            return null;
        }

        #endregion
    }
}
