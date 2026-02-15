using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// マスクツールのfallbackレジストリ
    /// com.nekoare.mask-creation-tool (UPM版) がない場合に、
    /// Assets版マスクツールが自身をハンドラとして登録する
    /// </summary>
    public static class MaskToolRegistry
    {
        public delegate void OpenMaskToolHandler(MaskToolOpenRequest request);
        public delegate void OpenSubmeshHandler(SkinnedMeshRenderer renderer, int submeshIndex);

        private static OpenMaskToolHandler _handler;
        private static OpenSubmeshHandler _submeshHandler;

        /// <summary>
        /// fallbackハンドラが登録されているかどうか
        /// </summary>
        public static bool HasHandler => _handler != null;

        /// <summary>
        /// SubMesh指定対応のハンドラが登録されているか（v1.1+）
        /// </summary>
        public static bool HasSubmeshHandler => _submeshHandler != null;

        /// <summary>
        /// マスクツールのfallbackハンドラを登録する
        /// </summary>
        public static void Register(OpenMaskToolHandler handler)
        {
            _handler = handler;
        }

        /// <summary>
        /// SubMesh指定対応のハンドラを登録する（v1.1+）
        /// </summary>
        public static void RegisterSubmesh(OpenSubmeshHandler handler)
        {
            _submeshHandler = handler;
        }

        /// <summary>
        /// 登録を解除する
        /// </summary>
        public static void Unregister()
        {
            _handler = null;
            _submeshHandler = null;
        }

        /// <summary>
        /// 登録されたハンドラでマスクツールを開く
        /// </summary>
        public static void Open(MaskToolOpenRequest request)
        {
            _handler?.Invoke(request);
        }

        /// <summary>
        /// SubMesh指定でマスクツールを開く（v1.1+）
        /// </summary>
        public static void OpenForSubmesh(SkinnedMeshRenderer renderer, int submeshIndex)
        {
            _submeshHandler?.Invoke(renderer, submeshIndex);
        }
    }
}
