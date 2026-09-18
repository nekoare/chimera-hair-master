using System.Collections.Generic;
using ChimeraHairMaster.Editor.Deformation;
using ChimeraHairMaster.Editor.Processing;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 頂点モードの減衰ウェイト計算（ドラッグ開始時と、ドラッグ前の影響範囲プレビューで共用）の挙動を固定する。
    /// </summary>
    public class FalloffWeightCalculatorTests
    {
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

        // X 軸上に 0, 0.5, 1.0, 2.0 の 4 頂点
        private static Vector3[] LineVertices() => new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0.5f, 0f, 0f),
            new Vector3(1.0f, 0f, 0f),
            new Vector3(2.0f, 0f, 0f),
        };

        private static (Dictionary<int, float> weights, Dictionary<int, float> distances) Run(
            Vector3[] vertices, HashSet<int> selected, float radius,
            FalloffType falloff, DistanceMetric metric, GeodesicDistanceCalculator geodesic = null)
        {
            var weights = new Dictionary<int, float>();
            var distances = new Dictionary<int, float>();
            FalloffWeightCalculator.Compute(
                vertices, selected, radius, falloff, metric, geodesic, weights, distances);
            return (weights, distances);
        }

        [Test]
        public void Euclidean_SelectedVertexHasWeightOne()
        {
            var (weights, _) = Run(LineVertices(), new HashSet<int> { 0 }, 1.0f,
                FalloffType.Smooth, DistanceMetric.Euclidean);

            Assert.That(weights[0], Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Euclidean_WeightFollowsFalloffCurveByDistanceRatio()
        {
            var (weights, _) = Run(LineVertices(), new HashSet<int> { 0 }, 1.0f,
                FalloffType.Linear, DistanceMetric.Euclidean);

            // 距離 0.5 / 半径 1.0 = t 0.5 → Linear: 1 - t = 0.5
            Assert.That(weights[1], Is.EqualTo(0.5f).Within(1e-6f));
            // 距離 1.0 = 半径ちょうど → t 1.0 → 0
            Assert.That(weights[2], Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void Euclidean_VertexBeyondRadius_IsNotWeightedButDistanceIsCached()
        {
            var (weights, distances) = Run(LineVertices(), new HashSet<int> { 0 }, 1.0f,
                FalloffType.Smooth, DistanceMetric.Euclidean);

            Assert.That(weights.ContainsKey(3), Is.False, "半径外の頂点にウェイトが張られている");
            Assert.That(distances[3], Is.EqualTo(2.0f).Within(1e-6f), "半径外でも距離はキャッシュされる");
        }

        [Test]
        public void Constant_DoesNotLeakBeyondRadius()
        {
            var (weights, _) = Run(LineVertices(), new HashSet<int> { 0 }, 1.0f,
                FalloffType.Constant, DistanceMetric.Euclidean);

            Assert.That(weights[1], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(weights.ContainsKey(3), Is.False, "均一減衰が半径外まで漏れている");
        }

        [Test]
        public void Euclidean_MultiSelection_UsesCentroidAsCenter()
        {
            // 頂点 0 (x=0) と 2 (x=1) を選択 → 中心 x=0.5 = 頂点 1 の位置
            var (weights, distances) = Run(LineVertices(), new HashSet<int> { 0, 2 }, 1.0f,
                FalloffType.Linear, DistanceMetric.Euclidean);

            Assert.That(distances[1], Is.EqualTo(0f).Within(1e-6f));
            Assert.That(weights[1], Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Geodesic_DisconnectedNearbyVertex_GetsNoWeight()
        {
            // 三角形 A (0,1,2) と、空間的には近いが繋がっていない三角形 B (3,4,5)
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0.1f, 0f),
                new Vector3(0f, 0f, 0.05f), new Vector3(0.1f, 0f, 0.05f), new Vector3(0f, 0.1f, 0.05f),
            };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            _cleanup.Add(mesh);
            var geodesic = new GeodesicDistanceCalculator(mesh);

            var (weights, _) = Run(mesh.vertices, new HashSet<int> { 0 }, 1.0f,
                FalloffType.Smooth, DistanceMetric.Geodesic, geodesic);

            Assert.That(weights.ContainsKey(1), Is.True, "同じ面の頂点にウェイトが無い");
            Assert.That(weights.ContainsKey(3), Is.False, "表面で繋がっていない頂点にウェイトが張られている");
        }

        [Test]
        public void Geodesic_WeightIsGatedByRadiusEvenThoughDistancesGoFurther()
        {
            // 一直線に繋がった帯: 0-1-2-3 (各 0.5 間隔)
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0f, 0f),
                new Vector3(1.0f, 0f, 0f), new Vector3(1.5f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0.5f, 1f, 0f),
                new Vector3(1.0f, 1f, 0f), new Vector3(1.5f, 1f, 0f),
            };
            mesh.triangles = new[]
            {
                0, 4, 1, 4, 5, 1,
                1, 5, 2, 5, 6, 2,
                2, 6, 3, 6, 7, 3,
            };
            _cleanup.Add(mesh);
            var geodesic = new GeodesicDistanceCalculator(mesh);

            var (weights, distances) = Run(mesh.vertices, new HashSet<int> { 0 }, 0.75f,
                FalloffType.Constant, DistanceMetric.Geodesic, geodesic);

            Assert.That(weights.ContainsKey(1), Is.True);
            Assert.That(weights.ContainsKey(2), Is.False, "半径 0.75 の外（表面距離 1.0）にウェイトが張られている");
            Assert.That(distances.ContainsKey(2), Is.True, "半径拡大に備えて 2R までの距離はキャッシュされる");
        }

        [Test]
        public void EmptySelection_ProducesNothing()
        {
            var (weights, distances) = Run(LineVertices(), new HashSet<int>(), 1.0f,
                FalloffType.Smooth, DistanceMetric.Euclidean);

            Assert.That(weights, Is.Empty);
            Assert.That(distances, Is.Empty);
        }

        [Test]
        public void Mirrored_AddsWeightsAroundMirroredCenter_ExcludingPrimaryVertices()
        {
            // x = -1, -0.5, 0.5, 1 の 4 頂点。頂点 3 (x=1) を選択、X 対称（オフセット 0）
            var vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(-0.5f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0f, 0f),
            };
            var selected = new HashSet<int> { 3 };
            var primary = new Dictionary<int, float>();
            FalloffWeightCalculator.Compute(vertices, selected, 0.6f,
                FalloffType.Linear, DistanceMetric.Euclidean, null, primary, new Dictionary<int, float>());
            Assert.That(primary.ContainsKey(2), Is.True, "テスト前提: 主側に頂点 2 が入る");

            var mirrored = new Dictionary<int, float>();
            FalloffWeightCalculator.ComputeMirrored(
                vertices, selected, 0.6f, FalloffType.Linear,
                p => new Vector3(-p.x, p.y, p.z), primary, mirrored);

            Assert.That(mirrored[0], Is.EqualTo(1f).Within(1e-6f), "鏡像中心の頂点はウェイト 1");
            Assert.That(mirrored[1], Is.EqualTo(1f - 0.5f / 0.6f).Within(1e-5f));
            Assert.That(mirrored.ContainsKey(2), Is.False, "主側に含まれる頂点は対称側に入れない（二重適用防止）");
            Assert.That(mirrored.ContainsKey(3), Is.False);
        }

        [Test]
        public void EvaluateFalloff_MatchesSceneEditorCurves()
        {
            Assert.That(FalloffWeightCalculator.EvaluateFalloff(0.5f, FalloffType.Constant), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(FalloffWeightCalculator.EvaluateFalloff(0.5f, FalloffType.Linear), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(FalloffWeightCalculator.EvaluateFalloff(0.5f, FalloffType.Smooth), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(FalloffWeightCalculator.EvaluateFalloff(0.6f, FalloffType.Sphere), Is.EqualTo(0.8f).Within(1e-6f));
            // 既存の公開 API と同じ値を返す
            Assert.That(FalloffWeightCalculator.EvaluateFalloff(0.3f, FalloffType.Smooth),
                Is.EqualTo(MeshDeformationSceneEditor.EvaluateFalloff(0.3f, FalloffType.Smooth)).Within(1e-6f));
        }
    }
}
