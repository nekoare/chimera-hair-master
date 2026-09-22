#if CHM_MODULAR_AVATAR
using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// Prefab出力の MA Merge Armature まわりを固定する。
    /// - SanitizeMergeArmatureReferences: Instantiate で複製側を指してしまう targetObject を落とし、referencePath だけ残す
    /// - AddMergeArmatureComponents: 既に接続手段（Merge Armature / Bone Proxy / Constraint）がある髪には
    ///   名前照合の Merge Armature を足さない。残った本体 Armature の複製には付ける
    /// 階層（複製ルート）:
    ///   Root/Armature/Hips/Head                 アバター本体 Armature の複製
    ///   Root/Hair/Armature/Hips.001/HairBone    髪の独立アーマチュア
    ///   Root/Hair/HairMesh                      髪 SMR（rootBone = HairBone）
    /// 元アバター: Avatar/Armature（FindAvatarArmaturePath 用）
    /// </summary>
    public class PrefabExporterMergeArmatureTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private GameObject _root;
        private GameObject _originalAvatar;
        private Transform _clonedAvatarArmature;
        private Transform _clonedAvatarHead;
        private Transform _hairContainer;
        private Transform _hairArmature;
        private SkinnedMeshRenderer _hair;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Root");
            _cleanup.Add(_root);

            _clonedAvatarArmature = PrefabExportTestHierarchy.Child(_root.transform, "Armature");
            var hips = PrefabExportTestHierarchy.Child(_clonedAvatarArmature, "Hips");
            _clonedAvatarHead = PrefabExportTestHierarchy.Child(hips, "Head");

            _hairContainer = PrefabExportTestHierarchy.Child(_root.transform, "Hair");
            _hairArmature = PrefabExportTestHierarchy.Child(_hairContainer, "Armature");
            var hairHips = PrefabExportTestHierarchy.Child(_hairArmature, "Hips.001");
            var hairBone = PrefabExportTestHierarchy.Child(hairHips, "HairBone");
            _hair = PrefabExportTestHierarchy.CreateSkinnedRenderer(_hairContainer, "HairMesh", hairBone, _cleanup);

            _originalAvatar = new GameObject("Avatar");
            _cleanup.Add(_originalAvatar);
            PrefabExportTestHierarchy.Child(_originalAvatar.transform, "Armature");
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

        // ========== SanitizeMergeArmatureReferences ==========

        [Test]
        public void Sanitize_ClearsTargetObjectAndKeepsReferencePath()
        {
            var merge = _hairArmature.gameObject.AddComponent<ModularAvatarMergeArmature>();
            merge.mergeTarget.referencePath = "Armature";
            SetTargetObject(merge, _clonedAvatarArmature.gameObject);

            PrefabExporter.SanitizeMergeArmatureReferences(_root);

            Assert.AreEqual("Armature", merge.mergeTarget.referencePath, "referencePath は保持されること");
            Assert.IsNull(GetTargetObject(merge), "複製側の Armature を指す targetObject が消えること");
        }

        [Test]
        public void Sanitize_DerivesReferencePathFromTargetObjectWhenPathIsEmpty()
        {
            var merge = _hairArmature.gameObject.AddComponent<ModularAvatarMergeArmature>();
            merge.mergeTarget.referencePath = "";
            SetTargetObject(merge, _clonedAvatarArmature.gameObject);

            PrefabExporter.SanitizeMergeArmatureReferences(_root);

            Assert.AreEqual("Armature", merge.mergeTarget.referencePath, "targetObject から複製ルート相対パスを導出すること");
            Assert.IsNull(GetTargetObject(merge));
        }

        // ========== AddMergeArmatureComponents ==========

        [Test]
        public void AddMergeArmature_SkipsHairArmatureWhenBoneProxyIsOnAncestor()
        {
            var proxy = _hairContainer.gameObject.AddComponent<ModularAvatarBoneProxy>();
            proxy.boneReference = HumanBodyBones.Head;

            PrefabExporter.AddMergeArmatureComponents(_root, new[] { _hair }, _originalAvatar);

            Assert.IsNull(_hairArmature.GetComponent<ModularAvatarMergeArmature>(),
                "Bone Proxy で接続済みの髪には名前照合の Merge Armature を足さないこと");
        }

        [Test]
        public void AddMergeArmature_SkipsHairArmatureWhenConstraintIsOnAncestor()
        {
            var constraint = _hairContainer.gameObject.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = _clonedAvatarHead, weight = 1f });

            PrefabExporter.AddMergeArmatureComponents(_root, new[] { _hair }, _originalAvatar);

            Assert.IsNull(_hairArmature.GetComponent<ModularAvatarMergeArmature>(),
                "Constraint で接続済みの髪には名前照合の Merge Armature を足さないこと");
        }

        [Test]
        public void AddMergeArmature_AddsToClonedAvatarArmatureRootWhenItRemains()
        {
            // Constraint のソースや probeAnchor で本体 Armature の複製が残った状態（SetUp の Root/Armature）
            PrefabExporter.AddMergeArmatureComponents(_root, new[] { _hair }, _originalAvatar);

            var merge = _clonedAvatarArmature.GetComponent<ModularAvatarMergeArmature>();
            Assert.IsNotNull(merge, "残った本体 Armature の複製を本体へ戻す Merge Armature が付くこと");
            Assert.AreEqual("Armature", merge.mergeTarget.referencePath);
        }

        [Test]
        public void AddMergeArmature_AddsByNameToHairArmatureWithoutAttachment()
        {
            PrefabExporter.AddMergeArmatureComponents(_root, new[] { _hair }, _originalAvatar);

            var merge = _hairArmature.GetComponent<ModularAvatarMergeArmature>();
            Assert.IsNotNull(merge, "接続手段が無い髪アーマチュアには従来どおり付与すること");
            Assert.AreEqual("Armature", merge.mergeTarget.referencePath);
            Assert.AreEqual("", merge.prefix);
            Assert.AreEqual("", merge.suffix);
        }

        // ========== helpers ==========

        /// <summary>AvatarObjectReference.targetObject は MA 内部フィールドのため SerializedObject 経由で扱う</summary>
        private static void SetTargetObject(ModularAvatarMergeArmature merge, GameObject target)
        {
            var so = new SerializedObject(merge);
            so.FindProperty("mergeTarget.targetObject").objectReferenceValue = target;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject GetTargetObject(ModularAvatarMergeArmature merge)
        {
            return new SerializedObject(merge).FindProperty("mergeTarget.targetObject").objectReferenceValue as GameObject;
        }
    }
}
#endif
