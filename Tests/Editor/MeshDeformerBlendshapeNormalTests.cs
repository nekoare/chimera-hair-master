using System.Collections.Generic;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 「Blendshape として出力」で法線デルタが含まれることを固定するテスト。
    /// 以前は AddBlendShapeFrame に normals=null を渡していたため、BlendShape を効かせても
    /// 陰影が変形前のままだった。法線デルタは「再計算した変形後法線 − 再計算した未変形法線」で、
    /// 変形頂点と三角形を共有しない頂点ではゼロになる。
    /// </summary>
    public class MeshDeformerBlendshapeNormalTests
    {
        private const int GridSize = 4; // 4x4 = 16 頂点、18 三角形
        private const int MovedVertex = 0;          // 角 (0,0,0)。属する三角形は (0,4,1) の 1 枚だけ
        private const int UntouchedVertex = 15;     // 対角の角 (3,0,3)。頂点 0 と三角形を共有しない
        private static readonly Vector3 MoveOffset = new Vector3(0f, 0.5f, 0f);

        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        /// <summary>
        /// XZ 平面の格子。三角形は上向き (+Y) 法線になる巻き順。
        /// 法線は authored 相当として RecalculateNormals 済みの値を持たせる。
        /// </summary>
        private Mesh CreateGridMesh()
        {
            var vertices = new Vector3[GridSize * GridSize];
            for (int z = 0; z < GridSize; z++)
            for (int x = 0; x < GridSize; x++)
                vertices[z * GridSize + x] = new Vector3(x, 0f, z);

            var triangles = new List<int>();
            for (int z = 0; z < GridSize - 1; z++)
            for (int x = 0; x < GridSize - 1; x++)
            {
                int a = z * GridSize + x;
                int b = (z + 1) * GridSize + x;
                int c = (z + 1) * GridSize + x + 1;
                int d = z * GridSize + x + 1;
                triangles.AddRange(new[] { a, b, d });
                triangles.AddRange(new[] { b, c, d });
            }

            var mesh = new Mesh { name = "Grid" };
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _cleanup.Add(mesh);
            return mesh;
        }

        private SkinnedMeshRenderer CreateRenderer(Mesh mesh)
        {
            var go = new GameObject("BlendshapeNormalTest");
            _cleanup.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        private static RendererDeformation CreateDeformation(Mesh mesh)
        {
            var deformation = new RendererDeformation(0, mesh.vertexCount);
            deformation.deltas.Add(new VertexDelta(MovedVertex, MoveOffset));
            return deformation;
        }

        private Mesh Export(out Mesh source, out string actualName)
        {
            source = CreateGridMesh();
            var renderer = CreateRenderer(source);
            var deformation = CreateDeformation(source);

            var exported = MeshDeformer.ExportDeformedMeshAsBlendshape(
                renderer, deformation, "TestShape", out actualName);
            Assert.That(exported, Is.Not.Null);
            _cleanup.Add(exported);
            return exported;
        }

        private static void ReadFrame(Mesh mesh, string shapeName,
            out Vector3[] deltaV, out Vector3[] deltaN, out Vector3[] deltaT)
        {
            int shapeIndex = mesh.GetBlendShapeIndex(shapeName);
            Assert.That(shapeIndex, Is.GreaterThanOrEqualTo(0), "BlendShape が追加されていない");
            deltaV = new Vector3[mesh.vertexCount];
            deltaN = new Vector3[mesh.vertexCount];
            deltaT = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, 0, deltaV, deltaN, deltaT);
        }

        /// <summary>
        /// 期待値: 「再計算した変形後法線 − 再計算した未変形法線」を独立に計算する。
        /// </summary>
        private Vector3 ExpectedNormalDelta(Mesh source, int vertexIndex)
        {
            var baseMesh = Object.Instantiate(source);
            _cleanup.Add(baseMesh);
            baseMesh.RecalculateNormals();
            var baseNormal = baseMesh.normals[vertexIndex];

            var deformed = Object.Instantiate(source);
            _cleanup.Add(deformed);
            var v = deformed.vertices;
            v[MovedVertex] += MoveOffset;
            deformed.vertices = v;
            deformed.RecalculateNormals();
            var deformedNormal = deformed.normals[vertexIndex];

            return deformedNormal - baseNormal;
        }

        [Test]
        public void UntouchedVertex_HasZeroNormalDelta()
        {
            var exported = Export(out _, out var name);
            ReadFrame(exported, name, out _, out var deltaN, out _);

            Assert.That(deltaN[UntouchedVertex].magnitude, Is.LessThan(1e-6f),
                "変形頂点と三角形を共有しない頂点に法線デルタが乗っている");
        }

        [Test]
        public void MovedVertex_HasNonZeroNormalDelta_MatchingRecalculatedDifference()
        {
            var exported = Export(out var source, out var name);
            ReadFrame(exported, name, out _, out var deltaN, out _);

            var expected = ExpectedNormalDelta(source, MovedVertex);
            Assert.That(expected.magnitude, Is.GreaterThan(0.01f), "テスト前提: 変形で法線が変わること");
            Assert.That(deltaN[MovedVertex].magnitude, Is.GreaterThan(0.01f),
                "変形頂点の法線デルタがゼロ（法線デルタが出力されていない）");
            Assert.That((deltaN[MovedVertex] - expected).magnitude, Is.LessThan(1e-4f),
                "法線デルタが再計算差分と一致しない");
        }

        [Test]
        public void NeighborOfMovedVertex_HasNormalDelta_MatchingRecalculatedDifference()
        {
            // 頂点 4 は (0,4,1) を共有するため、頂点 0 を動かすと法線が傾く
            const int neighbor = 4;
            var exported = Export(out var source, out var name);
            ReadFrame(exported, name, out _, out var deltaN, out _);

            var expected = ExpectedNormalDelta(source, neighbor);
            Assert.That(expected.magnitude, Is.GreaterThan(0.01f), "テスト前提: 隣接頂点の法線が変わること");
            Assert.That((deltaN[neighbor] - expected).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ExportedMesh_KeepsSourceBaseNormals()
        {
            var exported = Export(out var source, out _);

            var sourceNormals = source.normals;
            var exportedNormals = exported.normals;
            Assert.That(exportedNormals.Length, Is.EqualTo(sourceNormals.Length));
            for (int i = 0; i < sourceNormals.Length; i++)
            {
                Assert.That((exportedNormals[i] - sourceNormals[i]).magnitude, Is.LessThan(1e-6f),
                    $"出力メッシュの基準法線が書き換わっている (vertex {i})");
            }
        }

        [Test]
        public void VertexDelta_IsStillWrittenAsBefore()
        {
            var exported = Export(out _, out var name);
            ReadFrame(exported, name, out var deltaV, out _, out _);

            Assert.That((deltaV[MovedVertex] - MoveOffset).magnitude, Is.LessThan(1e-6f));
            Assert.That(deltaV[UntouchedVertex].magnitude, Is.LessThan(1e-6f));
        }
    }
}
