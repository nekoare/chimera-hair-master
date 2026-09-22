using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
#if CHM_MODULAR_AVATAR
using nadena.dev.modular_avatar.core;
#endif

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// Prefab出力の階層クリーンアップ（HierarchyDependencyResolver.CleanUp）が、髪をアバターに
    /// 接続しているコンポーネントを残すことを固定する。
    /// - Constraint: 髪を動かすもの（target が weighted bone 系に覆われる）は本体ごと残り、無関係なものは消える
    /// - MA Merge Armature / MA Bone Proxy: 生き残る GameObject に付いていれば設定ごと残る
    /// - 髪と無関係な衣装側の Merge Armature は GameObject ごと消える
    /// 階層:
    ///   Root/Armature/Hips/Head                 アバター本体
    ///   Root/Hair/Armature/Hips.001/HairBone    髪の独立アーマチュア（HairBone にウェイト）
    ///   Root/Hair/HairMesh                      髪 SMR
    ///   Root/Outfit/Armature/Hips               髪と無関係な衣装
    /// </summary>
    public class HierarchyDependencyResolverTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private GameObject _root;
        private Transform _avatarHead;
        private Transform _hairContainer;
        private Transform _hairArmature;
        private Transform _outfit;
        private Transform _outfitArmature;
        private SkinnedMeshRenderer _hair;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Root");
            _cleanup.Add(_root);

            var armature = PrefabExportTestHierarchy.Child(_root.transform, "Armature");
            var hips = PrefabExportTestHierarchy.Child(armature, "Hips");
            _avatarHead = PrefabExportTestHierarchy.Child(hips, "Head");

            _hairContainer = PrefabExportTestHierarchy.Child(_root.transform, "Hair");
            _hairArmature = PrefabExportTestHierarchy.Child(_hairContainer, "Armature");
            var hairHips = PrefabExportTestHierarchy.Child(_hairArmature, "Hips.001");
            var hairBone = PrefabExportTestHierarchy.Child(hairHips, "HairBone");
            _hair = PrefabExportTestHierarchy.CreateSkinnedRenderer(_hairContainer, "HairMesh", hairBone, _cleanup);

            _outfit = PrefabExportTestHierarchy.Child(_root.transform, "Outfit");
            _outfitArmature = PrefabExportTestHierarchy.Child(_outfit, "Armature");
            PrefabExportTestHierarchy.Child(_outfitArmature, "Hips");
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
        public void CleanUp_KeepsConstraintComponentThatMovesHairContainer()
        {
            var constraint = _hairContainer.gameObject.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = _avatarHead, weight = 1f });

            HierarchyDependencyResolver.CleanUp(_root, new[] { _hair });

            Assert.IsNotNull(_hairContainer.GetComponent<ParentConstraint>(), "髪コンテナの Constraint 本体が残ること");
            Assert.IsNotNull(_root.transform.Find("Armature/Hips/Head"), "Constraint のソースボーンが残ること");
        }

        [Test]
        public void CleanUp_RemovesConstraintUnrelatedToHair()
        {
            var constraint = _outfit.gameObject.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = _avatarHead, weight = 1f });

            HierarchyDependencyResolver.CleanUp(_root, new[] { _hair });

            Assert.IsNull(_root.transform.Find("Outfit"), "髪と無関係な Constraint は GameObject ごと消えること");
        }

#if CHM_MODULAR_AVATAR
        [Test]
        public void CleanUp_KeepsMergeArmatureWithSettingsOnHairArmatureRoot()
        {
            var merge = _hairArmature.gameObject.AddComponent<ModularAvatarMergeArmature>();
            merge.mergeTarget.referencePath = "Armature";
            merge.suffix = ".001";

            HierarchyDependencyResolver.CleanUp(_root, new[] { _hair });

            var kept = _hairArmature.GetComponent<ModularAvatarMergeArmature>();
            Assert.IsNotNull(kept, "髪アーマチュアの Merge Armature が残ること");
            Assert.AreEqual(".001", kept.suffix, "suffix 設定が引き継がれること");
            Assert.AreEqual("Armature", kept.mergeTarget.referencePath, "mergeTarget のパスが引き継がれること");
        }

        [Test]
        public void CleanUp_KeepsBoneProxyOnHairContainer()
        {
            var proxy = _hairContainer.gameObject.AddComponent<ModularAvatarBoneProxy>();
            proxy.boneReference = HumanBodyBones.Head;

            HierarchyDependencyResolver.CleanUp(_root, new[] { _hair });

            var kept = _hairContainer.GetComponent<ModularAvatarBoneProxy>();
            Assert.IsNotNull(kept, "髪コンテナの Bone Proxy が残ること");
            Assert.AreEqual(HumanBodyBones.Head, kept.boneReference);
        }

        [Test]
        public void CleanUp_RemovesMergeArmatureOfUnrelatedOutfit()
        {
            var merge = _outfitArmature.gameObject.AddComponent<ModularAvatarMergeArmature>();
            merge.mergeTarget.referencePath = "Armature";

            HierarchyDependencyResolver.CleanUp(_root, new[] { _hair });

            Assert.AreEqual(0, _root.GetComponentsInChildren<ModularAvatarMergeArmature>(true).Length,
                "無関係な衣装の Merge Armature は GameObject ごと消えること");
        }
#endif
    }
}
