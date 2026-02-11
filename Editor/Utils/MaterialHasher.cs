using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// マテリアルのプロパティからハッシュ値を計算するユーティリティ
    /// マテリアルの内部変更を検知するために使用
    /// </summary>
    public static class MaterialHasher
    {
        /// <summary>
        /// マテリアルの全プロパティからハッシュ値を計算
        /// </summary>
        public static int ComputeHash(Material material)
        {
            if (material == null) return 0;

            int hash = 17;

            // SerializedObjectを使用してマテリアルの全プロパティを取得
            var serializedObject = new SerializedObject(material);
            var iterator = serializedObject.GetIterator();

            while (iterator.NextVisible(true))
            {
                // プロパティの値に基づいてハッシュを計算
                switch (iterator.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        hash = hash * 31 + iterator.intValue.GetHashCode();
                        break;
                    case SerializedPropertyType.Boolean:
                        hash = hash * 31 + iterator.boolValue.GetHashCode();
                        break;
                    case SerializedPropertyType.Float:
                        hash = hash * 31 + iterator.floatValue.GetHashCode();
                        break;
                    case SerializedPropertyType.String:
                        hash = hash * 31 + (iterator.stringValue?.GetHashCode() ?? 0);
                        break;
                    case SerializedPropertyType.Color:
                        hash = hash * 31 + iterator.colorValue.GetHashCode();
                        break;
                    case SerializedPropertyType.Vector2:
                        hash = hash * 31 + iterator.vector2Value.GetHashCode();
                        break;
                    case SerializedPropertyType.Vector3:
                        hash = hash * 31 + iterator.vector3Value.GetHashCode();
                        break;
                    case SerializedPropertyType.Vector4:
                        hash = hash * 31 + iterator.vector4Value.GetHashCode();
                        break;
                    case SerializedPropertyType.ObjectReference:
                        if (iterator.objectReferenceValue != null)
                        {
                            hash = hash * 31 + iterator.objectReferenceValue.GetInstanceID();
                        }
                        break;
                    case SerializedPropertyType.Enum:
                        hash = hash * 31 + iterator.enumValueIndex.GetHashCode();
                        break;
                }
            }

            return hash;
        }
    }
}
