// Oklab/OKLCh / Linear RGB / sRGB 変換ユーティリティ (HLSL)
// Reference: Björn Ottosson "A perceptual color space for image processing"
// https://bottosson.github.io/posts/oklab/
//
// === 色空間運用ポリシー ===
// CHM の Compute Shader は sRGB 値（= sRGB import されたテクスチャの生値）を扱う前提。
// RenderTextureDescriptor.sRGB = true で作られた RT に Graphics.Blit でソースを転送する。
// RWTexture2D<float4> は UAV raw read/write なので、sRGB encode/decode は走らず
// シェーダーには sRGB 値がそのまま入る（CPU 側 GetPixels と整合）。
// したがって、Oklab / RGBDelta モードでは以下の明示的変換を使う:
//   sRGB → SRGBToLinear → LinearRGBToOklab → ... → OklabToLinearRGB → LinearToSRGB → sRGB
// HSV モードは sRGB 空間で直接 RGBToHSV / HSVToRGB を使う（v1.3.2 互換）。
// この運用を変更するときは CPU 側 (ColorProcessor.TransformXXX) の入出力色空間も合わせること。

#ifndef CHM_OKLAB_INCLUDED
#define CHM_OKLAB_INCLUDED

// sRGB <-> Linear
float SRGBToLinearChannel(float c)
{
    // HLSL の三項演算子は両辺を評価するため、c < 0 でも pow が呼ばれる。
    // pow に負値を渡すと警告 + 未定義動作になるので max で 0 にクランプ
    return (c <= 0.04045) ? (c / 12.92) : pow(max((c + 0.055) / 1.055, 0.0), 2.4);
}

float LinearToSRGBChannel(float c)
{
    return (c <= 0.0031308) ? (c * 12.92) : (1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055);
}

float3 SRGBToLinear(float3 srgb)
{
    return float3(SRGBToLinearChannel(srgb.r), SRGBToLinearChannel(srgb.g), SRGBToLinearChannel(srgb.b));
}

float3 LinearToSRGB(float3 lin)
{
    return float3(LinearToSRGBChannel(lin.r), LinearToSRGBChannel(lin.g), LinearToSRGBChannel(lin.b));
}

// 立方根（負値対応）
float SignedCbrt(float x)
{
    return sign(x) * pow(abs(x), 1.0 / 3.0);
}

// Linear RGB -> Oklab
float3 LinearRGBToOklab(float3 lrgb)
{
    float l = 0.4122214708 * lrgb.r + 0.5363325363 * lrgb.g + 0.0514459929 * lrgb.b;
    float m = 0.2119034982 * lrgb.r + 0.6806995451 * lrgb.g + 0.1073969566 * lrgb.b;
    float s = 0.0883024619 * lrgb.r + 0.2817188376 * lrgb.g + 0.6299787005 * lrgb.b;

    float l_ = SignedCbrt(l);
    float m_ = SignedCbrt(m);
    float s_ = SignedCbrt(s);

    return float3(
        0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
        1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
        0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    );
}

// Oklab -> Linear RGB
float3 OklabToLinearRGB(float3 lab)
{
    float l_ = lab.x + 0.3963377774 * lab.y + 0.2158037573 * lab.z;
    float m_ = lab.x - 0.1055613458 * lab.y - 0.0638541728 * lab.z;
    float s_ = lab.x - 0.0894841775 * lab.y - 1.2914855480 * lab.z;

    float l = l_ * l_ * l_;
    float m = m_ * m_ * m_;
    float s = s_ * s_ * s_;

    return float3(
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
    );
}

// Oklab <-> OKLCh （h はラジアン）
float3 OklabToOklch(float3 lab)
{
    return float3(lab.x, sqrt(lab.y * lab.y + lab.z * lab.z), atan2(lab.z, lab.y));
}

float3 OklchToOklab(float3 lch)
{
    return float3(lch.x, lch.y * cos(lch.z), lch.y * sin(lch.z));
}

// 色相環ラップ (-π, π]
float WrapHueRadians(float h)
{
    static const float TwoPi = 6.28318530717958647692;
    h = fmod(h + 3.14159265358979323846, TwoPi);
    if (h < 0.0) h += TwoPi;
    return h - 3.14159265358979323846;
}

// L 値の上下端 [0,1] へのソフトクリップ
float SoftClip01(float x, float softZone)
{
    if (x > 1.0 - softZone)
    {
        float shifted = x - (1.0 - softZone);
        float compressed = softZone * shifted / (shifted + softZone);
        return (1.0 - softZone) + compressed;
    }
    if (x < softZone)
    {
        float shifted = softZone - x;
        float compressed = softZone * shifted / (shifted + softZone);
        return softZone - compressed;
    }
    return x;
}

// OKLCh -> sRGB（gamut外は彩度を最大 8 回まで段階的に下げて収める）
// ε 閾値で float 誤差レベルのはみ出しは許容し、過剰な彩度縮小を避ける
float3 OklchToSRGBGamutMapped(float3 lch)
{
    float3 lab = OklchToOklab(lch);
    float3 lin = OklabToLinearRGB(lab);
    float c = lch.y;

    static const float gamutEps = 1e-3;
    static const float gamutShrink = 0.9;

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        if (lin.x >= -gamutEps && lin.x <= 1.0 + gamutEps &&
            lin.y >= -gamutEps && lin.y <= 1.0 + gamutEps &&
            lin.z >= -gamutEps && lin.z <= 1.0 + gamutEps)
            break;
        c *= gamutShrink;
        lab = OklchToOklab(float3(lch.x, c, lch.z));
        lin = OklabToLinearRGB(lab);
    }

    lin = saturate(lin);
    return LinearToSRGB(lin);
}

#endif // CHM_OKLAB_INCLUDED
