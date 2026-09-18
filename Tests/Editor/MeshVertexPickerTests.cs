using ChimeraHairMaster.Editor.Deformation;
using NUnit.Framework;
using UnityEngine;

namespace ChimeraHairMaster.Tests
{
    /// <summary>
    /// 頂点モードのクリック選択・ホバーで使う「遮蔽を考慮した頂点ピック」の幾何を固定する。
    /// Z-test ON のとき、他の三角形の裏に隠れた頂点はクリックで掴めない。
    /// </summary>
    public class MeshVertexPickerTests
    {
        // カメラは (0,0,-5) から +Z を見る。画面座標は x,y を 100 倍した簡易投影
        private static readonly Vector3 CameraPos = new Vector3(0f, 0f, -5f);
        private static Vector2 ToScreen(Vector3 p) => new Vector2(p.x, p.y) * 100f;
        private static Ray RayTo(Vector3 p) => new Ray(CameraPos, (p - CameraPos).normalized);
        private static float DistanceAlongRay(Ray ray, Vector3 p) => Vector3.Dot(p - ray.origin, ray.direction);

        /// <summary>
        /// 奥の三角形 B (0,1,2) は z=2、手前の大きな三角形 F (3,4,5) は z=1 で B を完全に覆う
        /// </summary>
        private static (Vector3[] vertices, int[] triangles) OccludedScene()
        {
            var vertices = new[]
            {
                new Vector3(0f, 0f, 2f), new Vector3(1f, 0f, 2f), new Vector3(0f, 1f, 2f),       // B
                new Vector3(-1f, -1f, 1f), new Vector3(3f, -1f, 1f), new Vector3(-1f, 3f, 1f),   // F
            };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };
            return (vertices, triangles);
        }

        #region RayTriangle

