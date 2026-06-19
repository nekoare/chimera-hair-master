using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor;
using NUnit.Framework;

namespace ChimeraHairMaster.Tests
{
    public class RendererIndexRemapTests
    {
        private class Entry
        {
            public int rendererIndex;
            public string tag;
            public Entry(int idx, string tag) { rendererIndex = idx; this.tag = tag; }
        }

        private static List<Entry> Remap(List<Entry> list, int removedIndex)
        {
            RendererIndexRemap.RemoveRenderer(
                list, removedIndex, e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            return list;
        }

        // 退行テスト: [A,B,C] から先頭(0)を削除すると、B/C の設定が
        // 1つ前にズレ、削除対象の設定だけが消える（末尾 C の設定は保持される）
        [Test]
        public void RemoveFirst_ShiftsRemainingDown_KeepsTheirSettings()
        {
            var list = new List<Entry>
            {
                new Entry(0, "A"),
                new Entry(1, "B"),
                new Entry(2, "C"),
            };

            Remap(list, 0);

            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list.Single(e => e.tag == "B").rendererIndex, Is.EqualTo(0));
            Assert.That(list.Single(e => e.tag == "C").rendererIndex, Is.EqualTo(1));
            Assert.That(list.Any(e => e.tag == "A"), Is.False);
        }

        [Test]
        public void RemoveMiddle_ShiftsOnlyHigherIndices()
        {
            var list = new List<Entry>
            {
                new Entry(0, "A"),
                new Entry(1, "B"),
                new Entry(2, "C"),
            };

            Remap(list, 1);

            Assert.That(list.Single(e => e.tag == "A").rendererIndex, Is.EqualTo(0));
            Assert.That(list.Single(e => e.tag == "C").rendererIndex, Is.EqualTo(1));
            Assert.That(list.Any(e => e.tag == "B"), Is.False);
        }

        [Test]
        public void RemoveLast_DropsLastKeepsOthers()
        {
            var list = new List<Entry>
            {
                new Entry(0, "A"),
                new Entry(1, "B"),
            };

            Remap(list, 1);

            Assert.That(list.Count, Is.EqualTo(1));
            Assert.That(list.Single().tag, Is.EqualTo("A"));
            Assert.That(list.Single().rendererIndex, Is.EqualTo(0));
        }

        // 同一 renderer に複数エントリ（複数 submesh）があっても全て正しく追従する
        [Test]
        public void MultipleEntriesPerRenderer_AllRemapped()
        {
            var list = new List<Entry>
            {
                new Entry(0, "r0s0"),
                new Entry(0, "r0s1"),
                new Entry(1, "r1s0"),
                new Entry(2, "r2s0"),
                new Entry(2, "r2s1"),
            };

            Remap(list, 0);

            Assert.That(list.Any(e => e.tag.StartsWith("r0")), Is.False);
            Assert.That(list.Single(e => e.tag == "r1s0").rendererIndex, Is.EqualTo(0));
            Assert.That(list.Where(e => e.tag.StartsWith("r2")).Select(e => e.rendererIndex),
                Is.All.EqualTo(1));
        }

        [Test]
        public void NullList_NoThrow()
        {
            Assert.DoesNotThrow(() =>
                RendererIndexRemap.RemoveRenderer<Entry>(null, 0, e => e.rendererIndex, (e, i) => e.rendererIndex = i));
        }
    }
}
