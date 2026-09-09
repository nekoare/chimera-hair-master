using System.Collections.Generic;
using ChimeraHairMaster.Editor;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 「色が合わないとき？」のマテリアル差し替えユーティリティのテスト。
    /// 指定スロットだけ差し替わる／null と範囲外は無視／差し替えで検知用ハッシュが変わる、を固定する。
    /// </summary>
    public class RendererMaterialSwapperTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private SkinnedMeshRenderer _renderer;
        private ChimeraHairMaster _component;
        private Material _a;
        private Material _b;
        private Material _c;

        [SetUp]
        public void SetUp()
        {
            _a = NewMaterial("A");
            _b = NewMaterial("B");
            _c = NewMaterial("C");

            var go = new GameObject("CHMSwapTestRenderer");
            _cleanup.Add(go);
            _renderer = go.AddComponent<SkinnedMeshRenderer>();
            _renderer.sharedMaterials = new[] { _a, _b };

            var holder = new GameObject("CHMSwapTest");
            _cleanup.Add(holder);
            _component = holder.AddComponent<ChimeraHairMaster>();
            _component.targetRenderers.Add(_renderer);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _cleanup.Clear();
        }

        private Material NewMaterial(string name)
        {
            var mat = new Material(Shader.Find("Standard")) { name = name };
            _cleanup.Add(mat);
            return mat;
        }

        [Test]
        public void TryReplaceSlot_ReplacesOnlyThatSlot()
        {
            bool replaced = RendererMaterialSwapper.TryReplaceSlot(_renderer, 1, _c);

            Assert.That(replaced, Is.True);
            Assert.That(_renderer.sharedMaterials[0], Is.SameAs(_a), "他のスロットは変わらない");
            Assert.That(_renderer.sharedMaterials[1], Is.SameAs(_c), "指定スロットだけ差し替わる");
        }

        [Test]
        public void TryReplaceSlot_Null_IsIgnored()
        {
            bool replaced = RendererMaterialSwapper.TryReplaceSlot(_renderer, 0, null);

            Assert.That(replaced, Is.False);
            Assert.That(_renderer.sharedMaterials[0], Is.SameAs(_a), "null のドロップで空スロットを作らない");
        }

        [Test]
        public void TryReplaceSlot_OutOfRange_IsIgnored()
        {
            Assert.That(RendererMaterialSwapper.TryReplaceSlot(_renderer, 2, _c), Is.False);
            Assert.That(RendererMaterialSwapper.TryReplaceSlot(_renderer, -1, _c), Is.False);
            Assert.That(_renderer.sharedMaterials, Is.EqualTo(new[] { _a, _b }));
        }

        [Test]
        public void TryReplaceSlot_SameMaterial_ReturnsFalse()
        {
            Assert.That(RendererMaterialSwapper.TryReplaceSlot(_renderer, 0, _a), Is.False);
        }

        [Test]
        public void ComputeSlotMaterialsHash_ChangesAfterSwap()
        {
            int before = RendererMaterialSwapper.ComputeSlotMaterialsHash(_component);

            RendererMaterialSwapper.TryReplaceSlot(_renderer, 1, _c);
            int after = RendererMaterialSwapper.ComputeSlotMaterialsHash(_component);

            Assert.That(after, Is.Not.EqualTo(before), "差し替えるとプレビュー再評価用のハッシュが変わること");
            Assert.That(RendererMaterialSwapper.ComputeSlotMaterialsHash(_component), Is.EqualTo(after), "同じ構成なら同じ値");
        }
    }
}
