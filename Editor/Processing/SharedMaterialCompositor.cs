#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 同じ元マテリアル（手動適用では同じ _MainTex）を共有する複数 (renderer, submesh) の
    /// 「領域ごとの処理結果」を、UV 使用領域マスクで 1 枚に合成するユーティリティ。
    ///
    /// - 領域が 1 ピクセルでも重なる場合は合成しない（null）。呼び出し側は従来の分割／先勝ち処理に落とす。
    /// - 先頭の結果を土台にし、2 つ目以降は自分のマスク内だけ書き戻す。
    /// - 2 つ目以降の領域の縁（dilation 分）は土台由来のままなので、union マスクで dilation をやり直す。
    /// - 入力テクスチャは変更しない（共有キャッシュの実体が混ざるため）。破棄も呼び出し側の責務。
    /// </summary>
    internal static class SharedMaterialCompositor
    {
        internal sealed class Region
        {
            public bool[] Mask { get; }
            public Texture2D Processed { get; }

            public Region(bool[] mask, Texture2D processed)
            {
                Mask = mask;
                Processed = processed;
            }
        }

        /// <summary>
        /// 2 つ以上のマスクで同時に true になるピクセルがあれば true。
        /// 長さ不一致・null も「合成不可」として true を返す。
        /// </summary>
        internal static bool HasOverlap(IReadOnlyList<bool[]> masks)
        {
            if (masks == null || masks.Count < 2) return false;
            int length = masks[0]?.Length ?? -1;
            if (length < 0) return true;

            var claimed = new bool[length];
            foreach (var mask in masks)
            {
                if (mask == null || mask.Length != length) return true;
                for (int i = 0; i < length; i++)
                {
                    if (!mask[i]) continue;
                    if (claimed[i]) return true;
                    claimed[i] = true;
                }
            }
            return false;
        }

        /// <summary>
        /// 合成できる条件（2 件以上・全て同サイズ・マスク長一致・非重複）
        /// </summary>
        internal static bool CanComposite(IReadOnlyList<Region> regions)
        {
            if (regions == null || regions.Count < 2) return false;
            var first = regions[0].Processed;
            if (first == null) return false;
            int width = first.width;
            int height = first.height;

            var masks = new List<bool[]>(regions.Count);
            foreach (var region in regions)
            {
                if (region.Processed == null || region.Mask == null) return false;
                if (region.Processed.width != width || region.Processed.height != height) return false;
                if (region.Mask.Length != width * height) return false;
                masks.Add(region.Mask);
            }
            return !HasOverlap(masks);
        }

        /// <summary>
        /// 領域ごとの結果を 1 枚に合成した非圧縮 RGBA32 の新規テクスチャを返す。
        /// 合成できない場合は null。
        /// </summary>
        internal static Texture2D? Composite(IReadOnlyList<Region> regions, int dilationIterations = 8)
        {
            if (!CanComposite(regions)) return null;

            var first = regions[0].Processed;
            var result = ColorProcessor.CopyTexture(first, compressResult: false);
            var pixels = result.GetPixels32();

            for (int i = 1; i < regions.Count; i++)
            {
                var source = regions[i].Processed;
                var readable = ColorProcessor.GetReadableTexture(source);
                var src = readable.GetPixels32();
                var mask = regions[i].Mask;
                for (int p = 0; p < pixels.Length; p++)
                {
                    if (mask[p]) pixels[p] = src[p];
                }
                if (readable != source) Object.DestroyImmediate(readable);
            }

            result.SetPixels32(pixels);
            result.Apply(true);

            if (dilationIterations > 0)
            {
                var union = new bool[pixels.Length];
                foreach (var region in regions)
                {
                    var mask = region.Mask;
                    for (int p = 0; p < union.Length; p++)
                    {
                        if (mask[p]) union[p] = true;
                    }
                }

                var dilated = ColorProcessor.DilateTexture(result, union, dilationIterations, compressResult: false);
                if (dilated != null)
                {
                    Object.DestroyImmediate(result);
                    result = dilated;
                }
            }

            return result;
        }
    }
}
