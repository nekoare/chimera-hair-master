#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// テクスチャユーティリティ
    /// </summary>
    public static class TextureUtils
    {
        /// <summary>
        /// 圧縮テクスチャの場合、元画像ファイルから非圧縮版を読み込む。
        /// DXT等の圧縮アーティファクトを回避するために使用する。
        /// 非圧縮テクスチャの場合はnullを返す（呼び出し元で元テクスチャをそのまま使う）。
        /// </summary>
        /// <param name="texture">対象テクスチャ</param>
        /// <returns>非圧縮版テクスチャ（呼び出し元が破棄責任を持つ）、不要または失敗時はnull</returns>
        public static Texture2D? LoadUncompressed(Texture2D texture)
        {
            if (texture == null) return null;

            // 既に非圧縮なら不要
            if (!IsCompressedFormat(texture.format)) return null;

            // アセットパスを取得
            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
            {
                WarnUncompressedFallback(texture, "アセットパスが取得できません（ランタイム生成テクスチャ等）");
                return null;
            }

            // フルパスに変換
            string fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                WarnUncompressedFallback(texture, "元画像ファイルが見つかりません");
                return null;
            }

            // サポートされた画像形式かチェック（ImageConversion.LoadImage対応: PNG, JPG, TGA, EXR）
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga" && ext != ".exr")
            {
                // PSD/TIFF等の非対応形式はフォールバック
                WarnUncompressedFallback(texture, $"非対応形式 {ext}");
                return null;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(fullPath);

                // 元テクスチャと同じサイズの非圧縮テクスチャを作成
                var uncompressed = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!ImageConversion.LoadImage(uncompressed, fileBytes, false))
                {
                    Object.DestroyImmediate(uncompressed);
                    return null;
                }

                // サイズ検証（インポーターのMax Size等でリサイズされている場合、
                // 元画像サイズと異なることがある → その場合はリサイズして合わせる）
                if (uncompressed.width != texture.width || uncompressed.height != texture.height)
                {
                    var resized = ResizeTexture(uncompressed, texture.width, texture.height);
                    Object.DestroyImmediate(uncompressed);
                    uncompressed = resized;
                }

                uncompressed.name = texture.name + "_Uncompressed";
                return uncompressed;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ChimeraHairMaster] 元画像ファイルの読み込みに失敗しました: {assetPath} - {ex.Message}");
                return null;
            }
        }

        // 同一テクスチャで警告を繰り返さないための抑制セット
        private static readonly System.Collections.Generic.HashSet<string> s_warnedUncompressedFallback
            = new System.Collections.Generic.HashSet<string>();

        private static void WarnUncompressedFallback(Texture2D texture, string reason)
        {
            string key = texture != null ? texture.name : "(null)";
            if (!s_warnedUncompressedFallback.Add(key)) return;
            Debug.LogWarning(
                $"[ChimeraHairMaster] '{key}' を非圧縮で読み込めないため（{reason}）、" +
                "インポート済みの圧縮データから処理します。ブロックノイズが気になる場合は元画像をPNG等にして取り込んでください。");
        }

        /// <summary>
        /// テクスチャフォーマットが圧縮形式かどうか判定
        /// </summary>
        private static bool IsCompressedFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.RGB24:
                case TextureFormat.BGRA32:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RGBAHalf:
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                case TextureFormat.R16:
                case TextureFormat.RG16:
                case TextureFormat.RFloat:
                case TextureFormat.RHalf:
                case TextureFormat.RG32:
                case TextureFormat.RGB48:
                case TextureFormat.RGBA64:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// テクスチャをリサイズ（RenderTexture経由）
        /// </summary>
        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var resized = new Texture2D(width, height, TextureFormat.RGBA32, true);
            resized.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            resized.Apply(true);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return resized;
        }
    }
}
