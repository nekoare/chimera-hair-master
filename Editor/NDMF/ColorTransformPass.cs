using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// 色変換処理パス
    /// </summary>
    public class ColorTransformPass : Pass<ColorTransformPass>
    {
        public override string DisplayName => "CHM: Color Transform";

        /// <summary>
        /// 処理済みテクスチャのキャッシュ（コンポーネントごと）
        /// </summary>
        internal static Dictionary<ChimeraHairMaster, Dictionary<Texture2D, Texture2D>> ProcessedTextureCache
            = new Dictionary<ChimeraHairMaster, Dictionary<Texture2D, Texture2D>>();

        /// <summary>
        /// (renderer, submesh, slot) 1 件分の処理結果（最終圧縮前・マテリアル割り当て前）
        /// </summary>
        private sealed class SlotResult
        {
            public int RendererIndex;
            public int SubmeshIndex;
            public Material OriginalMaterial;
            public string PropertyName;
            public Texture2D OriginalTexture;
            public bool[] UvMask;
            public Texture2D Processed;
            /// <summary>textureCache の実体を指している（直接圧縮・破棄禁止。圧縮時はコピーする）</summary>
            public bool IsSharedCacheEntry;
        }

        protected override void Execute(BuildContext context)
        {
            // キャッシュをクリア
            ProcessedTextureCache.Clear();

            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;

                ProcessComponent(component);
            }
        }

        /// <summary>
        /// 1 コンポーネント分の色変換を実行し、各 Renderer のマテリアルを差し替える。
        /// BuildContext に依存しないためテストから直接呼べる。
        /// </summary>
        internal static void ProcessComponent(ChimeraHairMaster component)
        {
            // このコンポーネント用のキャッシュを初期化
            var textureCache = new Dictionary<Texture2D, Texture2D>();
            ProcessedTextureCache[component] = textureCache;
            // texture 単位の共有キャッシュに紐づく UV 署名（別UV領域の取り違え防止）
            var textureCacheSig = new Dictionary<Texture2D, int>();

            // Pass 単位のピクセル読み込みキャッシュ（GetReadableTexture + GetPixels の重複を避ける）
            var pixelCache = new MeshUVSampler.PixelCache();

            // 色合わせが無効な場合はスキップ（空のキャッシュを登録して終了）
            if (!component.enableColorTransform)
            {
                Debug.Log($"[ChimeraHairMaster] 色変換スキップ（無効）: {component.gameObject.name}");
                return;
            }

            Debug.Log($"[ChimeraHairMaster] 色変換処理開始: {component.gameObject.name}");

            // 基準色を決定
            Color sourceColor = DetermineSourceColor(component);

            // 設定を作成
            ColorTransformSettings baseSettings = ColorTransformSettings.FromComponent(component, sourceColor);

            Debug.Log($"[ChimeraHairMaster] 色変換モード: {baseSettings.Mode}, ソース色: {sourceColor}, ターゲット色: {baseSettings.TargetColor}");

            // 統合対象のサブメッシュを取得
            var includedSubmeshes = component.GetIncludedSubmeshes();

            // サブメッシュごとにグループ化（rendererIndex別）
            var submeshesByRenderer = new Dictionary<int, List<int>>();
            foreach (var (rendererIndex, submeshIndex) in includedSubmeshes)
            {
                if (!submeshesByRenderer.ContainsKey(rendererIndex))
                    submeshesByRenderer[rendererIndex] = new List<int>();
                submeshesByRenderer[rendererIndex].Add(submeshIndex);
            }

            // 塗り感統一: お手本 Renderer の処理済みテクスチャと band 情報を事前計算
            // プレビューと同じ inline 方式を採用することで、per-renderer 調整（明度/ブラー/マスク）下でも適用可能にする
            StrandPatternApplier.RefData strandRef = null;
            try
            {
                strandRef = StrandPatternApplier.PrepareRefData(component, baseSettings, pixelCache);

                // 各Rendererのテクスチャを処理（統合対象のサブメッシュのみ）。
                // マテリアル生成・圧縮・割り当ては AssignMaterials でまとめて行う
                var results = new List<SlotResult>();
                foreach (var kvp in submeshesByRenderer)
                {
                    int rendererIndex = kvp.Key;
                    var submeshIndices = kvp.Value;

                    var renderer = component.targetRenderers[rendererIndex];
                    if (renderer == null) continue;

                    // Renderer単位の明度オフセットとブラー／シャープ強度を取得
                    float brightnessOffset = GetRendererBrightnessOffset(component, rendererIndex);
                    float blurSharp = GetRendererBlurSharp(component, rendererIndex);

                    results.AddRange(CollectSlotResults(component, rendererIndex, renderer, submeshIndices, baseSettings,
                        textureCache, textureCacheSig, pixelCache, brightnessOffset, blurSharp, strandRef));
                }

                AssignMaterials(component, includedSubmeshes, results);

                Debug.Log($"[ChimeraHairMaster] 色変換処理完了: {component.gameObject.name}, 処理テクスチャ数: {textureCache.Count}");
            }
            finally
            {
                // 塗り感統一の参照テクスチャを解放
                strandRef?.Dispose();
                // Pass 内で作成した readable テクスチャを解放
                pixelCache.Dispose();
            }
        }

        /// <summary>
        /// 基準色（ソース色）を決定
        /// 最初のRendererのテクスチャから色を抽出
        /// </summary>
        private static Color DetermineSourceColor(ChimeraHairMaster component)
        {
            if (component.targetRenderers.Count > 0 && component.targetRenderers[0] != null)
            {
                var materials = component.targetRenderers[0].sharedMaterials;
                foreach (var mat in materials)
                {
                    if (mat != null && mat.HasProperty("_MainTex"))
                    {
                        var tex = mat.GetTexture("_MainTex") as Texture2D;
                        if (tex != null)
                        {
                            return ColorProcessor.ExtractDominantColor(tex);
                        }
                    }
                }
            }
            return Color.white;
        }

        /// <summary>
        /// Renderer単位の明度オフセットを取得
        /// </summary>
        private static float GetRendererBrightnessOffset(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBrightnessAdjustments == null) return 0f;

            foreach (var adjustment in component.rendererBrightnessAdjustments)
            {
                if (adjustment.rendererIndex == rendererIndex)
                {
                    return adjustment.brightnessOffset;
                }
            }

            return 0f;
        }

        /// <summary>
        /// Renderer単位のブラー／シャープ強度を取得
        /// </summary>
        private static float GetRendererBlurSharp(ChimeraHairMaster component, int rendererIndex)
        {
            if (component.rendererBlurSharpAdjustments == null) return 0f;

            foreach (var adj in component.rendererBlurSharpAdjustments)
            {
                if (adj.rendererIndex == rendererIndex)
                {
                    return adj.blurSharp;
                }
            }

            return 0f;
        }

        /// <summary>
        /// 1 Renderer 分の統合対象サブメッシュについて、色変更対象スロットごとの処理結果を集める。
        /// ここではマテリアルを作らず、最終圧縮もしない（AssignMaterials が担当）。
        /// </summary>
        private static List<SlotResult> CollectSlotResults(
            ChimeraHairMaster component,
            int rendererIndex,
            SkinnedMeshRenderer renderer,
            List<int> submeshIndices,
            ColorTransformSettings baseSettings,
            Dictionary<Texture2D, Texture2D> textureCache,
            Dictionary<Texture2D, int> textureCacheSig,
            MeshUVSampler.PixelCache pixelCache,
            float brightnessOffset,
            float blurSharp,
            StrandPatternApplier.RefData strandRef)
        {
            var results = new List<SlotResult>();
            var materials = renderer.sharedMaterials;

            // 明度オフセットがある場合はログ出力
            if (Mathf.Abs(brightnessOffset) > 0.001f)
            {
                Debug.Log($"[ChimeraHairMaster] Renderer '{renderer.name}' に明度オフセット {brightnessOffset:F2} を適用");
            }

            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                // 統合対象外のサブメッシュはスキップ（元のマテリアルをそのまま使用）
                if (!submeshIndices.Contains(i)) continue;

                // ※ previewMaterial の数値設定・輪郭線シェーダの反映は、この後段の
                //   TextureAtlasPass.ProcessPerRendererMaterials（merge-OFF時に実行）で行う。
                //   ここで重複して適用しない。

                // 色変更対象のテクスチャを処理
                foreach (var slot in component.colorChangeTargets)
                {
                    if (!slot.applyColorChange) continue;
                    if (!material.HasProperty(slot.propertyName)) continue;

                    var texture = material.GetTexture(slot.propertyName) as Texture2D;
                    if (texture == null) continue;

                    // Renderer単位のキャッシュキー（明度オフセット・ブラー/シャープ込み）
                    bool hasOffset = Mathf.Abs(brightnessOffset) > 0.001f;
                    bool hasBlurSharp = Mathf.Abs(blurSharp) > 0.001f;
                    // 塗り感統一: _MainTex かつお手本以外の Renderer は per-renderer で結果が変わるためキャッシュ共有不可
                    bool applyStrand = strandRef != null
                        && slot.propertyName == "_MainTex"
                        && rendererIndex != strandRef.RefIndex;
                    bool useSharedCache = !hasOffset && !hasBlurSharp && !applyStrand;

                    // テクスチャ毎に Oklab/RGBDelta 用の事前計算を行った settings を構築
                    // ※ 代表色抽出は元テクスチャから（ブラー/シャープの影響を受けない）
                    var settings = MeshUVSampler.PrepareSettingsWithUVStats(baseSettings, renderer, submeshIndices, texture, pixelCache);
                    int uvSig = ColorProcessor.ComputeUvStatSignature(settings);

                    // UV 使用領域マスクを生成（dilation と前処理ブラー/シャープで共用）
                    bool[] uvMask = MeshUVRasterizer.Rasterize(
                        renderer, submeshIndices, texture.width, texture.height);

                    // キャッシュを確認（共有可能な場合のみ）
                    Texture2D processedTexture = null;
                    bool foundInCache = false;
                    // 同じテクスチャでも UV 署名（代表色）が一致する場合のみ共有キャッシュを再利用する。
                    // 別領域（署名違い）は取り違えになるため、そのまま新規処理する。
                    if (useSharedCache && textureCache.TryGetValue(texture, out var cached)
                        && textureCacheSig.TryGetValue(texture, out var cachedSig) && cachedSig == uvSig)
                    {
                        processedTexture = cached;
                        foundInCache = true;
                    }

                    if (!foundInCache)
                    {
                        // 色変換の入力テクスチャ（ブラー/シャープ前処理を適用）
                        Texture2D colorTransformInput = texture;
                        Texture2D preprocessed = null;
                        if (hasBlurSharp)
                        {
                            preprocessed = TextureBlurSharpener.Process(texture, blurSharp, uvMask);
                            if (preprocessed != null)
                            {
                                colorTransformInput = preprocessed;
                            }
                        }

                        // 色変換を実行
                        processedTexture = ColorProcessor.ProcessTexture(colorTransformInput, settings, compressResult: false);

                        // 前処理用の中間テクスチャを破棄
                        if (preprocessed != null)
                        {
                            Object.DestroyImmediate(preprocessed);
                        }

                        // 明度オフセットを適用
                        if (processedTexture != null && hasOffset)
                        {
                            var offsetApplied = ColorProcessor.ApplyBrightnessOffset(processedTexture, brightnessOffset, compressResult: false);
                            if (offsetApplied != null)
                            {
                                Object.DestroyImmediate(processedTexture);
                                processedTexture = offsetApplied;
                            }
                        }

                        // UV 使用領域外を edge dilation で塗り足し
                        if (processedTexture != null)
                        {
                            var dilated = ColorProcessor.DilateTexture(processedTexture, uvMask, 8, compressResult: false);
                            if (dilated != null)
                            {
                                Object.DestroyImmediate(processedTexture);
                                processedTexture = dilated;
                            }
                        }

                        if (processedTexture != null && useSharedCache && !textureCache.ContainsKey(texture))
                        {
                            textureCache[texture] = processedTexture;
                            textureCacheSig[texture] = uvSig;
                        }
                    }

                    // 色合わせ無視マスクを適用
                    // 注: useSharedCache=true 時は processedTexture が textureCache 内に登録済みのため、
                    // 旧 processedTexture を破棄せず参照だけ差し替える（cache に未マスク版が残るのは仕様）
                    if (processedTexture != null)
                    {
                        var masked = Processing.ColorMaskApplier.TryApply(component, rendererIndex, i, texture, processedTexture, compressResult: false);
                        if (masked != null)
                        {
                            processedTexture = masked;
                        }
                    }

                    // 塗り感統一を inline 適用（_MainTex かつお手本以外の Renderer のみ）
                    // useSharedCache を false に強制しているので processedTexture は per-renderer 固有のインスタンス
                    if (applyStrand && processedTexture != null)
                    {
                        var composed = StrandPatternApplier.TryComposeStrand(
                            processedTexture,
                            renderer,
                            submeshIndices,
                            strandRef,
                            texture.format,
                            compressResult: false);
                        if (composed != null && composed != processedTexture)
                        {
                            Object.DestroyImmediate(processedTexture);
                            processedTexture = composed;
                        }
                    }

                    if (processedTexture == null) continue;

                    // 共有キャッシュの実体をそのまま持っているか（後段で圧縮する際はコピーが必要）
                    bool isSharedCacheEntry = useSharedCache
                        && textureCache.TryGetValue(texture, out var sharedEntry)
                        && ReferenceEquals(sharedEntry, processedTexture);

                    results.Add(new SlotResult
                    {
                        RendererIndex = rendererIndex,
                        SubmeshIndex = i,
                        OriginalMaterial = material,
                        PropertyName = slot.propertyName,
                        OriginalTexture = texture,
                        UvMask = uvMask,
                        Processed = processedTexture,
                        IsSharedCacheEntry = isSharedCacheEntry,
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// 処理結果を元マテリアル単位にまとめ、Renderer にマテリアルを割り当てる。
        /// 統合OFFで同じ元マテリアルを 2 つ以上の (r, s) が共有し、全スロットで UV 使用領域が重ならなければ
        /// 1 マテリアル・1 テクスチャに合成する。それ以外は従来どおり (r, s) ごとに分割する。
        /// 統合ONは後段でアトラス化されるため共有しない（従来どおり）。
        /// </summary>
        private static void AssignMaterials(
            ChimeraHairMaster component,
            List<(int rendererIndex, int submeshIndex)> includedSubmeshes,
            List<SlotResult> results)
        {
            bool allowShare = !component.enableMeshMerge;

            // 元マテリアル → 参照する (r, s)（走査順維持）
            var groupOrder = new List<Material>();
            var groups = new Dictionary<Material, List<(int r, int s)>>();
            foreach (var (r, s) in includedSubmeshes)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                if (s >= mats.Length || mats[s] == null) continue;

                if (!groups.TryGetValue(mats[s], out var members))
                {
                    members = new List<(int r, int s)>();
                    groups[mats[s]] = members;
                    groupOrder.Add(mats[s]);
                }
                members.Add((r, s));
            }

            var assigned = new Dictionary<(int r, int s), Material>();
            int sharedCount = 0;
            int splitCount = 0;

            foreach (var original in groupOrder)
            {
                var members = groups[original];
                var groupResults = results.Where(x => x.OriginalMaterial == original).ToList();
                bool share = allowShare && members.Count >= 2 && CanShareGroup(groupResults, members.Count);

                if (members.Count >= 2)
                {
                    var memberNames = string.Join(", ", members.Select(m => $"{component.targetRenderers[m.r].name}[{m.s}]"));
                    string outcome = share ? "1 マテリアルに合成"
                        : !allowShare ? "統合ONのため分割（従来どおり）"
                        : "UV重複のため分割（従来どおり）";
                    Debug.Log($"[ChimeraHairMaster] 共有マテリアル '{original.name}' ({memberNames}) → {outcome}");
                }

                if (share)
                {
                    var newMaterial = new Material(original);
                    newMaterial.name = original.name + "_CHM";

                    foreach (var propertyName in groupResults.Select(x => x.PropertyName).Distinct().ToList())
                    {
                        var slotResults = groupResults.Where(x => x.PropertyName == propertyName).ToList();
                        var tex = FinalizeSharedSlot(slotResults);
                        if (tex != null)
                        {
                            newMaterial.SetTexture(propertyName, tex);
                        }
                    }

                    foreach (var m in members)
                    {
                        assigned[m] = newMaterial;
                    }
                    sharedCount++;
                }
                else
                {
                    foreach (var (r, s) in members)
                    {
                        var newMaterial = new Material(original);
                        newMaterial.name = original.name + "_CHM";

                        foreach (var x in groupResults.Where(x => x.RendererIndex == r && x.SubmeshIndex == s))
                        {
                            var tex = FinalizeSingleSlot(x, component.enableMeshMerge);
                            if (tex != null)
                            {
                                newMaterial.SetTexture(x.PropertyName, tex);
                            }
                        }

                        assigned[(r, s)] = newMaterial;
                    }
                    if (members.Count >= 2) splitCount++;
                }
            }

            // Renderer へ書き戻し（統合対象外スロットは元のまま）
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                var mats = renderer.sharedMaterials;
                var newMats = new Material[mats.Length];
                bool changed = false;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (assigned.TryGetValue((r, s), out var m))
                    {
                        newMats[s] = m;
                        changed = true;
                    }
                    else
                    {
                        newMats[s] = mats[s];
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = newMats;
                }
            }

            Debug.Log($"[ChimeraHairMaster] 共有マテリアル: 共有 {sharedCount} 件 / 分割 {splitCount} 件 / 対象マテリアル {groupOrder.Count} 種: {component.gameObject.name}");
        }

        /// <summary>
        /// 全スロットで合成可能なら true（スロットごとに Region を組んで CanComposite で判定）。
        /// 同じ元マテリアルなのでスロットのテクスチャは全メンバー共通。あるスロットの結果が
        /// メンバー数と一致しない（一部で処理失敗）場合は、欠けた領域が土台のままになるため共有しない。
        /// </summary>
        private static bool CanShareGroup(List<SlotResult> groupResults, int memberCount)
        {
            foreach (var propertyName in groupResults.Select(x => x.PropertyName).Distinct())
            {
                var regions = groupResults
                    .Where(x => x.PropertyName == propertyName)
                    .Select(x => new SharedMaterialCompositor.Region(x.UvMask, x.Processed))
                    .ToList();
                if (regions.Count != memberCount) return false;
                if (!SharedMaterialCompositor.CanComposite(regions)) return false;
            }
            return true;
        }

        /// <summary>
        /// 従来どおり 1 (r, s) 分を確定する。
        /// 統合OFFのみ: 中間は全段非圧縮のため、ここで元フォーマットに1回だけ Best 圧縮する。
        /// 共有キャッシュ実体を破壊しないよう、キャッシュと同一参照ならコピーしてから圧縮する
        /// （そうしないと後続の cache 命中 renderer が圧縮済みをデコードしてブロックノイズが再発する）。
        /// </summary>
        private static Texture2D FinalizeSingleSlot(SlotResult x, bool meshMergeEnabled)
        {
            var tex = x.Processed;
            if (!meshMergeEnabled)
            {
                if (x.IsSharedCacheEntry)
                {
                    // キャッシュは非圧縮のまま保持し、圧縮はこの renderer 専用コピーに対して行う
                    tex = ColorProcessor.CopyTexture(tex, compressResult: false);
                }
                ColorProcessor.CompressToMatch(tex, x.OriginalTexture.format);
            }
            return tex;
        }

        /// <summary>
        /// 共有グループの 1 スロット分を合成して確定する（統合OFF専用）。
        /// 領域ごとの中間テクスチャは破棄する（キャッシュ実体は残す）。
        /// </summary>
        private static Texture2D FinalizeSharedSlot(List<SlotResult> slotResults)
        {
            if (slotResults.Count == 1) return FinalizeSingleSlot(slotResults[0], meshMergeEnabled: false);

            var regions = slotResults
                .Select(x => new SharedMaterialCompositor.Region(x.UvMask, x.Processed))
                .ToList();
            var composite = SharedMaterialCompositor.Composite(regions);
            if (composite == null)
            {
                // CanShareGroup で弾いているので到達しない想定。万一の場合は先頭を従来処理で返す
                return FinalizeSingleSlot(slotResults[0], meshMergeEnabled: false);
            }

            foreach (var x in slotResults)
            {
                if (!x.IsSharedCacheEntry)
                {
                    Object.DestroyImmediate(x.Processed);
                }
            }

            ColorProcessor.CompressToMatch(composite, slotResults[0].OriginalTexture.format);
            return composite;
        }
    }
}
