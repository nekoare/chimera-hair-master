#nullable enable
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// シェーダーユーティリティ
    /// </summary>
    internal static class ShaderUtils
    {
        private static ComputeShader? _colorTransformShader;
        private static ComputeShader? _blurSharpenShader;
        private static ComputeShader? _dilateTextureShader;
        private static bool _initialized = false;

        /// <summary>
        /// 色変換ComputeShaderを取得
        /// </summary>
        internal static ComputeShader? GetColorTransformShader()
        {
            if (!_initialized)
            {
                Initialize();
            }
            return _colorTransformShader;
        }

        /// <summary>
        /// ブラー／シャープComputeShaderを取得
        /// </summary>
        internal static ComputeShader? GetBlurSharpenShader()
        {
            if (!_initialized)
            {
                Initialize();
            }
            return _blurSharpenShader;
        }

        /// <summary>
        /// Dilation ComputeShader を取得
        /// </summary>
        internal static ComputeShader? GetDilateTextureShader()
        {
            if (!_initialized)
            {
                Initialize();
            }
            return _dilateTextureShader;
        }

        private static void Initialize()
        {
            _initialized = true;

            _colorTransformShader = LoadShader("ColorTransform", "ColorTransform.compute");
            _blurSharpenShader = LoadShader("BlurSharpen", "BlurSharpen.compute");
            _dilateTextureShader = LoadShader("DilateTexture", "DilateTexture.compute");

            if (_colorTransformShader == null)
                Debug.LogError("[ChimeraHairMaster] ColorTransform.compute が見つかりません");
            if (_blurSharpenShader == null)
                Debug.LogError("[ChimeraHairMaster] BlurSharpen.compute が見つかりません");
            if (_dilateTextureShader == null)
                Debug.LogError("[ChimeraHairMaster] DilateTexture.compute が見つかりません");
        }

        private static ComputeShader? LoadShader(string searchName, string fileName)
        {
            string[] guids = AssetDatabase.FindAssets($"{searchName} t:ComputeShader");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("com.nekoare.chimera-hair-master"))
                {
                    var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    if (shader != null) return shader;
                }
            }

            string[] fallbackPaths = new[]
            {
                $"Packages/com.nekoare.chimera-hair-master/Editor/Shader/{fileName}",
                $"Assets/com.nekoare.chimera-hair-master/Editor/Shader/{fileName}"
            };
            foreach (string path in fallbackPaths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (shader != null) return shader;
            }

            return null;
        }

        /// <summary>
        /// GPU処理がサポートされているか確認
        /// </summary>
        internal static bool IsGPUSupported()
        {
            return SystemInfo.supportsComputeShaders;
        }

        /// <summary>
        /// テクスチャにミップストリーミング設定を適用
        /// VRChatで必須のミップストリーミングを有効にする
        /// </summary>
        internal static void EnableMipStreaming(Texture2D texture)
        {
            if (texture == null) return;

            using var serializedTexture = new SerializedObject(texture);
            var streamingMipmapsProperty = serializedTexture.FindProperty("m_StreamingMipmaps");
            var streamingMipmapsPriorityProperty = serializedTexture.FindProperty("m_StreamingMipmapsPriority");
            
            if (streamingMipmapsProperty != null)
            {
                streamingMipmapsProperty.boolValue = true;
            }
            if (streamingMipmapsPriorityProperty != null)
            {
                streamingMipmapsPriorityProperty.intValue = 0;
            }
            
            serializedTexture.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
