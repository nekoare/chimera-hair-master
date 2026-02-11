#nullable enable
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// Materialの状態をハッシュ化して変更検知に使用
    /// </summary>
    public static class MaterialHasher
    {
        /// <summary>
        /// Materialの全プロパティからハッシュ値を計算
        /// </summary>
        public static int ComputeHash(Material? material)
        {
            if (material == null) return 0;

            unchecked
            {
                int hash = 17;
                
                // シェーダー名
                if (material.shader != null)
                {
                    hash = hash * 31 + material.shader.name.GetHashCode();
                }

                // SerializedObjectで全プロパティを取得
                using var serializedMaterial = new SerializedObject(material);
                var property = serializedMaterial.GetIterator();
                
                while (property.NextVisible(true))
                {
                    hash = hash * 31 + GetPropertyHash(property);
                }

                return hash;
            }
        }

        /// <summary>
        /// SerializedPropertyのハッシュを計算
        /// </summary>
        private static int GetPropertyHash(SerializedProperty property)
        {
            unchecked
            {
                int hash = property.name.GetHashCode();

                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        hash = hash * 31 + property.intValue;
                        break;
                    case SerializedPropertyType.Boolean:
                        hash = hash * 31 + (property.boolValue ? 1 : 0);
                        break;
                    case SerializedPropertyType.Float:
                        hash = hash * 31 + property.floatValue.GetHashCode();
                        break;
                    case SerializedPropertyType.String:
                        hash = hash * 31 + (property.stringValue?.GetHashCode() ?? 0);
                        break;
                    case SerializedPropertyType.Color:
                        hash = hash * 31 + property.colorValue.GetHashCode();
                        break;
                    case SerializedPropertyType.ObjectReference:
                        hash = hash * 31 + (property.objectReferenceValue?.GetInstanceID() ?? 0);
                        break;
                    case SerializedPropertyType.Vector2:
                        hash = hash * 31 + property.vector2Value.GetHashCode();
                        break;
                    case SerializedPropertyType.Vector3:
                        hash = hash * 31 + property.vector3Value.GetHashCode();
                        break;
                    case SerializedPropertyType.Vector4:
                        hash = hash * 31 + property.vector4Value.GetHashCode();
                        break;
                    case SerializedPropertyType.Rect:
                        hash = hash * 31 + property.rectValue.GetHashCode();
                        break;
                    case SerializedPropertyType.Enum:
                        hash = hash * 31 + property.enumValueIndex;
                        break;
                    // 他のタイプは無視（ネストした構造体など）
                }

                return hash;
            }
        }

        /// <summary>
        /// 2つのMaterialが同じ設定かどうかを比較
        /// </summary>
        public static bool AreEqual(Material? a, Material? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return ComputeHash(a) == ComputeHash(b);
        }
    }
}
