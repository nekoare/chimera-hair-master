using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// 複数のSkinnedMeshRendererを統合するクラス
    /// </summary>
    public static class MeshMerger
    {
        /// <summary>
        /// メッシュ統合結果
        /// </summary>
        public class MergeResult
        {
            public Mesh MergedMesh { get; set; }
            public Transform[] Bones { get; set; }
            public Matrix4x4[] BindPoses { get; set; }
            public Transform RootBone { get; set; }
            public Bounds Bounds { get; set; }

            // 元のRendererに残すためのメッシュ（サブメッシュが除外された場合にのみ設定される）
            public Dictionary<SkinnedMeshRenderer, Mesh> RemainingMeshes { get; set; } = new Dictionary<SkinnedMeshRenderer, Mesh>();
        }

        /// <summary>
        /// 複数のRendererを統合
        /// </summary>
        public static MergeResult Merge(
            List<SkinnedMeshRenderer> renderers,
            Material outputMaterial,
            Transform avatarRoot,
            ChimeraHairMaster component = null)
        {
            if (renderers == null || renderers.Count == 0)
            {
                Debug.LogError("[ChimeraHairMaster] Renderers list is empty");
                return null;
            }

            // 有効なRendererをフィルタリング
            var validRenderers = new List<SkinnedMeshRenderer>();
            foreach (var renderer in renderers)
            {
                if (renderer != null && renderer.sharedMesh != null)
                {
                    validRenderers.Add(renderer);
                }
            }

            if (validRenderers.Count == 0)
            {
                Debug.LogError("[ChimeraHairMaster] No valid renderers to merge");
                return null;
            }

            // ボーン情報を収集・統合
            var boneData = CollectBoneData(validRenderers, avatarRoot);

            // メッシュデータを統合
            var mergedMesh = MergeMeshData(validRenderers, boneData, component, out var remainingMeshes);

            // 統合するジオメトリが存在しない場合はスキップ
            if (mergedMesh == null || mergedMesh.vertexCount == 0 || mergedMesh.GetTriangles(0).Length == 0)
            {
                Debug.Log("[ChimeraHairMaster] 統合対象のサブメッシュが存在しないため、メッシュ統合をスキップします");
                return null;
            }

            // Boundsを決定（継承モードを考慮）
            Bounds mergedBounds;
            if (component != null && ShouldApplyBoundsSetting(component))
            {
                // コンポーネントで設定を適用する場合
                mergedBounds = component.bounds;
                Debug.Log($"[ChimeraHairMaster] Using custom Bounds from component: center={mergedBounds.center}, size={mergedBounds.size}");
            }
            else
            {
                // 自動計算
                mergedBounds = CalculateMergedBounds(validRenderers);
            }

            // RootBoneを決定（継承モードを考慮）
            Transform rootBone;
            if (component != null && ShouldApplyBoundsSetting(component) && component.rootBone != null)
            {
                rootBone = component.rootBone;
            }
            else
            {
                rootBone = boneData.RootBone;
            }

            return new MergeResult
            {
                MergedMesh = mergedMesh,
                Bones = boneData.MergedBones,
                BindPoses = boneData.MergedBindPoses,
                RootBone = rootBone,
                Bounds = mergedBounds,
                RemainingMeshes = remainingMeshes
            };
        }

        /// <summary>
        /// Bounds設定を適用すべきかどうかを判定
        /// </summary>
        private static bool ShouldApplyBoundsSetting(ChimeraHairMaster component)
        {
            if (component == null) return false;

            switch (component.inheritBounds)
            {
                case MeshSettingsInheritMode.Set:
                    return true;
                case MeshSettingsInheritMode.DontSet:
                    return false;
                case MeshSettingsInheritMode.SetOrInherit:
                    // rootBoneが設定されている、またはデフォルトBounds以外が設定されていれば適用
                    return component.rootBone != null ||
                           component.bounds.size != Vector3.one * 2 ||
                           component.bounds.center != Vector3.zero;
                case MeshSettingsInheritMode.Inherit:
                default:
                    // 親からの継承は現在未実装（将来的な拡張用）
                    return false;
            }
        }

        /// <summary>
        /// 複数のRendererのlocalBoundsを統合
        /// </summary>
        private static Bounds CalculateMergedBounds(List<SkinnedMeshRenderer> renderers)
        {
            if (renderers.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            // 最初のRendererのBoundsを基準に開始
            Bounds mergedBounds = renderers[0].localBounds;

            // 残りのRendererのBoundsを統合
            for (int i = 1; i < renderers.Count; i++)
            {
                mergedBounds.Encapsulate(renderers[i].localBounds);
            }

            // さらに余裕を持たせる（アニメーションでの変形を考慮）
            // 各軸に20%の余裕を追加
            Vector3 extraSize = mergedBounds.size * 0.2f;
            mergedBounds.Expand(extraSize);

            Debug.Log($"[ChimeraHairMaster] Merged Bounds: center={mergedBounds.center}, size={mergedBounds.size}");

            return mergedBounds;
        }

        #region Bone Processing

        private class BoneData
        {
            public Transform[] MergedBones { get; set; }
            public Matrix4x4[] MergedBindPoses { get; set; }
            public Transform RootBone { get; set; }

            // Renderer -> (元のボーンインデックス -> 統合後のボーンインデックス)
            public Dictionary<SkinnedMeshRenderer, int[]> BoneIndexRemapping { get; set; }
        }

        private static BoneData CollectBoneData(List<SkinnedMeshRenderer> renderers, Transform avatarRoot)
        {
            var boneData = new BoneData
            {
                BoneIndexRemapping = new Dictionary<SkinnedMeshRenderer, int[]>()
            };

            // 全てのユニークなボーンを収集
            var uniqueBones = new List<Transform>();
            var boneToIndex = new Dictionary<Transform, int>();
            var boneToBindPose = new Dictionary<Transform, Matrix4x4>();

            foreach (var renderer in renderers)
            {
                var bones = renderer.bones;
                var mesh = renderer.sharedMesh;
                var bindPoses = mesh != null ? mesh.bindposes : new Matrix4x4[0];
                var remapping = new int[bones.Length];

                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    if (bone == null) continue;

                    if (!boneToIndex.TryGetValue(bone, out int index))
                    {
                        index = uniqueBones.Count;
                        uniqueBones.Add(bone);
                        boneToIndex[bone] = index;
                        
                        // 元のメッシュからBindPoseを取得
                        if (i < bindPoses.Length)
                        {
                            boneToBindPose[bone] = bindPoses[i];
                        }
                        else
                        {
                            // BindPoseがない場合は現在のワールド行列から計算
                            boneToBindPose[bone] = bone.worldToLocalMatrix;
                        }
                    }
                    remapping[i] = index;
                }

                boneData.BoneIndexRemapping[renderer] = remapping;
            }

            boneData.MergedBones = uniqueBones.ToArray();

            // BindPosesを設定（元のメッシュから取得したものを使用）
            boneData.MergedBindPoses = new Matrix4x4[uniqueBones.Count];
            for (int i = 0; i < uniqueBones.Count; i++)
            {
                if (boneToBindPose.TryGetValue(uniqueBones[i], out var bindPose))
                {
                    boneData.MergedBindPoses[i] = bindPose;
                }
                else
                {
                    boneData.MergedBindPoses[i] = uniqueBones[i].worldToLocalMatrix;
                }
            }

            // RootBoneを決定（最初のRendererのものを使用）
            boneData.RootBone = renderers[0].rootBone;

            return boneData;
        }

        #endregion

        #region Mesh Processing

        private static Mesh MergeMeshData(List<SkinnedMeshRenderer> renderers, BoneData boneData, ChimeraHairMaster component, out Dictionary<SkinnedMeshRenderer, Mesh> remainingMeshes)
        {
            remainingMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();

            var mergedMesh = new Mesh();
            mergedMesh.name = "CHM_MergedMesh";

            // 統合データ用リスト
            var allVertices = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allTangents = new List<Vector4>();
            var allUV0 = new List<Vector2>();
            var allUV1 = new List<Vector2>();
            var allColors = new List<Color>();
            var allBoneWeights = new List<BoneWeight>();
            var allTriangles = new List<int>();

            int vertexOffset = 0;

            // まず各Rendererごとに含まれる/除外されるサブメッシュの三角形を収集
            var excludedTrianglesPerRenderer = new Dictionary<SkinnedMeshRenderer, Dictionary<int, int[]>>();

            for (int r = 0; r < renderers.Count; r++)
            {
                var renderer = renderers[r];
                var mesh = renderer.sharedMesh;

                // 頂点データ
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uv0 = mesh.uv;
                var uv1 = mesh.uv2;
                var colors = mesh.colors;
                var boneWeights = mesh.boneWeights;

                // BlendShapeの値を頂点に適用（ベイク）
                ApplyBlendShapesToVertices(renderer, mesh, ref vertices, ref normals, ref tangents);

                // ボーンインデックスのリマッピングを取得
                var boneRemap = boneData.BoneIndexRemapping[renderer];

                // 頂点を追加（レンダラー単位で全頂点を追加する。サブメッシュごとの厳密な頂点再割当は行わない）
                for (int i = 0; i < vertices.Length; i++)
                {
                    allVertices.Add(vertices[i]);

                    if (i < normals.Length)
                        allNormals.Add(normals[i]);
                    else
                        allNormals.Add(Vector3.up);

                    if (i < tangents.Length)
                        allTangents.Add(tangents[i]);
                    else
                        allTangents.Add(new Vector4(1, 0, 0, 1));

                    if (i < uv0.Length)
                        allUV0.Add(uv0[i]);
                    else
                        allUV0.Add(Vector2.zero);

                    if (i < uv1.Length)
                        allUV1.Add(uv1[i]);
                    else
                        allUV1.Add(Vector2.zero);

                    if (i < colors.Length)
                        allColors.Add(colors[i]);
                    else
                        allColors.Add(Color.white);

                    if (i < boneWeights.Length)
                    {
                        var weight = boneWeights[i];
                        weight.boneIndex0 = RemapBoneIndex(weight.boneIndex0, boneRemap);
                        weight.boneIndex1 = RemapBoneIndex(weight.boneIndex1, boneRemap);
                        weight.boneIndex2 = RemapBoneIndex(weight.boneIndex2, boneRemap);
                        weight.boneIndex3 = RemapBoneIndex(weight.boneIndex3, boneRemap);
                        allBoneWeights.Add(weight);
                    }
                    else
                    {
                        allBoneWeights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1f });
                    }
                }

                // サブメッシュ単位で三角形を処理
                int submeshCount = mesh.subMeshCount;
                var excluded = new Dictionary<int, int[]>();

                for (int s = 0; s < submeshCount; s++)
                {
                    var subTriangles = mesh.GetTriangles(s);

                    bool included = true;
                    if (component != null)
                    {
                        int compIndex = component.targetRenderers.IndexOf(renderer);
                        if (compIndex >= 0)
                        {
                            included = component.IsSubmeshIncluded(compIndex, s);
                        }
                    }

                    if (included)
                    {
                        // 統合対象: 頂点オフセットを掛けてマージ配列へ追加
                        for (int i = 0; i < subTriangles.Length; i++)
                        {
                            allTriangles.Add(subTriangles[i] + vertexOffset);
                        }
                    }
                    else
                    {
                        // 除外: 後で元のRendererに残すために保存
                        excluded[s] = subTriangles;
                    }
                }

                if (excluded.Count > 0)
                {
                    excludedTrianglesPerRenderer[renderer] = excluded;
                }

                vertexOffset += vertices.Length;
            }

            // マージメッシュを作成
            mergedMesh.SetVertices(allVertices);
            mergedMesh.SetNormals(allNormals);
            mergedMesh.SetTangents(allTangents);
            mergedMesh.SetUVs(0, allUV0);
            mergedMesh.SetUVs(1, allUV1);
            mergedMesh.SetColors(allColors);
            mergedMesh.boneWeights = allBoneWeights.ToArray();
            mergedMesh.bindposes = boneData.MergedBindPoses;
            mergedMesh.SetTriangles(allTriangles, 0);
            mergedMesh.RecalculateBounds();

            // 残すべきサブメッシュを持つレンダラーごとに新しいMeshを作成
            foreach (var kvp in excludedTrianglesPerRenderer)
            {
                var renderer = kvp.Key;
                var excludedMap = kvp.Value;
                var srcMesh = renderer.sharedMesh;

                var newMesh = new Mesh();
                newMesh.name = srcMesh.name + "_CHM_Remaining";

                // コピー可能なバッファを複製
                newMesh.vertices = srcMesh.vertices;
                newMesh.normals = srcMesh.normals;
                newMesh.tangents = srcMesh.tangents;
                newMesh.colors = srcMesh.colors;
                newMesh.uv = srcMesh.uv;
                newMesh.uv2 = srcMesh.uv2;
                newMesh.boneWeights = srcMesh.boneWeights;
                newMesh.bindposes = srcMesh.bindposes;

                // サブメッシュ数を設定
                int submeshCount = srcMesh.subMeshCount;
                newMesh.subMeshCount = submeshCount;

                // サブメッシュごとに除外された三角形だけを設定
                for (int s = 0; s < submeshCount; s++)
                {
                    if (excludedMap.TryGetValue(s, out var tris))
                    {
                        newMesh.SetTriangles(tris, s);
                    }
                    else
                    {
                        // 空のサブメッシュ
                        newMesh.SetTriangles(new int[0], s);
                    }
                }

                newMesh.RecalculateBounds();
                remainingMeshes[renderer] = newMesh;
            }

            return mergedMesh;
        }

        private static int RemapBoneIndex(int originalIndex, int[] remapping)
        {
            if (originalIndex < 0 || originalIndex >= remapping.Length)
                return 0;
            return remapping[originalIndex];
        }

        /// <summary>
        /// BlendShapeの値を頂点データに適用（ベイク）
        /// </summary>
        private static void ApplyBlendShapesToVertices(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            ref Vector3[] vertices,
            ref Vector3[] normals,
            ref Vector4[] tangents)
        {
            int blendShapeCount = mesh.blendShapeCount;
            if (blendShapeCount == 0) return;

            bool hasModifications = false;

            // 各BlendShapeの重みを確認
            for (int blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                float weight = renderer.GetBlendShapeWeight(blendShapeIndex);
                if (weight > 0.001f) // 重みがほぼ0の場合はスキップ
                {
                    hasModifications = true;
                    break;
                }
            }

            if (!hasModifications) return;

            Debug.Log($"[ChimeraHairMaster] Applying BlendShapes for {renderer.gameObject.name}");

            // BlendShapeを適用
            var deltaVertices = new Vector3[vertices.Length];
            var deltaNormals = new Vector3[normals.Length];
            var deltaTangents = new Vector3[tangents.Length];

            for (int blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                float weight = renderer.GetBlendShapeWeight(blendShapeIndex);
                if (weight <= 0.001f) continue;

                int frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
                if (frameCount == 0) continue;

                // 最後のフレーム（通常は100%の状態）を使用
                int frameIndex = frameCount - 1;
                float frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);

                // デルタ値を取得
                mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

                // 重みに応じてデルタを適用
                float normalizedWeight = weight / 100f; // BlendShapeの重みは0-100
                if (frameWeight > 0)
                {
                    normalizedWeight *= (frameWeight / 100f);
                }

                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] += deltaVertices[i] * normalizedWeight;
                }

                if (normals.Length == vertices.Length)
                {
                    for (int i = 0; i < normals.Length; i++)
                    {
                        normals[i] += deltaNormals[i] * normalizedWeight;
                        normals[i].Normalize();
                    }
                }

                if (tangents.Length == vertices.Length)
                {
                    for (int i = 0; i < tangents.Length; i++)
                    {
                        Vector3 tangentDelta = deltaTangents[i] * normalizedWeight;
                        tangents[i] = new Vector4(
                            tangents[i].x + tangentDelta.x,
                            tangents[i].y + tangentDelta.y,
                            tangents[i].z + tangentDelta.z,
                            tangents[i].w
                        );
                        // タンジェントのXYZを正規化
                        Vector3 t = new Vector3(tangents[i].x, tangents[i].y, tangents[i].z).normalized;
                        tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// 統合結果を新しいRendererに適用
        /// </summary>
        public static SkinnedMeshRenderer CreateMergedRenderer(
            MergeResult result,
            Material material,
            Transform parent,
            string name = "CHM_MergedHair",
            ChimeraHairMaster component = null)
        {
            // 新しいGameObjectを作成
            var mergedObject = new GameObject(name);
            mergedObject.transform.SetParent(parent);
            mergedObject.transform.localPosition = Vector3.zero;
            mergedObject.transform.localRotation = Quaternion.identity;
            mergedObject.transform.localScale = Vector3.one;

            // SkinnedMeshRendererを追加
            var renderer = mergedObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = result.MergedMesh;
            renderer.bones = result.Bones;
            renderer.rootBone = result.RootBone;
            renderer.localBounds = result.Bounds;
            renderer.sharedMaterial = material;
            
            // Probe Anchorを設定（継承モードを考慮）
            if (component != null && ShouldApplyProbeAnchorSetting(component))
            {
                renderer.probeAnchor = component.probeAnchor;
            }

            return renderer;
        }

        /// <summary>
        /// ProbeAnchor設定を適用すべきかどうかを判定
        /// </summary>
        private static bool ShouldApplyProbeAnchorSetting(ChimeraHairMaster component)
        {
            if (component == null) return false;

            switch (component.inheritProbeAnchor)
            {
                case MeshSettingsInheritMode.Set:
                    return component.probeAnchor != null;
                case MeshSettingsInheritMode.DontSet:
                    return false;
                case MeshSettingsInheritMode.SetOrInherit:
                    // ProbeAnchorが設定されていれば適用
                    return component.probeAnchor != null;
                case MeshSettingsInheritMode.Inherit:
                default:
                    // 親からの継承は現在未実装（将来的な拡張用）
                    return false;
            }
        }

        /// <summary>
        /// 元のRendererを無効化
        /// </summary>
        public static void DisableOriginalRenderers(List<SkinnedMeshRenderer> renderers)
        {
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = false;
                }
            }
        }

        /// <summary>
        /// 元のRendererのGameObjectを削除
        /// </summary>
        public static void DestroyOriginalRenderers(List<SkinnedMeshRenderer> renderers)
        {
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                {
                    Object.DestroyImmediate(renderer.gameObject);
                }
            }
        }
    }
}
