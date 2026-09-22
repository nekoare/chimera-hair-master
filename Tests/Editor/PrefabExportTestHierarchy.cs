using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// Prefab出力系テストで使う階層構築ヘルパー
    /// </summary>
    internal static class PrefabExportTestHierarchy
    {
        public static Transform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>
        /// 3頂点すべてが bone にウェイトされた SkinnedMeshRenderer を parent 直下に作る
        /// </summary>
        public static SkinnedMeshRenderer CreateSkinnedRenderer(Transform parent, string name, Transform bone, List<Object> cleanup)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            mesh.boneWeights = new[] { FullWeight(0), FullWeight(0), FullWeight(0) };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            cleanup.Add(mesh);

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone };
            smr.rootBone = bone;
            return smr;
        }

        private static BoneWeight FullWeight(int boneIndex)
        {
            return new BoneWeight { boneIndex0 = boneIndex, weight0 = 1f };
        }
    }
}
