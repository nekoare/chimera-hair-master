using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// Oklab 暗部の明るさ自動調整（UpdateOklabLDarkEndRatioFromTargetColor）のテスト。
    /// 暗い側と中間ピーク（0.9）は従来の V 三角波を維持し、明るい側の端点だけを
    /// 彩度で 無彩色0.7（白の銀髪化防止）↔ ビビッド0.4 に振り分ける
    /// </summary>
    public class OklabDarkEndAutoAdjustTests
    {
        private GameObject _go;
        private ChimeraHairMaster _component;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("CHMOklabDarkEndTest");
            _component = _go.AddComponent<ChimeraHairMaster>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private float Auto(Color color)
        {
            _component.targetColor = color;
            _component.UpdateOklabLDarkEndRatioFromTargetColor();
            return _component.oklabLDarkEndRatio;
        }

        // 退行テスト: 従来式は白で 0.3（暗部が中間グレーまで落ちて銀髪化）だった
        [Test]
        public void White_GivesHighDarkEnd()
        {
            Assert.That(Auto(Color.white), Is.EqualTo(0.7f).Within(0.01f));
        }

        // ビビッド色は深い影のルックをほぼ維持（0.3 → 0.4 に微調整）
        [Test]
        public void PureRed_KeepsDeepShading()
        {
            Assert.That(Auto(Color.red), Is.EqualTo(0.4f).Within(0.01f));
        }

        [Test]
        public void PureBlue_KeepsDeepShading()
        {
            Assert.That(Auto(Color.blue), Is.EqualTo(0.4f).Within(0.01f));
        }

        // 中間 target のフラット化ピーク（明度感を揃える従来挙動）を維持
        [Test]
        public void MidGray_KeepsFlatteningPeak()
        {
            Assert.That(Auto(new Color(128f / 255f, 128f / 255f, 128f / 255f)),
                Is.EqualTo(0.9f).Within(0.01f));
        }

        // 暗い側は従来式（0.3 + 1.2V）のまま
        [Test]
        public void DarkGray_KeepsLegacyFormula()
        {
            float v = 30f / 255f;
            float expected = 0.3f + 1.2f * v;
            Assert.That(Auto(new Color(v, v, v)), Is.EqualTo(expected).Within(0.01f));
        }

        // 中間彩度（パステル）は無彩色0.7とビビッド0.4の間に落ちる
        [Test]
        public void PastelPink_BlendsBetweenEndpoints()
        {
            Assert.That(Auto(new Color(1f, 200f / 255f, 220f / 255f)), Is.InRange(0.45f, 0.69f));
        }

        // どの色でもスライダー範囲 [0,1] 内の妥当な帯に収まる
        [Test]
        public void RepresentativeColors_StayInValidRange()
        {
            var colors = new[]
            {
                Color.white, Color.black, Color.red, Color.green, Color.blue,
                Color.yellow, Color.cyan, Color.magenta, Color.gray,
                new Color(1f, 0.78f, 0.86f), // パステルピンク
                new Color(0.59f, 0.39f, 0.31f), // 茶髪
            };
            foreach (var c in colors)
            {
                float ratio = Auto(c);
                Assert.That(ratio, Is.InRange(0.29f, 0.91f), $"color={c}");
            }
        }

        // 退行テスト: 以前は OnValidate が target 色変更で手動値を上書きしていた
        [Test]
        public void OnValidate_DoesNotClobberManualValue()
        {
            _component.oklabLDarkEndRatio = 0.77f;
            _component.targetColor = Color.red;
            _go.SendMessage("OnValidate");
            Assert.That(_component.oklabLDarkEndRatio, Is.EqualTo(0.77f));
        }
    }
}
