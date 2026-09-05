using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 退行テスト (v1.5.14以前): プレビュー無効化ハッシュが色合わせ無視マスクを
    /// InstanceID でしか見ておらず、同じPNGアセットへの上書き保存（マスクの塗り直し）が
    /// プレビューに反映されない不具合。
    /// マスク内容（imageContentsHash）を集約する ComputeMaskContentsHash の挙動を固定する。
    /// </summary>
    public class ColorMaskContentsHashTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private ChimeraHairMaster _component;

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        private void SetUpComponent()
        {
            var go = new GameObject("CHMMaskContentsHashTest");
            _cleanup.Add(go);
            _component = go.AddComponent<ChimeraHairMaster>();
        }

        private Texture2D AddMask(int rendererIndex, int submeshIndex, Hash128 contentsHash)
        {
            var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            mask.imageContentsHash = contentsHash;
            _cleanup.Add(mask);
            _component.colorMasks.Add(
                new ColorMaskEntry(rendererIndex, submeshIndex) { mask = mask });
            return mask;
        }

        [Test]
        public void ComputeMaskContentsHash_ChangesWhenMaskRepainted()
        {
            SetUpComponent();
            var mask = AddMask(0, 0, new Hash128(1u, 2u, 3u, 4u));
            int before = ColorMaskApplier.ComputeMaskContentsHash(_component);

            // 同一アセットへの上書き保存を再現: InstanceID は不変のまま内容ハッシュだけ変わる
            mask.imageContentsHash = new Hash128(5u, 6u, 7u, 8u);
            int after = ColorMaskApplier.ComputeMaskContentsHash(_component);

            Assert.That(after, Is.Not.EqualTo(before),
                "マスクの塗り直し（imageContentsHash の変化）でハッシュが変わること" +
                "（v1.5.14以前は InstanceID のみでプレビューが更新されなかった）");
        }

        [Test]
        public void ComputeMaskContentsHash_StableWhenUnchanged()
        {
            SetUpComponent();
            AddMask(0, 0, new Hash128(1u, 2u, 3u, 4u));
            AddMask(1, 0, new Hash128(9u, 10u, 11u, 12u));

            int first = ColorMaskApplier.ComputeMaskContentsHash(_component);
            int second = ColorMaskApplier.ComputeMaskContentsHash(_component);

            Assert.That(second, Is.EqualTo(first),
                "内容が変わらなければハッシュも変わらないこと（不要な再インスタンス化を起こさない）");
        }

        [Test]
        public void ComputeMaskContentsHash_ToleratesEmptyAndNullEntries()
        {
            SetUpComponent();
            int emptyHash = ColorMaskApplier.ComputeMaskContentsHash(_component);

            // mask 未設定のエントリは無視される（例外にならない）
            _component.colorMasks.Add(new ColorMaskEntry(0, 0));
            int withNullMask = ColorMaskApplier.ComputeMaskContentsHash(_component);

            Assert.That(withNullMask, Is.EqualTo(emptyHash),
                "mask が null のエントリは内容ハッシュに寄与しないこと");
        }
    }
}
