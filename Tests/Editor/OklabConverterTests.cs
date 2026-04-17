using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    public class OklabConverterTests
    {
        private const float ColorEps = 1e-3f;    // 色成分の往復許容誤差（~1/1000）
        private const float AngleEps = 1e-3f;    // ラジアン角度の許容誤差

        [Test]
        public void SRGBToLinear_RoundTrip()
        {
            float[] samples = { 0f, 0.04045f, 0.1f, 0.5f, 0.9f, 1f };
            foreach (var v in samples)
            {
                float lin = OklabConverter.SRGBToLinear(v);
                float back = OklabConverter.LinearToSRGB(lin);
                Assert.That(back, Is.EqualTo(v).Within(ColorEps), $"SRGB round trip failed at {v}");
            }
        }

        [TestCase(0.0f, 0.0f, 0.0f)]
        [TestCase(1.0f, 1.0f, 1.0f)]
        [TestCase(0.5f, 0.3f, 0.8f)]
        [TestCase(0.72f, 0.12f, 0.05f)]
        public void SRGB_Oklab_RoundTrip(float r, float g, float b)
        {
            var srgb = new Color(r, g, b);
            var lin = OklabConverter.SRGBToLinear(srgb);
            var lab = OklabConverter.LinearRGBToOklab(lin);
            var linBack = OklabConverter.OklabToLinearRGB(lab);
            var srgbBack = OklabConverter.LinearToSRGB(linBack, srgb.a);

            Assert.That(srgbBack.r, Is.EqualTo(srgb.r).Within(ColorEps));
            Assert.That(srgbBack.g, Is.EqualTo(srgb.g).Within(ColorEps));
            Assert.That(srgbBack.b, Is.EqualTo(srgb.b).Within(ColorEps));
        }

        [Test]
        public void OklabToOklch_PreservesL()
        {
            var lab = new Vector3(0.6f, 0.1f, -0.05f);
            var lch = OklabConverter.OklabToOklch(lab);
            Assert.That(lch.x, Is.EqualTo(lab.x).Within(1e-6f));
            // C = sqrt(a^2 + b^2)
            float expectedC = Mathf.Sqrt(lab.y * lab.y + lab.z * lab.z);
            Assert.That(lch.y, Is.EqualTo(expectedC).Within(1e-6f));
        }

        [Test]
        public void OklabOklchRoundTrip()
        {
            var lab = new Vector3(0.6f, 0.1f, -0.05f);
            var lch = OklabConverter.OklabToOklch(lab);
            var labBack = OklabConverter.OklchToOklab(lch);
            Assert.That(labBack.x, Is.EqualTo(lab.x).Within(1e-6f));
            Assert.That(labBack.y, Is.EqualTo(lab.y).Within(1e-6f));
            Assert.That(labBack.z, Is.EqualTo(lab.z).Within(1e-6f));
        }

        [Test]
        public void SoftClip01_InsideRange_ReturnsInput()
        {
            // 境界から softZone 内側の値はそのまま
            Assert.That(OklabConverter.SoftClip01(0.5f, 0.05f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(OklabConverter.SoftClip01(0.1f, 0.05f), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(OklabConverter.SoftClip01(0.9f, 0.05f), Is.EqualTo(0.9f).Within(1e-6f));
        }

        [Test]
        public void SoftClip01_AbovOne_StaysBelowOne()
        {
            float y = OklabConverter.SoftClip01(1.5f, 0.05f);
            Assert.Less(y, 1f);
            Assert.Greater(y, 0.95f); // 1 - softZone より大きい
        }

        [Test]
        public void SoftClip01_BelowZero_StaysAboveZero()
        {
            float y = OklabConverter.SoftClip01(-0.3f, 0.05f);
            Assert.Greater(y, 0f);
            Assert.Less(y, 0.05f);
        }

        [Test]
        public void WrapHueRadians_WithinRange_Unchanged()
        {
            Assert.That(OklabConverter.WrapHueRadians(0f), Is.EqualTo(0f).Within(AngleEps));
            Assert.That(OklabConverter.WrapHueRadians(1f), Is.EqualTo(1f).Within(AngleEps));
            Assert.That(OklabConverter.WrapHueRadians(-1f), Is.EqualTo(-1f).Within(AngleEps));
        }

        [Test]
        public void WrapHueRadians_Above2Pi_Wraps()
        {
            // 2π 相当の入力は 0 付近に戻る
            float wrapped = OklabConverter.WrapHueRadians(2f * Mathf.PI + 0.1f);
            Assert.That(wrapped, Is.EqualTo(0.1f).Within(AngleEps));
        }

        [Test]
        public void WrapHueRadians_BelowMinusPi_Wraps()
        {
            // -2π は 0 に戻る
            float wrapped = OklabConverter.WrapHueRadians(-2f * Mathf.PI);
            Assert.That(wrapped, Is.EqualTo(0f).Within(AngleEps));
        }

        [Test]
        public void SRGBToOklch_BlackIsZeroL()
        {
            var lch = OklabConverter.SRGBToOklch(Color.black);
            Assert.That(lch.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(lch.y, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void SRGBToOklch_WhiteIsOneL()
        {
            var lch = OklabConverter.SRGBToOklch(Color.white);
            Assert.That(lch.x, Is.EqualTo(1f).Within(1e-2f));
            Assert.That(lch.y, Is.EqualTo(0f).Within(1e-2f));
        }
    }
}
