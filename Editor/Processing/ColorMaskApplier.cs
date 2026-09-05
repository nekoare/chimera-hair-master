#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 色合わせ無視マスクの適用ヘルパ。
    /// 各処理経路 (ビルド / プレビュー / PNG 出力 / Prefab 出力) で同じ呼び方を共有し、
    /// 経路追加時の処理漏れを防ぐ。
    ///
    /// 所有権: 新規 Texture2D を返すのみで、引数の processedTex は破棄しない。
    /// 呼び出し側がキャッシュ所有権に応じて破棄要否を判断する。
    /// </summary>
    public static class ColorMaskApplier
    {
        /// <summary>
        /// 色合わせ無視マスクを適用した新規テクスチャを返す。
        /// マスク未設定 / 引数不正 / 適用失敗時は null（呼び出し側は元 processedTex を維持）。
        /// </summary>
        public static Texture2D? TryApply(
            ChimeraHairMaster component,
            int rendererIndex,
            int submeshIndex,
            Texture2D originalTex,
            Texture2D processedTex,
            bool compressResult = true)
        {
            if (component == null || originalTex == null || processedTex == null) return null;
            var colorMask = component.GetColorMask(rendererIndex, submeshIndex);
            if (colorMask == null) return null;
            return ColorProcessor.ApplyColorMask(originalTex, processedTex, colorMask, compressResult);
        }

        /// <summary>
        /// 指定マテリアルを共有する全ての統合対象 (renderer, submesh) のマスクを収集し、
        /// 最小値（黒=維持が優先）で合成して適用した新規テクスチャを返す。
        /// Prefab 出力はマテリアル単位で1枚（連番PNG）しかテクスチャを出力できず、
        /// (r, s) 単位の適用では走査順で最初でない (r, s) のマスクが脱落するため、
        /// この経路ではこちらを使う（マスク付き変種が勝つプレビューの実効挙動と一致させる）。
        /// PNG 出力（色合わせ適用）は出力パスが衝突するため TryApplyForSharedMainTex を使うこと。
        /// マスク未設定 / 引数不正 / 適用失敗時は null（呼び出し側は元 processedTex を維持）。
        /// </summary>
        public static Texture2D? TryApplyForSharedMaterial(
            ChimeraHairMaster component,
            Material material,
            Texture2D originalTex,
            Texture2D processedTex,
            bool compressResult = true)
        {
            if (component == null || material == null || originalTex == null || processedTex == null) return null;

            var masks = CollectMasks(component, m => m == material);
            if (masks.Count == 0) return null;
            return ColorProcessor.ApplyColorMask(originalTex, processedTex, masks, compressResult);
        }

        /// <summary>
        /// _MainTex に指定テクスチャを使う全ての統合対象 (renderer, submesh) のマスクを収集し、
        /// 最小値（黒=維持が優先）で合成して適用した新規テクスチャを返す。
        /// PNG 出力（色合わせ適用）は出力パスが元テクスチャ名由来（{名前}_CHM.png・上書き保存）のため、
        /// 別マテリアルでも同一テクスチャなら同じファイルに後勝ちで上書きされる。
        /// マテリアル単位ではなくテクスチャ単位でマスクを解決することで、
        /// どの順で書かれても同じマスクが乗り、上書きが無害になる。
        /// マスク未設定 / 引数不正 / 適用失敗時は null（呼び出し側は元 processedTex を維持）。
        /// </summary>
        public static Texture2D? TryApplyForSharedMainTex(
            ChimeraHairMaster component,
            Texture2D originalTex,
            Texture2D processedTex,
            bool compressResult = true)
        {
            if (component == null || originalTex == null || processedTex == null) return null;

            var masks = CollectMasks(component,
                m => m.HasProperty("_MainTex") && m.GetTexture("_MainTex") == originalTex);
            if (masks.Count == 0) return null;
            return ColorProcessor.ApplyColorMask(originalTex, processedTex, masks, compressResult);
        }

        /// <summary>
        /// 全マスクの内容ハッシュ（imageContentsHash）を集約して返す。
        /// 同じPNGアセットへの上書き保存（マスクの塗り直し）は InstanceID が変わらないため、
        /// プレビュー無効化はこのハッシュの変化で検知する（previewMaterialHash と同じポーリング方式）。
        /// </summary>
        public static int ComputeMaskContentsHash(ChimeraHairMaster component)
        {
            if (component == null || component.colorMasks == null) return 0;

            unchecked
            {
                int hash = 17;
                foreach (var entry in component.colorMasks)
                {
                    if (entry?.mask == null) continue;
                    hash = hash * 31 + entry.mask.imageContentsHash.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>
        /// 述語に一致するマテリアルを持つ統合対象 (renderer, submesh) からマスクを重複なしで収集する
        /// </summary>
        private static List<Texture2D> CollectMasks(
            ChimeraHairMaster component,
            System.Func<Material, bool> materialMatches)
        {
            var masks = new List<Texture2D>();
            for (int r = 0; r < component.targetRenderers.Count; r++)
            {
                var renderer = component.targetRenderers[r];
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                for (int s = 0; s < materials.Length; s++)
                {
                    if (materials[s] == null || !materialMatches(materials[s])) continue;
                    if (!component.IsSubmeshIncluded(r, s)) continue;

                    var mask = component.GetColorMask(r, s);
                    if (mask != null && !masks.Contains(mask)) masks.Add(mask);
                }
            }
            return masks;
        }
    }
}
