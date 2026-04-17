using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// sRGB ↔ Linear RGB ↔ Oklab/OKLCh の変換ユーティリティ
    /// 参考: Björn Ottosson "A perceptual color space for image processing" (https://bottosson.github.io/posts/oklab/)
    /// </summary>
    public static class OklabConverter
    {
        #region sRGB <-> Linear RGB

        /// <summary>
        /// sRGB (0-1) を Linear RGB (0-1) に変換
        /// </summary>
        public static float SRGBToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Linear RGB (0-1) を sRGB (0-1) に変換
        /// </summary>
        public static float LinearToSRGB(float c)
        {
            if (c <= 0.0031308f) return c * 12.92f;
            return 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        public static Vector3 SRGBToLinear(Color srgb)
        {
            return new Vector3(SRGBToLinear(srgb.r), SRGBToLinear(srgb.g), SRGBToLinear(srgb.b));
        }

        public static Color LinearToSRGB(Vector3 lin, float alpha = 1f)
        {
            return new Color(LinearToSRGB(lin.x), LinearToSRGB(lin.y), LinearToSRGB(lin.z), alpha);
        }

        #endregion

        #region Linear RGB <-> Oklab

        /// <summary>
        /// Linear RGB を Oklab (L, a, b) に変換
        /// </summary>
        public static Vector3 LinearRGBToOklab(Vector3 lrgb)
        {
            float l = 0.4122214708f * lrgb.x + 0.5363325363f * lrgb.y + 0.0514459929f * lrgb.z;
            float m = 0.2119034982f * lrgb.x + 0.6806995451f * lrgb.y + 0.1073969566f * lrgb.z;
            float s = 0.0883024619f * lrgb.x + 0.2817188376f * lrgb.y + 0.6299787005f * lrgb.z;

            float l_ = Cbrt(l);
            float m_ = Cbrt(m);
            float s_ = Cbrt(s);

            return new Vector3(
                0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
                1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
                0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_
            );
        }

        /// <summary>
        /// Oklab (L, a, b) を Linear RGB に変換
        /// </summary>
        public static Vector3 OklabToLinearRGB(Vector3 lab)
        {
            float l_ = lab.x + 0.3963377774f * lab.y + 0.2158037573f * lab.z;
            float m_ = lab.x - 0.1055613458f * lab.y - 0.0638541728f * lab.z;
            float s_ = lab.x - 0.0894841775f * lab.y - 1.2914855480f * lab.z;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            return new Vector3(
                4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s
            );
        }

        #endregion

        #region Oklab <-> OKLCh

        /// <summary>
        /// Oklab (L, a, b) を OKLCh (L, C, h) に変換。h は ラジアン (-π, π]
        /// </summary>
        public static Vector3 OklabToOklch(Vector3 lab)
        {
            float c = Mathf.Sqrt(lab.y * lab.y + lab.z * lab.z);
            float h = Mathf.Atan2(lab.z, lab.y);
            return new Vector3(lab.x, c, h);
        }

        /// <summary>
        /// OKLCh (L, C, h) を Oklab (L, a, b) に変換。h は ラジアン
        /// </summary>
        public static Vector3 OklchToOklab(Vector3 lch)
        {
            float a = lch.y * Mathf.Cos(lch.z);
            float b = lch.y * Mathf.Sin(lch.z);
            return new Vector3(lch.x, a, b);
        }

        #endregion

        #region 高レベル便利関数

        /// <summary>
        /// sRGB Color → OKLCh (L, C, h_radians)
        /// </summary>
        public static Vector3 SRGBToOklch(Color srgb)
        {
            return OklabToOklch(LinearRGBToOklab(SRGBToLinear(srgb)));
        }

        /// <summary>
        /// OKLCh (L, C, h_radians) → sRGB Color (アルファは別途指定)
        /// 結果が sRGB の [0,1] 範囲外の場合は単純 saturate でクリップ
        /// </summary>
        public static Color OklchToSRGB(Vector3 lch, float alpha = 1f)
        {
            Vector3 lin = OklabToLinearRGB(OklchToOklab(lch));
            lin.x = Mathf.Clamp01(lin.x);
            lin.y = Mathf.Clamp01(lin.y);
            lin.z = Mathf.Clamp01(lin.z);
            return LinearToSRGB(lin, alpha);
        }

        /// <summary>
        /// L 値の上下端 [0, 1] へのソフトクリップ
        /// 範囲 [softZone, 1 - softZone] では線形、外側では端へ滑らかに漸近
        /// </summary>
        public static float SoftClip01(float x, float softZone)
        {
            // 上端
            if (x > 1f - softZone)
            {
                float shifted = x - (1f - softZone);
                float compressed = softZone * shifted / (shifted + softZone);
                return (1f - softZone) + compressed;
            }
            // 下端
            if (x < softZone)
            {
                float shifted = softZone - x;
                float compressed = softZone * shifted / (shifted + softZone);
                return softZone - compressed;
            }
            return x;
        }

        /// <summary>
        /// 色相環での加算（ラジアン単位）。結果は (-π, π] の範囲に正規化
        /// </summary>
        public static float WrapHueRadians(float h)
        {
            const float TwoPi = Mathf.PI * 2f;
            h = (h + Mathf.PI) % TwoPi;
            if (h < 0f) h += TwoPi;
            return h - Mathf.PI;
        }

        #endregion

        #region Helpers

        private static float Cbrt(float x)
        {
            // C# には立方根がないため、符号付きの pow で実装
            if (x >= 0f) return Mathf.Pow(x, 1f / 3f);
            return -Mathf.Pow(-x, 1f / 3f);
        }

        #endregion
    }
}
