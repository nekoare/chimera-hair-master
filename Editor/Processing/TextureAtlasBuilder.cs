using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

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

            // AOマップ（_ShadowStrengthMask）のアトラスを生成
            var aoAtlas = BuildAOMapAtlas(component, resolution, islandPlacements, isPreview);
            if (aoAtlas != null)
            {
                result.AtlasTextures["_ShadowStrengthMask"] = aoAtlas;
            }

            // ノーマルマップ（_BumpMap）のアトラスを生成
            var normalAtlas = BuildNormalMapAtlas(component, resolution, islandPlacements, isPreview);
            if (normalAtlas != null)
            {
                result.AtlasTextures["_BumpMap"] = normalAtlas;
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
        /// 後方互換性のため残す
        /// </summary>
        private static List<UVIslandPlacement> GetOrCalculatePlacements(ChimeraHairMaster component)
        {
            if (component.uvPlacements != null && component.uvPlacements.Count > 0)
            {
                bool hasValidPlacement = false;
                foreach (var p in component.uvPlacements)
                {
                    if (p.renderer != null && (p.scale.x > 0 || p.scale.y > 0))
                    {
                        hasValidPlacement = true;
                        break;
                    }
                }

                if (hasValidPlacement)
                {
                    return component.uvPlacements;
                }
            }

            return CalculateAutoLayout(component.targetRenderers);
        }

        /// <summary>
        /// 自動レイアウトを計算（後方互換性のため残す）
        /// </summary>
        private static List<UVIslandPlacement> CalculateAutoLayout(List<SkinnedMeshRenderer> renderers)
        {
            var placements = new List<UVIslandPlacement>();
            int count = renderers.Count;

            if (count == 0) return placements;

            int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt((float)count / cols);

            float cellWidth = 1f / cols;
            float cellHeight = 1f / rows;

            for (int i = 0; i < count; i++)
            {
                if (renderers[i] == null) continue;

                int col = i % cols;
                int row = i / cols;

                var placement = new UVIslandPlacement(renderers[i])
                {
                    position = new Vector2(col * cellWidth, row * cellHeight),
                    scale = new Vector2(cellWidth, cellHeight),
                    rotation = 0
                };

                placements.Add(placement);
            }

            return placements;
        }

        /// <summary>
        /// 特定のスロット用にアトラスを生成
        /// メッシュの実際のUV範囲を考慮してテクスチャを配置
        /// </summary>
        private static Texture2D BuildAtlasForSlot(
            ChimeraHairMaster component,
            string propertyName,
            int resolution,
            List<UVIslandPlacement> placements,
            Dictionary<Texture2D, Texture2D> processedTextureCache)
        {
            // アトラステクスチャを作成
            var atlas = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            atlas.name = $"Atlas_{propertyName}";

            // 背景を透明で初期化
            var clearPixels = new Color[resolution * resolution];
            for (int i = 0; i < clearPixels.Length; i++)
            {
                clearPixels[i] = Color.clear;
            }
            atlas.SetPixels(clearPixels);

            bool hasAnyTexture = false;

            // 各Rendererのテクスチャを配置
            foreach (var placement in placements)
            {
                if (placement.renderer == null) continue;

                // マテリアルからテクスチャを取得（nullの場合は白フォールバック）
                Texture2D sourceTexture = GetTextureFromRenderer(placement.renderer, propertyName);
                if (sourceTexture == null) sourceTexture = GetWhiteFallbackTexture();

                // 処理済みテクスチャがあればそれを使用
                if (processedTextureCache != null &&
                    processedTextureCache.TryGetValue(sourceTexture, out var processedTexture))
                {
                    sourceTexture = processedTexture;
                }

                // メッシュのUV境界を取得
                Rect uvBounds = new Rect(0, 0, 1, 1);
                if (placement.renderer.sharedMesh != null)
                {
                    uvBounds = MeshUVRemapper.CalculateUVBounds(placement.renderer.sharedMesh);
                }

                // テクスチャをアトラスに配置（UV境界を考慮）
                BlitTextureToAtlasWithUVBounds(
                    atlas,
                    sourceTexture,
                    placement.position,
                    placement.scale,
                    placement.rotation,
                    uvBounds,
                    resolution
                );

                hasAnyTexture = true;
            }

            if (!hasAnyTexture)
            {
                Object.DestroyImmediate(atlas);
                return null;
            }

            // MipMapブリード防止のためエッジをダイレーション
            DilateAtlas(atlas);

            atlas.Apply(true);

            // テクスチャ圧縮（DXT5 = Normal Quality相当）
            EditorUtility.CompressTexture(atlas, GetAtlasColorFormat(), TextureCompressionQuality.Best);

            // VRChat向けにミップストリーミングを有効化
            ShaderUtils.EnableMipStreaming(atlas);

            return atlas;
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
                // テクスチャ圧縮（DXT5 = Normal Quality相当）（ビルド時のみ）
                EditorUtility.CompressTexture(atlas, GetAtlasColorFormat(), TextureCompressionQuality.Best);
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
        /// テクスチャをアトラスに配置（UV境界を考慮）
        /// メッシュの実際のUV範囲のみを切り出して配置
        /// </summary>
        private static void BlitTextureToAtlasWithUVBounds(
            Texture2D atlas,
            Texture2D source,
            Vector2 position,
            Vector2 scale,
            float rotation,
            Rect uvBounds,
            int resolution)
        {
            // 読み取り可能なテクスチャを取得
            Texture2D readable = GetReadableTexture(source);

            // 配置範囲を計算（アトラス上の位置）
            int startX = Mathf.FloorToInt(position.x * resolution);
            int startY = Mathf.FloorToInt(position.y * resolution);
            int width = Mathf.FloorToInt(scale.x * resolution);
            int height = Mathf.FloorToInt(scale.y * resolution);

            // 少なくとも1ピクセルの幅と高さを確保
            width = Mathf.Max(width, 1);
            height = Mathf.Max(height, 1);

            // 回転を考慮してピクセルをコピー
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // アトラス上の座標
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    // 配置領域内での正規化座標 (0-1)
                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    // 回転を適用
                    if (rotation != 0)
                    {
                        float rad = -rotation * Mathf.Deg2Rad;
                        float cos = Mathf.Cos(rad);
                        float sin = Mathf.Sin(rad);
                        float cx = normalizedX - 0.5f;
                        float cy = normalizedY - 0.5f;
                        normalizedX = cx * cos - cy * sin + 0.5f;
                        normalizedY = cx * sin + cy * cos + 0.5f;
                    }

                    // UV境界にマッピング（正規化座標 -> 元のUV空間）
                    float u = uvBounds.x + normalizedX * uvBounds.width;
                    float v = uvBounds.y + normalizedY * uvBounds.height;

                    // ソーステクスチャからサンプリング
                    Color pixel = SampleTexture(readable, u, v);

                    // 透明でない場合のみ書き込み（ノイズ防止）
                    if (pixel.a > 0.01f)
                    {
                        atlas.SetPixel(atlasX, atlasY, pixel);
                    }
                }
            }

            // 一時テクスチャをクリーンアップ
            if (readable != source)
            {
                Object.DestroyImmediate(readable);
            }
        }

        /// <summary>
        /// テクスチャをアトラスに配置（旧メソッド、互換性のため残す）
        /// </summary>
        private static void BlitTextureToAtlas(
            Texture2D atlas,
            Texture2D source,
            Vector2 position,
            Vector2 scale,
            float rotation,
            int resolution)
        {
            BlitTextureToAtlasWithUVBounds(atlas, source, position, scale, rotation, new Rect(0, 0, 1, 1), resolution);
        }

        /// <summary>
        /// AOマップ（_ShadowStrengthMask）のアトラスを生成
        /// 設定されていないアイランドは白（255, 255, 255）で塗りつぶす
        /// </summary>
        private static Texture2D BuildAOMapAtlas(
            ChimeraHairMaster component,
            int resolution,
            List<IslandPlacement> islandPlacements,
            bool isPreview = false)
        {
            // ピクセルバッファを作成（白で初期化: AOなし = 影なし）
            var pixels = new Color[resolution * resolution];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.white;
            }

            // 各アイランドを配置（バッファに直接書き込み）
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // サブメッシュに対応するマテリアルからAOマップを取得
                Texture2D sourceTexture = GetTextureFromRenderer(renderer, "_ShadowStrengthMask", island.submeshIndex);

                if (sourceTexture != null)
                {
                    BlitIslandToAtlasWithFallback(
                        pixels,
                        sourceTexture,
                        island.originalBounds,
                        island.atlasPosition,
                        island.atlasScale,
                        resolution,
                        Color.white
                    );
                }
                else
                {
                    // テクスチャが設定されていない場合は白で塗りつぶし（既に白で初期化済み）
                    FillIslandWithColor(pixels, island.atlasPosition, island.atlasScale, resolution, Color.white);
                }
            }

            // アトラステクスチャを作成してバッファを一括反映
            var atlas = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            atlas.name = "Atlas_ShadowStrengthMask";
            atlas.SetPixels(pixels);
            atlas.Apply(true);

            if (!isPreview)
            {
                // テクスチャ圧縮（DXT5 = Normal Quality相当）（ビルド時のみ）
                EditorUtility.CompressTexture(atlas, GetAtlasColorFormat(), TextureCompressionQuality.Best);
                ShaderUtils.EnableMipStreaming(atlas);
            }

            return atlas;
        }

        /// <summary>
        /// ノーマルマップ（_BumpMap）のアトラスを生成
        /// 設定されていないアイランドはフラットノーマル（128, 128, 255）で塗りつぶす
        /// </summary>
        private static Texture2D BuildNormalMapAtlas(
            ChimeraHairMaster component,
            int resolution,
            List<IslandPlacement> islandPlacements,
            bool isPreview = false)
        {
            // フラットノーマル色（頂点法線の向きを変更しない）
            Color flatNormal = new Color(128f / 255f, 128f / 255f, 1f, 1f);

            // ピクセルバッファを作成（フラットノーマルで初期化）
            var pixels = new Color[resolution * resolution];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = flatNormal;
            }

            // 各アイランドを配置（バッファに直接書き込み）
            foreach (var island in islandPlacements)
            {
                if (island.rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[island.rendererIndex];
                if (renderer == null) continue;

                // サブメッシュに対応するマテリアルからノーマルマップを取得
                Texture2D sourceTexture = GetTextureFromRenderer(renderer, "_BumpMap", island.submeshIndex);

                if (sourceTexture != null)
                {
                    BlitIslandToAtlasWithFallback(
                        pixels,
                        sourceTexture,
                        island.originalBounds,
                        island.atlasPosition,
                        island.atlasScale,
                        resolution,
                        flatNormal
                    );
                }
                else
                {
                    // テクスチャが設定されていない場合はフラットノーマルで塗りつぶし
                    FillIslandWithColor(pixels, island.atlasPosition, island.atlasScale, resolution, flatNormal);
                }
            }

            // アトラステクスチャを作成してバッファを一括反映
            var atlas = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            atlas.name = "Atlas_BumpMap";
            atlas.SetPixels(pixels);
            atlas.Apply(true);

            if (!isPreview)
            {
                // ノーマルマップ圧縮（BC5 = 2チャンネル専用）（ビルド時のみ）
                EditorUtility.CompressTexture(atlas, GetAtlasNormalFormat(), TextureCompressionQuality.Best);
                ShaderUtils.EnableMipStreaming(atlas);
            }

            return atlas;
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
        /// 透明ピクセルはフォールバック色で置き換える
        /// </summary>
        private static void BlitIslandToAtlasWithFallback(
            Color[] pixels,
            Texture2D source,
            Rect originalBounds,
            Vector2 atlasPosition,
            Vector2 atlasScale,
            int resolution,
            Color fallbackColor)
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
                    if (pixel.a > 0.01f)
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
