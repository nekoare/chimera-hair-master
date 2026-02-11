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

        private static void Initialize()
        {
            _initialized = true;

            // パッケージ内のシェーダーを検索
            string[] guids = AssetDatabase.FindAssets("ColorTransform t:ComputeShader");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("com.nekoare.chimera-hair-master"))
                {
                    _colorTransformShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    if (_colorTransformShader != null)
                    {
                        return;
                    }
                }
            }

            // 見つからない場合はパスで直接検索
            string[] searchPaths = new[]
            {
                "Packages/com.nekoare.chimera-hair-master/Editor/Shader/ColorTransform.compute",
                "Assets/com.nekoare.chimera-hair-master/Editor/Shader/ColorTransform.compute"
            };

            foreach (string path in searchPaths)
            {
                _colorTransformShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (_colorTransformShader != null)
                {
                    return;
                }
            }

            Debug.LogError("[ChimeraHairMaster] ColorTransform.compute が見つかりません");
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
