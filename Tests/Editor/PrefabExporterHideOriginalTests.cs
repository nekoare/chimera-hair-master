using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// Prefab出力の「元の髪を非表示にする」（PrefabExporter.HideOriginalRenderers）を固定する。
    /// - 対象 Renderer の GameObject を非表示にし EditorOnly タグを付ける（アップロードからも外す）
    /// - null 項目と、既に非表示かつ EditorOnly のものは数えない
    /// - Undo で表示とタグの両方が元に戻る
    /// </summary>
    public class PrefabExporterHideOriginalTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private ChimeraHairMaster _component;
        private GameObject _hairA;
        private GameObject _hairB;

        [SetUp]
        public void SetUp()
        {
            var root = new GameObject("Root");
            _cleanup.Add(root);
            _component = root.AddComponent<ChimeraHairMaster>();

            _hairA = new GameObject("HairA");
            _hairA.transform.SetParent(root.transform, false);
            _hairB = new GameObject("HairB");
            _hairB.transform.SetParent(root.transform, false);

            _component.targetRenderers = new List<SkinnedMeshRenderer>
            {
                _hairA.AddComponent<SkinnedMeshRenderer>(),
                null,
                _hairB.AddComponent<SkinnedMeshRenderer>(),
            };
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        [Test]
        public void HideOriginalRenderers_DeactivatesAndTagsEditorOnly_SkippingNullAndAlreadyHidden()
        {
            _hairB.SetActive(false);
            _hairB.tag = "EditorOnly";

            int hidden = PrefabExporter.HideOriginalRenderers(_component);

            Assert.IsFalse(_hairA.activeSelf, "対象 Renderer の GameObject が非表示になること");
            Assert.AreEqual("EditorOnly", _hairA.tag, "EditorOnly タグが付くこと");
            Assert.IsFalse(_hairB.activeSelf);
            Assert.AreEqual(1, hidden, "null 項目と既に非表示かつ EditorOnly のものは数えないこと");
        }

        [Test]
        public void HideOriginalRenderers_TagsAlreadyInactiveObjectWithoutEditorOnly()
        {
            _hairB.SetActive(false);

            int hidden = PrefabExporter.HideOriginalRenderers(_component);

            Assert.AreEqual("EditorOnly", _hairB.tag, "非表示でもタグが無ければ EditorOnly を付けること");
            Assert.AreEqual(2, hidden);
        }

        [Test]
        public void HideOriginalRenderers_IsUndoable()
        {
            Undo.IncrementCurrentGroup();

            PrefabExporter.HideOriginalRenderers(_component);
            Assert.IsFalse(_hairA.activeSelf);

            Undo.PerformUndo();

            Assert.IsTrue(_hairA.activeSelf, "Undo で元の髪が再表示されること");
            Assert.IsTrue(_hairB.activeSelf);
            Assert.AreEqual("Untagged", _hairA.tag, "Undo でタグも元に戻ること");
            Assert.AreEqual("Untagged", _hairB.tag);
        }
    }
}
