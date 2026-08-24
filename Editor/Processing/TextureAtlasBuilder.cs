using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// テクスチャアトラスを生成するビルダー
    /// </summary>
    public static class TextureAtlasBuilder
    {
        /// <summary>
        /// アクティブビルドターゲットが Android/Quest かどうか。
        /// Android では DXT5/BC5 が非対応のため、アトラス圧縮を ASTC に切り替える必要がある。
        /// </summary>
        private static bool IsAndroidBuildTarget() =>
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;

        /// <summary>
        /// カラーアトラスの圧縮フォーマット（Android は ASTC_6x6、それ以外は DXT5）。
        /// ASTC_6x6 は Quest で有効な標準的フォーマットで、品質とサイズのバランスが良い。
        /// </summary>
        private static TextureFormat GetAtlasColorFormat() =>
            IsAndroidBuildTarget() ? TextureFormat.ASTC_6x6 : TextureFormat.DXT5;

        /// <summary>
        /// ノーマルマップアトラスの圧縮フォーマット（Android は ASTC_6x6、それ以外は BC5）。
        /// </summary>
        private static TextureFormat GetAtlasNormalFormat() =>
            IsAndroidBuildTarget() ? TextureFormat.ASTC_6x6 : TextureFormat.BC5;

        /// <summary>
        /// サブアトラス（AO/ノーマル）の解像度を計算。
        /// アイランド配置は正規化座標なのでメインアトラスと解像度が異なっても正しく配置される
        /// </summary>
        private static int GetSubResolution(int resolution, AtlasSubResolution subResolution)
        {
            int divisor = Mathf.Max((int)subResolution, 1);
            return Mathf.Max(resolution / divisor, 64);
        }

        /// <summary>
        /// AOマップアトラスの圧縮フォーマット（Android は ASTC_6x6、それ以外は DXT1）。
        /// AO はグレースケール（lilToon は R チャンネル参照）でアルファ不要のため、
        /// DXT1 で十分（DXT5 の半分のメモリ）。
        /// </summary>
        private static TextureFormat GetAtlasAOFormat() =>
            IsAndroidBuildTarget() ? TextureFormat.ASTC_6x6 : TextureFormat.DXT1;

        /// <summary>
        /// フラットノーマル色（頂点法線の向きを変更しない）
        /// </summary>
        private static readonly Color FlatNormal = new Color(128f / 255f, 128f / 255f, 1f, 1f);

        /// <summary>
        /// マスク系アトラススロットの定義。
        /// カラーアトラス（_MainTex 系）は色変更・不透明判定など独自ロジックを持つため
        /// このテーブルには乗せず、「カラー系」と「マスク系」の2系統とする
        /// </summary>
        internal class MaskSlotDefinition
        {
            /// <summary>対象テクスチャプロパティ名</summary>
            public string propertyName;

            /// <summary>
            /// テクスチャ未設定アイランドの塗りつぶし色。
            /// 引数はそのアイランドのマテリアル（null の場合は背景初期化用の既定値を返すこと）
            /// </summary>
            public Func<Material, Color> fallbackColor;

            /// <summary>リニアデータか（ノーマルマップのみ true。sRGB 扱いだと法線の向きが狂う）</summary>
            public bool linear;

            /// <summary>圧縮フォーマット（ビルドターゲット依存のため関数）</summary>
            public Func<TextureFormat> format;

            /// <summary>従う解像度設定</summary>
            public Func<ChimeraHairMaster, AtlasSubResolution> resolution;

            /// <summary>
            /// 機能のON/OFFトグルプロパティ名（例 "_UseMatCap"）。
            /// 指定すると生成条件・塗りつぶし色が機能のON/OFFを考慮する。null なら常時有効扱い
            /// </summary>
            public string enableProperty;

            /// <summary>
            /// UVモードプロパティ名（例 "_EmissionMap_UVMode"）。
            /// 指定時、値がUV0以外のマテリアルがあるとスロット全体を放置（生成もクリアもしない）。
            /// プレビューの入力ハッシュにも使われる
            /// </summary>
            public string uvModeProperty;

            /// <summary>
            /// スクロール/回転アニメのプロパティ名（例 "_EmissionMap_ScrollRotate"）。
            /// 指定時、非ゼロのマテリアルがあるとスロット全体を放置。
            /// プレビューの入力ハッシュにも使われる
            /// </summary>
            public string scrollRotateProperty;

            /// <summary>UVモード/スクロールの制約ガードを持つか</summary>
            public bool HasAtlasGuard => uvModeProperty != null || scrollRotateProperty != null;

            /// <summary>
            /// ソースのピクセルを透明置換せずそのまま焼き込むか。
            /// 既定（false、AO/ノーマル用）は透明ピクセル（α≒0）をフォールバック色に置換するが、
            /// エミッション系はアルファ自体が発光強度データのため置換すると
            /// 「アルファで消している」領域が白＝フル発光に化ける。
            /// マットキャップマスクもシェーダは .rgb をそのまま読むため、置換しない方が実挙動に忠実
            /// </summary>
            public bool blitRaw;

            /// <summary>
            /// シェーダがアルファを読まないスロットか（.rgb / .r のみ参照）。
            /// PNG生成時にインポータの alphaSource=None を設定し、
            /// ソース由来のアルファ混入で DXT5（メモリ2倍）に化けるのを防いで DXT1 に固定する
            /// </summary>
            public bool alphaUnused;
        }

        /// <summary>
        /// ビルド時に自動でアトラス化するマスク系スロット（AO/ノーマルのみ）。
        /// マットキャップ/エミッション系は自動適用せず、AdditionalMaskSlots による
        /// アセット生成（ユーザーが previewMaterial に手動割り当て）で扱う
        /// </summary>
        internal static readonly MaskSlotDefinition[] MaskSlots =
        {
            new MaskSlotDefinition
            {
                propertyName = "_ShadowStrengthMask",
                fallbackColor = _ => Color.white, // AOなし = 影なし
                format = GetAtlasAOFormat,
                resolution = c => c.aoAtlasResolution,
            },
            new MaskSlotDefinition
            {
                propertyName = "_BumpMap",
                fallbackColor = _ => FlatNormal,
                linear = true,
                format = GetAtlasNormalFormat,
                resolution = c => c.normalAtlasResolution,
            },
        };

        /// <summary>
        /// 「マスクテクスチャを生成」でPNGアセットとして書き出す追加マスクスロット。
        /// ビルドでは自動適用しない——ユーザーがマテリアル設定（previewMaterial）に
        /// 割り当てたものだけが従来のテクスチャと同様に出力へ乗る。
        ///
        /// 塗りつぶしは白=フル適用（lilToonのマスク無しと同義）、機能OFFの素材は黒/透明=適用しない。
        /// これにより「一部の房だけマットキャップ/発光」も1枚のマスクで再現できる。
        /// - マットキャップマスクは .rgb のみ参照（lilToon）のため黒でよい
        /// - エミッション系はマスクのアルファが発光強度に乗る（emissionColor.a）ため
        ///   OFF島は透明 (0,0,0,0) で塗る
        /// - エミッションはUVモード/スクロールがUV0固定でない場合、生成対象から除外（警告）
        /// </summary>
        internal static readonly MaskSlotDefinition[] AdditionalMaskSlots =
        {
            new MaskSlotDefinition
            {
                propertyName = "_MatCapBlendMask",
                enableProperty = "_UseMatCap",
                fallbackColor = mat => FeatureFallback(mat, "_UseMatCap", Color.black),
                blitRaw = true,
                alphaUnused = true,
            },
            new MaskSlotDefinition
            {
                propertyName = "_MatCap2ndBlendMask",
                enableProperty = "_UseMatCap2nd",
                fallbackColor = mat => FeatureFallback(mat, "_UseMatCap2nd", Color.black),
                blitRaw = true,
                alphaUnused = true,
            },
            new MaskSlotDefinition
            {
                propertyName = "_EmissionBlendMask",
                enableProperty = "_UseEmission",
                fallbackColor = mat => FeatureFallback(mat, "_UseEmission", Color.clear),
                blitRaw = true,
                scrollRotateProperty = "_EmissionBlendMask_ScrollRotate",
            },
            new MaskSlotDefinition
            {
                propertyName = "_Emission2ndBlendMask",
                enableProperty = "_UseEmission2nd",
                fallbackColor = mat => FeatureFallback(mat, "_UseEmission2nd", Color.clear),
                blitRaw = true,
                scrollRotateProperty = "_Emission2ndBlendMask_ScrollRotate",
            },
            new MaskSlotDefinition
            {
                propertyName = "_EmissionMap",
                enableProperty = "_UseEmission",
                fallbackColor = mat => FeatureFallback(mat, "_UseEmission", Color.clear),
                blitRaw = true,
                uvModeProperty = "_EmissionMap_UVMode",
                scrollRotateProperty = "_EmissionMap_ScrollRotate",
            },
            new MaskSlotDefinition
            {
                propertyName = "_Emission2ndMap",
                enableProperty = "_UseEmission2nd",
                fallbackColor = mat => FeatureFallback(mat, "_UseEmission2nd", Color.clear),
                blitRaw = true,
                uvModeProperty = "_Emission2ndMap_UVMode",
                scrollRotateProperty = "_Emission2ndMap_ScrollRotate",
            },
            // 光沢（Reflection）。_SmoothnessTex/_MetallicGlossMap は .r のみ参照のグレースケール。
            // OFF島の黒は smoothness/metallic=0 で「ほぼ無効」だが完全OFFではない——
            // 実質のOFFゲートは _ReflectionColorTex の透明（反射色が消える）
            new MaskSlotDefinition
            {
                propertyName = "_SmoothnessTex",
                enableProperty = "_UseReflection",
                fallbackColor = mat => FeatureFallback(mat, "_UseReflection", Color.black),
                blitRaw = true,
                alphaUnused = true,
            },
            new MaskSlotDefinition
            {
                propertyName = "_MetallicGlossMap",
                enableProperty = "_UseReflection",
                fallbackColor = mat => FeatureFallback(mat, "_UseReflection", Color.black),
                blitRaw = true,
                alphaUnused = true,
            },
            new MaskSlotDefinition
            {
                propertyName = "_ReflectionColorTex",
                enableProperty = "_UseReflection",
                fallbackColor = mat => FeatureFallback(mat, "_UseReflection", Color.clear),
                blitRaw = true,
            },
            // ラメ（Glitter）の色/マスク。UVモード（UV0-3）があるためガード付き。
            // ※ _GlitterShapeTex はラメ粒子空間でサンプリングされるため対象外（再配置すると壊れる）
            new MaskSlotDefinition
            {
                propertyName = "_GlitterColorTex",
                enableProperty = "_UseGlitter",
                fallbackColor = mat => FeatureFallback(mat, "_UseGlitter", Color.clear),
                blitRaw = true,
                uvModeProperty = "_GlitterColorTex_UVMode",
            },
        };

        /// <summary>
        /// マテリアルで機能トグルがONか（プロパティが無い＝非lilToon等は OFF 扱い）
        /// </summary>
        private static bool IsFeatureEnabled(Material mat, string enableProperty)
        {
            return mat != null &&
                   mat.HasProperty(enableProperty) &&
                   mat.GetFloat(enableProperty) != 0f;
        }

        /// <summary>
        /// 機能ONなら白（フル適用）、OFFなら offColor（適用しない）。
        /// mat == null（背景初期化・不明マテリアル）は offColor —— アイランド外の未使用領域を
        /// 白にすると mipmap 縮小時にアイランド縁へ「フル適用」がにじむため、安全側に倒す。
        /// offColor はマスクの使われ方に合わせる:
        /// - マットキャップマスクは .rgb のみ参照されるため黒で十分
        /// - エミッション系はアルファが発光強度（emissionColor.a）に乗るため、
        ///   ブレンドモードに依らず無効化できる透明 (0,0,0,0) を使う
        /// </summary>
        private static Color FeatureFallback(Material mat, string enableProperty, Color offColor)
        {
            if (mat == null) return offColor;
            return IsFeatureEnabled(mat, enableProperty) ? Color.white : offColor;
        }

        /// <summary>
        /// テクスチャのUV参照がUV0かつスクロール/回転アニメ無しか。
        /// lilToon のエミッションは UVMode（0=UV0/1-3=UV1-3/4=Rim）と ScrollRotate を持ち、
        /// UV0 以外やアニメ使用時はアトラス化すると表示が壊れる。
        /// プロパティが存在しない場合は制約なしとして true
        /// </summary>
        internal static bool IsUV0WithoutAnimation(Material mat, MaskSlotDefinition def)
        {
            if (mat == null) return true;
            if (def.uvModeProperty != null &&
                mat.HasProperty(def.uvModeProperty) &&
                mat.GetFloat(def.uvModeProperty) != 0f) return false;
            if (def.scrollRotateProperty != null &&
                mat.HasProperty(def.scrollRotateProperty) &&
                mat.GetVector(def.scrollRotateProperty) != Vector4.zero) return false;
            return true;
        }

        /// <summary>
        /// 不透明とみなすアルファ値のしきい値。
        /// ソースが DXT5 圧縮済みだと不透明ピクセルでも 254 前後に化けるため 255 にしない
        /// </summary>
        private const byte OpaqueAlphaThreshold = 250;

        /// <summary>
        /// アトラスのアイランド領域が実質不透明（全ピクセル α ≥ しきい値）かどうか。
        /// アトラス背景は透明（α=0）で初期化されるため全面走査では常に false になる。
        /// メッシュUVがサンプリングするのはアイランド矩形内のみなので、そこだけを判定対象にする
        /// </summary>
        internal static bool IsEffectivelyOpaqueInIslands(
            Texture2D atlas,
            List<IslandPlacement> islandPlacements)
        {
            int resolution = atlas.width;
            var pixels = atlas.GetPixels32(0);

            foreach (var island in islandPlacements)
            {
                // Blit と同じ矩形計算（BlitIslandToAtlas 参照）
                int startX = Mathf.FloorToInt(island.atlasPosition.x * resolution);
                int startY = Mathf.FloorToInt(island.atlasPosition.y * resolution);
                int width = Mathf.Max(Mathf.FloorToInt(island.atlasScale.x * resolution), 1);
                int height = Mathf.Max(Mathf.FloorToInt(island.atlasScale.y * resolution), 1);

                int endX = Mathf.Min(startX + width, resolution);
                int endY = Mathf.Min(startY + height, resolution);
                startX = Mathf.Max(startX, 0);
                startY = Mathf.Max(startY, 0);

                for (int y = startY; y < endY; y++)
                {
                    int rowOffset = y * resolution;
                    for (int x = startX; x < endX; x++)
                    {
                        if (pixels[rowOffset + x].a < OpaqueAlphaThreshold) return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// カラーアトラスの圧縮フォーマットを決定。
        /// PC ではアイランド領域が実質不透明なら DXT1（DXT5 の半分のメモリ）、それ以外は DXT5。
        /// Android(ASTC_6x6) はアルファ有無で bpp が変わらないため判定不要。
        /// preserveAtlasAlpha 有効時は判定せず常に DXT5（エスケープハッチ）
        /// </summary>
        private static TextureFormat ChooseColorAtlasFormat(
            Texture2D atlas,
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements)
        {
            if (IsAndroidBuildTarget()) return TextureFormat.ASTC_6x6;
            if (component != null && component.preserveAtlasAlpha) return TextureFormat.DXT5;
            return IsEffectivelyOpaqueInIslands(atlas, islandPlacements) ? TextureFormat.DXT1 : TextureFormat.DXT5;
        }

        /// <summary>
        /// アトラスビルド結果
        /// </summary>
        public class AtlasResult
        {
            /// <summary>
            /// 生成されたアトラステクスチャ（プロパティ名 -> テクスチャ）
            /// </summary>
            public Dictionary<string, Texture2D> AtlasTextures { get; set; }
                = new Dictionary<string, Texture2D>();

            /// <summary>
            /// UV変換情報（Renderer -> UVTransform）
            /// </summary>
            public Dictionary<SkinnedMeshRenderer, UVTransform> UVTransforms { get; set; }
                = new Dictionary<SkinnedMeshRenderer, UVTransform>();

            /// <summary>
            /// アイランド単位の配置情報（UV変換時に使用）
            /// </summary>
            public List<IslandPlacement> IslandPlacements { get; set; }
                = new List<IslandPlacement>();

            /// <summary>
            /// アトラス生成をスキップしたため、出力マテリアルで null クリアすべきプロパティ名。
            /// 出力マテリアルは元マテリアルの完全コピーのため、クリアしないと
            /// 元のテクスチャが旧UVのまま残ってしまう
            /// </summary>
            public List<string> ClearedProperties { get; set; }
                = new List<string>();
        }

        /// <summary>
        /// UV変換情報
        /// </summary>
        public class UVTransform
        {
            public Vector2 Offset { get; set; }
            public Vector2 Scale { get; set; }
            public float Rotation { get; set; }

            public UVTransform(Vector2 offset, Vector2 scale, float rotation)
            {
                Offset = offset;
                Scale = scale;
                Rotation = rotation;
            }

            /// <summary>
            /// UV座標を変換
            /// </summary>
            public Vector2 TransformUV(Vector2 uv)
            {
                // 回転を適用
                if (Rotation != 0)
                {
                    float rad = Rotation * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(rad);
                    float sin = Mathf.Sin(rad);
                    Vector2 centered = uv - new Vector2(0.5f, 0.5f);
                    uv = new Vector2(
                        centered.x * cos - centered.y * sin,
                        centered.x * sin + centered.y * cos
                    ) + new Vector2(0.5f, 0.5f);
                }

                // スケールとオフセットを適用
                return uv * Scale + Offset;
            }
        }

        /// <summary>
        /// アトラステクスチャをビルド（アイランド単位）
        /// </summary>
        public static AtlasResult Build(
            ChimeraHairMaster component,
            int resolution,
            Dictionary<Texture2D, Texture2D> processedTextureCache,
            bool isPreview = false,
            Dictionary<(int rendererIndex, int submeshIndex, Texture2D texture), Texture2D> perIslandTextureCache = null)
        {
            var result = new AtlasResult();

            if (component.targetRenderers.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] 対象Rendererがありません");
                return result;
            }

            // アイランド単位の配置情報を取得（なければ自動生成）
            var islandPlacements = GetOrCalculateIslandPlacements(component);

            if (islandPlacements.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] アイランド配置情報がありません");
                return result;
            }

            // 各Renderer用のUV変換情報を生成（MeshUVRemapper用）
            // 各アイランドの変換を集約
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // このRendererにまだ変換情報がなければ作成
                if (!result.UVTransforms.ContainsKey(renderer))
                {
                    // 注: アイランド単位の変換はMeshUVRemapperで別途処理
                    result.UVTransforms[renderer] = new UVTransform(
                        Vector2.zero,
                        Vector2.one,
                        0
                    );
                }
            }

            // テクスチャスロットごとにアトラスを生成
            foreach (var slot in component.colorChangeTargets)
            {
                var atlasTexture = BuildAtlasForSlotByIslands(
                    component,
                    slot.propertyName,
                    resolution,
                    islandPlacements,
                    processedTextureCache,
                    isPreview,
                    perIslandTextureCache
                );

                if (atlasTexture != null)
                {
                    result.AtlasTextures[slot.propertyName] = atlasTexture;
                }
            }

            // メインテクスチャがない場合は追加
            if (!result.AtlasTextures.ContainsKey("_MainTex"))
            {
                var mainAtlas = BuildAtlasForSlotByIslands(
                    component,
                    "_MainTex",
                    resolution,
                    islandPlacements,
                    processedTextureCache,
                    isPreview,
                    perIslandTextureCache
                );

                if (mainAtlas != null)
                {
                    result.AtlasTextures["_MainTex"] = mainAtlas;
                }
            }

            // アイランド配置情報を結果に保存（UV変換時に使用）
            result.IslandPlacements = islandPlacements;

            // マスク系アトラス（AO/ノーマル）をスロットテーブルに従って生成。
            // ソースが1枚も無い場合は全面フォールバック色のアトラスになるだけなので生成せず、
            // 出力マテリアル側でプロパティをクリアさせる（メモリ節約）。
            // マットキャップ/エミッション系はここでは扱わない——BuildAdditionalMaskTextures で
            // アセット生成し、ユーザーが previewMaterial に割り当てたものがそのまま出力に乗る
            foreach (var def in MaskSlots)
            {
                if (AnyIslandHasTexture(component, islandPlacements, def.propertyName))
                {
                    int maskResolution = GetSubResolution(resolution, def.resolution(component));
                    var maskAtlas = BuildMaskAtlas(component, def, maskResolution, islandPlacements, isPreview);
                    if (maskAtlas != null)
                    {
                        result.AtlasTextures[def.propertyName] = maskAtlas;
                    }
                }
                else
                {
                    result.ClearedProperties.Add(def.propertyName);
                }
            }

            return result;
        }

        /// <summary>
        /// アイランド単位の配置情報を取得または自動計算
        /// </summary>
        private static List<IslandPlacement> GetOrCalculateIslandPlacements(ChimeraHairMaster component)
        {
            // コンポーネントにアイランド配置がある場合はそれを使用（統合対象のみフィルタリング）
            if (component.islandPlacements != null && component.islandPlacements.Count > 0)
            {
                var filtered = new List<IslandPlacement>();
                foreach (var placement in component.islandPlacements)
                {
                    // 統合対象のサブメッシュのみ含める
                    if (component.IsSubmeshIncluded(placement.rendererIndex, placement.submeshIndex))
                    {
                        filtered.Add(placement);
                    }
                }
                return filtered;
            }

            // 自動配置を計算（UVIslandDetectorを使用）
            return CalculateAutoIslandLayout(component);
        }

        /// <summary>
        /// アイランド単位の自動レイアウトを計算
        /// </summary>
        private static List<IslandPlacement> CalculateAutoIslandLayout(ChimeraHairMaster component)
        {
            var placements = new List<IslandPlacement>();
            var allIslands = new List<(int rendererIndex, int submeshIndex, int localIndex, Rect bounds)>();

            // 全Rendererのサブメッシュごとにアイランドを検出（統合対象のみ）
            for (int i = 0; i < component.targetRenderers.Count; i++)
            {
                var renderer = component.targetRenderers[i];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // サブメッシュごとにアイランドを検出（テクスチャ解像度に基づく最小パディング付き）
                var texResolutions = UVIslandDetector.GetSubmeshTextureResolutions(renderer);
                var islandBoundsPerSubmesh = UVIslandDetector.DetectIslandBoundsPerSubmesh(renderer.sharedMesh, 0.05f, texResolutions);
                for (int submeshIdx = 0; submeshIdx < islandBoundsPerSubmesh.Count; submeshIdx++)
                {
                    // 統合対象外のサブメッシュはスキップ
                    if (!component.IsSubmeshIncluded(i, submeshIdx))
                        continue;

                    var submeshIslands = islandBoundsPerSubmesh[submeshIdx];
                    for (int localIdx = 0; localIdx < submeshIslands.Count; localIdx++)
                    {
                        allIslands.Add((i, submeshIdx, localIdx, submeshIslands[localIdx]));
                    }
                }
            }

            int count = allIslands.Count;
            if (count == 0) return placements;

            // グリッドレイアウトを計算
            int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt((float)count / cols);

            float cellWidth = 1f / cols;
            float cellHeight = 1f / rows;

            for (int i = 0; i < count; i++)
            {
                var (rendererIndex, submeshIndex, localIndex, bounds) = allIslands[i];

                int col = i % cols;
                int row = i / cols;

                // セル内に収まるスケールを計算
                float scaleX = (cellWidth * 0.9f) / bounds.width;
                float scaleY = (cellHeight * 0.9f) / bounds.height;
                float uniformScale = Mathf.Min(scaleX, scaleY, 1f);

                float atlasW = bounds.width * uniformScale;
                float atlasH = bounds.height * uniformScale;

                // セルの中央に配置
                float atlasX = col * cellWidth + (cellWidth - atlasW) * 0.5f;
                float atlasY = row * cellHeight + (cellHeight - atlasH) * 0.5f;

                placements.Add(new IslandPlacement
                {
                    rendererIndex = rendererIndex,
                    submeshIndex = submeshIndex,
                    localIslandIndex = localIndex,
                    originalBounds = bounds,
                    atlasPosition = new Vector2(atlasX, atlasY),
                    atlasScale = new Vector2(atlasW, atlasH)
                });
            }

            return placements;
        }

        /// <summary>
        /// 特定のスロット用にアトラスを生成（アイランド単位）
        /// UV配置プレビューと同じ結果を再現
        /// </summary>
        private static Texture2D BuildAtlasForSlotByIslands(
            ChimeraHairMaster component,
            string propertyName,
            int resolution,
            List<IslandPlacement> islandPlacements,
            Dictionary<Texture2D, Texture2D> processedTextureCache,
            bool isPreview = false,
            Dictionary<(int rendererIndex, int submeshIndex, Texture2D texture), Texture2D> perIslandTextureCache = null)
        {
            // ピクセルバッファを作成（透明で初期化）
            var pixels = new Color[resolution * resolution];
            // Color のデフォルト値は (0,0,0,0) = Color.clear なので初期化不要

            bool hasAnyTexture = false;
            Texture2D representativeSource = null; // アトラスへ継承するサンプラー設定の代表ソース（最初の実ソース）

            // 各アイランドを配置（バッファに直接書き込み）
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // サブメッシュに対応するマテリアルからテクスチャを取得（nullの場合は白フォールバック）
                Texture2D sourceTexture = GetTextureFromRenderer(renderer, propertyName, island.submeshIndex);
                if (representativeSource == null && sourceTexture != null) representativeSource = sourceTexture;
                if (sourceTexture == null) sourceTexture = GetWhiteFallbackTexture();

                // 処理済みテクスチャがあればそれを使用
                // per-island（renderer×submesh 固有: blur/sharp・色マスク・strand 等）を優先し、
                // 無ければ共有キャッシュ（テクスチャ単位）を参照する
                if (perIslandTextureCache != null &&
                    perIslandTextureCache.TryGetValue((island.rendererIndex, island.submeshIndex, sourceTexture), out var perIslandTexture))
                {
                    sourceTexture = perIslandTexture;
                }
                else if (processedTextureCache != null &&
                    processedTextureCache.TryGetValue(sourceTexture, out var processedTexture))
                {
                    sourceTexture = processedTexture;
                }

                // アイランドのUV境界（originalBounds）からテクスチャをクロップして配置
                BlitIslandToAtlas(
                    pixels,
                    sourceTexture,
                    island.originalBounds,    // ソーステクスチャ内のUV範囲
                    island.atlasPosition,     // アトラス上の配置位置
                    island.atlasScale,        // アトラス上のサイズ
                    resolution
                );

                hasAnyTexture = true;
            }

            if (!hasAnyTexture)
            {
                return null;
            }

            // アトラステクスチャを作成してバッファを一括反映
            var atlas = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            atlas.name = $"Atlas_{propertyName}";
            atlas.SetPixels(pixels);

            // 元テクスチャのサンプラー設定（filterMode/aniso/wrapMode）を代表ソースからアトラスへ継承
            if (representativeSource != null) ColorProcessor.CopyTextureSettings(representativeSource, atlas);

            // MipMapブリード防止のためエッジをダイレーション
            // （プレビューでもUV島境界の継ぎ目を防ぐため実行。圧縮/ミップストリーミングはビルド時のみ）
            DilateAtlas(atlas);

            atlas.Apply(true);

            if (!isPreview)
            {
                // テクスチャ圧縮（ビルド時のみ）。実質不透明なら DXT1 でメモリ半減
                EditorUtility.CompressTexture(atlas, ChooseColorAtlasFormat(atlas, component, islandPlacements), TextureCompressionQuality.Best);
                ShaderUtils.EnableMipStreaming(atlas);
            }

            return atlas;
        }

        /// <summary>
        /// アイランドをアトラスに配置
        /// originalBoundsで指定されたUV範囲をソーステクスチャから切り出し、
        /// atlasPosition/atlasScaleで指定された位置・サイズにマッピング
        /// </summary>
        private static void BlitIslandToAtlas(
            Color[] pixels,
            Texture2D source,
            Rect originalBounds,
            Vector2 atlasPosition,
            Vector2 atlasScale,
            int resolution)
        {
            // 読み取り可能なテクスチャを取得
            Texture2D readable = GetReadableTexture(source);

            // アトラス上の配置範囲（ピクセル）
            int startX = Mathf.FloorToInt(atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(atlasScale.y * resolution), 1);

            // ピクセルをコピー（配列に直接書き込み）
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    float u = originalBounds.x + normalizedX * originalBounds.width;
                    float v = originalBounds.y + normalizedY * originalBounds.height;

                    Color pixel = SampleTexture(readable, u, v);

                    if (pixel.a > 0.01f)
                    {
                        pixels[atlasY * resolution + atlasX] = pixel;
                    }
                }
            }

            // 一時テクスチャをクリーンアップ
            if (readable != source)
            {
                Object.DestroyImmediate(readable);
            }
        }

        private static Texture2D _whiteFallbackTexture;

        /// <summary>
        /// 白のフォールバックテクスチャ（256x256）を取得
        /// テクスチャが設定されていないマテリアル用
        /// </summary>
        private static Texture2D GetWhiteFallbackTexture()
        {
            if (_whiteFallbackTexture == null)
            {
                _whiteFallbackTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                var pixels = new Color[256 * 256];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = Color.white;
                _whiteFallbackTexture.SetPixels(pixels);
                _whiteFallbackTexture.Apply();
                _whiteFallbackTexture.name = "WhiteFallback";
            }
            return _whiteFallbackTexture;
        }

        /// <summary>
        /// いずれかのアイランドのソースに指定プロパティのテクスチャが存在するか。
        /// アトラス生成時の Blit ループと同じ取得経路（GetTextureFromRenderer + submeshIndex）で
        /// 判定するため、false ⇔ 全アイランドがフォールバック色で塗られる、が保証される
        /// </summary>
        internal static bool AnyIslandHasTexture(
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements,
            string propertyName)
        {
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                if (GetTextureFromRenderer(renderer, propertyName, island.submeshIndex) != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// いずれかのアイランドのマテリアルが条件を満たすか
        /// （AnyIslandHasTexture と同じ走査規則）
        /// </summary>
        private static bool AnyIslandMaterial(
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements,
            Func<Material, bool> predicate)
        {
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                var mat = GetMaterialFromRenderer(renderer, island.submeshIndex);
                if (mat != null && predicate(mat))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// スロットのマスクを再配置生成してよいか（EmissionMap の UVモード/スクロールガード）。
        /// 機能ONのアイランドのいずれかで制約に引っかかる場合、スロット全体を対象外にする
        /// （UV0以外やアニメ使用のテクスチャは再配置しても正しく表示できないため）
        /// </summary>
        private static bool CanAtlasAdditionalSlot(
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements,
            MaskSlotDefinition def)
        {
            if (!def.HasAtlasGuard) return true;

            bool ok = !AnyIslandMaterial(component, islandPlacements,
                m => IsFeatureEnabled(m, def.enableProperty) && !IsUV0WithoutAnimation(m, def));

            if (!ok)
            {
                Debug.LogWarning($"[ChimeraHairMaster] {def.propertyName} はUVモードがUV0以外、" +
                    "またはスクロール/回転アニメ使用のため生成対象から除外しました");
            }
            return ok;
        }

        /// <summary>
        /// 機能ONの島に実テクスチャがあるか。
        /// 機能OFFの島のテクスチャは焼き込まれない（OFF色で塗られる）ため数えない
        /// </summary>
        private static bool AnyEnabledIslandHasTexture(
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements,
            MaskSlotDefinition def)
        {
            return AnyIslandMaterial(component, islandPlacements, m =>
                (def.enableProperty == null || IsFeatureEnabled(m, def.enableProperty)) &&
                m.HasProperty(def.propertyName) &&
                m.GetTexture(def.propertyName) is Texture2D tex && tex != null);
        }

        /// <summary>
        /// 追加マスク（マットキャップ/エミッション）のテクスチャを生成すべきか。
        /// 機能ONの島に実テクスチャがあるか、島の間で機能ON/OFFが混在している場合に生成する
        /// （混在時はテクスチャが無くても白/黒の塗り分けマスクとして意味がある）。
        /// 全島OFF・全島ONでテクスチャ無しの場合は生成不要（マスク無し=フル適用で足りる）
        /// </summary>
        internal static bool ShouldGenerateAdditionalMask(
            ChimeraHairMaster component,
            List<IslandPlacement> islandPlacements,
            MaskSlotDefinition def)
        {
            bool anyIslandOn = AnyIslandMaterial(component, islandPlacements,
                m => IsFeatureEnabled(m, def.enableProperty));
            if (!anyIslandOn) return false;

            if (AnyEnabledIslandHasTexture(component, islandPlacements, def)) return true;

            // テクスチャが無くても、ON/OFFが混在していれば塗り分けマスクが必要
            return AnyIslandMaterial(component, islandPlacements,
                m => !IsFeatureEnabled(m, def.enableProperty));
        }

        /// <summary>
        /// 追加マスク（マットキャップ/エミッション）を統合後UVに再配置した非圧縮テクスチャとして生成。
        /// PNG書き出し用で、ビルドでは自動適用しない。
        /// 生成対象が無いスロット・UVモード等の制約に引っかかるスロットは含まれない
        /// </summary>
        internal static List<(MaskSlotDefinition def, Texture2D texture)> BuildAdditionalMaskTextures(
            ChimeraHairMaster component,
            int resolution)
        {
            var results = new List<(MaskSlotDefinition, Texture2D)>();

            var islandPlacements = GetOrCalculateIslandPlacements(component);
            if (islandPlacements.Count == 0)
            {
                Debug.LogWarning("[ChimeraHairMaster] アイランド配置情報がありません");
                return results;
            }

            foreach (var def in AdditionalMaskSlots)
            {
                if (!CanAtlasAdditionalSlot(component, islandPlacements, def)) continue;
                if (!ShouldGenerateAdditionalMask(component, islandPlacements, def)) continue;

                // 非圧縮で生成（isPreview: true）し、PNG化は呼び出し側で行う
                var texture = BuildMaskAtlas(component, def, resolution, islandPlacements, isPreview: true);
                if (texture != null)
                {
                    results.Add((def, texture));
                }
            }
            return results;
        }

        /// <summary>
        /// 追加マスク生成の入力ハッシュ（UV配置・各島のマスクテクスチャ・機能ON/OFF・UVモード）。
        /// 生成時に保存しておき、現在値と食い違ったら「再生成が必要」の警告に使う
        /// </summary>
        internal static int ComputeAdditionalMaskInputHash(ChimeraHairMaster component)
        {
            unchecked
            {
                int hash = 17;
                var islandPlacements = GetOrCalculateIslandPlacements(component);
                foreach (var island in islandPlacements)
                {
                    hash = hash * 31 + island.rendererIndex;
                    hash = hash * 31 + island.submeshIndex;
                    hash = hash * 31 + island.originalBounds.GetHashCode();
                    hash = hash * 31 + island.atlasPosition.GetHashCode();
                    hash = hash * 31 + island.atlasScale.GetHashCode();

                    if (island.rendererIndex >= component.targetRenderers.Count) continue;
                    var renderer = component.targetRenderers[island.rendererIndex];
                    if (renderer == null) continue;
                    var mat = GetMaterialFromRenderer(renderer, island.submeshIndex);
                    if (mat == null) continue;

                    foreach (var def in AdditionalMaskSlots)
                    {
                        if (mat.HasProperty(def.propertyName))
                        {
                            var tex = mat.GetTexture(def.propertyName);
                            hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                        }
                        if (mat.HasProperty(def.enableProperty))
                        {
                            hash = hash * 31 + mat.GetFloat(def.enableProperty).GetHashCode();
                        }
                        if (def.uvModeProperty != null && mat.HasProperty(def.uvModeProperty))
                        {
                            hash = hash * 31 + mat.GetFloat(def.uvModeProperty).GetHashCode();
                        }
                        if (def.scrollRotateProperty != null && mat.HasProperty(def.scrollRotateProperty))
                        {
                            hash = hash * 31 + mat.GetVector(def.scrollRotateProperty).GetHashCode();
                        }
                    }
                }
                return hash;
            }
        }

        /// <summary>
        /// 統合対象の素材で使われているマットキャップ画像（_MatCapTex / _MatCap2ndTex）を収集。
        /// マットキャップ画像は視線ベースサンプリングのため再配置は不要・不可能で、
        /// ユーザーがどれを使うか選んで previewMaterial に割り当てるための一覧
        /// </summary>
        internal static List<(string propertyName, Texture2D texture)> CollectMatCapTextures(
            ChimeraHairMaster component)
        {
            var results = new List<(string, Texture2D)>();
            var seen = new HashSet<Texture2D>();
            string[] matCapProps = { "_MatCapTex", "_MatCap2ndTex" };

            var islandPlacements = GetOrCalculateIslandPlacements(component);
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;
                var mat = GetMaterialFromRenderer(renderer, island.submeshIndex);
                if (mat == null) continue;

                foreach (var prop in matCapProps)
                {
                    if (!mat.HasProperty(prop)) continue;
                    if (mat.GetTexture(prop) is Texture2D tex && tex != null && seen.Add(tex))
                    {
                        results.Add((prop, tex));
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Rendererからテクスチャを取得（全マテリアルから探す）
        /// </summary>
        private static Texture2D GetTextureFromRenderer(SkinnedMeshRenderer renderer, string propertyName)
        {
            return GetTextureFromRenderer(renderer, propertyName, -1);
        }

        /// <summary>
        /// Rendererからテクスチャを取得（サブメッシュ指定対応）
        /// </summary>
        /// <param name="renderer">対象のRenderer</param>
        /// <param name="propertyName">テクスチャプロパティ名</param>
        /// <param name="submeshIndex">サブメッシュインデックス（-1の場合は全マテリアルから探す）</param>
        private static Texture2D GetTextureFromRenderer(SkinnedMeshRenderer renderer, string propertyName, int submeshIndex)
        {
            var materials = renderer.sharedMaterials;

            // サブメッシュ指定がある場合、対応するマテリアルのみから取得（フォールバックなし）
            if (submeshIndex >= 0 && submeshIndex < materials.Length)
            {
                var mat = materials[submeshIndex];
                if (mat != null && mat.HasProperty(propertyName))
                {
                    var tex = mat.GetTexture(propertyName) as Texture2D;
                    if (tex != null) return tex;
                }
                // サブメッシュ指定時は他のマテリアルにフォールバックしない
                return null;
            }

            // submeshIndex == -1 の場合（後方互換）：全マテリアルから探す
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                if (!mat.HasProperty(propertyName)) continue;

                var tex = mat.GetTexture(propertyName) as Texture2D;
                if (tex != null) return tex;
            }
            return null;
        }

        /// <summary>
        /// マスク系アトラス（AO/ノーマル等）をスロット定義に従って生成。
        /// テクスチャ未設定のアイランドは定義のフォールバック色で塗りつぶす。
        ///
        /// ノーマルマップは linear:true で作成する。sRGB 扱いだとサンプル時に
        /// 誤ってデコードされ、法線の向きが狂う。
        /// （PC ビルドは BC5 に sRGB バリアントが無いため無影響。
        ///   プレビューの非圧縮表示と Android の ASTC でのみ効く）
        /// </summary>
        private static Texture2D BuildMaskAtlas(
            ChimeraHairMaster component,
            MaskSlotDefinition def,
            int resolution,
            List<IslandPlacement> islandPlacements,
            bool isPreview = false)
        {
            // ピクセルバッファを作成（マテリアル非依存の既定フォールバック色で初期化）
            Color background = def.fallbackColor(null);
            var pixels = new Color[resolution * resolution];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = background;
            }

            // 各アイランドを配置（バッファに直接書き込み）
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // サブメッシュに対応するマテリアルからテクスチャを取得
                Texture2D sourceTexture = GetTextureFromRenderer(renderer, def.propertyName, island.submeshIndex);
                var islandMaterial = GetMaterialFromRenderer(renderer, island.submeshIndex);
                Color fallback = def.fallbackColor(islandMaterial);

                // 機能OFFの島は、テクスチャが割り当てられていても適用しない
                // （割り当てたままトグルOFFにしている素材のマスクが焼かれるのを防ぐ）
                if (def.enableProperty != null && !IsFeatureEnabled(islandMaterial, def.enableProperty))
                {
                    sourceTexture = null;
                }

                if (sourceTexture != null)
                {
                    BlitIslandToAtlasWithFallback(
                        pixels,
                        sourceTexture,
                        island.originalBounds,
                        island.atlasPosition,
                        island.atlasScale,
                        resolution,
                        fallback,
                        replaceTransparent: !def.blitRaw
                    );
                }
                else
                {
                    // テクスチャが設定されていない場合はフォールバック色で塗りつぶし
                    FillIslandWithColor(pixels, island.atlasPosition, island.atlasScale, resolution, fallback);
                }
            }

            // アトラステクスチャを作成してバッファを一括反映
            var atlas = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true, def.linear);
            atlas.name = "Atlas" + def.propertyName;
            atlas.SetPixels(pixels);
            atlas.Apply(true);

            // テクスチャ圧縮（ビルド時のみ）。
            // AdditionalMaskSlots は format 未定義（常に非圧縮生成→PNG化）なので対象外
            if (!isPreview && def.format != null)
            {
                EditorUtility.CompressTexture(atlas, def.format(), TextureCompressionQuality.Best);
                ShaderUtils.EnableMipStreaming(atlas);
            }

            return atlas;
        }

        /// <summary>
        /// Rendererからサブメッシュに対応するマテリアルを取得
        /// （GetTextureFromRenderer のサブメッシュ指定時と同じ解決規則）
        /// </summary>
        private static Material GetMaterialFromRenderer(SkinnedMeshRenderer renderer, int submeshIndex)
        {
            var materials = renderer.sharedMaterials;
            if (submeshIndex >= 0 && submeshIndex < materials.Length)
            {
                return materials[submeshIndex];
            }
            return null;
        }

        /// <summary>
        /// アイランドを指定した色で塗りつぶす
        /// </summary>
        private static void FillIslandWithColor(
            Color[] pixels,
            Vector2 atlasPosition,
            Vector2 atlasScale,
            int resolution,
            Color fillColor)
        {
            int startX = Mathf.FloorToInt(atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(atlasScale.y * resolution), 1);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX >= 0 && atlasX < resolution &&
                        atlasY >= 0 && atlasY < resolution)
                    {
                        pixels[atlasY * resolution + atlasX] = fillColor;
                    }
                }
            }
        }

        /// <summary>
        /// アイランドをアトラスに配置（フォールバック色付き）
        /// replaceTransparent が true の場合、透明ピクセルはフォールバック色で置き換える
        /// （エミッション系のようにアルファ自体がデータのスロットでは false にして
        /// ソースのピクセルをそのまま焼き込むこと）
        /// </summary>
        private static void BlitIslandToAtlasWithFallback(
            Color[] pixels,
            Texture2D source,
            Rect originalBounds,
            Vector2 atlasPosition,
            Vector2 atlasScale,
            int resolution,
            Color fallbackColor,
            bool replaceTransparent = true)
        {
            Texture2D readable = GetReadableTexture(source);

            int startX = Mathf.FloorToInt(atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(atlasScale.y * resolution), 1);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    float u = originalBounds.x + normalizedX * originalBounds.width;
                    float v = originalBounds.y + normalizedY * originalBounds.height;

                    Color pixel = SampleTexture(readable, u, v);

                    int idx = atlasY * resolution + atlasX;
                    if (!replaceTransparent || pixel.a > 0.01f)
                    {
                        pixels[idx] = pixel;
                    }
                    else
                    {
                        pixels[idx] = fallbackColor;
                    }
                }
            }

            if (readable != source)
            {
                Object.DestroyImmediate(readable);
            }
        }

        /// <summary>
        /// アトラステクスチャにダイレーション（エッジ拡張）を適用
        /// 透明ピクセルに隣接する不透明ピクセルのRGBをコピーし、alphaは0のままにする。
        /// これによりMipMap生成時にエッジが黒と平均化されることを防ぐ。
        /// </summary>
        /// <param name="atlas">対象のアトラステクスチャ</param>
        /// <param name="iterations">拡張回数（ピクセル数）</param>
        private static void DilateAtlas(Texture2D atlas, int iterations = 4)
        {
            int w = atlas.width;
            int h = atlas.height;
            var pixels = atlas.GetPixels();

            for (int iter = 0; iter < iterations; iter++)
            {
                var dst = (Color[])pixels.Clone();
                bool anyChanged = false;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (pixels[idx].a > 0.01f) continue; // 既に不透明

                        // 8近傍の不透明ピクセルからRGBを収集
                        float sumR = 0, sumG = 0, sumB = 0;
                        int count = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;

                                int nIdx = ny * w + nx;
                                if (pixels[nIdx].a > 0.01f)
                                {
                                    sumR += pixels[nIdx].r;
                                    sumG += pixels[nIdx].g;
                                    sumB += pixels[nIdx].b;
                                    count++;
                                }
                            }
                        }

                        if (count > 0)
                        {
                            // RGBは隣接ピクセルの平均、alphaは0のまま
                            // GPU側のMipMap生成でRGBが黒方向にブレンドされるのを防ぐ
                            dst[idx] = new Color(sumR / count, sumG / count, sumB / count, 0f);
                            anyChanged = true;
                        }
                    }
                }

                pixels = dst;
                if (!anyChanged) break;
            }

            atlas.SetPixels(pixels);
        }

        /// <summary>
        /// テクスチャをサンプリング（バイリニアフィルタリング）
        /// </summary>
        private static Color SampleTexture(Texture2D texture, float u, float v)
        {
            // UV範囲外は透明を返す（クランプではなく）
            if (u < 0 || u > 1 || v < 0 || v > 1)
            {
                return Color.clear;
            }

            // ピクセル座標を計算
            float x = u * (texture.width - 1);
            float y = v * (texture.height - 1);

            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(x0 + 1, texture.width - 1);
            int y1 = Mathf.Min(y0 + 1, texture.height - 1);

            float fx = x - x0;
            float fy = y - y0;

            // バイリニア補間
            Color c00 = texture.GetPixel(x0, y0);
            Color c10 = texture.GetPixel(x1, y0);
            Color c01 = texture.GetPixel(x0, y1);
            Color c11 = texture.GetPixel(x1, y1);

            Color c0 = Color.Lerp(c00, c10, fx);
            Color c1 = Color.Lerp(c01, c11, fx);

            return Color.Lerp(c0, c1, fy);
        }

        /// <summary>
        /// 読み取り可能なテクスチャを取得
        /// 圧縮テクスチャの場合は元画像ファイルから非圧縮版を読み込み、DXTアーティファクトを回避する
        /// </summary>
        private static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable) return source;

            // 圧縮テクスチャの場合、元画像ファイルから非圧縮版を読み込む
            var uncompressed = TextureUtils.LoadUncompressed(source);
            if (uncompressed != null) return uncompressed;

            // フォールバック: RenderTextureを経由してコピー
            RenderTexture tmp = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            );

            Graphics.Blit(source, tmp);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return readable;
        }
    }
}