        [Test]
        public void RayTriangle_HitsTriangle_ReturnsDistanceAlongRay()
        {
            var ray = new Ray(Vector3.zero, Vector3.forward);
            bool hit = MeshVertexPicker.RayTriangle(ray,
                new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(0f, 1f, 1f), out float t);

            Assert.That(hit, Is.True);
            Assert.That(t, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void RayTriangle_MissesOutsideTriangle()
        {
            var ray = new Ray(new Vector3(5f, 5f, 0f), Vector3.forward);
            bool hit = MeshVertexPicker.RayTriangle(ray,
                new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(0f, 1f, 1f), out _);

            Assert.That(hit, Is.False);
        }

        [Test]
        public void RayTriangle_HitsBackFaceToo()
        {
            // 遮蔽物は両面扱い（Cull Off の髪シェーダーでは裏面も描画される）
            var ray = new Ray(Vector3.zero, Vector3.forward);
            bool hit = MeshVertexPicker.RayTriangle(ray,
                new Vector3(0f, 1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(-1f, -1f, 1f), out float t);

            Assert.That(hit, Is.True);
            Assert.That(t, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void RayTriangle_IgnoresTriangleBehindRayOrigin()
        {
            var ray = new Ray(new Vector3(0f, 0f, 2f), Vector3.forward);
            bool hit = MeshVertexPicker.RayTriangle(ray,
                new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(0f, 1f, 1f), out _);

            Assert.That(hit, Is.False);
        }

        #endregion

        #region IsOccluded

        [Test]
        public void IsOccluded_VertexBehindAnotherTriangle_IsOccluded()
        {
            var (vertices, triangles) = OccludedScene();
            var ray = RayTo(vertices[0]);

            bool occluded = MeshVertexPicker.IsOccluded(
                ray, DistanceAlongRay(ray, vertices[0]), 0, vertices, triangles);

            Assert.That(occluded, Is.True);
        }

        [Test]
        public void IsOccluded_OwnTrianglesAreIgnored()
        {
            var (vertices, triangles) = OccludedScene();
            // 手前の三角形の頂点 3 は自分の三角形 F しか通らない
            var ray = RayTo(vertices[3]);

            bool occluded = MeshVertexPicker.IsOccluded(
                ray, DistanceAlongRay(ray, vertices[3]), 3, vertices, triangles);

            Assert.That(occluded, Is.False);
        }

        [Test]
        public void IsOccluded_CoincidentSeamDuplicateTriangle_DoesNotOcclude()
        {
            // UV 継ぎ目で分割された重複頂点: 頂点 3 と同じ位置の頂点 6 を含む共面三角形は
            // t == 頂点までの距離 でヒットするため遮蔽扱いにしない
            var vertices = new[]
            {
                new Vector3(0f, 0f, 2f), new Vector3(1f, 0f, 2f), new Vector3(0f, 1f, 2f),
                new Vector3(-1f, -1f, 1f), new Vector3(3f, -1f, 1f), new Vector3(-1f, 3f, 1f),
                new Vector3(-1f, -1f, 1f), new Vector3(-1f, -3f, 1f), new Vector3(2f, -3f, 1f),
            };
            var triangles = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            var ray = RayTo(vertices[3]);

            bool occluded = MeshVertexPicker.IsOccluded(
                ray, DistanceAlongRay(ray, vertices[3]), 3, vertices, triangles);

            Assert.That(occluded, Is.False);
        }

        #endregion

        #region PickVisibleVertex

        private static int Pick(Vector3[] vertices, int[] triangles, Vector2 mouse,
            bool testOcclusion, bool[] visibleMask = null, float maxScreenDistance = 200f)
        {
            return MeshVertexPicker.PickVisibleVertex(
                vertices, triangles, visibleMask,
                ToScreen, mouse, maxScreenDistance,
                RayTo, testOcclusion);
        }

        [Test]
        public void Pick_ReturnsNearestOnScreen_WhenItIsVisible()
        {
            var (vertices, triangles) = OccludedScene();
            // 頂点 3 (-1,-1,1) の真上をクリック
            int picked = Pick(vertices, triangles, new Vector2(-100f, -100f), testOcclusion: true);

            Assert.That(picked, Is.EqualTo(3));
        }

        [Test]
        public void Pick_SkipsOccludedNearestVertex_AndReturnsNextVisibleOne()
        {
            var (vertices, triangles) = OccludedScene();
            // 画面 (0,0): 最寄りは奥の頂点 0 だが手前の F に隠れている。
            // 次に近い可視頂点は 3 (-1,-1,1) → 画面距離 141
            int picked = Pick(vertices, triangles, Vector2.zero, testOcclusion: true);

            Assert.That(picked, Is.EqualTo(3));
        }

        [Test]
        public void Pick_WithoutOcclusionTest_ReturnsNearestEvenIfHidden()
        {
            // Z-test OFF 相当: 隠れた頂点も見えている前提で従来通り最寄りを返す
            var (vertices, triangles) = OccludedScene();
            int picked = Pick(vertices, triangles, Vector2.zero, testOcclusion: false);

            Assert.That(picked, Is.EqualTo(0));
        }

        [Test]
        public void Pick_RespectsVisibleMask()
        {
            var (vertices, triangles) = OccludedScene();
            // 背面カリング相当: 頂点 3 を不可視にすると、(−100,−100) 付近の次点 0 は遮蔽、
            // 残る可視候補は 4 (300,−100) と 5 (−100,300) で 200px 以内に無い → -1
            var mask = new[] { true, true, true, false, true, true };
            int picked = Pick(vertices, triangles, new Vector2(-100f, -100f), testOcclusion: true, visibleMask: mask);

            Assert.That(picked, Is.EqualTo(-1));
        }

        [Test]
        public void Pick_ReturnsMinusOne_WhenNothingWithinScreenDistance()
        {
            var (vertices, triangles) = OccludedScene();
            int picked = Pick(vertices, triangles, new Vector2(5000f, 5000f), testOcclusion: true);

            Assert.That(picked, Is.EqualTo(-1));
        }

        [Test]
        public void Pick_AllCandidatesOccluded_ReturnsMinusOne()
        {
            var (vertices, triangles) = OccludedScene();
            // 画面距離 120px 以内は奥の 0,1,2 のみ（全部 F に隠れている）
            int picked = Pick(vertices, triangles, Vector2.zero, testOcclusion: true, maxScreenDistance: 120f);

            Assert.That(picked, Is.EqualTo(-1));
        }

        #endregion
    }
}
