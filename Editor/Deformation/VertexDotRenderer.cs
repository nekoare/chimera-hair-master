using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Deformation
{
    /// <summary>
    /// GPU instancingによる高速頂点ドット描画
    /// 数千〜数万頂点でもScene Viewのパフォーマンスを維持する
    /// </summary>
    public class VertexDotRenderer
    {
        private const int MAX_BATCH_SIZE = 1023; // Graphics.DrawMeshInstanced の上限
        private const string SHADER_NAME = "Hidden/CHM/VertexDot";

        private Material _material;
        private Mesh _quadMesh;

        // instancing用プロパティバッファ
        private readonly List<Matrix4x4> _matrices = new List<Matrix4x4>();
        private readonly List<Vector4> _colors = new List<Vector4>();
        private readonly List<Vector4> _positions = new List<Vector4>();
        private MaterialPropertyBlock _propertyBlock;

        private static readonly int ColorProp = Shader.PropertyToID("_Color");
        private static readonly int WorldPosProp = Shader.PropertyToID("_WorldPos");
        private static readonly int SizeProp = Shader.PropertyToID("_Size");

        /// <summary>
        /// ドットの表示サイズ（ワールド空間に対するカメラ距離スケール係数）
        /// </summary>
        public float DotSize { get; set; } = 0.003f;

        /// <summary>
        /// Z-testを有効にするか（有効=遮蔽された頂点は非表示）
        /// </summary>
        public bool EnableZTest { get; set; } = true;

        public void Initialize()
        {
            if (_material == null)
            {
                var shader = Shader.Find(SHADER_NAME);
                if (shader == null)
                {
                    Debug.LogWarning("[CHM] VertexDot shader not found. Falling back to Handles drawing.");
                    return;
                }
                _material = new Material(shader);
                _material.enableInstancing = true;
            }

            if (_quadMesh == null)
            {
                _quadMesh = CreateQuadMesh();
            }

            if (_propertyBlock == null)
            {
                _propertyBlock = new MaterialPropertyBlock();
            }
        }

        /// <summary>
        /// 頂点群を一括描画する
        /// </summary>
        /// <param name="worldPositions">ワールド座標の配列</param>
        /// <param name="colors">各頂点の色（worldPositionsと同じ長さ）</param>
        public void Draw(Vector3[] worldPositions, Color[] colors)
        {
            if (_material == null || _quadMesh == null) return;
            if (worldPositions == null || worldPositions.Length == 0) return;

            _material.SetFloat(SizeProp, DotSize);

            int passIndex = EnableZTest ? 0 : 1;

            // バッチ単位で描画
            for (int offset = 0; offset < worldPositions.Length; offset += MAX_BATCH_SIZE)
            {
                int count = Mathf.Min(MAX_BATCH_SIZE, worldPositions.Length - offset);

                _matrices.Clear();
                _positions.Clear();
                _colors.Clear();

                for (int i = 0; i < count; i++)
                {
                    int idx = offset + i;
                    _matrices.Add(Matrix4x4.identity);
                    _positions.Add(new Vector4(
                        worldPositions[idx].x,
                        worldPositions[idx].y,
                        worldPositions[idx].z, 1));
                    _colors.Add(colors != null && idx < colors.Length
                        ? (Vector4)colors[idx]
                        : new Vector4(0, 0.8f, 1, 0.3f));
                }

                _propertyBlock.SetVectorArray(WorldPosProp, _positions);
                _propertyBlock.SetVectorArray(ColorProp, _colors);

                Graphics.DrawMeshInstanced(
                    _quadMesh,
                    0,
                    _material,
                    _matrices,
                    _propertyBlock,
                    UnityEngine.Rendering.ShadowCastingMode.Off,
                    false,
                    0, // layer
                    null, // camera (null = all)
                    UnityEngine.Rendering.LightProbeUsage.Off);
            }
        }

        public void Dispose()
        {
            if (_material != null) Object.DestroyImmediate(_material);
            if (_quadMesh != null) Object.DestroyImmediate(_quadMesh);
            _material = null;
            _quadMesh = null;
        }

        /// <summary>
        /// ビルボード用の1x1クアッドメッシュを作成
        /// </summary>
        private static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh { name = "CHM_VertexDotQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3( 0.5f, -0.5f, 0),
                new Vector3( 0.5f,  0.5f, 0),
                new Vector3(-0.5f,  0.5f, 0),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
