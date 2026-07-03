#nullable enable
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
    }
}
